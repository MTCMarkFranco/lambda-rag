using LambdaRag.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace LambdaRag.Parsing;

/// <summary>DI registration for the LambdaRag.Parsing module.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PdfParser"/>, <see cref="DocxParser"/>,
    /// <see cref="MarkdownParser"/> (each as a singleton
    /// <see cref="IDocumentParser"/>), and the <see cref="ParserRegistry"/>
    /// that selects among them.
    /// </summary>
    public static IServiceCollection AddLambdaRagParsing(this IServiceCollection services)
    {
        services.AddSingleton<IDocumentParser, PdfParser>();
        services.AddSingleton<IDocumentParser, DocxParser>();
        services.AddSingleton<IDocumentParser, MarkdownParser>();
        services.AddSingleton<ParserRegistry>();
        return services;
    }
}
