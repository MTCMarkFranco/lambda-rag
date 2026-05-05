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
    public ContentHash Fingerprint()
    {
        var parts = new List<string> { Id, Version, Domain };
        foreach (var r in Rules.OrderBy(r => r.Id, StringComparer.Ordinal))
            parts.Add(r.Fingerprint().Value);
        return ContentHash.Compose(parts.ToArray());
    }
}
