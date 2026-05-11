using System.ClientModel;
using System.Diagnostics.CodeAnalysis;
using Azure.Identity;
using LambdaRag.Authoring.Embeddings;
using LambdaRag.Markup;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Responses;

namespace LambdaRag.Authoring.Editing;

/// <summary>
/// Reads <see cref="ComplianceEditorOptions"/> from <see cref="IConfiguration"/>
/// (with environment-variable fallback) and builds a real
/// <see cref="ComplianceEditor"/> backed by an <see cref="AIAgent"/>
/// constructed on the OpenAI **Responses API** (v2 SDK). The Azure
/// Foundry v2 endpoint speaks the Responses protocol at
/// <c>{endpoint}/openai/v1/responses</c>, so we point an
/// <see cref="ResponsesClient"/> at it directly instead of going
/// through <c>Azure.AI.OpenAI</c> (whose <c>GetOpenAIResponseClient</c>
/// helper has unstable signatures across the 2.x preview line that
/// conflict with our pinned <c>Microsoft.Agents.AI.OpenAI</c> bits).
///
/// Returns <c>null</c> when no editor endpoint+deployment is configured
/// — callers then fall back to
/// <see cref="DeterministicMockClauseRewriter"/> so unit tests and
/// offline runs do not need network access. Returns <c>null</c> when no
/// API key is configured: token-credential auth against the Responses
/// API is not yet supported in this SDK pin, so the mock fallback is
/// the safe choice instead of a runtime failure.
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
        var endpoint = Resolve(configuration, EditEndpointKey, EditEndpointVar)
                       ?? Resolve(configuration, FoundryEmbedderFactory.EndpointKey, FoundryEmbedderFactory.EndpointVar);
        var deployment = Resolve(configuration, EditDeploymentKey, EditDeploymentVar);
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(deployment))
            return null;

        var apiKey = Resolve(configuration, EditApiKeyKey, EditApiKeyVar);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // No API key + no supported token-credential path for Responses
            // API in this SDK pin → fall back to the deterministic mock.
            return null;
        }

        var cacheDir = Resolve(configuration, EditCacheDirKey, EditCacheDirVar)
                       ?? Path.Combine("out", "rewrite-cache");

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(NormalizeEndpoint(endpoint)),
        };
        var openAi = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
        ResponsesClient responsesClient = openAi.GetResponsesClient();

        IChatClient chatClient = responsesClient.AsIChatClient(deployment);
        AIAgent agent = chatClient.AsAIAgent(
            instructions: ComplianceEditor.SystemPrompt,
            name: ComplianceEditor.AgentName);

        var options = new ComplianceEditorOptions
        {
            Endpoint = endpoint,
            Deployment = deployment,
            ApiKey = apiKey,
            CacheDir = cacheDir,
        };
        return new ComplianceEditor(agent, options);
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        endpoint = endpoint.TrimEnd('/');
        if (endpoint.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase)) return endpoint;
        if (endpoint.EndsWith("/openai", StringComparison.OrdinalIgnoreCase)) return endpoint + "/v1";
        return endpoint + "/openai/v1";
    }

    private static string? Resolve(IConfiguration? configuration, string key, string envVar)
    {
        var fromConfig = configuration?[key];
        if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig;
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }

    // Reserve a reference to Azure.Identity so the using stays valid;
    // a future Entra-ID-auth path will swap ApiKeyCredential for a
    // TokenCredential-backed pipeline policy.
    private static readonly Type _azureIdentityMarker = typeof(DefaultAzureCredential);
}
