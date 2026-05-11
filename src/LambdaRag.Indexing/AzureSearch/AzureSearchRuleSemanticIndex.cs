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
            // Pull section identity from rule metadata when present. Authoring
            // pipelines that don't stamp these fields just leave them null —
            // the index field is filterable but optional.
            rule.Metadata.TryGetValue("parentDocumentId", out var parentDoc);
            rule.Metadata.TryGetValue("sectionId", out var sectionId);
            batch.Actions.Add(IndexDocumentsAction.MergeOrUpload(new IndexedRule
            {
                Id = rule.Id,
                RuleSetId = ruleSet.Id,
                RuleSetVersion = ruleSet.Version,
                EmbedderId = _options.EmbedderId,
                Predicate = rule.Predicate,
                NaturalLanguage = rule.NaturalLanguage,
                SourceContent = rule.SourceContent ?? string.Empty,
                ParentDocumentId = parentDoc ?? rule.SourceSpan.DocumentId,
                SectionId = sectionId,
                SourceCharStart = rule.SourceSpan.CharStart,
                Embedding = rule.SourceEmbedding.ToArray(),
            }));
        }
        if (batch.Actions.Count == 0) return;
        await _searchClient.IndexDocumentsAsync(batch, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Group rules by (parentDocumentId, sectionId) and project each group
    /// down to a single canonical <see cref="ReassembledRuleHandle"/> ordered
    /// by <c>SourceCharStart</c>. Callers materializing a runtime
    /// <see cref="RuleSet"/> from the authoring index should call this to
    /// merge sibling chunks of the same source clause back into one rule —
    /// the redline pipeline relies on full-clause evidence to widen
    /// deletions / replacements to the right span (issue #87).
    ///
    /// Rules without a sectionId pass through ungrouped (each becomes its
    /// own single-entry group) so legacy data behaves identically to the
    /// pre-#87 path.
    /// </summary>
    public async Task<IReadOnlyList<ReassembledRuleHandle>> ReassembleAsync(
        string ruleSetId,
        CancellationToken ct = default)
    {
        var options = new SearchOptions
        {
            Filter = $"ruleSetId eq '{Escape(ruleSetId)}'",
            Size = 1000,
        };
        options.Select.Add("id");
        options.Select.Add("parentDocumentId");
        options.Select.Add("sectionId");
        options.Select.Add("sourceCharStart");
        options.Select.Add("naturalLanguage");
        options.Select.Add("sourceContent");

        var response = await _searchClient
            .SearchAsync<IndexedRule>(searchText: null, options, ct)
            .ConfigureAwait(false);

        var rows = new List<IndexedRule>();
        await foreach (var r in response.Value.GetResultsAsync().WithCancellation(ct).ConfigureAwait(false))
        {
            rows.Add(r.Document);
        }

        var groups = new Dictionary<string, List<IndexedRule>>(StringComparer.Ordinal);
        var singletons = new List<IndexedRule>();
        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.SectionId) || string.Equals(r.SectionId, "section:no-heading", StringComparison.Ordinal))
            {
                singletons.Add(r);
                continue;
            }
            var key = (r.ParentDocumentId ?? "") + "\u001f" + r.SectionId;
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<IndexedRule>();
                groups[key] = list;
            }
            list.Add(r);
        }

        var output = new List<ReassembledRuleHandle>(groups.Count + singletons.Count);
        foreach (var (key, list) in groups.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            list.Sort((a, b) => (a.SourceCharStart ?? 0).CompareTo(b.SourceCharStart ?? 0));
            var head = list[0];
            output.Add(new ReassembledRuleHandle(
                CanonicalRuleId: head.Id ?? key,
                ParentDocumentId: head.ParentDocumentId ?? string.Empty,
                SectionId: head.SectionId ?? string.Empty,
                MemberRuleIds: list.Select(m => m.Id ?? string.Empty).ToArray(),
                ConcatenatedNaturalLanguage: string.Join(
                    "\n",
                    list.Select(m => m.NaturalLanguage).Where(s => !string.IsNullOrEmpty(s))!),
                ConcatenatedSourceContent: string.Join(
                    "\n",
                    list.Select(m => m.SourceContent).Where(s => !string.IsNullOrEmpty(s))!)));
        }
        foreach (var r in singletons.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            output.Add(new ReassembledRuleHandle(
                CanonicalRuleId: r.Id ?? "",
                ParentDocumentId: r.ParentDocumentId ?? string.Empty,
                SectionId: r.SectionId ?? string.Empty,
                MemberRuleIds: new[] { r.Id ?? string.Empty },
                ConcatenatedNaturalLanguage: r.NaturalLanguage ?? string.Empty,
                ConcatenatedSourceContent: r.SourceContent ?? string.Empty));
        }
        return output;
    }

    private static string Escape(string s) => s.Replace("'", "''");

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

        /// <summary>
        /// Outer-scope identity of the source policy document. Combined
        /// with <see cref="SectionId"/> to group sibling chunks of a
        /// multi-chunk clause during <see cref="ReassembleAsync"/>.
        /// </summary>
        [SimpleField(IsFilterable = true, IsSortable = true)]
        public string? ParentDocumentId { get; set; }

        /// <summary>
        /// Stable id of the source section the chunk was extracted from.
        /// Two chunks of the same clause share this id; differ in
        /// <see cref="SourceCharStart"/>.
        /// </summary>
        [SimpleField(IsFilterable = true, IsSortable = true)]
        public string? SectionId { get; set; }

        /// <summary>
        /// Character offset of the chunk inside the source document. Used
        /// as the deterministic ordering key when reassembling sibling
        /// chunks of the same <see cref="SectionId"/>.
        /// </summary>
        [SimpleField(IsFilterable = true, IsSortable = true)]
        public int? SourceCharStart { get; set; }

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

/// <summary>
/// Result of grouping authoring-index rows by
/// (parentDocumentId, sectionId). Carries enough information for a
/// runtime materializer to merge sibling chunks back into a single
/// canonical <see cref="LambdaRag.Core.Domain.Rule"/> before evaluation.
/// </summary>
public sealed record ReassembledRuleHandle(
    string CanonicalRuleId,
    string ParentDocumentId,
    string SectionId,
    IReadOnlyList<string> MemberRuleIds,
    string ConcatenatedNaturalLanguage,
    string ConcatenatedSourceContent);
