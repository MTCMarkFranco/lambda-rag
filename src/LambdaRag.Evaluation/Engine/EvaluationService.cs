using System.Text.Json.Nodes;
using LambdaRag.Core;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Evaluation.Workflow;
using Microsoft.Extensions.Logging;
using RE = RulesEngine;

namespace LambdaRag.Evaluation.Engine;

/// <summary>
/// The deterministic core of lambda-rag.
///
/// Pipeline per rule (sorted by Id for stable verdict order):
///   1. Run the rule's <see cref="Rule.Selector"/> against the projected
///      document → candidate <see cref="MatchedSection"/>s. Pure code.
///   2. For each candidate, run the rule's <see cref="Rule.Predicate"/>
///      (a compiled RulesEngine bool LambdaExpression) — the *applicability
///      gate*. Sections that fail the predicate are skipped.
///   3. For each surviving candidate, run the rule's <see cref="Rule.Lambda"/>.
///      The bool result becomes Pass / Fail.
///   4. If the lambda returned Fail and the rule defined a remediation
///      template, render it via <see cref="RemediationRenderer"/>.
///   5. If no candidates passed the predicate, emit a single NotApplicable
///      verdict so the audit trail still cites the rule.
///
/// No LLM is involved at any step. Given the same RuleSet and the same
/// ProjectedDocument, results are byte-for-byte identical.
/// </summary>
public sealed class EvaluationService
{
    private readonly ISelectorMatcher _matcher;
    private readonly ILogger<EvaluationService> _logger;
    private readonly TimeProvider _time;
    private readonly ICandidateRuleFilter? _candidateFilter;

    public EvaluationService(
        ISelectorMatcher matcher,
        ILogger<EvaluationService> logger,
        TimeProvider? time = null,
        ICandidateRuleFilter? candidateFilter = null)
    {
        _matcher = matcher;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _candidateFilter = candidateFilter;
    }

    public async Task<ComplianceReport> EvaluateAsync(
        RuleSet ruleSet,
        ProjectedDocument document,
        CancellationToken ct = default)
    {
        var verdicts = new List<Verdict>();
        foreach (var rule in ruleSet.Rules.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var matches = _matcher.Match(rule.Selector, document);

            if (matches.Count == 0)
            {
                verdicts.Add(BuildVerdict(
                    rule, ruleSet,
                    outcome: NoMatchOutcome(rule),
                    section: null,
                    input: new JsonObject(),
                    span: rule.SourceSpan,
                    error: null,
                    remediationText: NoMatchRemediation(rule)));
                continue;
            }

            var emittedForRule = 0;
            foreach (var section in matches)
            {
                // Strict-superset pre-filter: if a candidate filter is wired up
                // and explicitly excludes this rule for this section, skip the
                // predicate compile. The compiled predicate is still the
                // decision-maker for any rule the filter admits — determinism
                // is preserved.
                if (_candidateFilter is { IsReady: true } filter
                    && !filter.LookupCandidates(section.Node).Contains(rule.Id))
                {
                    continue;
                }

                var (predicateApplies, predicateError) = await EvaluatePredicateAsync(rule, section).ConfigureAwait(false);
                if (predicateError is not null)
                {
                    verdicts.Add(BuildVerdict(
                        rule, ruleSet,
                        outcome: VerdictOutcome.Error,
                        section: section,
                        input: SnapshotInput(section),
                        span: section.Span,
                        error: $"predicate: {predicateError}",
                        remediationText: null));
                    emittedForRule++;
                    continue;
                }
                if (!predicateApplies)
                {
                    continue;
                }

                var verdict = await EvaluateRuleAsync(rule, ruleSet, section).ConfigureAwait(false);
                verdicts.Add(verdict);
                emittedForRule++;
            }

            if (emittedForRule == 0)
            {
                verdicts.Add(BuildVerdict(
                    rule, ruleSet,
                    outcome: NoMatchOutcome(rule),
                    section: null,
                    input: new JsonObject(),
                    span: rule.SourceSpan,
                    error: null,
                    remediationText: NoMatchRemediation(rule)));
            }
        }

        return BuildReport(ruleSet, document, verdicts);
    }

    /// <summary>
    /// Decide what to emit when no section matched the rule's selector or
    /// passed the predicate: a Mandatory rule produces a <c>Gap</c>
    /// (the document silently failed to address it); Conditional /
    /// Optional rules produce <c>NotApplicable</c> (their scope simply
    /// wasn't met).
    /// </summary>
    private static VerdictOutcome NoMatchOutcome(Rule rule) =>
        rule.Applicability == RuleApplicability.Mandatory
            ? VerdictOutcome.Gap
            : VerdictOutcome.NotApplicable;

    /// <summary>
    /// For a <c>Gap</c> verdict the suggested remediation is simply the
    /// rule's natural-language statement — the user knows what content
    /// they need to add. For <c>NotApplicable</c> we emit nothing.
    /// </summary>
    private static string? NoMatchRemediation(Rule rule) =>
        rule.Applicability == RuleApplicability.Mandatory
            ? $"Document does not address: {rule.NaturalLanguage}"
            : null;

