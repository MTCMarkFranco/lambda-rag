using LambdaRag.Core.Domain;
using LambdaRag.Indexing.Abstractions;

namespace LambdaRag.Indexing.InMemory;

/// <summary>
/// In-memory exact-cosine semantic index over rule source embeddings.
/// Suitable for tens of thousands of rules; for millions, swap with
/// <see cref="LambdaRag.Indexing.AzureSearch.AzureSearchRuleSemanticIndex"/>.
/// </summary>
public sealed class InMemoryRuleSemanticIndex : IRuleSemanticIndex
{
    public string IndexId => "in-memory:cosine";

    private readonly List<(string RuleId, IReadOnlyList<float> Vector)> _entries = new();

    public Task BuildAsync(RuleSet ruleSet, CancellationToken ct = default)
    {
        _entries.Clear();
        foreach (var rule in ruleSet.Rules)
        {
            if (rule.SourceEmbedding is null || rule.SourceEmbedding.Count == 0) continue;
            _entries.Add((rule.Id, rule.SourceEmbedding));
        }
        // Stable order (rule id ordinal) so SearchAsync ties break deterministically.
        _entries.Sort((a, b) => string.CompareOrdinal(a.RuleId, b.RuleId));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SemanticHit>> SearchAsync(
        IReadOnlyList<float> queryEmbedding,
        int topK = 5,
        CancellationToken ct = default)
    {
        if (queryEmbedding is null || queryEmbedding.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<SemanticHit>>([]);
        }

        var hits = _entries
            .Select(e => new SemanticHit(e.RuleId, Cosine(queryEmbedding, e.Vector)))
            .OrderByDescending(h => h.Similarity)
            .ThenBy(h => h.RuleId, StringComparer.Ordinal)
            .Take(topK)
            .ToList();
        return Task.FromResult<IReadOnlyList<SemanticHit>>(hits);
    }

    internal static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count == 0 || a.Count != b.Count) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            na  += a[i] * a[i];
            nb  += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
