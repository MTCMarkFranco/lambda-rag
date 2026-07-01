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
/// • <c>Skipped</c>: rule deliberately not evaluated (e.g. its
///   <see cref="LambdaRag.Core.Domain.Rule.AppliesToDocKinds"/> did not
///   intersect the resolved doc kind). The audit trail still cites the
///   rule so coverage is never silently dropped.
/// </summary>
public enum VerdictOutcome { Pass, Fail, NotApplicable, Gap, Error, Skipped }

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

    /// <summary>
    /// Paragraph- or section-aligned span used for tracked-change
    /// replacements / deletions. The narrow <see cref="SourceSpan"/>
    /// stays as the *evidence* anchor for reviewer comments; this wider
    /// span is what the markup engine widens deletions to so a clause
    /// that crosses paragraph boundaries is fully struck through instead
    /// of partially. Null on verdicts authored before #87 — the markup
    /// engine then falls back to single-paragraph clamping. Folded into
    /// the verdict-id hash only when non-null so existing byte-identity
    /// replay holds.
    /// </summary>
    public SourceSpan? ClauseSpan { get; init; }

    /// <summary>
    /// Pillar 6 (#124) — semantic bindings recorded during evaluation.
    /// One <see cref="BindingRecord"/> per (anchor, matched token) pair
    /// whose cosine cleared the anchor threshold. Null / empty for
    /// verdicts produced by rules without <c>semanticAnchors</c> so
    /// pre-Pillar-6 verdict JSON stays byte-identical.
    /// </summary>
    public IReadOnlyList<BindingRecord>? SemanticBindings { get; init; }
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

    /// <summary>
    /// Pillar 1 (#116) — count of <see cref="VerdictOutcome.Skipped"/>
    /// verdicts (rules excluded by the doc-kind gate). Never affects the
    /// score; the rule simply did not apply. Defaults to <c>null</c>
    /// (omitted from canonical JSON) so existing reports remain
    /// byte-identical when no doc-kind gating is in play.
    /// </summary>
    public int? Skipped { get; init; }

    /// <summary>
    /// Pillar 1 (#116) — true when every rule the engine ran was skipped
    /// by the doc-kind gate, i.e. the operator picked the wrong ruleset
    /// profile for this artifact. Defaults to <c>null</c> (omitted) so
    /// existing reports stay byte-identical when no gating fires.
    /// </summary>
    public bool? WrongProfile { get; init; }

    /// <summary>
    /// When a review was run with <c>--overlay</c>, this captures which
    /// overlay was applied (fingerprint + disabled list + annotations) so
    /// the audit trail proves that suppressing rule X was a documented
    /// governance decision, not silent rule editing.
    /// </summary>
    public OverlayApplied? OverlayApplied { get; init; }

    /// <summary>
    /// Pillar 10 — count of distinct rules in the ruleset (not verdicts).
    /// The legacy <see cref="TotalRules"/> field is actually the count of
    /// emitted <see cref="Verdict"/>s, which for broad-selector rulesets
    /// (e.g. <c>$.sections[*]</c>) inflates by section count and makes the
    /// per-verdict <see cref="Score"/> hard to interpret. This field is the
    /// honest denominator for the rule-level score.
    /// Null (omitted) when rule-level stats aren't emitted so existing
    /// golden-master reports stay byte-identical.
    /// </summary>
    public int? TotalUniqueRules { get; init; }

    /// <summary>
    /// Pillar 10 — count of unique rules whose aggregate outcome was
    /// <see cref="VerdictOutcome.Pass"/> (any per-section Pass). Rule-level
    /// counterpart of <see cref="Passed"/>. Null when off.
    /// </summary>
    public int? RulesPassed { get; init; }

    /// <summary>Pillar 10 — count of rules whose aggregate outcome was Fail. Null when off.</summary>
    public int? RulesFailed { get; init; }

    /// <summary>Pillar 10 — count of rules whose aggregate outcome was NotApplicable. Null when off.</summary>
    public int? RulesNotApplicable { get; init; }

    /// <summary>Pillar 10 — count of rules whose aggregate outcome was Gap. Null when off.</summary>
    public int? RulesGap { get; init; }

    /// <summary>Pillar 10 — count of rules whose aggregate outcome was Error. Null when off.</summary>
    public int? RulesErrored { get; init; }

    /// <summary>Pillar 10 — count of rules whose aggregate outcome was Skipped. Null when off.</summary>
    public int? RulesSkipped { get; init; }

    /// <summary>
    /// Pillar 10 — rule-level score: <c>rulesPassed / (rulesPassed + rulesFailed + rulesGap)</c>.
    /// Silent-topic (NotApplicable) rules never enter the denominator, so a
    /// ruleset that spans 24 domains but the reviewed doc only covers 3 no
    /// longer gets penalised for the 21 silent domains.
    /// Null (omitted) when rule-level stats aren't emitted.
    /// </summary>
    public double? RuleScore { get; init; }

    /// <summary>
    /// Pillar 10 — per-rule aggregate rollup, one entry per distinct
    /// rule, giving reviewers a rule-first view without post-processing
    /// the full <see cref="Verdicts"/> array. Null (omitted) when
    /// rule-level stats aren't emitted so byte-identity is preserved.
    /// </summary>
    public IReadOnlyList<RuleSummary>? RuleSummaries { get; init; }
}

/// <summary>
/// Pillar 10 — per-rule rollup emitted alongside the granular
/// <see cref="Verdict"/> stream. One entry per unique rule; the
/// <see cref="AggregateOutcome"/> follows the precedence:
/// Pass &gt; Fail &gt; Error &gt; Gap &gt; NotApplicable &gt; Skipped
/// (any single Pass wins; a Fail with real evidence beats a silent Gap).
/// </summary>
public sealed record RuleSummary(
    string RuleId,
    VerdictOutcome AggregateOutcome,
    int PassCount,
    int FailCount,
    int NotApplicableCount,
    int GapCount,
    int ErrorCount,
    int SkippedCount,
    int SectionsEvaluated)
{
    /// <summary>
    /// Optional pointer at the "best" per-section verdict for this rule
    /// (the Pass if one exists, else the first Fail, else the first
    /// verdict emitted). Lets tooling jump straight to the anchor.
    /// </summary>
    public string? RepresentativeVerdictId { get; init; }
}
