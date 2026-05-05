using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LambdaRag.Authoring;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the deterministic mock authoring agent + an
    /// <see cref="IRuleEmbedder"/>. When <paramref name="configuration"/>
    /// supplies <c>LambdaRag:Foundry:*</c> values (or the legacy
    /// <c>LAMBDA_RAG_FOUNDRY_*</c> env vars are set) a real Azure Foundry
    /// embedder is wired up; otherwise the deterministic hash embedder is
    /// used so unit tests + offline replays still work without any cloud
    /// credentials.
    /// </summary>
    public static IServiceCollection AddLambdaRagAuthoring(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.AddSingleton<IRuleEmbedder>(_ =>
            (IRuleEmbedder?)Embeddings.FoundryEmbedderFactory.TryCreate(configuration)
                ?? new DeterministicHashEmbedder());
        services.AddSingleton<IRuleAuthoringAgent, DeterministicMockAuthoringAgent>();
        return services;
    }
}
