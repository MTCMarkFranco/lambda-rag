using System.Text.Json.Nodes;
using LambdaRag.Core;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Semantic;
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
    private readonly ISemanticVectorStore _vectorStore;

    public EvaluationService(
        ISelectorMatcher matcher,
        ILogger<EvaluationService> logger,
        TimeProvider? time = null,
        ICandidateRuleFilter? candidateFilter = null,
        ISemanticVectorStore? vectorStore = null)
    {
        _matcher = matcher;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _candidateFilter = candidateFilter;
        _vectorStore = vectorStore ?? new NotConfiguredSemanticVectorStore();
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

                // Semantic applicability gate. When the rule has a positive
                // GateThreshold and both vectors exist in the active store,
                // a section whose cosine to the rule description is below
                // the threshold is treated as "this rule does not apply
                // here" — we skip the predicate entirely. This is the
                // semantic equivalent of the predicate's compiled gate, but
                // resilient to paraphrase. Missing vectors fall through
                // (gate disabled) so determinism is preserved when the
                // store has not been populated for this rule/section pair.
                if (rule.GateThreshold > 0 &&
                    !PassesApplicabilityGate(rule, section))
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
            var engine = new RE.RulesEngine([workflow], WorkflowFactory.CreateReSettings());
            using var _ = VectorStoreAccessor.Push(_vectorStore);
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
            var engine = new RE.RulesEngine([workflow], WorkflowFactory.CreateReSettings());
            using var _ = VectorStoreAccessor.Push(_vectorStore);
            var results = await engine.ExecuteAllRulesAsync(WorkflowFactory.WorkflowName, input!).ConfigureAwait(false);
            var result = results.Single();

            // Distinguish a "rule legitimately returned false" from a
            // "lambda failed to compile / threw at runtime". RulesEngine
            // packs both into IsSuccess=false; the parse/runtime path
            // surfaces a stack-trace-shaped ExceptionMessage. We treat the
            // latter as Error so silent failures don't masquerade as Fail.
            if (!result.IsSuccess && IsRuntimeException(result.ExceptionMessage))
            {
                _logger.LogWarning(
                    "Rule {RuleId} ({RuleVersion}) lambda raised a runtime/parse exception at {Path}: {Message}",
                    rule.Id, rule.Version, section.Path, result.ExceptionMessage);
                return BuildVerdict(
                    rule, ruleSet, VerdictOutcome.Error,
                    section: section,
                    input: inputJson,
                    span: section.Span,
                    error: result.ExceptionMessage,
                    remediationText: null);
            }

            var outcome = result.IsSuccess ? VerdictOutcome.Pass : VerdictOutcome.Fail;
            var span = outcome == VerdictOutcome.Fail
                ? RefineAnchor(rule, section)
                : section.Span;
            var remediationText = outcome == VerdictOutcome.Fail
                ? RemediationRenderer.Render(rule.Remediation, rule, section)
                : null;
            return BuildVerdict(
                rule, ruleSet, outcome,
                section: section,
                input: inputJson,
                span: span,
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

    private bool PassesApplicabilityGate(Rule rule, MatchedSection section)
    {
        if (_vectorStore is NotConfiguredSemanticVectorStore) return true;
        if (section.Node is not JsonObject obj) return true;
        if (obj["id"] is not JsonValue idVal || !idVal.TryGetValue<string>(out var sectionId))
            return true;

        IReadOnlyList<float>? sectionVec = null;
        IReadOnlyList<float>? ruleVec = null;
        try
        {
            if (!_vectorStore.TryGetSection(sectionId, out sectionVec!)) return true;
            var ruleKey = $"rule:{rule.Id}";
            if (!_vectorStore.TryGetConcept(ruleKey, out ruleVec!)) return true;
        }
        catch (InvalidOperationException)
        {
            // NotConfigured / store-shape mismatch — fall back to "gate off"
            // rather than producing an Error verdict for every section.
            return true;
        }

        var cosine = SemanticFunctions.Cosine(ruleVec!, sectionVec!);
        return cosine >= rule.GateThreshold;
    }

    private static bool IsRuntimeException(string? exceptionMessage)
    {
        if (string.IsNullOrEmpty(exceptionMessage)) return false;
        // RulesEngine wraps parse / type-binding errors with a recognizable
        // prefix. Anything matching this shape is engine-side, not a
        // legitimate "rule said false". The semantic-functions marker is
        // also surfaced here so missing-vector lookups fail loud rather
        // than masquerading as Fail.
        return exceptionMessage.StartsWith("Exception while parsing expression", StringComparison.Ordinal)
            || exceptionMessage.Contains("RuleException", StringComparison.Ordinal)
            || exceptionMessage.Contains(LambdaRag.Core.Semantic.SemanticFunctions.ErrorMarker, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refine the markup anchor for a Fail verdict so reviewer comments land
    /// on the substring that triggered the verdict, not the section heading.
    /// Strategy:
    ///   1. If the rule declares an explicit <see cref="Rule.Anchor"/> regex,
    ///      use the first match inside the section's body text.
    ///   2. Otherwise, scan the lambda for <c>Contains("…")</c> literals and
    ///      anchor on the first one found in the body text. (Helps "must
    ///      contain X" rules anchor to the X they did find but rejected, and
    ///      also "must not contain Y" rules anchor to the offending Y.)
    ///   3. Fallback to the section's existing span (heading) when nothing
    ///      matches — better than dropping the comment entirely.
    /// All lookups read the section's <c>text_char_start</c> projection
    /// field so spans line up with the canonical document offsets.
    /// </summary>
    private static SourceSpan RefineAnchor(Rule rule, MatchedSection section)
    {
        if (section.Node is not JsonObject obj) return section.Span;
        var text = obj["text"]?.GetValue<string>() ?? string.Empty;
        if (text.Length == 0) return section.Span;
        long bodyStart = section.Span.CharStart;
        var bodyStartNode = obj["text_char_start"];
        if (bodyStartNode is JsonValue bv)
        {
            if (bv.TryGetValue<long>(out var lv)) bodyStart = lv;
            else if (bv.TryGetValue<int>(out var iv)) bodyStart = iv;
        }

        (int Index, int Length)? hit = null;
        if (!string.IsNullOrEmpty(rule.Anchor))
        {
            try
            {
                var rx = new System.Text.RegularExpressions.Regex(
                    rule.Anchor,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.Singleline,
                    TimeSpan.FromMilliseconds(200));
                var m = rx.Match(text);
                if (m.Success && m.Length > 0) hit = (m.Index, m.Length);
            }
            catch
            {
                // Treat invalid anchor regex as "no anchor" — fall through.
            }
        }

        if (hit is null)
        {
            foreach (var literal in ExtractContainsLiterals(rule.Lambda))
            {
                var idx = text.IndexOf(literal, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    hit = (idx, literal.Length);
                    break;
                }
            }
        }

        if (hit is null) return section.Span;

        return new SourceSpan(
            section.Span.DocumentId,
            CharStart: (int)(bodyStart + hit.Value.Index),
            CharLength: hit.Value.Length,
            PageNumber: section.Span.PageNumber,
            HeadingPath: section.Span.HeadingPath);
    }

    private static readonly System.Text.RegularExpressions.Regex ContainsLiteralRx = new(
        "Contains\\(\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*\\)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static IEnumerable<string> ExtractContainsLiterals(string lambda)
    {
        if (string.IsNullOrEmpty(lambda)) yield break;
        foreach (System.Text.RegularExpressions.Match m in ContainsLiteralRx.Matches(lambda))
        {
            var raw = m.Groups[1].Value;
            if (raw.Length > 0)
                yield return System.Text.RegularExpressions.Regex.Unescape(raw);
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
