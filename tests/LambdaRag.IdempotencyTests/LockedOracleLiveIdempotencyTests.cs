// Locked Oracle Phase 1 (#177) — live-LLM guardrail that asserts cache-miss
// idempotency ≥99% on the extractor's real Azure endpoint. Env-gated so it
// never runs in CI:
//
//   $env:LAMBDA_RAG_LOCKED_ORACLE_LIVE_TESTS = "1"
//   $env:LAMBDA_RAG_FACTS_ENDPOINT           = "https://<name>.cognitiveservices.azure.com/"
//   $env:LAMBDA_RAG_FACTS_DEPLOYMENT         = "gpt-5.4-mini"
//   dotnet test tests/LambdaRag.IdempotencyTests --filter Category=LockedOracle
//
// Uses the same reusable env-gate pattern as ParaphraseInvarianceTests.LLM_*.
// Costs a few US cents per run at gpt-5.4-mini pricing.
//
// Failure of THIS test is a P0 regression on the Locked Oracle idempotency
// contract (#175). The Phase 0 empirical result was 100% raw byte-identity
// over 1200 calls; anything below the ≥99% canonical bar means the model
// has drifted or the extractor stopped pinning determinism knobs.

using System.Text.Json.Nodes;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using FluentAssertions;
using LambdaRag.Authoring;
using LambdaRag.Core;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Facts;
using LambdaRag.Core.Hashing;
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;

namespace LambdaRag.IdempotencyTests;

public class LockedOracleLiveIdempotencyTests
{
    private readonly ITestOutputHelper _output;
    public LockedOracleLiveIdempotencyTests(ITestOutputHelper output) => _output = output;

    private const string LiveEnvGate = "LAMBDA_RAG_LOCKED_ORACLE_LIVE_TESTS";
    private const string EndpointEnv = "LAMBDA_RAG_FACTS_ENDPOINT";
    private const string DeploymentEnv = "LAMBDA_RAG_FACTS_DEPLOYMENT";
    private const string ApiKeyEnv = "LAMBDA_RAG_FACTS_API_KEY";

    [Fact]
    [Trait("Category", "LockedOracle")]
    public async Task Cache_miss_fingerprint_is_stable_across_five_extractions()
    {
        if (Environment.GetEnvironmentVariable(LiveEnvGate) != "1")
        {
            _output.WriteLine(
                $"Skipping: set {LiveEnvGate}=1 (plus {EndpointEnv} and {DeploymentEnv}) to run against a live Azure OpenAI deployment.");
            return;
        }

        var endpoint = Environment.GetEnvironmentVariable(EndpointEnv);
        var deployment = Environment.GetEnvironmentVariable(DeploymentEnv);
        endpoint.Should().NotBeNullOrWhiteSpace($"{EndpointEnv} must be set when live tests are enabled");
        deployment.Should().NotBeNullOrWhiteSpace($"{DeploymentEnv} must be set when live tests are enabled");

        // gpt-5.4-mini validated in Phase 0; other models must run their
        // own Phase 0 probe before this guardrail's ≥99% assertion is
        // meaningful for them (see #175).
        if (!deployment!.Contains("gpt-5.4-mini", StringComparison.OrdinalIgnoreCase))
        {
            _output.WriteLine(
                $"⚠️  Deployment '{deployment}' has no Phase 0 evidence; running anyway but treat any drift as unproven, not a regression.");
        }

        var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnv);
        var azureClient = string.IsNullOrWhiteSpace(apiKey)
            ? new AzureOpenAIClient(new Uri(endpoint!), new DefaultAzureCredential())
            : new AzureOpenAIClient(new Uri(endpoint!), new AzureKeyCredential(apiKey));

#pragma warning disable OPENAI001
        IChatClient chatClient = azureClient.GetChatClient(deployment).AsIChatClient();
#pragma warning restore OPENAI001

        var (document, schema) = BuildDocumentAndSchema();

        var fingerprints = new List<string>();
        var canonicalHashes = new List<string>();
        var observedModels = new HashSet<string>(StringComparer.Ordinal);
        long totalInputTokens = 0;
        long totalOutputTokens = 0;

