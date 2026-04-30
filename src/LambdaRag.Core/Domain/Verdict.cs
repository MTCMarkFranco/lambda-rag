using System.Text.Json.Nodes;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Core.Domain;

/// <summary>
/// Per-rule outcome of a single document review.
/// • <c>Pass</c> / <c>Fail</c>: a section matched and the lambda evaluated.
/// • <c>NotApplicable</c>: the rule did not apply (Optional or Conditional
///   rule whose scope wasn't met).
/// • <c>Gap</c>: the rule is Mandatory but the document is silent on it
///   — a compliance finding ("you should have addressed this and didn't").
/// • <c>Error</c>: predicate or lambda threw at evaluation time.
/// </summary>
public enum VerdictOutcome { Pass, Fail, NotApplicable, Gap, Error }

/// <summary>
/// One rule applied to one matched section. Carries the full audit trail
/// needed to defend the verdict in legal review.
/// </summary>
public sealed record Verdict(
    string Id,
    string RuleId,
    string RuleSetVersion,
    VerdictOutcome Outcome,
    string LambdaText,
    JsonObject EvaluatedInput,
    SourceSpan SourceSpan,
    string? ErrorMessage,
    IReadOnlyList<string> EvidenceQuotes,
    DateTimeOffset EvaluatedAt)
{
    /// <summary>
    /// Optional id of the section this verdict applies to (taken from the
    /// projection's <c>sections[].id</c> when present). Lets the markup
    /// engine and coverage tool cross-reference verdicts with sections.
    /// </summary>
    public string? MatchedSectionId { get; init; }

    /// <summary>
    /// Optional rewrite suggestion in the language of the source document,
    /// rendered from the rule's remediation template. Only populated when
    /// <see cref="Outcome"/> is <see cref="VerdictOutcome.Fail"/> and the
    /// rule defined a remediation template.
    /// </summary>
    public string? RemediationText { get; init; }

    /// <summary>
    /// The predicate expression that gated this verdict's applicability,
    /// captured for audit. Empty string when the rule's predicate was the
    /// default <c>"true"</c>.
    /// </summary>
    public string PredicateText { get; init; } = string.Empty;
}

/// <summary>
/// The full report of a document review. Hashing this object gives a
/// fingerprint of the entire run; idempotency tests assert this is stable.
/// </summary>
public sealed record ComplianceReport(
    ContentHash DocumentId,
    string RuleSetId,
    string RuleSetVersion,
    ContentHash RuleSetFingerprint,
    string ProjectorId,
    string ProjectorVersion,
    double Score,
    int TotalRules,
    int Passed,
    int Failed,
    int NotApplicable,
    int Errored,
    IReadOnlyList<Verdict> Verdicts,
    DateTimeOffset GeneratedAt)
{
    /// <summary>
    /// Count of <see cref="VerdictOutcome.Gap"/> verdicts — Mandatory rules
    /// the document did not address. These count against the compliance
    /// score: <c>Score = pass / (pass + fail + gap)</c>.
    /// </summary>
    public int Gaps { get; init; }
}
