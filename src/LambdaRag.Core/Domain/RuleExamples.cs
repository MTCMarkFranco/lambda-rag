namespace LambdaRag.Core.Domain;

/// <summary>
/// Self-validation example corpus carried alongside a <see cref="Rule"/>.
///
/// Three positive snippets that the rule MUST match and three negative
/// snippets that the rule MUST NOT match. Generated at extraction time by
/// the GenAI prompt skill (issue #72) so that Phase B (issue #73) can
/// score each example against the rule's concept vectors and:
///
///   1. Reject the rule outright when negatives outscore positives, and
///   2. Pick a deterministic per-rule cosine threshold equal to the
///      midpoint of <c>min(positiveCosine)</c> and <c>max(negativeCosine)</c>.
///
/// The threshold is then baked back into <see cref="Rule.GateThreshold"/>,
/// so the runtime never re-runs the calibration — it just consumes the
/// frozen number.
///
/// Examples participate in <see cref="Rule.Fingerprint"/> only when
/// present, so existing example-less rulesets keep their identity.
/// </summary>
public sealed record RuleExamples(
    IReadOnlyList<string> Positive,
    IReadOnlyList<string> Negative);
