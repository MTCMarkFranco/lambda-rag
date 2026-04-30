using LambdaRag.Core.Abstractions;
using LambdaRag.Projection.Projectors;
using Microsoft.Extensions.DependencyInjection;

namespace LambdaRag.Projection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLambdaRagProjection(this IServiceCollection services)
    {
        services.AddSingleton<DeterministicContractProjector>();
        services.AddSingleton<IDocumentProjector>(sp => sp.GetRequiredService<DeterministicContractProjector>());
        return services;
    }
}
