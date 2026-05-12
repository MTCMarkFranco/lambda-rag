using LambdaRag.Markup;
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

        // Bind IClauseRewriter: real Responses-API ComplianceEditor when
        // LambdaRag:Foundry:Edit:* is configured, Noop otherwise. We do
        // NOT fall back to DeterministicMockClauseRewriter here — that
        // mock returns the rule's remediation text verbatim as the new
        // clause body, which would inject rule guidance into the
        // document. Noop returns null so the markup pipeline keeps the
        // historical Comment-only behavior when no real LLM editor is
        // configured (see issue #90).
#pragma warning disable OPENAI001
        services.AddSingleton<IClauseRewriter>(_ =>
            Editing.ComplianceEditorFactory.TryCreate(configuration)
                ?? (IClauseRewriter)NoopClauseRewriter.Instance);
#pragma warning restore OPENAI001
        return services;
    }
}
