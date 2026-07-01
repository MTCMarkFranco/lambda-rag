using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using LambdaRag.Authoring.Editing;
using LambdaRag.Authoring.Embeddings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LambdaRag.Authoring;

/// <summary>
/// Builds a <see cref="FoundryRuleAuthoringAgent"/> from
/// <see cref="IConfiguration"/>. Mirrors the client-construction pattern of
/// <see cref="ComplianceEditorFactory"/> (same endpoint keys, same
/// <see cref="DefaultAzureCredential"/> fallback, same
/// <c>OPENAI001</c> preview-warning suppression) so the two Foundry-backed
/// components stay auth-symmetric.
///
/// Returns <c>null</c> when the required config values
/// (<c>LambdaRag:Foundry:Edit:Endpoint</c> and
/// <c>LambdaRag:Foundry:Edit:Deployment</c>) are missing — the caller then
/// falls back to <see cref="DeterministicMockAuthoringAgent"/>.
/// </summary>
[Experimental("OPENAI001")]
public static class FoundryRuleAuthoringAgentFactory
{
    // Reuse the ComplianceEditor config keys — the same Foundry deployment
    // handles both authoring and editing in every environment we ship. If
    // an ops team later wants to split them, adding
    // LambdaRag:Foundry:Authoring:* keys with the same lookup order is a
    // two-line change; not doing it now avoids config sprawl.

    public static IRuleAuthoringAgent? TryCreate(
        IConfiguration? configuration,
        IRuleEmbedder embedder,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(embedder);

        var endpoint = Resolve(configuration,
                                ComplianceEditorFactory.EditEndpointKey,
                                ComplianceEditorFactory.EditEndpointVar)
                       ?? Resolve(configuration,
                                   FoundryEmbedderFactory.EndpointKey,
                                   FoundryEmbedderFactory.EndpointVar);
        var deployment = Resolve(configuration,
                                  ComplianceEditorFactory.EditDeploymentKey,
                                  ComplianceEditorFactory.EditDeploymentVar);
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(deployment))
            return null;

        var apiKey = Resolve(configuration,
                              ComplianceEditorFactory.EditApiKeyKey,
                              ComplianceEditorFactory.EditApiKeyVar);
        var azureClient = string.IsNullOrWhiteSpace(apiKey)
            ? new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
            : new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

#pragma warning disable OPENAI001
        IChatClient chatClient = azureClient.GetChatClient(deployment).AsIChatClient();
#pragma warning restore OPENAI001

        var logger = loggerFactory?.CreateLogger<FoundryRuleAuthoringAgent>();
        return new FoundryRuleAuthoringAgent(chatClient, embedder, logger);
    }

    private static string? Resolve(IConfiguration? configuration, string key, string envVar)
    {
        var fromConfig = configuration?[key];
        if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig;
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }
}
