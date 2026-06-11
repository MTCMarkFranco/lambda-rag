using System.Text.Json.Nodes;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Selectors;

namespace LambdaRag.Core.Domain;

public enum RuleSeverity { Suggestion, Deviation, Violation, Critical }

/// <summary>
/// Whether a rule MUST apply to every document in the domain.
///
/// • <see cref="Mandatory"/>: every document is expected to address this
///   rule. If no section in the projected document matches the rule's
///   selector / predicate, the evaluator emits a <c>Gap</c> verdict — the
///   document is silently missing required content. This is the default
///   for a compliance review where "the doc didn't address X" is itself
///   a finding.
/// • <see cref="Conditional"/>: the rule applies only when its scope
///   condition is met (typically captured in the predicate). If the
///   predicate matches no section, that's by design, not a gap. Emits
///   <c>NotApplicable</c>.
/// • <see cref="Optional"/>: the rule is a recommendation, not a
///   requirement. A document that doesn't address it is fine. Emits
///   <c>NotApplicable</c>.
///
/// Inferred deterministically at authoring time from the policy text:
/// "must / shall / required / mandatory" → Mandatory; "should /
/// recommended / preferred" → Optional; "if / when / where / unless" →
/// Conditional. Default is Mandatory.
/// </summary>
public enum RuleApplicability { Mandatory, Conditional, Optional }

