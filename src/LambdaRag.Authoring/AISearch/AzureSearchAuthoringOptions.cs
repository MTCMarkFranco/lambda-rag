namespace LambdaRag.Authoring.AISearch;

/// <summary>
/// Connection settings for the lambda-rag AI Search authoring stack.
/// All values come from configuration / user-secrets / env vars and are
/// only ever consumed at AUTHORING time. The runtime evaluation path
/// must never load this type — Phase C guardrails enforce that the
/// runtime project graph does not transitively reference Azure.Search.*.
/// </summary>
public sealed record AzureSearchAuthoringOptions
{
    /// <summary>e.g. <c>srch-lambdarag-dev</c>.</summary>
    public required string SearchServiceName { get; init; }

    /// <summary>e.g. <c>https://lambdaragauthdev.blob.core.windows.net</c>.</summary>
    public required string StorageAccountUrl { get; init; }

    /// <summary>Blob container that holds the source policy documents.</summary>
    public string SourceContainerName { get; init; } = "policies";

    /// <summary>Search index name. Matches <c>infra/search/rest/index.json</c>.</summary>
    public string IndexName { get; init; } = "lambda-rag-rules";

    /// <summary>Indexer name. Matches <c>infra/search/rest/indexer.json</c>.</summary>
    public string IndexerName { get; init; } = "lambda-rag-rules-indexer";

    /// <summary>Search REST API version used for indexer + admin operations.</summary>
    public string ApiVersion { get; init; } = "2024-11-01-preview";

    public Uri SearchEndpoint => new($"https://{SearchServiceName}.search.windows.net");
}
