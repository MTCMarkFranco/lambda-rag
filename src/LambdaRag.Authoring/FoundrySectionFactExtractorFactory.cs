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

        // Issue #181: fold deployment + region into the Locked Oracle
        // fingerprint so that swapping endpoints or regions loudly
        // invalidates the sidecar cache (via SectionFactSidecarMismatchException).
        var determinism = LockedOracleSettings.Default.WithRuntime(
            deploymentId: deployment,
            region: TryExtractRegion(endpoint));

        return new FoundrySectionFactExtractor(
            chatClient,
            modelId: deployment,
            log: logger,
            cacheDirOverride: cacheDirOverride,
            refresh: refresh,
            determinism: determinism);
    }

    /// <summary>
    /// Best-effort region extractor from an Azure OpenAI / Foundry endpoint.
    /// Recognizes hostnames like <c>foundry-cc-canada.services.ai.azure.com</c>
    /// or <c>my-account-eastus.openai.azure.com</c> and returns the trailing
    /// hyphen segment ("canada", "eastus"). Falls back to the full host if no
    /// pattern matches. Never throws — a null endpoint returns null.
    /// </summary>
    public static string? TryExtractRegion(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return endpoint;
        var host = uri.Host;
        var firstDot = host.IndexOf('.');
        var account = firstDot < 0 ? host : host[..firstDot];
        var lastHyphen = account.LastIndexOf('-');
        return lastHyphen > 0 && lastHyphen < account.Length - 1
            ? account[(lastHyphen + 1)..]
            : host;
    }

    private static string? Resolve(IConfiguration? configuration, string key, string envVar)
    {
        var fromConfig = configuration?[key];
        if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig;
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }
}
