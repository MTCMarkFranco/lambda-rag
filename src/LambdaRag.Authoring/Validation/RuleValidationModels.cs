namespace LambdaRag.Authoring.Validation;

/// <summary>
/// One example scored against the union of a rule''s concept vectors.
///
/// <see cref="TopScore"/> is <c>max_c cosine(embed(example), embed(concept))</c>
/// — exactly the value the runtime <c>MatchesAnyMeaning</c> function
/// compares to the rule''s threshold. Capturing the winning concept makes
/// rule-quality reports easy to read ("the rule fires on the negative
/// because its strongest concept is too generic").
/// </summary>
public sealed record ScoredExample(string Text, double TopScore, string TopConcept);

/// <summary>
/// Outcome of self-validating a single rule''s positive/negative example
/// corpus. <see cref="CalibratedThreshold"/> is the midpoint of
/// <see cref="MinPositive"/> and <see cref="MaxNegative"/>; it is what gets
/// baked into <c>Rule.GateThreshold</c> when the rule is accepted.
/// </summary>
public sealed record RuleValidationResult(
    string RuleId,
    IReadOnlyList<ScoredExample> Positives,
    IReadOnlyList<ScoredExample> Negatives,
    double MinPositive,
    double MaxNegative,
    double Margin,
    double CalibratedThreshold,
    bool Accepted,
    string? RejectionReason);

/// <summary>
/// Aggregate report from validating an entire ruleset. Stable shape so the
/// JSON serialisation can act as an audit-grade artifact.
/// </summary>
public sealed record RuleSetValidationReport(
    string RulesetId,
    string RulesetVersion,
    string EmbedderId,
    double Epsilon,
    IReadOnlyList<RuleValidationResult> Results,
    bool AllAccepted)
{
    public int RuleCount => Results.Count;
    public int AcceptedCount => Results.Count(r => r.Accepted);
    public int RejectedCount => Results.Count(r => !r.Accepted);
}
