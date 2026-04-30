using System.Text.RegularExpressions;
using LambdaRag.Projection;

namespace LambdaRag.Authoring;

/// <summary>
/// Deterministic topic suggester. Given an unknown section, scores every
/// topic in the current map by keyword-overlap with the section text;
/// returns the top-N existing topics, plus (if the best score is below a
/// threshold) a candidate NEW topic id derived from the heading. No LLM
/// in the loop — same inputs always produce same outputs.
/// </summary>
public sealed class KeywordHeuristicTopicSuggester : ITopicSuggester
{
    private readonly int _topN;
    private readonly double _newTopicThreshold;

    public KeywordHeuristicTopicSuggester(int topN = 3, double newTopicThreshold = 0.05)
    {
        _topN = Math.Max(1, topN);
        _newTopicThreshold = newTopicThreshold;
    }

    public Task<IReadOnlyList<TopicSuggestion>> SuggestAsync(
        TopicSuggestionRequest request,
        CancellationToken ct = default)
    {
        var lowered = (request.Heading + "\n" + request.Body).ToLowerInvariant();
        var headingLower = request.Heading.ToLowerInvariant();
        var totalLen = Math.Max(1, lowered.Length);

        var scored = new List<(string Topic, double Score, string MatchedKw)>();
        foreach (var t in request.CurrentMap.Topics.Where(t => t.Axis is null))
        {
            double best = 0;
            string matched = string.Empty;
            foreach (var kw in t.Keywords)
            {
                if (lowered.Contains(kw, StringComparison.Ordinal))
                {
                    var score = (double)kw.Length / totalLen
                        + (headingLower.Contains(kw, StringComparison.Ordinal) ? 0.3 : 0);
                    if (score > best) { best = score; matched = kw; }
                }
            }
            if (best > 0) scored.Add((t.Id, best, matched));
        }

        var ranked = scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Topic, StringComparer.Ordinal)
            .Take(_topN)
            .Select(x => new TopicSuggestion(
                TopicId: x.Topic,
                SeedKeywords: new[] { x.MatchedKw },
                IsExisting: true,
                Confidence: Math.Round(Math.Min(1.0, x.Score * 4.0), 3),
                Rationale: $"Existing topic '{x.Topic}' matched on keyword '{x.MatchedKw}'."))
            .ToList();

        // If best existing match is weak, propose a new topic id from the heading.
        var topScore = ranked.FirstOrDefault()?.Confidence ?? 0;
        if (topScore < _newTopicThreshold * 4 && !string.IsNullOrWhiteSpace(request.Heading))
        {
            var candidateId = SlugifyId(request.Heading);
            if (!string.IsNullOrEmpty(candidateId)
                && !request.CurrentMap.Topics.Any(t => t.Id.Equals(candidateId, StringComparison.OrdinalIgnoreCase)))
            {
                var seeds = ExtractSeedKeywords(request.Heading, request.Body);
                ranked.Add(new TopicSuggestion(
                    TopicId: candidateId,
                    SeedKeywords: seeds,
                    IsExisting: false,
                    Confidence: 0.3,
                    Rationale: $"No existing topic exceeded threshold; proposing new topic '{candidateId}' derived from heading."));
            }
        }

        return Task.FromResult<IReadOnlyList<TopicSuggestion>>(ranked);
    }

    internal static string SlugifyId(string heading)
    {
        var lowered = heading.ToLowerInvariant();
        var cleaned = Regex.Replace(lowered, "[^a-z0-9]+", "_").Trim('_');
        // Collapse repeats, trim length, drop very short tokens
        var parts = cleaned.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length >= 2)
            .Take(4);
        return string.Join("_", parts);
    }

    internal static IReadOnlyList<string> ExtractSeedKeywords(string heading, string body)
    {
        var headingTokens = Regex.Replace(heading.ToLowerInvariant(), "[^a-z0-9 ]", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 4 && !StopWords.Contains(t))
            .Distinct()
            .Take(3)
            .ToList();
        var bodyTokens = Regex.Replace(body.ToLowerInvariant(), "[^a-z0-9 ]", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 5 && !StopWords.Contains(t))
            .GroupBy(t => t)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(3)
            .Select(g => g.Key)
            .ToList();
        return headingTokens.Concat(bodyTokens).Distinct().ToList();
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "shall","will","must","should","this","that","the","with","from","into","of","and",
        "any","not","such","then","than","when","where","which","other","party","section",
        "agreement","provider","customer","including","without","limitation","subject","applicable"
    };
}
