using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Selectors;

namespace LambdaRag.Indexing.AzureSearch;

/// <summary>
/// Production IRuleStore implementation backed by Azure AI Search.
/// Queries lambda-rag-rules index with hybrid retrieval (BM25 + vector).
/// Always filters to status='approved' and the specified ruleset name/version.
/// </summary>
public sealed class AzureSearchRuleStore : IRuleStore
{
    private readonly SearchClient _client;
    private readonly string _endpoint;
    private readonly string _indexName;

    public AzureSearchRuleStore(string endpoint, string indexName)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Endpoint is required", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(indexName))
            throw new ArgumentException("Index name is required", nameof(indexName));

        _endpoint = endpoint;
        _indexName = indexName;

        var uri = new Uri(endpoint);
        _client = new SearchClient(uri, indexName, new DefaultAzureCredential());
    }

    public async Task<IReadOnlyList<string>> GetAvailableVersionsAsync(
        string rulesetName,
        CancellationToken ct = default)
    {
        var filter = $"status eq 'approved' and rulesetName eq '{Escape(rulesetName)}'";

        var options = new SearchOptions
        {
            Filter = filter,
            Facets = { "rulesetVersion,count:1000" },
            Size = 0,
        };

        var response = await _client.SearchAsync<SearchDocument>("*", options, ct);
        var facets = response.Value.Facets;

        if (!facets.TryGetValue("rulesetVersion", out var versionFacets))
            return Array.Empty<string>();

        return versionFacets
            .Select(f => f.Value?.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<RuleQueryResult> RetrieveAsync(
        RuleQuery query,
        CancellationToken ct = default)
    {
        var filter = $"status eq 'approved' and rulesetName eq '{Escape(query.RulesetName)}' and rulesetVersion eq '{Escape(query.RulesetVersion)}'";

        var options = new SearchOptions
        {
            Filter = filter,
            Size = query.TopK,
        };

        options.SearchFields.Add("naturalLanguage");
        options.SearchFields.Add("concepts");
        options.SearchFields.Add("predicate");

        if (query.QueryVector is not null && query.QueryVector.Count > 0)
        {
            var vectorQuery = new VectorizedQuery(query.QueryVector.ToArray())
            {
                KNearestNeighborsCount = query.TopK,
                Fields = { "conceptsVector" }
            };
            options.VectorSearch = new VectorSearchOptions
            {
                Queries = { vectorQuery }
            };
        }

        var response = await _client.SearchAsync<SearchDocument>(query.QueryText, options, ct);

        var rules = new List<Rule>();
        var contentHashes = new List<(string ruleId, string contentHash)>();

        await foreach (var result in response.Value.GetResultsAsync())
        {
            var doc = result.Document;
            var rule = MapToRule(doc);
            rules.Add(rule);

            var contentHash = doc.ContainsKey("contentHash") ? doc["contentHash"]?.ToString() ?? "" : "";
            contentHashes.Add((rule.Id, contentHash));
        }

        var snapshotHash = ComputeSnapshotHash(contentHashes);

        var metadata = new RulesetMetadata(
            query.RulesetName,
            query.RulesetVersion,
            _endpoint,
            snapshotHash);

        return new RuleQueryResult(rules, metadata);
    }

    public async Task<RuleQueryResult> RetrieveAllAsync(
        string rulesetName,
        string rulesetVersion,
        CancellationToken ct = default)
    {
        var filter = $"status eq 'approved' and rulesetName eq '{Escape(rulesetName)}' and rulesetVersion eq '{Escape(rulesetVersion)}'";

        var options = new SearchOptions
        {
            Filter = filter,
            Size = 1000,
            OrderBy = { "ruleId asc" }
        };

        var rules = new List<Rule>();
        var contentHashes = new List<(string ruleId, string contentHash)>();

        var response = await _client.SearchAsync<SearchDocument>("*", options, ct);

        await foreach (var page in response.Value.GetResultsAsync().AsPages())
        {
            foreach (var result in page.Values)
            {
                var doc = result.Document;
                var rule = MapToRule(doc);
                rules.Add(rule);

                var contentHash = doc.ContainsKey("contentHash") ? doc["contentHash"]?.ToString() ?? "" : "";
                contentHashes.Add((rule.Id, contentHash));
            }
        }

        var snapshotHash = ComputeSnapshotHash(contentHashes);

        var metadata = new RulesetMetadata(
            rulesetName,
            rulesetVersion,
            _endpoint,
            snapshotHash);

        return new RuleQueryResult(rules, metadata);
    }

    private static Rule MapToRule(SearchDocument doc)
    {
        var id = doc["ruleId"]?.ToString() ?? throw new InvalidDataException("ruleId is required");
        var version = doc.ContainsKey("rulesetVersion") ? doc["rulesetVersion"]?.ToString() ?? "unknown" : "unknown";
        var naturalLanguage = doc["naturalLanguage"]?.ToString() ?? "";
        var lambda = doc["lambda"]?.ToString() ?? "true";
        var predicate = doc.ContainsKey("predicate") ? doc["predicate"]?.ToString() : null;
        var severityStr = doc.ContainsKey("severity") ? doc["severity"]?.ToString() : "Violation";
        var evidenceQuote = doc.ContainsKey("evidenceQuote") ? doc["evidenceQuote"]?.ToString() : null;

        if (!Enum.TryParse<RuleSeverity>(severityStr, true, out var severity))
            severity = RuleSeverity.Violation;

        var schemaJson = @"{""type"":""object"",""properties"":{""id"":{""type"":""string""},""text"":{""type"":""string""}}}";
        var schemaNode = JsonNode.Parse(schemaJson);
        var appliesToSchema = schemaNode as JsonObject ?? new JsonObject();

        var sourceSpan = new SourceSpan(doc.ContainsKey("documentId") ? doc["documentId"]?.ToString() ?? "" : "", 0, 0, null, null);

        var metadata = new Dictionary<string, string>();
        if (doc.ContainsKey("domain"))
            metadata["domain"] = doc["domain"]?.ToString() ?? "";

        // Must match the projection emitted by IDocumentProjector ($.sections[*]).
        // A mismatched selector causes the evaluator to match zero sections, turning
        // every rule into a Gap verdict with no anchor span — which strips all
        // per-clause comments / track-changes from the redlined docx (see issue #100).
        var selector = new PathSelector("$.sections[*]");

        return new Rule(
            Id: id,
            Version: version,
            NaturalLanguage: naturalLanguage,
            Lambda: lambda,
            AppliesToSchema: appliesToSchema,
            Selector: selector,
            Severity: severity,
            SourceSpan: sourceSpan,
            EvidenceQuote: evidenceQuote ?? "",
            Metadata: metadata)
        {
            Predicate = predicate ?? "true"
        };
    }

    private static string ComputeSnapshotHash(List<(string ruleId, string contentHash)> hashes)
    {
        if (hashes.Count == 0)
            return string.Empty;

        var sorted = hashes.OrderBy(h => h.ruleId, StringComparer.Ordinal).ToList();

        var json = JsonSerializer.Serialize(
            sorted.Select(h => new { ruleId = h.ruleId, contentHash = h.contentHash }),
            new JsonSerializerOptions { WriteIndented = false });

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Escape(string value)
    {
        return value.Replace("'", "''");
    }
}

