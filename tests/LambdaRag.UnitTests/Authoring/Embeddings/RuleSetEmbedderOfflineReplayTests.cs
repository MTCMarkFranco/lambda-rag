using System.Linq;
using FluentAssertions;
using LambdaRag.Authoring;
using LambdaRag.Authoring.Embeddings;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using System.Text.Json.Nodes;
using Xunit;

namespace LambdaRag.UnitTests.Authoring.Embeddings;

/// <summary>
/// Pins the determinism contract that the embedder pipeline must honour:
///   • running the embedder twice on the same ruleset produces byte-for-byte
///     identical vectors and an identical key set;
///   • once vectors are cached, a replay reading them from
///     <see cref="FileBackedEmbeddingCache"/> through
///     <see cref="AzureFoundryEmbeddingProvider"/> issues *zero* calls to
///     the underlying generator (CallCount == 0). This is what makes CI
///     runs offline and ensures verdict reproducibility from snapshot.
/// </summary>
public class RuleSetEmbedderOfflineReplayTests
{
    [Fact]
    public async Task EmbedAsync_twice_yields_byte_identical_vectors()
    {
        var ruleset = MakeRuleSet();
        var embedder = new DeterministicHashEmbedder();
        var sut = new RuleSetEmbedder(embedder);

        var first = await sut.EmbedAsync(ruleset);
        var second = await sut.EmbedAsync(ruleset);

        foreach (var key in new[]
        {
            RuleSetEmbedder.RuleDescriptionKey("R1"),
            "intellectual property infringement",
            "works made for hire",
            "hereby assigns",
        })
        {
            first.TryGetConcept(key, out var a).Should().BeTrue($"key '{key}' must be present after first run");
            second.TryGetConcept(key, out var b).Should().BeTrue($"key '{key}' must be present after second run");
            a.ToArray().Should().BeEquivalentTo(b.ToArray(), $"vectors for '{key}' must be byte-identical across runs");
        }
    }

    [Fact]
    public async Task Cache_warm_replay_calls_underlying_generator_zero_times()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "lambda-rag-replay-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string modelId = "mock-model/2";
            var cache = new FileBackedEmbeddingCache(cacheDir, modelId, dimensions: 2);

            // Warm pass: a recording generator returns a fixed vector and the
            // provider persists it through the cache.
            var warmGenerator = new RecordingEmbeddingGenerator(new float[] { 3f, 4f });
            var warmProvider = new AzureFoundryEmbeddingProvider(warmGenerator, modelId, dimensions: 2, cache);
            _ = await warmProvider.EmbedAsync("provider shall maintain ISO 27001 controls");
            _ = await warmProvider.EmbedAsync("works made for hire");
            warmGenerator.CallCount.Should().Be(2, "warm pass must call the generator once per unique text");

            // Replay pass: brand-new provider + brand-new generator pointed at
            // the same on-disk cache. No calls expected — every read should
            // hit the persisted vector.
            var replayGenerator = new RecordingEmbeddingGenerator(new float[] { 99f, 99f });
            var replayProvider = new AzureFoundryEmbeddingProvider(replayGenerator, modelId, dimensions: 2, cache);
            var v1 = await replayProvider.EmbedAsync("provider shall maintain ISO 27001 controls");
            var v2 = await replayProvider.EmbedAsync("works made for hire");
            replayGenerator.CallCount.Should().Be(0, "replay must be served entirely from the cache");

            // And the vectors hydrated from cache must be byte-equal to the
            // L2-normalised warm-pass output (3,4) / 5 = (0.6, 0.8).
            v1.Should().BeEquivalentTo(new float[] { 0.6f, 0.8f });
            v2.Should().BeEquivalentTo(new float[] { 0.6f, 0.8f });
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    private static RuleSet MakeRuleSet() => new(
        Id: "rs_test_offline",
        Version: "1.0.0",
        Domain: "test",
        PublishedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Rules: new[]
        {
            new Rule(
                Id: "R1",
                Version: "1.0.0",
                NaturalLanguage: "Indemnification must cover IP infringement claims.",
                Lambda:
                    "SemanticFunctions.ContainsMeaning(input1.id, \"intellectual property infringement\", 0.78) && " +
                    "SemanticFunctions.MatchesAnyMeaning(input1.id, \"works made for hire|hereby assigns\", 0.78)",
                AppliesToSchema: new JsonObject(),
                Selector: new PathSelector("$.sections[*]"),
                Severity: RuleSeverity.Violation,
                SourceSpan: new SourceSpan("test", 0, 1, 1, null),
                EvidenceQuote: string.Empty,
                Metadata: new Dictionary<string, string>()),
        },
        Metadata: new Dictionary<string, string>());

    /// <summary>
    /// Drop-in copy of the recorder used by EmbeddingProviderTests, kept
    /// local so this fixture can stand alone if those tests move.
    /// </summary>
    private sealed class RecordingEmbeddingGenerator
        : Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>
    {
        private readonly float[] _vec;
        public int CallCount { get; private set; }

        public RecordingEmbeddingGenerator(float[] vec) => _vec = vec;

        public Task<Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            Microsoft.Extensions.AI.EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount += values.Count();
            var list = values
                .Select(_ => new Microsoft.Extensions.AI.Embedding<float>(_vec.AsMemory()))
                .ToList();
            return Task.FromResult(new Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>(list));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
