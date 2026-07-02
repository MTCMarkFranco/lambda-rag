using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using LambdaRag.Authoring.Editing;
using LambdaRag.Core.Facts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LambdaRag.Authoring;

/// <summary>
/// Pillar 12 (#153) — builds a <see cref="FoundrySectionFactExtractor"/>
/// from <see cref="IConfiguration"/>. Reuses the same edit-endpoint keys as
/// <see cref="FoundryRuleAuthoringAgentFactory"/> so the two Foundry-backed
/// components stay auth-symmetric.
///
/// Returns <c>null</c> when the required config values are missing — the
/// caller then either falls back to a mock (unit tests) or reports the
/// missing configuration to the operator (CLI).
/// </summary>
[Experimental("OPENAI001")]
public static class FoundrySectionFactExtractorFactory
{
    public static IFactExtractor? TryCreate(
        IConfiguration? configuration,
        ILoggerFactory? loggerFactory = null,
        string? cacheDirOverride = null,
        bool refresh = false)
    {
        var endpoint = Resolve(configuration,
                                ComplianceEditorFactory.EditEndpointKey,
                                ComplianceEditorFactory.EditEndpointVar);
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

        var logger = loggerFactory?.CreateLogger<FoundrySectionFactExtractor>();
        return new FoundrySectionFactExtractor(
            chatClient,
            modelId: deployment,
            log: logger,
            cacheDirOverride: cacheDirOverride,
            refresh: refresh);
    }

    private static string? Resolve(IConfiguration? configuration, string key, string envVar)
    {
        var fromConfig = configuration?[key];
        if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig;
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }
}
