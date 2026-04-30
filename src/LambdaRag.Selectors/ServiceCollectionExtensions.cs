using LambdaRag.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace LambdaRag.Selectors;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="JsonPathSelectorMatcher"/> as the singleton
    /// <see cref="ISelectorMatcher"/> implementation.
    /// Requires <c>AddLogging()</c> to have been called on the same container.
    /// </summary>
    public static IServiceCollection AddLambdaRagSelectors(this IServiceCollection services)
    {
        services.AddSingleton<ISelectorMatcher, JsonPathSelectorMatcher>();
        return services;
    }
}
