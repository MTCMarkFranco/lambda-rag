using LambdaRag.Core.Domain;

namespace LambdaRag.Core;

/// <summary>
/// Applies a <see cref="RuleOverlay"/> to a <see cref="RuleSet"/> by
/// filtering out disabled rules, returning a new RuleSet plus the audit
/// shape that should be folded into the report.
///
/// Pure, deterministic, no I/O — testable in isolation.
/// </summary>
public static class OverlayApplier
{
    public sealed record Result(RuleSet RuleSet, OverlayApplied Audit, IReadOnlyList<string> UnknownRuleIds);

    public static Result Apply(RuleSet ruleset, RuleOverlay overlay)
    {
        if (!string.Equals(ruleset.Id, overlay.RuleSetId, StringComparison.Ordinal) ||
            !string.Equals(ruleset.Version, overlay.RuleSetVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Overlay binds to {overlay.RuleSetId}@{overlay.RuleSetVersion} but ruleset is " +
                $"{ruleset.Id}@{ruleset.Version}. Refusing to apply: regenerate the overlay against the current ruleset.");
        }

        var present = ruleset.Rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = new List<string>();
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in overlay.Disabled)
        {
            if (!present.Contains(d.RuleId)) { unknown.Add(d.RuleId); continue; }
            disabled.Add(d.RuleId);
        }
        foreach (var a in overlay.Annotations)
            if (!present.Contains(a.RuleId)) unknown.Add(a.RuleId);

        var filteredRules = ruleset.Rules.Where(r => !disabled.Contains(r.Id)).ToList();
        var filtered = ruleset with { Rules = filteredRules };

        var audit = new OverlayApplied(
            Fingerprint: overlay.Fingerprint(),
            DisabledCount: disabled.Count,
            AnnotatedCount: overlay.Annotations.Count(a => present.Contains(a.RuleId)),
            Disabled: overlay.Disabled.Where(d => disabled.Contains(d.RuleId)).ToList(),
            Annotations: overlay.Annotations.Where(a => present.Contains(a.RuleId)).ToList())
        {
            CreatedBy = overlay.CreatedBy,
        };

        return new Result(filtered, audit, unknown);
    }
}
