using LambdaRag.Evaluation.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace LambdaRag.Evaluation;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLambdaRagEvaluation(this IServiceCollection services)
    {
        services.AddSingleton<EvaluationService>();
        return services;
    }
}
