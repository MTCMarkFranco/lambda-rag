using System.Text.Json.Nodes;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Core.Domain;

public enum VerdictOutcome { Pass, Fail, NotApplicable, Error }

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
    DateTimeOffset EvaluatedAt);

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
    DateTimeOffset GeneratedAt);
