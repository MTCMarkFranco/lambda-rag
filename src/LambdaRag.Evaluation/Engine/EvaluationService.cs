using System.Text.Json.Nodes;
using LambdaRag.Core;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Evaluation.Workflow;
using Microsoft.Extensions.Logging;
using RE = RulesEngine;
using REM = RulesEngine.Models;

namespace LambdaRag.Evaluation.Engine;

/// <summary>
/// The deterministic core of lambda-rag.
///
/// Pipeline per rule:
///   1. Run the rule's selector against the projected document (pure code,
///      no LLM) → MatchedSections.
///   2. For each matched section, convert the sub-graph to an ExpandoObject
///      and execute a one-rule RulesEngine workflow.
///   3. Capture the verdict with full audit trail (lambda text, evaluated
///      input, source span, error if any).
///
/// No LLM is involved at any step here. Given the same RuleSet and the
/// same ProjectedDocument, results are byte-for-byte identical.
/// </summary>
public sealed class EvaluationService
{
    private readonly ISelectorMatcher _matcher;
    private readonly ILogger<EvaluationService> _logger;
    private readonly TimeProvider _time;

    public EvaluationService(
        ISelectorMatcher matcher,
        ILogger<EvaluationService> logger,
        TimeProvider? time = null)
    {
        _matcher = matcher;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<ComplianceReport> EvaluateAsync(
        RuleSet ruleSet,
        ProjectedDocument document,
        CancellationToken ct = default)
    {
        var verdicts = new List<Verdict>();
        // Stable iteration: rules sorted by Id so verdict order is deterministic.
        foreach (var rule in ruleSet.Rules.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var matches = _matcher.Match(rule.Selector, document);

            if (matches.Count == 0)
            {
                // Rule did not apply to this document — record as NotApplicable
                // so the audit trail still mentions the rule.
                verdicts.Add(BuildVerdict(
                    rule, ruleSet,
                    outcome: VerdictOutcome.NotApplicable,
                    input: new JsonObject(),
                    span: rule.SourceSpan,
                    error: null));
                continue;
            }

            foreach (var section in matches)
            {
                var verdict = await EvaluateRuleAsync(rule, ruleSet, section, ct).ConfigureAwait(false);
                verdicts.Add(verdict);
            }
        }

        return BuildReport(ruleSet, document, verdicts);
    }

    private async Task<Verdict> EvaluateRuleAsync(
        Rule rule,
        RuleSet ruleSet,
        MatchedSection section,
        CancellationToken ct)
    {
        // Snapshot the input we evaluate against — this is what an auditor sees.
        var inputJson = section.Node is JsonObject obj
            ? CanonicalJson.Clone(obj)
            : new JsonObject { ["value"] = section.Node.DeepClone() };

        try
        {
            var input = JsonToExpando.Convert(section.Node);
            var workflow = WorkflowFactory.ForRule(rule);
            var engine = new RE.RulesEngine([workflow]);
            var results = await engine.ExecuteAllRulesAsync(WorkflowFactory.WorkflowName, input!).ConfigureAwait(false);

            // Single-rule workflow → single result.
            var result = results.Single();
            var outcome = result.IsSuccess ? VerdictOutcome.Pass : VerdictOutcome.Fail;
            return BuildVerdict(rule, ruleSet, outcome, inputJson, section.Span, error: result.IsSuccess ? null : result.ExceptionMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rule {RuleId} ({RuleVersion}) errored evaluating against {Path}",
                rule.Id, rule.Version, section.Path);
            return BuildVerdict(rule, ruleSet, VerdictOutcome.Error, inputJson, section.Span, ex.Message);
        }
    }

    private Verdict BuildVerdict(
        Rule rule,
        RuleSet ruleSet,
        VerdictOutcome outcome,
        JsonObject input,
        SourceSpan span,
        string? error)
    {
        // Stable verdict id derived from rule + ruleset + span — gives idempotent
        // ids across runs, which is critical for the markup engine's stable
        // change-id story.
        var id = ContentHash.Compose(
            "verdict",
            rule.Id,
            rule.Version,
            ruleSet.Id,
            ruleSet.Version,
            span.DocumentId,
            span.CharStart.ToString(System.Globalization.CultureInfo.InvariantCulture),
            span.CharLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            outcome.ToString()).Value;

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
            EvaluatedAt: _time.GetUtcNow());
    }

    private ComplianceReport BuildReport(RuleSet ruleSet, ProjectedDocument document, List<Verdict> verdicts)
    {
        // Score: pass / (pass + fail), excluding NotApplicable and Error
        // (errors are surfaced separately; NotApplicable doesn't move the score).
        var pass = verdicts.Count(v => v.Outcome == VerdictOutcome.Pass);
        var fail = verdicts.Count(v => v.Outcome == VerdictOutcome.Fail);
        var na = verdicts.Count(v => v.Outcome == VerdictOutcome.NotApplicable);
        var err = verdicts.Count(v => v.Outcome == VerdictOutcome.Error);
        var denominator = pass + fail;
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
            GeneratedAt: _time.GetUtcNow());
    }
}