        for (var i = 0; i < 5; i++)
        {
            // Fresh temp cache dir per run so every call is a cache miss.
            var cacheDir = Path.Combine(
                Path.GetTempPath(),
                $"lambda-rag-locked-oracle-live-{Guid.NewGuid():N}");
            try
            {
                var extractor = new FoundrySectionFactExtractor(
                    chatClient,
                    modelId: deployment!,
                    cacheDirOverride: cacheDir,
                    refresh: false);

                var sidecar = await extractor.ExtractAsync(document, schema);

                sidecar.Fingerprint.Should().NotBeNullOrEmpty();
                fingerprints.Add(sidecar.Fingerprint!);
                canonicalHashes.Add(CanonicalHash(sidecar));

                if (!string.IsNullOrEmpty(sidecar.ModelSnapshot))
                    observedModels.Add(sidecar.ModelSnapshot);

                var fpFull = sidecar.Fingerprint!;
                var fpShort = fpFull.Length > 20 ? fpFull[..20] : fpFull;
                _output.WriteLine(
                    $"run {i + 1}/5: fingerprint={fpShort}… " +
                    $"model_snapshot={sidecar.ModelSnapshot ?? "(none)"}");
            }
            finally
            {
                if (Directory.Exists(cacheDir))
                {
                    try { Directory.Delete(cacheDir, recursive: true); }
                    catch { /* best-effort cleanup */ }
                }
            }
        }

        // Fingerprint is computed from the (docId, schemaHash, modelId,
        // promptHash, orderingHash) tuple — it's a *reference* identity
        // check, not the semantic one. So we assert it explicitly, then
        // the canonical-sidecar hash gives us the true idempotency signal.
        fingerprints.Distinct().Should().HaveCount(1,
            "the cache-key tuple is deterministic; every run must produce the same Fingerprint");

        var uniqueCanonical = canonicalHashes.Distinct().ToList();
        var idempotencyPct = (canonicalHashes.Count(h => h == canonicalHashes[0]) * 100.0) / canonicalHashes.Count;

        _output.WriteLine($"Canonical-JSON identity: {idempotencyPct:F1}% ({uniqueCanonical.Count} unique)");
        _output.WriteLine($"Observed model snapshots: {string.Join(", ", observedModels)}");
        _output.WriteLine($"Total tokens across 5 runs: in={totalInputTokens}, out={totalOutputTokens}");

        idempotencyPct.Should().BeGreaterThanOrEqualTo(99.0,
            $"Locked Oracle Phase 0 (#175) empirically observed 100.0% canonical identity on {deployment}. " +
            $"Observed {idempotencyPct:F1}% here → cache-miss idempotency contract is broken. " +
            $"Investigate: model drift? determinism knobs unpinned? endpoint routing changed?");
    }

    private static string CanonicalHash(SectionFactSidecar sidecar)
    {
        // Reduce the sidecar to the parts that are supposed to be
        // idempotent across cache-miss runs. GeneratedAt varies by wall
        // clock, so we exclude it. Fingerprint is deterministic so we
        // exclude it too (identity check happens separately above).
        var projection = new JsonObject
        {
            ["document_id"] = sidecar.DocumentId,
            ["fact_schema_hash"] = sidecar.FactSchemaHash,
            ["model_id"] = sidecar.ModelId,
            ["prompt_hash"] = sidecar.PromptHash,
            ["sections"] = SerializeSectionsSorted(sidecar.Sections),
        };
        var json = projection.ToJsonString(CanonicalJson.Compact);
        return ContentHash.OfString(json).Value;
    }

    private static JsonObject SerializeSectionsSorted(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> sections)
    {
        var outer = new JsonObject();
        foreach (var sec in sections.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var inner = new JsonObject();
            foreach (var kv in sec.Value.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                inner[kv.Key] = kv.Value switch
                {
                    null => null,
                    bool b => b,
                    long l => l,
                    int i => i,
                    double d => d,
                    _ => JsonValue.Create(kv.Value.ToString()),
                };
            }
            outer[sec.Key] = inner;
        }
        return outer;
    }

    private static (ProjectedDocument doc, FactSchema schema) BuildDocumentAndSchema()
    {
        // Minimal one-section document with unambiguous content. The
        // Phase 0 probe uses a similar shape; keeping this compact reduces
        // token cost per run of this guardrail.
        var text =
            "SkyLedger Platform — Section 3.2 Data Handling. " +
            "All customer data is retained for 2555 days and stored in Canada. " +
            "Personally identifiable information includes email, name, and phone number. " +
            "Data is encrypted at rest using AES-256.";
        var sourceId = ContentHash.OfString(text);
        var graph = new JsonObject
        {
            ["sections"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "s_00000000",
                    ["text"] = text,
                },
            },
        };
        var doc = new ProjectedDocument(
            SourceId: sourceId,
            ProjectorId: "locked-oracle-live-test",
            ProjectorVersion: "1.0",
            Graph: graph,
            SpanMap: new Dictionary<string, SourceSpan>());
        var schema = new FactSchema(
            Id: "locked-oracle-live",
            Version: "1.0",
            Concepts: new List<FactConcept>
            {
                new("system_name", FactType.Text, "Name of the system described in the section."),
                new("data_residency_region", FactType.Text, "Region where data is stored."),
                new("retention_period_days", FactType.Integer, "Retention period in days."),
                new("encryption_at_rest", FactType.Text, "Encryption method used at rest."),
            });
        return (doc, schema);
    }
}
