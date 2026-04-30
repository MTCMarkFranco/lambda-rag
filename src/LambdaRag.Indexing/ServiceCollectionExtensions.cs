using LambdaRag.Indexing.Abstractions;
using LambdaRag.Indexing.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace LambdaRag.Indexing;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory implementations of the indexing layer. For
    /// production deployments swap <see cref="IRuleSemanticIndex"/> with
    /// the Azure AI Search-backed implementation.
    /// </summary>
    public static IServiceCollection AddLambdaRagIndexing(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryRuleSignatureIndex>();
        services.AddSingleton<IRuleSignatureIndex>(sp => sp.GetRequiredService<InMemoryRuleSignatureIndex>());
        services.AddSingleton<LambdaRag.Core.Abstractions.ICandidateRuleFilter>(
            sp => sp.GetRequiredService<InMemoryRuleSignatureIndex>());
        services.AddSingleton<IRuleSemanticIndex, InMemoryRuleSemanticIndex>();
        services.AddTransient<IDocumentSectionIndex, InMemoryDocumentSectionIndex>();
        return services;
    }
}
