using Microsoft.Extensions.DependencyInjection;

namespace LambdaRag.Authoring;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the deterministic mock authoring agent + hash embedder.
    /// Production hosts should replace these with LLM-backed implementations
    /// behind the same interfaces.
    /// </summary>
    public static IServiceCollection AddLambdaRagAuthoring(this IServiceCollection services)
    {
        // Prefer a real Azure Foundry embedder when LAMBDA_RAG_FOUNDRY_*
        // environment variables are set; otherwise fall back to the
        // deterministic hash embedder so unit tests + offline replays still
        // work without any cloud credentials.
        services.AddSingleton<IRuleEmbedder>(_ =>
            (IRuleEmbedder?)Embeddings.FoundryEmbedderFactory.TryCreateFromEnvironment()
                ?? new DeterministicHashEmbedder());
        services.AddSingleton<IRuleAuthoringAgent, DeterministicMockAuthoringAgent>();
        return services;
    }
}
