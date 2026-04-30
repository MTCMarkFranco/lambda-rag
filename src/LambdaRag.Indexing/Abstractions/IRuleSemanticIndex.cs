using LambdaRag.Core.Domain;

namespace LambdaRag.Indexing.Abstractions;

/// <summary>
/// Top-k semantic search over rule source content embeddings. Used by
/// authoring (duplicate detection) and coverage (similarity audit). Never
/// consulted by the runtime evaluator — it is an authoring-time tool.
/// </summary>
public interface IRuleSemanticIndex
{
    /// <summary>Stable identifier — e.g. "in-memory" or "azure-search:rules-v1".</summary>
    string IndexId { get; }

    Task BuildAsync(RuleSet ruleSet, CancellationToken ct = default);

    /// <summary>Return the top-k rules whose source embedding is closest (cosine) to <paramref name="queryEmbedding"/>.</summary>
    Task<IReadOnlyList<SemanticHit>> SearchAsync(
        IReadOnlyList<float> queryEmbedding,
        int topK = 5,
        CancellationToken ct = default);
}

public sealed record SemanticHit(string RuleId, double Similarity);
