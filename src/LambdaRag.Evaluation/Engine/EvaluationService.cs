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
    private readonly SemanticBindingResolver? _bindingResolver;

    private readonly bool _enforceSoftCohesion;
    private readonly int _minEvidencedAnchors;

    public EvaluationService(
        ISelectorMatcher matcher,
        ILogger<EvaluationService> logger,
        TimeProvider? time = null,
        ICandidateRuleFilter? candidateFilter = null,
        ISemanticVectorStore? vectorStore = null,
        ITokenEmbedder? tokenEmbedder = null,
        double semanticThresholdOffset = 0.0,
        double minEffectiveSemanticThreshold = 0.0,
        bool enforceSoftCohesion = false,
        int minEvidencedAnchors = 2)
    {
        _matcher = matcher;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _candidateFilter = candidateFilter;
        _vectorStore = vectorStore ?? new NotConfiguredSemanticVectorStore();
        _bindingResolver = tokenEmbedder is null
            ? null
            : new SemanticBindingResolver(
                tokenEmbedder,
                thresholdOffset: semanticThresholdOffset,
                minEffectiveThreshold: minEffectiveSemanticThreshold);
        _enforceSoftCohesion = enforceSoftCohesion;
        if (minEvidencedAnchors < 1)
            throw new ArgumentOutOfRangeException(nameof(minEvidencedAnchors),
                "minEvidencedAnchors must be at least 1.");
        _minEvidencedAnchors = minEvidencedAnchors;
    }

    public async Task<ComplianceReport> EvaluateAsync(
        RuleSet ruleSet,
        ProjectedDocument document,
        CancellationToken ct = default)
        => await EvaluateAsync(ruleSet, document, docKind: null, ct).ConfigureAwait(false);

    /// <summary>
    /// Pillar 1 (#116) overload — when <paramref name="docKind"/> is non-null
    /// every rule whose <see cref="Rule.AppliesToDocKinds"/> (or the
    /// ruleset-level list) is non-empty and does not contain the resolved
    /// kind is skipped *before* selector match, emitting a single
    /// <see cref="VerdictOutcome.Skipped"/> verdict with
    /// <c>ErrorMessage = "doc_kind_mismatch:&lt;kind&gt;"</c> so the audit
    /// trail still cites the rule. Rules with no doc-kind declaration are
    /// evaluated normally (backward compatible).
    /// </summary>
    public async Task<ComplianceReport> EvaluateAsync(
        RuleSet ruleSet,
        ProjectedDocument document,
        string? docKind,
        CancellationToken ct = default)
    {
        // Pillar 3 (#118) — fail loud on embedder drift. When the ruleset
        // pinned an embedder id but the active vector store reports a
        // different model, the precomputed sourceEmbedding vectors on
        // rules can't be trusted and we must not silently produce a
        // verdict against the wrong vectors.
        if (!string.IsNullOrWhiteSpace(ruleSet.EmbedderId)
            && _vectorStore is not NotConfiguredSemanticVectorStore
            && !string.Equals(ruleSet.EmbedderId, _vectorStore.ModelId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Ruleset '{ruleSet.Id}' was authored against embedder '{ruleSet.EmbedderId}' " +
                $"but the runtime vector store reports model '{_vectorStore.ModelId}'. " +
                "Re-embed the ruleset or wire the matching embedder.");
        }

        // Pillar 3 (#118) — expose the ruleset's phrasebooks to
        // LambdaPrimitives.PhraseMatch for the duration of this evaluation.
        // AsyncLocal-scoped so concurrent evaluations are isolated.
        using var _phrasebookScope = PhrasebookAccessor.Push(
            new DictionaryPhrasebookStore(ruleSet.Phrasebooks));

        var verdicts = new List<Verdict>();
        foreach (var rule in ruleSet.Rules.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            // Pillar 1 doc-kind gate. Only fires when both a doc kind is
            // known and the rule (or its ruleset) declared an explicit
            // applies-to list. Otherwise behaviour is byte-identical to the
            // pre-Pillar-1 path so all existing golden masters stay green.
            if (!string.IsNullOrWhiteSpace(docKind)
                && !DocKindResolver.Applies(rule.AppliesToDocKinds, ruleSet.AppliesToDocKinds, docKind))
            {
                verdicts.Add(BuildVerdict(
                    rule, ruleSet,
                    outcome: VerdictOutcome.Skipped,
                    section: null,
                    input: new JsonObject(),
                    span: rule.SourceSpan,
                    error: $"doc_kind_mismatch:{docKind}",
                    remediationText: null));
                continue;
            }

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

                // Pillar 6 — resolve semantic bindings BEFORE the predicate
                // gate so both the predicate AND the lambda see the same
                // pre-resolved bindings. (Pillar 9 port from
                // policy-compiler-spike v0.1.1: the predicate gate is the
                // place semantic signal is needed most — to admit chunks
                // the metadata projector under-tagged.) Skipped entirely
                // for rules without anchors so legacy lambdas run exactly
                // as before (byte-identity guarantee).
                IReadOnlyDictionary<string, IReadOnlyList<TokenMatch>>? bindingMap = null;
                IReadOnlyList<BindingRecord>? bindingRecords = null;
                if (_bindingResolver is not null
                    && rule.SemanticAnchors is { Count: > 0 } anchors)
                {
                    var sectionText = section.Node is JsonObject so
                        && so["text"] is JsonValue tv
                        && tv.TryGetValue<string>(out var t)
                        ? t
                        : string.Empty;
                    if (!string.IsNullOrEmpty(sectionText))
                    {
                        var (b, r) = await _bindingResolver
                            .ResolveAsync(anchors, sectionText, ct)
                            .ConfigureAwait(false);
                        bindingMap = b;
                        bindingRecords = r;
                    }
                }

                var (predicateApplies, predicateError) = await EvaluatePredicateAsync(rule, section, bindingMap).ConfigureAwait(false);
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

                var verdict = await EvaluateRuleAsync(rule, ruleSet, section, bindingMap, bindingRecords)
                    .ConfigureAwait(false);
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

    private async Task<(bool Applies, string? Error)> EvaluatePredicateAsync(
        Rule rule,
        MatchedSection section,
        IReadOnlyDictionary<string, IReadOnlyList<TokenMatch>>? bindingMap = null)
    {
        try
        {
            var input = JsonToExpando.Convert(section.Node);
            var workflow = WorkflowFactory.ForPredicate(rule);
            var engine = new RE.RulesEngine([workflow], WorkflowFactory.CreateReSettings());
            using var _ = VectorStoreAccessor.Push(_vectorStore);
            // Pillar 9 — make semantic bindings visible to the predicate
            // (not just the lambda) so a predicate may use SemanticBindings
            // as part of its applicability check. Null bindingMap ⇒ no
            // scope pushed ⇒ legacy behaviour preserved.
            using var _bindingScope = bindingMap is null
                ? (IDisposable)NullScope.Instance
                : SemanticBindingAccessor.Push(new DictionarySemanticBindingScope(bindingMap));
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

    private async Task<Verdict> EvaluateRuleAsync(
        Rule rule,
        RuleSet ruleSet,
        MatchedSection section,
        IReadOnlyDictionary<string, IReadOnlyList<TokenMatch>>? bindingMap = null,
        IReadOnlyList<BindingRecord>? bindingRecords = null)
    {
        var inputJson = SnapshotInput(section);

        try
        {
            var input = JsonToExpando.Convert(section.Node);
            var workflow = WorkflowFactory.ForRule(rule);
            var engine = new RE.RulesEngine([workflow], WorkflowFactory.CreateReSettings());
            using var _ = VectorStoreAccessor.Push(_vectorStore);
            // Pillar 6 — bindings are visible inside the lambda for the
            // duration of this call. The scope is AsyncLocal so concurrent
            // evaluations of different rules don't see each other's bindings.
            using var _bindingScope = bindingMap is null
                ? (IDisposable)NullScope.Instance
                : SemanticBindingAccessor.Push(new DictionarySemanticBindingScope(bindingMap));
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

            // Pillar 9 — soft cohesion post-filter ported from
            // policy-compiler-spike v0.1.1. For rules with ≥2 anchors,
            // demote a Pass verdict to NotApplicable when fewer than
            // _minEvidencedAnchors of the rule's anchors actually produced
            // bindings on this section. Rationale: a Pass is the claim
            // "the document addresses this requirement"; a Pass driven by
            // a single anchor on a multi-anchor rule is too thin a signal
            // to assert compliance (it's the classic ARB-PSA FP pattern).
            // Fail outcomes are left untouched so genuine gaps still
            // surface. Default-off → byte-identity preserved.
            if (_enforceSoftCohesion
                && outcome == VerdictOutcome.Pass
                && rule.SemanticAnchors is { Count: var anchorCount } anchorsForCohesion
                && anchorCount >= 2
                && bindingMap is not null)
            {
                var evidencedAnchors = 0;
                foreach (var anchor in anchorsForCohesion)
                {
                    if (bindingMap.TryGetValue(anchor.Name, out var matches)
                        && matches.Count > 0)
                    {
                        evidencedAnchors++;
                    }
                }
                if (evidencedAnchors < _minEvidencedAnchors)
                {
                    _logger.LogDebug(
                        "Soft cohesion demoted Pass→NotApplicable for rule {RuleId} at {Path}: "
                        + "{Evidenced} of {Total} anchors evidenced (< {Min}).",
                        rule.Id, section.Path, evidencedAnchors, anchorCount, _minEvidencedAnchors);
                    outcome = VerdictOutcome.NotApplicable;
                }
            }

            SourceSpan span;
            SourceSpan? clauseSpan = null;
            if (outcome == VerdictOutcome.Fail)
            {
                var (narrow, clause) = RefineSpans(rule, section);
                span = narrow;
                clauseSpan = clause;
            }
            else
            {
                span = section.Span;
            }
            var remediationText = outcome == VerdictOutcome.Fail
                ? RemediationRenderer.Render(rule.Remediation, rule, section)
                : null;
            return BuildVerdict(
                rule, ruleSet, outcome,
                section: section,
                input: inputJson,
                span: span,
                error: result.IsSuccess ? null : result.ExceptionMessage,
                remediationText: remediationText,
                clauseSpan: clauseSpan,
                bindings: bindingRecords);
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
            || exceptionMessage.Contains(LambdaRag.Core.Semantic.SemanticFunctions.ErrorMarker, StringComparison.Ordinal)
            || exceptionMessage.Contains(LambdaRag.Core.Semantic.LambdaPrimitives.ErrorMarker, StringComparison.Ordinal);
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
        var (narrow, _) = RefineSpans(rule, section);
        return narrow;
    }

    /// <summary>
    /// Compute the pair of spans the markup pipeline needs for issue #87:
    ///   • <c>Narrow</c> — substring-precise evidence anchor (used for the
    ///     reviewer comment marker, unchanged from pre-#87 behaviour).
    ///   • <c>Clause</c> — paragraph-aligned widening of the same hit,
    ///     used for tracked-change deletions / replacements so a clause
    ///     that crosses paragraph boundaries is fully struck through.
    /// Reads the section's <c>paragraphs[]</c> array (emitted by the
    /// contract projector v1.5.0). Returns <c>Clause=null</c> when the
    /// section has no paragraph metadata, so older cached projections
    /// degrade gracefully to the pre-#87 single-paragraph behaviour.
    /// </summary>
    private static (SourceSpan Narrow, SourceSpan? Clause) RefineSpans(Rule rule, MatchedSection section)
    {
        if (section.Node is not JsonObject obj) return (section.Span, null);
        var text = obj["text"]?.GetValue<string>() ?? string.Empty;
        if (text.Length == 0) return (section.Span, null);
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

        if (hit is null) return (section.Span, WidenToParagraph(section.Span, obj, bodyStart, 0, text.Length));

        var narrow = new SourceSpan(
            section.Span.DocumentId,
            CharStart: (int)(bodyStart + hit.Value.Index),
            CharLength: hit.Value.Length,
            PageNumber: section.Span.PageNumber,
            HeadingPath: section.Span.HeadingPath);

        var clause = WidenToParagraph(
            section.Span, obj, bodyStart,
            hit.Value.Index, hit.Value.Index + hit.Value.Length);
        return (narrow, clause);
    }

    /// <summary>
    /// Widen a hit (offsets relative to <paramref name="bodyText"/>) to
    /// the paragraph(s) that fully contain it, using the section's
    /// <c>paragraphs[]</c> projection metadata. Returns null when the
    /// section has no paragraph metadata (older cached projection).
    /// </summary>
    private static SourceSpan? WidenToParagraph(
        SourceSpan baseSpan,
        JsonObject sectionObj,
        long bodyStart,
        int hitStartInBody,
        int hitEndInBody)
    {
        if (sectionObj["paragraphs"] is not JsonArray paragraphs || paragraphs.Count == 0)
            return null;

        int firstStart = -1;
        int lastEnd = -1;
        foreach (var node in paragraphs)
        {
            if (node is not JsonObject p) continue;
            var pStart = p["char_start"]?.GetValue<int>() ?? 0;
            var pLen = p["char_length"]?.GetValue<int>() ?? 0;
            var pEnd = pStart + pLen;
            // Paragraph overlaps the hit if it contains either endpoint
            // or the hit spans across it. Empty paragraphs at the hit's
            // boundary do not extend the clause.
            if (pLen == 0) continue;
            if (pEnd <= hitStartInBody) continue;
            if (pStart >= hitEndInBody) break;
            if (firstStart < 0) firstStart = pStart;
            lastEnd = pEnd;
        }

        if (firstStart < 0 || lastEnd <= firstStart)
            return null;

        return new SourceSpan(
            baseSpan.DocumentId,
            CharStart: (int)(bodyStart + firstStart),
            CharLength: lastEnd - firstStart,
            PageNumber: baseSpan.PageNumber,
            HeadingPath: baseSpan.HeadingPath);
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
        string? remediationText,
        SourceSpan? clauseSpan = null,
        IReadOnlyList<BindingRecord>? bindings = null)
    {
        // Stable verdict id derived from rule + ruleset + predicate + span.
        // Predicate hash is folded in so a predicate-only change creates a
        // different verdict id even if every other input is identical.
        // ClauseSpan participates only when set (mirrors the GateThreshold
        // pattern in Rule.Fingerprint) so existing byte-identity replay
        // fixtures stay green when the projection has no paragraph metadata.
        var idParts = new List<string>
        {
            "verdict",
            rule.Id,
            rule.Version,
            ruleSet.Id,
            ruleSet.Version,
            rule.PredicateHash().Value,
            span.DocumentId,
            span.CharStart.ToString(System.Globalization.CultureInfo.InvariantCulture),
            span.CharLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            outcome.ToString(),
        };
        if (clauseSpan is not null)
        {
            idParts.Add("clause:" + clauseSpan.CharStart.ToString(System.Globalization.CultureInfo.InvariantCulture));
            idParts.Add("clauseLen:" + clauseSpan.CharLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var id = ContentHash.Compose(idParts.ToArray()).Value;

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
            ClauseSpan = clauseSpan,
            // Pillar 6 — only attach when non-empty so legacy verdict JSON
            // remains byte-identical for rules that don't declare anchors.
            SemanticBindings = bindings is { Count: > 0 } ? bindings : null,
        };
    }

    /// <summary>No-op IDisposable so the binding-scope using-statement is uniform.</summary>
    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }

    private ComplianceReport BuildReport(RuleSet ruleSet, ProjectedDocument document, List<Verdict> verdicts)
    {
        var pass = verdicts.Count(v => v.Outcome == VerdictOutcome.Pass);
        var fail = verdicts.Count(v => v.Outcome == VerdictOutcome.Fail);
        var na = verdicts.Count(v => v.Outcome == VerdictOutcome.NotApplicable);
        var gap = verdicts.Count(v => v.Outcome == VerdictOutcome.Gap);
        var err = verdicts.Count(v => v.Outcome == VerdictOutcome.Error);
        var skipped = verdicts.Count(v => v.Outcome == VerdictOutcome.Skipped);
        // Gaps count against the score: a Mandatory rule the document
        // never addressed is just as much a finding as an explicit fail.
        // Skipped verdicts (Pillar 1 doc-kind mismatch) never count — the
        // rule simply did not apply to this artifact kind.
        var denominator = pass + fail + gap;
        var score = denominator == 0 ? 1.0 : (double)pass / denominator;

        // Pillar 1 (#116) — when every rule that ran got skipped via the
        // doc-kind gate, the operator picked the wrong ruleset profile for
        // this artifact. Surface that fact at the report level so the
        // audit trail is unambiguous instead of looking like a perfect
        // pass on zero adjudicated rules.
        var wrongProfile = verdicts.Count > 0
            && skipped == verdicts.Count;

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
            // Pillar 1 (#116) — leave Skipped / WrongProfile unset (null)
            // when the doc-kind gate did not fire, so legacy reports stay
            // byte-identical. The fields appear only when actually relevant.
            Skipped = skipped > 0 ? skipped : null,
            WrongProfile = wrongProfile ? true : null,
        };
    }
}