    private async Task<(bool Applies, string? Error)> EvaluatePredicateAsync(Rule rule, MatchedSection section)
    {
        try
        {
            var input = JsonToExpando.Convert(section.Node);
            var workflow = WorkflowFactory.ForPredicate(rule);
            var engine = new RE.RulesEngine([workflow]);
            var results = await engine
                .ExecuteAllRulesAsync(WorkflowFactory.PredicateWorkflowName, input!)
                .ConfigureAwait(false);
            var result = results.Single();
            return (result.IsSuccess, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Predicate for rule {RuleId} ({RuleVersion}) errored at {Path}",
                rule.Id, rule.Version, section.Path);
            return (false, ex.Message);
        }
    }

    private async Task<Verdict> EvaluateRuleAsync(Rule rule, RuleSet ruleSet, MatchedSection section)
    {
        var inputJson = SnapshotInput(section);

        try
        {
            var input = JsonToExpando.Convert(section.Node);
            var workflow = WorkflowFactory.ForRule(rule);
            var engine = new RE.RulesEngine([workflow]);
            var results = await engine.ExecuteAllRulesAsync(WorkflowFactory.WorkflowName, input!).ConfigureAwait(false);
            var result = results.Single();
            var outcome = result.IsSuccess ? VerdictOutcome.Pass : VerdictOutcome.Fail;
            var remediationText = outcome == VerdictOutcome.Fail
                ? RemediationRenderer.Render(rule.Remediation, rule, section)
                : null;
            return BuildVerdict(
                rule, ruleSet, outcome,
                section: section,
                input: inputJson,
                span: section.Span,
                error: result.IsSuccess ? null : result.ExceptionMessage,
                remediationText: remediationText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rule {RuleId} ({RuleVersion}) errored evaluating against {Path}",
                rule.Id, rule.Version, section.Path);
            return BuildVerdict(
                rule, ruleSet, VerdictOutcome.Error,
                section: section,
                input: inputJson,
                span: section.Span,
                error: ex.Message,
                remediationText: null);
        }
    }

    private static JsonObject SnapshotInput(MatchedSection section) =>
        section.Node is JsonObject obj
            ? CanonicalJson.Clone(obj)
            : new JsonObject { ["value"] = section.Node.DeepClone() };

    private Verdict BuildVerdict(
        Rule rule,
        RuleSet ruleSet,
        VerdictOutcome outcome,
        MatchedSection? section,
        JsonObject input,
        SourceSpan span,
        string? error,
        string? remediationText)
    {
        // Stable verdict id derived from rule + ruleset + predicate + span.
        // Predicate hash is folded in so a predicate-only change creates a
        // different verdict id even if every other input is identical.
        var id = ContentHash.Compose(
            "verdict",
            rule.Id,
            rule.Version,
            ruleSet.Id,
            ruleSet.Version,
            rule.PredicateHash().Value,
            span.DocumentId,
            span.CharStart.ToString(System.Globalization.CultureInfo.InvariantCulture),
            span.CharLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            outcome.ToString()).Value;

        var matchedSectionId = section?.Node is JsonObject obj && obj["id"] is JsonNode idNode
            ? idNode.GetValue<string>()
            : null;

        var predicateText = string.Equals(rule.Predicate, "true", StringComparison.Ordinal)
            ? string.Empty
            : rule.Predicate;

        return new Verdict(
            Id: id,
            RuleId: rule.Id,
            RuleSetVersion: ruleSet.Version,
            Outcome: outcome,
            LambdaText: rule.Lambda,
            EvaluatedInput: input,
            SourceSpan: span,
            ErrorMessage: error,
            EvidenceQuotes: rule.EvidenceQuote is { Length: > 0 } ? [rule.EvidenceQuote] : [],
            EvaluatedAt: _time.GetUtcNow())
        {
            MatchedSectionId = matchedSectionId,
            RemediationText = remediationText,
            PredicateText = predicateText,
        };
    }

    private ComplianceReport BuildReport(RuleSet ruleSet, ProjectedDocument document, List<Verdict> verdicts)
    {
        var pass = verdicts.Count(v => v.Outcome == VerdictOutcome.Pass);
        var fail = verdicts.Count(v => v.Outcome == VerdictOutcome.Fail);
        var na = verdicts.Count(v => v.Outcome == VerdictOutcome.NotApplicable);
        var gap = verdicts.Count(v => v.Outcome == VerdictOutcome.Gap);
        var err = verdicts.Count(v => v.Outcome == VerdictOutcome.Error);
        // Gaps count against the score: a Mandatory rule the document
        // never addressed is just as much a finding as an explicit fail.
        var denominator = pass + fail + gap;
        var score = denominator == 0 ? 1.0 : (double)pass / denominator;

        return new ComplianceReport(
            DocumentId: document.SourceId,
            RuleSetId: ruleSet.Id,
            RuleSetVersion: ruleSet.Version,
            RuleSetFingerprint: ruleSet.Fingerprint(),
            ProjectorId: document.ProjectorId,
            ProjectorVersion: document.ProjectorVersion,
            Score: score,
            TotalRules: verdicts.Count,
            Passed: pass,
            Failed: fail,
            NotApplicable: na,
            Errored: err,
            Verdicts: verdicts
                .OrderBy(v => v.RuleId, StringComparer.Ordinal)
                .ThenBy(v => v.SourceSpan.CharStart)
                .ThenBy(v => v.Id, StringComparer.Ordinal)
                .ToList(),
            GeneratedAt: _time.GetUtcNow())
        {
            Gaps = gap,
        };
    }
}
