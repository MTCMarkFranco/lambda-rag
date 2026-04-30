using LambdaRag.Persistence.Interfaces;
using LambdaRag.Persistence.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace LambdaRag.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLambdaRagPersistence(
        this IServiceCollection services,
        Action<LambdaRagPersistenceOptions>? configure = null)
    {
        services.AddOptions<LambdaRagPersistenceOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.AddSingleton<LambdaRagPersistenceInitializer>();
        services.AddSingleton<ISourceDocumentStore, SqliteSourceDocumentStore>();
        services.AddSingleton<IRuleSetStore, SqliteRuleSetStore>();
        services.AddSingleton<IProjectionCache, SqliteProjectionCache>();
        services.AddSingleton<IEvaluationStore, SqliteEvaluationStore>();

        return services;
    }
}
