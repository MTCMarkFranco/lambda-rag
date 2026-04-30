using System.Text.Json.Nodes;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Selectors;

namespace LambdaRag.Core.Domain;

public enum RuleSeverity { Suggestion, Deviation, Violation, Critical }

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
    /// Optional string template rendered when the lambda returns false.
    /// Supported placeholders are described in
    /// <c>LambdaRag.Evaluation.Engine.RemediationRenderer</c>.
    /// </summary>
    public string? Remediation { get; init; }

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
    public ContentHash Fingerprint() => ContentHash.Compose(
        Id,
        Version,
        Predicate,
        Lambda,
        Remediation ?? string.Empty,
        AppliesToSchema.ToJsonString(),
        Severity.ToString());
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
