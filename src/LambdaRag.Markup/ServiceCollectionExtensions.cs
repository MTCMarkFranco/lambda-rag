using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LambdaRag.Markup;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLambdaRagMarkup(this IServiceCollection services)
    {
        services.AddSingleton<OpenXmlMarkupService>();
        // Default rewriter is the no-op so historical callers (and the
        // determinism golden tests) keep their existing Comment-only
        // behavior. Replace this registration via DI in projects that
        // wire in an AI-backed IClauseRewriter (e.g. LambdaRag.Authoring's
        // ComplianceEditor).
        services.TryAddSingleton<IClauseRewriter>(NoopClauseRewriter.Instance);
        return services;
    }
}
