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
        services.AddSingleton<IRuleEmbedder, DeterministicHashEmbedder>();
        services.AddSingleton<IRuleAuthoringAgent, DeterministicMockAuthoringAgent>();
        return services;
    }
}
