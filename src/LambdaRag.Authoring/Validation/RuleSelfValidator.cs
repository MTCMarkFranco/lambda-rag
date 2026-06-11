using LambdaRag.Authoring.Embeddings;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Semantic;

namespace LambdaRag.Authoring.Validation;

/// <summary>
/// Authoring-time self-validation of a single rule against its own
/// positive/negative example corpus (issue #73).
///
/// For each example, we compute <c>max_c cosine(embed(example), embed(concept))</c>
/// over every concept literal extracted from the rule''s lambda — the same
/// max-over-concepts that <see cref="SemanticFunctions.MatchesAnyMeaning"/>
/// performs at runtime. The rule is accepted iff:
///
///   • every positive''s top score exceeds every negative''s top score, and
///   • the gap between <c>min(positive)</c> and <c>max(negative)</c> is at
///     least <c>epsilon</c> (default 0.05 on the cosine scale).
///
/// On acceptance, the <see cref="RuleValidationResult.CalibratedThreshold"/>
/// is the midpoint of the two extremes and is meant to be written back into
/// <see cref="Rule.GateThreshold"/> by the caller. The threshold is purely
/// authoring-time — the runtime never re-runs this calibration.
///
/// All cosine math is deterministic for a given embedder, so re-running on
/// the same inputs produces byte-identical results — required so the gold
/// corpus regression stays stable.
/// </summary>
public sealed class RuleSelfValidator
{
    private readonly IRuleEmbedder _embedder;
    private readonly double _epsilon;

    public const double DefaultEpsilon = 0.05;

    public RuleSelfValidator(IRuleEmbedder embedder, double epsilon = DefaultEpsilon)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        if (epsilon < 0 || epsilon > 1)
            throw new ArgumentOutOfRangeException(nameof(epsilon), "epsilon must lie in [0, 1].");
        _epsilon = epsilon;
    }

    /// <summary>
    /// Score a rule''s example corpus and return its acceptance verdict +
    /// calibrated threshold. The rule must have non-null
    /// <see cref="Rule.Examples"/> and at least one extractable concept.
    /// </summary>
    public async Task<RuleValidationResult> ValidateAsync(Rule rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Examples is null)
            throw new InvalidOperationException(
                $"Rule '{rule.Id}' has no Examples — call ValidateAsync only on rules emitted by the Phase B authoring pipeline.");

        var concepts = RuleSetEmbedder.ExtractConcepts(rule.Lambda).Distinct(StringComparer.Ordinal).ToList();
        if (concepts.Count == 0)
            throw new InvalidOperationException(
                $"Rule '{rule.Id}' lambda contains no SemanticFunctions concepts — nothing to validate against.");

        // Deterministic order: dedupe, then sort, then embed once each.
        concepts.Sort(StringComparer.Ordinal);
        var conceptVectors = new (string concept, float[] vec)[concepts.Count];
        for (var i = 0; i < concepts.Count; i++)
        {
            var v = await _embedder.EmbedAsync(concepts[i], ct).ConfigureAwait(false);
            conceptVectors[i] = (concepts[i], v);
        }

        var positives = await ScoreAsync(rule.Examples.Positive, conceptVectors, ct).ConfigureAwait(false);
        var negatives = await ScoreAsync(rule.Examples.Negative, conceptVectors, ct).ConfigureAwait(false);

        var minPos = positives.Count > 0 ? positives.Min(s => s.TopScore) : 0.0;
        var maxNeg = negatives.Count > 0 ? negatives.Max(s => s.TopScore) : 0.0;
        var margin = minPos - maxNeg;
        var calibrated = (minPos + maxNeg) / 2.0;

        var (accepted, reason) = Decide(rule.Id, positives, negatives, minPos, maxNeg, margin);

        return new RuleValidationResult(
            RuleId: rule.Id,
            Positives: positives,
            Negatives: negatives,
            MinPositive: minPos,
            MaxNegative: maxNeg,
            Margin: margin,
            CalibratedThreshold: calibrated,
            Accepted: accepted,
            RejectionReason: reason);
    }

    private async Task<IReadOnlyList<ScoredExample>> ScoreAsync(
        IReadOnlyList<string> texts,
        (string concept, float[] vec)[] conceptVectors,
        CancellationToken ct)
    {
        var results = new ScoredExample[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            var exampleVec = await _embedder.EmbedAsync(texts[i], ct).ConfigureAwait(false);
            var topScore = double.NegativeInfinity;
            var topConcept = string.Empty;
            foreach (var (concept, vec) in conceptVectors)
            {
                var score = SemanticFunctions.Cosine(exampleVec, vec);
                if (score > topScore)
                {
                    topScore = score;
                    topConcept = concept;
                }
            }
            // Empty concept set is impossible by precondition, but guard anyway.
            if (double.IsNegativeInfinity(topScore)) topScore = 0;
            results[i] = new ScoredExample(texts[i], topScore, topConcept);
        }
        return results;
    }

    private (bool accepted, string? reason) Decide(
        string ruleId,
        IReadOnlyList<ScoredExample> pos,
        IReadOnlyList<ScoredExample> neg,
        double minPos,
        double maxNeg,
        double margin)
    {
        if (pos.Count == 0 || neg.Count == 0)
            return (false, "Rule must have at least one positive and one negative example.");

        if (minPos <= maxNeg)
        {
            var worstPos = pos.OrderBy(p => p.TopScore).First();
            var bestNeg = neg.OrderByDescending(n => n.TopScore).First();
            return (false,
                $"Negative '{Truncate(bestNeg.Text)}' (score={bestNeg.TopScore:F4} via '{bestNeg.TopConcept}') " +
                $"matches at least as strongly as positive '{Truncate(worstPos.Text)}' " +
                $"(score={worstPos.TopScore:F4} via '{worstPos.TopConcept}'). " +
                "Reword the rule''s concepts to better separate the two.");
        }

        if (margin < _epsilon)
            return (false,
                $"Margin between positives and negatives is {margin:F4}, below epsilon {_epsilon:F4}. " +
                "Add a more discriminating concept to the rule lambda.");

        return (true, null);
    }

    private static string Truncate(string s, int n = 80)
        => s.Length <= n ? s : s[..n] + "…";

    /// <summary>
    /// Pillar 1/3 structural check (#116, #118) — every rule must carry
    /// <see cref="Rule.EvidenceQuote"/> and a non-default
    /// <see cref="Rule.SourceSpan"/>. Returns the offending rule ids;
    /// empty list = all rules are audit-trail safe. Pure-code, no I/O —
    /// safe to call from authoring tools or CI gates.
    /// </summary>
    public static IReadOnlyList<string> ValidateStructural(RuleSet ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        var bad = new List<string>();
        foreach (var rule in ruleset.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.EvidenceQuote))
            {
                bad.Add($"{rule.Id}: missing evidenceQuote");
                continue;
            }
            if (rule.SourceSpan is null
                || string.IsNullOrEmpty(rule.SourceSpan.DocumentId)
                || rule.SourceSpan.DocumentId == "(unknown)")
            {
                bad.Add($"{rule.Id}: missing or unknown sourceSpan.documentId");
            }
        }
        return bad;
    }
}
