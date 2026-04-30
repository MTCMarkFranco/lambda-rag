using Microsoft.Extensions.DependencyInjection;

namespace LambdaRag.Markup;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLambdaRagMarkup(this IServiceCollection services)
    {
        services.AddSingleton<OpenXmlMarkupService>();
        return services;
    }
}
