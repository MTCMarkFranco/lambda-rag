using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace LambdaRag.Authoring.AISearch;

/// <summary>
/// Drives the AI Search-backed authoring pipeline:
/// upload source documents to the policies container, kick the indexer,
/// and poll the indexer status until the run terminates.
///
/// AUTHORING-TIME ONLY. Do not reference from any runtime evaluation
/// project — the Phase C guardrail tests will fail the build.
/// </summary>
public sealed class AzureSearchAuthoringDriver
{
    private static readonly string[] s_searchScope = { "https://search.azure.com/.default" };

    private readonly AzureSearchAuthoringOptions _options;
    private readonly TokenCredential _credential;
    private readonly HttpClient _http;

    public AzureSearchAuthoringDriver(
        AzureSearchAuthoringOptions options,
        TokenCredential? credential = null,
        HttpClient? http = null)
    {
        _options = options;
        _credential = credential ?? new DefaultAzureCredential();
        _http = http ?? new HttpClient();
    }

    /// <summary>
    /// Upload one or more local files to the source policies container.
    /// Returns the names assigned in blob storage.
    /// </summary>
    public async Task<IReadOnlyList<string>> UploadSourcesAsync(
        IEnumerable<string> localPaths,
        CancellationToken ct = default)
    {
        var container = new BlobContainerClient(
            new Uri($"{_options.StorageAccountUrl.TrimEnd('/')}/{_options.SourceContainerName}"),
            _credential);

        var uploaded = new List<string>();
        foreach (var path in localPaths)
        {
            var name = Path.GetFileName(path);
            var blob = container.GetBlobClient(name);
            await using var stream = File.OpenRead(path);
            await blob.UploadAsync(
                stream,
                new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = ContentTypeFor(path) } },
                ct).ConfigureAwait(false);
            uploaded.Add(name);
        }
        return uploaded;
    }

    /// <summary>
    /// Kick the indexer (POST .../indexers/{name}/run) and poll its status
    /// until success or transient failure terminates.
    /// </summary>
    public async Task<IndexerRunResult> RunIndexerAsync(
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        pollInterval ??= TimeSpan.FromSeconds(5);
        timeout ??= TimeSpan.FromMinutes(15);

        var token = await GetSearchTokenAsync(ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_options.SearchEndpoint}/indexers('{_options.IndexerName}')/search.run?api-version={_options.ApiVersion}");
        req.Headers.Add("Authorization", $"Bearer {token}");
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var deadline = DateTimeOffset.UtcNow + timeout.Value;
        IndexerStatus? lastStatus = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(pollInterval.Value, ct).ConfigureAwait(false);
            lastStatus = await GetIndexerStatusAsync(ct).ConfigureAwait(false);
            if (lastStatus.LastResultStatus is "success" or "transientFailure" or "persistentFailure" or "reset" or "error")
            {
                return new IndexerRunResult(
                    Success: lastStatus.LastResultStatus == "success",
                    Status: lastStatus.LastResultStatus,
                    ErrorMessage: lastStatus.LastResultErrorMessage,
                    ItemsProcessed: lastStatus.LastResultItemsProcessed,
                    ItemsFailed: lastStatus.LastResultItemsFailed);
            }
        }

        return new IndexerRunResult(
            Success: false,
            Status: $"timeout (last={lastStatus?.LastResultStatus ?? "unknown"})",
            ErrorMessage: "Indexer run did not terminate within the polling window.",
            ItemsProcessed: lastStatus?.LastResultItemsProcessed ?? 0,
            ItemsFailed: lastStatus?.LastResultItemsFailed ?? 0);
    }

    private async Task<IndexerStatus> GetIndexerStatusAsync(CancellationToken ct)
    {
        var token = await GetSearchTokenAsync(ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_options.SearchEndpoint}/indexers('{_options.IndexerName}')/search.status?api-version={_options.ApiVersion}");
        req.Headers.Add("Authorization", $"Bearer {token}");
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var root = doc.RootElement;
        var lastResult = root.TryGetProperty("lastResult", out var lr) && lr.ValueKind == JsonValueKind.Object ? lr : default;
        return new IndexerStatus(
            Status: root.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown",
            LastResultStatus: TryString(lastResult, "status") ?? "running",
            LastResultErrorMessage: TryString(lastResult, "errorMessage"),
            LastResultItemsProcessed: TryInt(lastResult, "itemsProcessed") ?? 0,
            LastResultItemsFailed: TryInt(lastResult, "itemsFailed") ?? 0);
    }

    private async Task<string> GetSearchTokenAsync(CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(s_searchScope), ct).ConfigureAwait(false);
        return token.Token;
    }

    private static string? TryString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? TryInt(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

    private static string ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            _ => "application/octet-stream",
        };
}

public sealed record IndexerRunResult(
    bool Success,
    string Status,
    string? ErrorMessage,
    int ItemsProcessed,
    int ItemsFailed);

internal sealed record IndexerStatus(
    string Status,
    string LastResultStatus,
    string? LastResultErrorMessage,
    int LastResultItemsProcessed,
    int LastResultItemsFailed);