/// <summary>
/// A rule extracted from a source policy. The combination of
/// (selector, predicate, lambda, applies_to_schema) is what makes rule
/// application 100% deterministic at runtime.
///
/// • <see cref="Selector"/> projects the rule onto candidate slices of a
///   ProjectedDocument (pure code, no LLM).
/// • <see cref="Predicate"/> is a Microsoft RulesEngine bool LambdaExpression
///   evaluated against each candidate. It is the *applicability gate* —
///   no semantic / vector matching happens at runtime; if the compiled
///   bool says "applies", the rule applies. The default of "true" means
///   "applies to every candidate the selector returned".
/// • <see cref="AppliesToSchema"/> describes the input shape both the
///   predicate and the lambda expect.
/// • <see cref="Lambda"/> is a Microsoft RulesEngine bool LambdaExpression
///   that returns the pass/fail determination. No NL interpretation runs.
/// • <see cref="Remediation"/> is an optional string template evaluated
///   only when <see cref="Lambda"/> returns false; it produces a suggested
///   rewrite of the matched section in the language of the document.
/// • <see cref="SourceContent"/> + <see cref="SourceEmbedding"/> capture
///   the original chunk this rule was extracted from. They are evidence
///   used only by the coverage / audit tooling — never by the runtime
///   evaluation path.
/// </summary>
public sealed record Rule(
    string Id,
    string Version,
    string NaturalLanguage,
    string Lambda,
    JsonObject AppliesToSchema,
    Selector Selector,
    RuleSeverity Severity,
    SourceSpan SourceSpan,
    string EvidenceQuote,
    IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>
    /// RulesEngine bool LambdaExpression — the *applicability gate*.
    /// Default <c>"true"</c> = "always applicable to every selector match".
    /// Override with e.g. <c>input1.category == "payment_terms"</c>.
    /// </summary>
    public string Predicate { get; init; } = "true";

    /// <summary>
    /// Whether the document MUST address this rule. Drives the Gap-vs-
    /// NotApplicable decision when no section matches. Defaults to
    /// <see cref="RuleApplicability.Mandatory"/> so compliance reviews
    /// surface silent gaps by default. See <see cref="RuleApplicability"/>.
    /// </summary>
    public RuleApplicability Applicability { get; init; } = RuleApplicability.Mandatory;

    /// <summary>
    /// Optional string template rendered when the lambda returns false.
    /// Supported placeholders are described in
    /// <c>LambdaRag.Evaluation.Engine.RemediationRenderer</c>.
    /// </summary>
    public string? Remediation { get; init; }

    /// <summary>
    /// Optional regex used to refine the markup anchor when the rule
    /// produces a Fail verdict. The first match inside the matched
    /// section's body text becomes the anchor span (substring-precise
    /// instead of section-wide). When unset, the engine falls back to
    /// extracting the first <c>Contains("…")</c> literal from the lambda
    /// (positive-keyword rules) or to the section's first sentence
    /// (absence-of-keyword rules). Case-insensitive.
    /// </summary>
    public string? Anchor { get; init; }

    /// <summary>
    /// The original source chunk this rule was extracted from. Stored for
    /// audit and used by the coverage tool — never by the runtime evaluator.
    /// </summary>
    public string? SourceContent { get; init; }

    /// <summary>
    /// Optional dense embedding of <see cref="SourceContent"/>. Used only
    /// for coverage/audit reporting (cosine similarity to candidate sections).
    /// The runtime evaluation path never reads this.
    /// </summary>
    public IReadOnlyList<float>? SourceEmbedding { get; init; }

    /// <summary>
     /// Optional cosine-similarity threshold used as an *applicability gate*
     /// when both the rule's natural-language description and the candidate
     /// section have precomputed vectors in the active
     /// <see cref="ISemanticVectorStore"/>. The evaluator skips a section
     /// before running its predicate when
     /// <c>cosine(rule.descriptionVector, section.vector) &lt; GateThreshold</c>.
     /// Default <c>0.0</c> = gate is off (every selector match is evaluated,
     /// preserving the pre-semantic behaviour). Typical "on" values for
     /// <c>text-embedding-3-large</c> live in the 0.55–0.70 band.
     /// </summary>
     public double GateThreshold { get; init; }

    /// <summary>
    /// Optional self-validation example corpus used by the Phase B authoring
    /// gate (#73). Three positives the rule MUST match and three negatives
    /// the rule MUST NOT match. Embedded once at authoring time so the
    /// authoring driver can score them, reject misbehaving rules, and bake
    /// the calibrated <see cref="GateThreshold"/> into the published artifact.
    ///
    /// Null on rules authored before Phase B — backward compatible and not
    /// folded into <see cref="Fingerprint"/> when absent.
    /// </summary>
    public RuleExamples? Examples { get; init; }

    /// <summary>
    /// Pillar 6 (#124) — semantic-binding anchors. Each anchor declares a
    /// named natural-language phrase plus an embedding the runtime cosine-
    /// compares against every token embedding of the candidate section.
    /// Tokens whose cosine meets or exceeds the anchor's threshold become
    /// <i>bindings</i> the rule's lambda accesses via
    /// <c>LambdaPrimitives.SemanticBindings(input1, "name")</c>.
    ///
    /// Optional and nullable so pre-Pillar-6 rules are unaffected: when the
    /// list is null or empty no binding pass runs and the rule behaves
    /// exactly as before (folded into <see cref="Fingerprint"/> only when
    /// non-empty so byte-identity replay holds for legacy verdicts).
    /// </summary>
    public IReadOnlyList<SemanticAnchor>? SemanticAnchors { get; init; }

    /// <summary>
    /// Optional list of doc-kind identifiers (e.g. <c>"arb-psa"</c>,
    /// <c>"contract"</c>) this rule applies to. <c>null</c> or empty means
    /// "applies to every doc kind" — backward-compatible default for all
    /// pre-Pillar-1 rulesets. The evaluator's doc-kind gate skips a rule
    /// whose list is non-empty and does not contain the resolved doc kind,
    /// emitting <see cref="LambdaRag.Core.Domain.VerdictOutcome.Skipped"/>
    /// so the rule still appears in the audit trail.
    /// </summary>
    public IReadOnlyList<string>? AppliesToDocKinds { get; init; }

    /// <summary>SHA-256 of the predicate expression. Changes if the gate changes.</summary>
     public ContentHash PredicateHash() => ContentHash.OfString(Predicate);

    /// <summary>SHA-256 of the lambda expression. Changes if the determination changes.</summary>
    public ContentHash LambdaHash() => ContentHash.OfString(Lambda);

    /// <summary>SHA-256 of the remediation template, or empty hash if not set.</summary>
    public ContentHash RemediationHash() => ContentHash.OfString(Remediation ?? string.Empty);

    /// <summary>
    /// Composite fingerprint over every behaviour-affecting field. Two rules
    /// with the same Id but different predicates have different fingerprints,
    /// so a predicate-only change forces a new rule version downstream.
    /// </summary>
    public ContentHash Fingerprint()
    {
        var parts = new List<string>
        {
            Id,
            Version,
            Predicate,
            Lambda,
            Remediation ?? string.Empty,
            Anchor ?? string.Empty,
            AppliesToSchema.ToJsonString(),
            Severity.ToString(),
            Applicability.ToString(),
        };
        // Only fold the gate threshold into the fingerprint when it is
        // actively in use — keeps existing rulesets binary-compatible with
        // pre-semantic verdict ids while still ensuring a non-zero gate is
        // a meaningful change.
        if (GateThreshold > 0)
        {
            parts.Add("gate:" + GateThreshold.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        // Examples participate in the fingerprint only when present so
        // pre-Phase-B rulesets keep their existing identity.
        if (Examples is not null)
        {
            parts.Add("examples.positive:" + string.Join("\u001f", Examples.Positive));
            parts.Add("examples.negative:" + string.Join("\u001f", Examples.Negative));
        }
        // AppliesToDocKinds only folds in when non-empty so pre-Pillar-1
        // rulesets keep byte-identical fingerprints and verdict ids.
        if (AppliesToDocKinds is { Count: > 0 })
        {
            var kinds = AppliesToDocKinds
                .Select(k => k.Trim())
                .Where(k => k.Length > 0)
                .OrderBy(k => k, StringComparer.Ordinal);
            parts.Add("appliesToDocKinds:" + string.Join("\u001f", kinds));
        }
        // Pillar 6 — semantic anchors fold in only when non-empty so
        // pre-Pillar-6 rules keep their existing fingerprints. Anchor
        // embeddings are fingerprinted by name + threshold + text + ngram,
        // not the vector bytes (the embedder id pinned at the ruleset
        // level already gates against drift).
        if (SemanticAnchors is { Count: > 0 })
        {
            foreach (var a in SemanticAnchors.OrderBy(a => a.Name, StringComparer.Ordinal))
            {
                var ng = a.Ngram is null
                    ? "1-2"
                    : string.Join(",", a.Ngram.OrderBy(n => n));
                parts.Add($"anchor:{a.Name}|{a.AnchorText}|{a.Threshold.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}|{ng}");
            }
        }
        return ContentHash.Compose(parts.ToArray());
    }
}

/// <summary>
/// A versioned collection of rules. Once published, a RuleSet is immutable;
/// edits produce a new version. Verdicts always cite (rule_id, ruleset_version).
/// </summary>
public sealed record RuleSet(
    string Id,
    string Version,
    string Domain,
    DateTimeOffset PublishedAt,
    IReadOnlyList<Rule> Rules,
    IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>
    /// Optional list of doc-kind identifiers this whole ruleset is
    /// authored for. <c>null</c> or empty means "applies to every kind".
    /// The effective gate for an individual rule is the union of the
    /// ruleset-level list and the rule-level list — a rule may narrow but
    /// not widen the ruleset's declared scope. See
    /// <see cref="Rule.AppliesToDocKinds"/>.
    /// </summary>
    public IReadOnlyList<string>? AppliesToDocKinds { get; init; }

    /// <summary>
    /// Pillar 3 (#118) — signed phrasebooks the runtime exposes to rule
    /// lambdas via <c>LambdaPrimitives.PhraseMatch(text, phrasebookId)</c>.
    /// Keying is by id (e.g. <c>"dr_rpo"</c>); each value is the list of
    /// case-insensitive substring phrases that count as a match. Folded
    /// into <see cref="Fingerprint"/> only when non-empty so pre-Pillar-3
    /// rulesets stay byte-identical.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Phrasebooks { get; init; }

    /// <summary>
    /// Pillar 3 (#118) — the embedder id the rule's
    /// <see cref="Rule.SourceEmbedding"/> vectors were produced with
    /// (e.g. <c>azure-openai:text-embedding-3-large@2025-04</c>). When
    /// non-null and the runtime <c>ISemanticVectorStore.ModelId</c>
    /// disagrees, evaluation throws — drifted embedding models can never
    /// silently pass. Folded into <see cref="Fingerprint"/> only when set.
    /// </summary>
    public string? EmbedderId { get; init; }

    public ContentHash Fingerprint()
    {
        var parts = new List<string> { Id, Version, Domain };
        foreach (var r in Rules.OrderBy(r => r.Id, StringComparer.Ordinal))
            parts.Add(r.Fingerprint().Value);
        if (AppliesToDocKinds is { Count: > 0 })
        {
            var kinds = AppliesToDocKinds
                .Select(k => k.Trim())
                .Where(k => k.Length > 0)
                .OrderBy(k => k, StringComparer.Ordinal);
            parts.Add("appliesToDocKinds:" + string.Join("\u001f", kinds));
        }
        if (Phrasebooks is { Count: > 0 })
        {
            foreach (var kvp in Phrasebooks.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var phrases = string.Join("\u001f", kvp.Value.OrderBy(p => p, StringComparer.Ordinal));
                parts.Add($"phrasebook:{kvp.Key}={phrases}");
            }
        }
        if (!string.IsNullOrWhiteSpace(EmbedderId))
            parts.Add("embedderId:" + EmbedderId);
        return ContentHash.Compose(parts.ToArray());
    }
}
