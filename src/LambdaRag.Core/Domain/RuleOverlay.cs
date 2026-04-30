using LambdaRag.Core.Hashing;

namespace LambdaRag.Core.Domain;

/// <summary>
/// A version-controlled sidecar that customers maintain *outside* the
/// extracted <see cref="RuleSet"/> to record local governance decisions
/// without ever touching the rules themselves. The chain of custody from
/// signed policy → extracted ruleset → verdict is preserved; the overlay
/// is recorded on the report as a separate, attributable artifact.
///
/// Two operations are supported:
/// • <see cref="Disabled"/> — explicitly suppress a rule from evaluation
///   with a documented reason (e.g. "superseded by side-letter dated
///   2026-Q2"). Disabled rules still appear in the audit trail.
/// • <see cref="Annotations"/> — attach human notes to a rule without
///   changing its predicate or lambda. Notes are reviewer commentary;
///   they never affect the evaluation outcome.
///
/// Overlays are bound to a specific <see cref="RuleSet.Id"/> /
/// <see cref="RuleSet.Version"/> pair so they cannot silently drift onto
/// a different ruleset.
/// </summary>
public sealed record RuleOverlay(
    string RuleSetId,
    string RuleSetVersion,
    DateTimeOffset CreatedAt,
    IReadOnlyList<DisabledRule> Disabled,
    IReadOnlyList<RuleAnnotation> Annotations)
{
    /// <summary>Optional human/system identity that authored the overlay.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>SHA-256 over every governance-affecting field. Folded into the report so reviewers can prove which overlay was active.</summary>
    public ContentHash Fingerprint()
    {
        var parts = new List<string> { "overlay", RuleSetId, RuleSetVersion };
        foreach (var d in Disabled.OrderBy(d => d.RuleId, StringComparer.Ordinal))
            parts.Add($"disable:{d.RuleId}|{d.Reason}");
        foreach (var a in Annotations.OrderBy(a => a.RuleId, StringComparer.Ordinal).ThenBy(a => a.Note, StringComparer.Ordinal))
            parts.Add($"note:{a.RuleId}|{a.Note}");
        return ContentHash.Compose(parts.ToArray());
    }
}

/// <summary>One entry in <see cref="RuleOverlay.Disabled"/>.</summary>
public sealed record DisabledRule(
    string RuleId,
    string Reason,
    DateTimeOffset DisabledAt)
{
    public string? DisabledBy { get; init; }
}

/// <summary>One entry in <see cref="RuleOverlay.Annotations"/>.</summary>
public sealed record RuleAnnotation(
    string RuleId,
    string Note,
    DateTimeOffset AnnotatedAt)
{
    public string? AuthoredBy { get; init; }
}

/// <summary>
/// The summary of an overlay's effect on a single review run. Stored on
/// <see cref="ComplianceReport.OverlayApplied"/> when a review was run
/// with <c>--overlay</c>; the empty case is encoded as <c>null</c>.
/// </summary>
public sealed record OverlayApplied(
    ContentHash Fingerprint,
    int DisabledCount,
    int AnnotatedCount,
    IReadOnlyList<DisabledRule> Disabled,
    IReadOnlyList<RuleAnnotation> Annotations)
{
    public string? CreatedBy { get; init; }
}
