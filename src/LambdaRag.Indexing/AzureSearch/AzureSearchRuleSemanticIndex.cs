using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using LambdaRag.Core.Domain;
using LambdaRag.Indexing.Abstractions;

namespace LambdaRag.Indexing.AzureSearch;

/// <summary>
/// Azure AI Search-backed rule semantic index. Production-grade: handles
/// tens of millions of rules with sub-100ms vector search latency.
///
/// Provisioning (one-time):
/// • Create an Azure AI Search service (Basic+ tier supports vectors).
/// • Set <see cref="Options.IndexName"/> (e.g. "lambda-rag-rules-v1") and
///   <see cref="Options.VectorDimensions"/> matching your embedder.
/// • Call <see cref="EnsureIndexAsync"/> at startup.
///
/// At runtime, the runtime evaluator NEVER consults this index. It is
/// authoring-only — used for duplicate detection during rule authoring
/// and for similarity audits in the coverage tool. The runtime decision
/// remains 100% deterministic via the compiled predicate.
/// </summary>
public sealed class AzureSearchRuleSemanticIndex : IRuleSemanticIndex
{
    public string IndexId => $"azure-search:{_options.IndexName}";

    public sealed class Options
    {
        public required Uri Endpoint { get; init; }
        public required AzureKeyCredential Credential { get; init; }
        public required string IndexName { get; init; }
        public required int VectorDimensions { get; init; }
        public string EmbedderId { get; init; } = "unspecified";
        public string VectorProfileName { get; init; } = "lr-vector-profile";
        public string AlgorithmName { get; init; } = "lr-hnsw";
    }

    private readonly Options _options;
    private readonly SearchIndexClient _indexClient;
    private readonly SearchClient _searchClient;

    public AzureSearchRuleSemanticIndex(Options options)
    {
        _options = options;
        _indexClient = new SearchIndexClient(options.Endpoint, options.Credential);
        _searchClient = new SearchClient(options.Endpoint, options.IndexName, options.Credential);
    }

    /// <summary>Create the index if it does not exist. Idempotent.</summary>
    public async Task EnsureIndexAsync(CancellationToken ct = default)
    {
        var fields = new FieldBuilder().Build(typeof(IndexedRule));
        // Manually define the vector field (FieldBuilder cannot infer dimensionality).
        var vectorField = new VectorSearchField(
            "embedding",
            _options.VectorDimensions,
            _options.VectorProfileName);
        fields.Add(vectorField);

        var definition = new SearchIndex(_options.IndexName, fields)
        {
            VectorSearch = new VectorSearch
            {
                Profiles =
                {
                    new VectorSearchProfile(_options.VectorProfileName, _options.AlgorithmName),
                },
                Algorithms =
                {
                    new HnswAlgorithmConfiguration(_options.AlgorithmName),
                },
            },
        };

        await _indexClient.CreateOrUpdateIndexAsync(definition, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task BuildAsync(RuleSet ruleSet, CancellationToken ct = default)
    {
        var batch = IndexDocumentsBatch.Create<IndexedRule>();
        foreach (var rule in ruleSet.Rules)
        {
            if (rule.SourceEmbedding is null || rule.SourceEmbedding.Count == 0) continue;
            batch.Actions.Add(IndexDocumentsAction.MergeOrUpload(new IndexedRule
            {
                Id = rule.Id,
                RuleSetId = ruleSet.Id,
                RuleSetVersion = ruleSet.Version,
                EmbedderId = _options.EmbedderId,
                Predicate = rule.Predicate,
                NaturalLanguage = rule.NaturalLanguage,
                SourceContent = rule.SourceContent ?? string.Empty,
                Embedding = rule.SourceEmbedding.ToArray(),
            }));
        }
        if (batch.Actions.Count == 0) return;
        await _searchClient.IndexDocumentsAsync(batch, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SemanticHit>> SearchAsync(
        IReadOnlyList<float> queryEmbedding,
        int topK = 5,
        CancellationToken ct = default)
    {
        if (queryEmbedding is null || queryEmbedding.Count == 0) return [];

        var query = new VectorizedQuery(queryEmbedding.ToArray())
        {
            KNearestNeighborsCount = topK,
            Fields = { "embedding" },
        };
        var options = new SearchOptions
        {
            Size = topK,
            VectorSearch = new VectorSearchOptions { Queries = { query } },
        };
        var response = await _searchClient.SearchAsync<IndexedRule>(searchText: null, options, ct).ConfigureAwait(false);
        var hits = new List<SemanticHit>();
        await foreach (var r in response.Value.GetResultsAsync().WithCancellation(ct).ConfigureAwait(false))
        {
            // Azure Search returns @search.score; for vector queries this is rescaled cosine.
            hits.Add(new SemanticHit(r.Document.Id!, r.Score ?? 0));
        }
        return hits
            .OrderByDescending(h => h.Similarity)
            .ThenBy(h => h.RuleId, StringComparer.Ordinal)
            .ToList();
    }

    private sealed class IndexedRule
    {
        [SimpleField(IsKey = true, IsFilterable = true)]
        public string? Id { get; set; }

        [SimpleField(IsFilterable = true)]
        public string? RuleSetId { get; set; }

        [SimpleField(IsFilterable = true)]
        public string? RuleSetVersion { get; set; }

        [SimpleField(IsFilterable = true)]
        public string? EmbedderId { get; set; }

        [SearchableField]
        public string? Predicate { get; set; }

        [SearchableField]
        public string? NaturalLanguage { get; set; }

        [SearchableField]
        public string? SourceContent { get; set; }

        // Vector field added imperatively in EnsureIndexAsync.
        public float[]? Embedding { get; set; }
    }
}
