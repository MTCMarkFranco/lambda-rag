using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace LambdaRag.Authoring.Embeddings;

/// <summary>
/// Builds an <see cref="AzureFoundryEmbeddingProvider"/> from
/// <see cref="IConfiguration"/> (e.g. <c>dotnet user-secrets</c> or
/// <c>appsettings.json</c>) with environment-variable fallback. Returns
/// <c>null</c> when the required values are missing — the caller can then
/// fall back to <see cref="DeterministicHashEmbedder"/> for offline /
/// unit-test runs.
///
/// Configuration keys (preferred — set via <c>dotnet user-secrets</c>):
///   • <c>LambdaRag:Foundry:Endpoint</c>     — e.g. https://&lt;project&gt;.openai.azure.com/openai/v1
///   • <c>LambdaRag:Foundry:Deployment</c>   — Azure deployment name for the embedding model
///   • <c>LambdaRag:Foundry:Model</c>        — defaults to "text-embedding-3-large"
///   • <c>LambdaRag:Foundry:Dimensions</c>   — defaults per model (3072 for 3-large)
///   • <c>LambdaRag:Foundry:ApiKey</c>       — when set, uses key auth; otherwise DefaultAzureCredential (Entra ID)
///   • <c>LambdaRag:EmbeddingCache</c>       — directory for the file-backed cache; defaults to <c>out/embedding-cache</c>
///
/// Environment-variable fallback (legacy / CI):
///   • <c>LAMBDA_RAG_FOUNDRY_ENDPOINT</c>, <c>LAMBDA_RAG_FOUNDRY_DEPLOYMENT</c>,
///     <c>LAMBDA_RAG_FOUNDRY_MODEL</c>, <c>LAMBDA_RAG_FOUNDRY_DIMENSIONS</c>,
///     <c>LAMBDA_RAG_FOUNDRY_API_KEY</c>, <c>LAMBDA_RAG_EMBEDDING_CACHE</c>
///
/// All embeddings flow through a <see cref="FileBackedEmbeddingCache"/> so
/// repeat runs (and CI replays) are 100% offline once the cache is warm.
/// </summary>
public static class FoundryEmbedderFactory
{
    public const string EndpointVar = "LAMBDA_RAG_FOUNDRY_ENDPOINT";
    public const string DeploymentVar = "LAMBDA_RAG_FOUNDRY_DEPLOYMENT";
    public const string ModelVar = "LAMBDA_RAG_FOUNDRY_MODEL";
    public const string DimensionsVar = "LAMBDA_RAG_FOUNDRY_DIMENSIONS";
    public const string ApiKeyVar = "LAMBDA_RAG_FOUNDRY_API_KEY";
    public const string CacheDirVar = "LAMBDA_RAG_EMBEDDING_CACHE";

    // Preferred Pillar 6 (#126) keys — embedding endpoint lives under
    // :Foundry:Embed:* so it cleanly co-exists with the chat/edit endpoint
    // configured under :Foundry:Edit:*. The legacy unsegmented keys
    // (EndpointKeyLegacy etc.) are read as a fallback for backward compat.
    public const string EndpointKey = "LambdaRag:Foundry:Embed:Endpoint";
    public const string DeploymentKey = "LambdaRag:Foundry:Embed:Deployment";
    public const string ModelKey = "LambdaRag:Foundry:Embed:Model";
    public const string DimensionsKey = "LambdaRag:Foundry:Embed:Dimensions";
    public const string ApiKeyKey = "LambdaRag:Foundry:Embed:ApiKey";
    public const string CacheDirKey = "LambdaRag:EmbeddingCache";

    private const string EndpointKeyLegacy = "LambdaRag:Foundry:Endpoint";
    private const string DeploymentKeyLegacy = "LambdaRag:Foundry:Deployment";
    private const string ModelKeyLegacy = "LambdaRag:Foundry:Model";
    private const string DimensionsKeyLegacy = "LambdaRag:Foundry:Dimensions";
    private const string ApiKeyKeyLegacy = "LambdaRag:Foundry:ApiKey";

    /// <summary>
    /// Reads Foundry settings from <paramref name="configuration"/> first,
    /// then falls back to environment variables. Pass a configuration root
    /// built with <c>AddUserSecrets()</c> + <c>AddEnvironmentVariables()</c>
    /// to honour both <c>dotnet user-secrets</c> and legacy CI vars.
    /// </summary>
    public static AzureFoundryEmbeddingProvider? TryCreate(IConfiguration? configuration)
    {
        var endpoint = Resolve(configuration, EndpointKey, EndpointVar)
            ?? configuration?[EndpointKeyLegacy];
        var deployment = Resolve(configuration, DeploymentKey, DeploymentVar)
            ?? configuration?[DeploymentKeyLegacy];
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(deployment))
            return null;

        var modelId = Resolve(configuration, ModelKey, ModelVar)
            ?? configuration?[ModelKeyLegacy]
            ?? "text-embedding-3-large";
        var dimensions = ResolveDimensions(configuration, modelId);

        var apiKey = Resolve(configuration, ApiKeyKey, ApiKeyVar)
            ?? configuration?[ApiKeyKeyLegacy];
        var azureClient = string.IsNullOrWhiteSpace(apiKey)
            ? new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
            : new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

        IEmbeddingGenerator<string, Embedding<float>> generator =
            azureClient.GetEmbeddingClient(deployment).AsIEmbeddingGenerator();

        var cacheDir = Resolve(configuration, CacheDirKey, CacheDirVar)
            ?? Path.Combine("out", "embedding-cache");
        var cache = new FileBackedEmbeddingCache(cacheDir, modelId, dimensions);

        return new AzureFoundryEmbeddingProvider(generator, modelId, dimensions, cache);
    }

    /// <summary>
    /// Backwards-compatible wrapper that reads only from environment variables.
    /// New callers should prefer <see cref="TryCreate(IConfiguration?)"/>.
    /// </summary>
    public static AzureFoundryEmbeddingProvider? TryCreateFromEnvironment()
        => TryCreate(configuration: null);

    private static string? Resolve(IConfiguration? configuration, string key, string envVar)
    {
        var fromConfig = configuration?[key];
        if (!string.IsNullOrWhiteSpace(fromConfig))
            return fromConfig;
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }

    private static int ResolveDimensions(IConfiguration? configuration, string modelId)
    {
        var explicitDims = Resolve(configuration, DimensionsKey, DimensionsVar)
            ?? configuration?[DimensionsKeyLegacy];
        if (!string.IsNullOrWhiteSpace(explicitDims) && int.TryParse(explicitDims, out var d) && d > 0)
            return d;
        return modelId switch
        {
            "text-embedding-3-large" => 3072,
            "text-embedding-3-small" => 1536,
            "text-embedding-ada-002" => 1536,
            _ => 3072,
        };
    }
}
