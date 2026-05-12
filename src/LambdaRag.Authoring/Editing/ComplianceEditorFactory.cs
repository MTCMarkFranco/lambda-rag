using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using LambdaRag.Authoring.Embeddings;
using LambdaRag.Markup;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace LambdaRag.Authoring.Editing;

/// <summary>
/// Reads <see cref="ComplianceEditorOptions"/> from <see cref="IConfiguration"/>
/// (with environment-variable fallback) and builds a real
/// <see cref="ComplianceEditor"/> backed by an <see cref="AIAgent"/>
/// constructed on the Azure OpenAI endpoint via <c>Azure.AI.OpenAI</c>'s
/// <see cref="AzureOpenAIClient"/> — the same SDK + auth strategy the
/// embedder uses (<see cref="FoundryEmbedderFactory"/>). When no API key
/// is supplied we authenticate with <see cref="DefaultAzureCredential"/>
/// (Entra ID), matching the rest of the LambdaRag stack.
///
/// The agent is constructed against <c>GetChatClient(deployment)</c>
/// (which Azure routes transparently to the appropriate model endpoint
/// — Responses-capable models like <c>gpt-5.1</c> are served through the
/// same client surface in <c>Azure.AI.OpenAI</c> 2.1.0).
///
/// Returns <c>null</c> when no editor endpoint+deployment is configured
/// — callers then fall back to <see cref="NoopClauseRewriter"/> so the
/// markup pipeline emits Comment annotations only.
/// </summary>
[Experimental("OPENAI001")]
public static class ComplianceEditorFactory
{
    public const string EditEndpointKey = "LambdaRag:Foundry:Edit:Endpoint";
    public const string EditDeploymentKey = "LambdaRag:Foundry:Edit:Deployment";
    public const string EditApiKeyKey = "LambdaRag:Foundry:Edit:ApiKey";
    public const string EditCacheDirKey = "LambdaRag:Foundry:Edit:CacheDir";

    public const string EditEndpointVar = "LAMBDA_RAG_FOUNDRY_EDIT_ENDPOINT";
    public const string EditDeploymentVar = "LAMBDA_RAG_FOUNDRY_EDIT_DEPLOYMENT";
    public const string EditApiKeyVar = "LAMBDA_RAG_FOUNDRY_EDIT_API_KEY";
    public const string EditCacheDirVar = "LAMBDA_RAG_REWRITE_CACHE";

    public static IClauseRewriter? TryCreate(IConfiguration? configuration)
    {
        // The editor endpoint defaults to the embedder endpoint when the
        // Edit:Endpoint override isn't set — typical deployment story is
        // one Foundry project hosting both an embedding deployment and a
        // chat/Responses deployment.
        var endpoint = Resolve(configuration, EditEndpointKey, EditEndpointVar)
                       ?? Resolve(configuration, FoundryEmbedderFactory.EndpointKey, FoundryEmbedderFactory.EndpointVar);
        var deployment = Resolve(configuration, EditDeploymentKey, EditDeploymentVar);
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(deployment))
            return null;

        // API key is optional. When absent we fall back to
        // DefaultAzureCredential (Entra ID) — same auth path as the
        // embedder. The developer's az-cli login (or a managed identity
        // in CI / production) supplies the token.
        var apiKey = Resolve(configuration, EditApiKeyKey, EditApiKeyVar);
        var azureClient = string.IsNullOrWhiteSpace(apiKey)
            ? new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
            : new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

#pragma warning disable OPENAI001
        IChatClient chatClient = azureClient.GetChatClient(deployment).AsIChatClient();
#pragma warning restore OPENAI001
        AIAgent agent = chatClient.AsAIAgent(
            instructions: ComplianceEditor.SystemPrompt,
            name: ComplianceEditor.AgentName);

        var cacheDir = Resolve(configuration, EditCacheDirKey, EditCacheDirVar)
                       ?? Path.Combine("out", "rewrite-cache");

        var options = new ComplianceEditorOptions
        {
            Endpoint = endpoint,
            Deployment = deployment,
            ApiKey = apiKey,
            CacheDir = cacheDir,
        };
        return new ComplianceEditor(agent, options);
    }

    private static string? Resolve(IConfiguration? configuration, string key, string envVar)
    {
        var fromConfig = configuration?[key];
        if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig;
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }
}
