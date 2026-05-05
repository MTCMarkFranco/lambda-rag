using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;

namespace LambdaRag.Authoring.Embeddings;

/// <summary>
/// Builds an <see cref="AzureFoundryEmbeddingProvider"/> from environment
/// variables. Returns <c>null</c> when the required variables are missing —
/// the caller can then fall back to <see cref="DeterministicHashEmbedder"/>
/// for offline / unit-test runs.
///
/// Required:
///   • <c>LAMBDA_RAG_FOUNDRY_ENDPOINT</c>     — e.g. https://&lt;project&gt;.openai.azure.com/
///   • <c>LAMBDA_RAG_FOUNDRY_DEPLOYMENT</c>   — Azure deployment name for the embedding model
///
/// Optional:
///   • <c>LAMBDA_RAG_FOUNDRY_MODEL</c>        — defaults to "text-embedding-3-large"
///   • <c>LAMBDA_RAG_FOUNDRY_DIMENSIONS</c>   — defaults to 3072 for `3-large`, 1536 for `3-small`, 1536 for `ada-002`
///   • <c>LAMBDA_RAG_FOUNDRY_API_KEY</c>      — when set, uses key auth; otherwise DefaultAzureCredential (Entra ID)
///   • <c>LAMBDA_RAG_EMBEDDING_CACHE</c>      — directory for the file-backed cache; defaults to <c>out/embedding-cache</c>
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

    public static AzureFoundryEmbeddingProvider? TryCreateFromEnvironment()
    {
        var endpoint = Environment.GetEnvironmentVariable(EndpointVar);
        var deployment = Environment.GetEnvironmentVariable(DeploymentVar);
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(deployment))
            return null;

        var modelId = Environment.GetEnvironmentVariable(ModelVar) ?? "text-embedding-3-large";
        var dimensions = ResolveDimensions(modelId);

        var apiKey = Environment.GetEnvironmentVariable(ApiKeyVar);
        var azureClient = string.IsNullOrWhiteSpace(apiKey)
            ? new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
            : new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

        IEmbeddingGenerator<string, Embedding<float>> generator =
            azureClient.GetEmbeddingClient(deployment).AsIEmbeddingGenerator();

        var cacheDir = Environment.GetEnvironmentVariable(CacheDirVar)
            ?? Path.Combine("out", "embedding-cache");
        var cache = new FileBackedEmbeddingCache(cacheDir, modelId, dimensions);

        return new AzureFoundryEmbeddingProvider(generator, modelId, dimensions, cache);
    }

    private static int ResolveDimensions(string modelId)
    {
        var explicitDims = Environment.GetEnvironmentVariable(DimensionsVar);
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
