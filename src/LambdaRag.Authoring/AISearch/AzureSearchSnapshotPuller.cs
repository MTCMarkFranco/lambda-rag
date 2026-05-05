using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;

namespace LambdaRag.Authoring.AISearch;

/// <summary>
/// Filter-based snapshot puller for the lambda-rag rules index.
///
/// Pulls all approved rules for (domain, version) via a paged
/// <c>$filter</c> + <c>$orderby</c> query, hashes the canonical JSON,
/// and writes a deterministic ruleset file the runtime can load.
///
/// AUTHORING-TIME ONLY. The runtime never executes this code; it only
/// ever reads the on-disk snapshot this puller produces.
/// </summary>
public sealed class AzureSearchSnapshotPuller
{
    private readonly AzureSearchAuthoringOptions _options;
    private readonly TokenCredential _credential;

    public AzureSearchSnapshotPuller(
        AzureSearchAuthoringOptions options,
        TokenCredential? credential = null)
    {
        _options = options;
        _credential = credential ?? new DefaultAzureCredential();
    }

    /// <summary>
    /// Pull all rules matching <paramref name="filterExpression"/> from the
    /// index, build a ruleset envelope, write it to <paramref name="outPath"/>,
    /// and return the SHA-256 of the canonical JSON bytes.
    /// </summary>
    public async Task<SnapshotPullResult> PullAsync(
        string domain,
        string version,
        string outPath,
        SnapshotPullDefaults? defaults = null,
        string? status = "approved",
        CancellationToken ct = default)
    {
        defaults ??= SnapshotPullDefaults.ArchitectureReview;

        var filter = $"domain eq '{domain}' and version eq '{version}'"
            + (string.IsNullOrWhiteSpace(status) ? string.Empty : $" and status eq '{status}'");

        var client = new SearchClient(_options.SearchEndpoint, _options.IndexName, _credential);
        var rules = new List<JsonObject>();

        var searchOptions = new SearchOptions
        {
            Filter = filter,
            OrderBy = { "ruleId asc" },
            Size = 50,
        };

        var response = await client.SearchAsync<SearchDocument>("*", searchOptions, ct).ConfigureAwait(false);
        await foreach (var page in response.Value.GetResultsAsync().AsPages(default, 50).WithCancellation(ct))
        {
            foreach (var hit in page.Values)
            {
                rules.Add(MapToRuntimeRule(hit.Document, version, defaults));
            }
        }

        // Canonical: sort by ruleId for replay safety.
        var sortedRules = new JsonArray(
            rules.OrderBy(r => (string?)r["id"], StringComparer.Ordinal)
                 .Select(r => (JsonNode?)r.DeepClone())
                 .ToArray());

        var envelope = new JsonObject
        {
            ["id"] = $"rs_{domain}_{version}".Replace('-', '_'),
            ["version"] = version,
            ["domain"] = domain,
            ["publishedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["metadata"] = new JsonObject
            {
                ["source"] = $"AI Search index '{_options.IndexName}' (domain={domain}, version={version}, status={status ?? "*"})",
                ["topicMap"] = defaults.TopicMap,
            },
            ["rules"] = sortedRules,
        };

        var canonical = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath, canonical, new UTF8Encoding(false), ct).ConfigureAwait(false);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new SnapshotPullResult(rules.Count, outPath, hash);
    }

    private static JsonObject MapToRuntimeRule(SearchDocument doc, string version, SnapshotPullDefaults defaults)
    {
        // SearchDocument is IDictionary<string, object?>. Use ToString round-trip
        // through JsonNode for predictable shape.
        var asJson = JsonSerializer.SerializeToNode(doc) as JsonObject
            ?? throw new InvalidOperationException("Expected SearchDocument to serialize to a JSON object.");

        var rule = new JsonObject
        {
            ["id"] = (string?)asJson["ruleId"],
            ["version"] = version,
            ["naturalLanguage"] = (string?)asJson["naturalLanguage"],
            ["predicate"] = ((string?)asJson["predicate"]) ?? "true",
            ["lambda"] = (string?)asJson["lambda"],
            ["appliesToSchema"] = defaults.AppliesToSchema.DeepClone(),
            ["selector"] = defaults.Selector.DeepClone(),
            ["severity"] = (string?)asJson["severity"] ?? "Violation",
            ["gateThreshold"] = defaults.GateThreshold,
            ["sourceSpan"] = asJson["sourceSpan"]?.DeepClone(),
            ["evidenceQuote"] = (string?)asJson["evidenceQuote"],
            ["anchor"] = null,
            ["remediation"] = (string?)asJson["remediation"],
            ["metadata"] = asJson["metadata"]?.DeepClone(),
        };
        return rule;
    }
}

public sealed record SnapshotPullResult(int RuleCount, string OutputPath, string ContentHash);

/// <summary>
/// Per-domain defaults applied when projecting an index document into the
/// runtime ruleset shape. Index-side fields take precedence; defaults
/// supply the runtime-only fields (selector, gate threshold, schema).
/// </summary>
public sealed record SnapshotPullDefaults(
    string TopicMap,
    JsonObject AppliesToSchema,
    JsonObject Selector,
    double GateThreshold)
{
    public static SnapshotPullDefaults ArchitectureReview { get; } = new(
        TopicMap: "architecture-review.v1",
        AppliesToSchema: new JsonObject { ["type"] = "object" },
        Selector: new JsonObject { ["kind"] = "path", ["path"] = "$.sections[*]" },
        GateThreshold: 0.45);
}
