using FluentAssertions;
using LambdaRag.Authoring.Embeddings;
using LambdaRag.Core.Semantic;
using Microsoft.Extensions.AI;
using Xunit;

namespace LambdaRag.UnitTests.Authoring.Embeddings;

public class FileBackedEmbeddingCacheTests
{
    [Fact]
    public void NormalizeText_collapses_whitespace_and_lowercases()
    {
        FileBackedEmbeddingCache.NormalizeText("  Hello\t World  ").Should().Be("hello world");
        FileBackedEmbeddingCache.NormalizeText("HELLO\nWORLD").Should().Be("hello world");
        FileBackedEmbeddingCache.NormalizeText("").Should().Be("");
    }

    [Fact]
    public void ComputeKey_is_stable_across_whitespace_and_case()
    {
        var a = FileBackedEmbeddingCache.ComputeKey("model", "Works Made For Hire");
        var b = FileBackedEmbeddingCache.ComputeKey("model", " works  made for hire ");
        a.Should().Be(b);
    }

    [Fact]
    public void ComputeKey_differs_per_model()
    {
        FileBackedEmbeddingCache.ComputeKey("model-a", "x")
            .Should().NotBe(FileBackedEmbeddingCache.ComputeKey("model-b", "x"));
    }

    [Fact]
    public void Roundtrip_persists_vector_byte_for_byte()
    {
        var root = NewTempDir();
        var cache = new FileBackedEmbeddingCache(root, "test-model", 4);
        var vec = new float[] { 0.1f, -0.2f, 0.3f, -0.4f };

        cache.Write("hello", vec);
        cache.TryRead("hello", out var read).Should().BeTrue();
        read.Should().Equal(vec);
    }

    [Fact]
    public void TryRead_miss_returns_false_without_throwing()
    {
        var cache = new FileBackedEmbeddingCache(NewTempDir(), "m", 4);
        cache.TryRead("nope", out var v).Should().BeFalse();
        v.Should().BeEmpty();
    }

    [Fact]
    public void Write_with_wrong_dim_throws()
    {
        var cache = new FileBackedEmbeddingCache(NewTempDir(), "m", 4);
        var act = () => cache.Write("k", new float[] { 1f, 2f });
        act.Should().Throw<InvalidOperationException>();
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lambdarag-cache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public class AzureFoundryEmbeddingProviderTests
{
    [Fact]
    public async Task Cache_hit_skips_underlying_generator()
    {
        var generator = new RecordingEmbeddingGenerator(new float[] { 1f, 0f });
        var cacheDir = Path.Combine(Path.GetTempPath(), "lambdarag-prov-tests", Guid.NewGuid().ToString("N"));
        var cache = new FileBackedEmbeddingCache(cacheDir, "test-model", 2);
        cache.Write("seeded", new float[] { 0.5f, 0.5f });

        var provider = new AzureFoundryEmbeddingProvider(generator, "test-model", 2, cache);

        var v = await provider.EmbedAsync("seeded");
        v.Should().Equal(new[] { 0.5f, 0.5f });
        generator.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Cache_miss_calls_generator_then_persists_normalised_vector()
    {
        var generator = new RecordingEmbeddingGenerator(new float[] { 3f, 4f });
        var cacheDir = Path.Combine(Path.GetTempPath(), "lambdarag-prov-tests", Guid.NewGuid().ToString("N"));
        var cache = new FileBackedEmbeddingCache(cacheDir, "test-model", 2);
        var provider = new AzureFoundryEmbeddingProvider(generator, "test-model", 2, cache);

        var v = await provider.EmbedAsync("first");
        v.Should().HaveCount(2);
        v[0].Should().BeApproximately(0.6f, 1e-6f);
        v[1].Should().BeApproximately(0.8f, 1e-6f);
        generator.CallCount.Should().Be(1);

        cache.TryRead("first", out var persisted).Should().BeTrue();
        persisted[0].Should().BeApproximately(0.6f, 1e-6f);

        await provider.EmbedAsync("first");
        generator.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Generator_dimension_mismatch_throws()
    {
        var generator = new RecordingEmbeddingGenerator(new float[] { 1f, 2f, 3f });
        var provider = new AzureFoundryEmbeddingProvider(generator, "m", dimensions: 2);
        var act = () => provider.EmbedAsync("x");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class RecordingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly float[] _vec;
        public int CallCount { get; private set; }
        public RecordingEmbeddingGenerator(float[] vec) => _vec = vec;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var list = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values)
                list.Add(new Embedding<float>(_vec));
            return Task.FromResult(list);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

public class SemanticVectorStoreSnapshotTests
{
    [Fact]
    public void Roundtrip_preserves_keys_and_vectors_via_cache()
    {
        var root = Path.Combine(Path.GetTempPath(), "lambdarag-snap-tests", Guid.NewGuid().ToString("N"));
        var cache = new FileBackedEmbeddingCache(root, "test-model", 3);

        var sectionVec = new float[] { 1f, 0f, 0f };
        var conceptVec = new float[] { 0f, 1f, 0f };
        cache.Write("s_001", sectionVec);
        cache.Write("works made for hire", conceptVec);

        var store = new InMemorySemanticVectorStore("test-model", 3);
        store.AddSection("s_001", sectionVec);
        store.AddConcept("works made for hire", conceptVec);

        var snapPath = Path.Combine(root, "snapshot.json");
        SemanticVectorStoreSnapshot.WriteJson(store, snapPath);
        File.Exists(snapPath).Should().BeTrue();

        var loaded = SemanticVectorStoreSnapshot.ReadJson(snapPath, cache);
        loaded.ModelId.Should().Be("test-model");
        loaded.Dimensions.Should().Be(3);
        loaded.TryGetSection("s_001", out var s).Should().BeTrue();
        s.Should().Equal(sectionVec);
        loaded.TryGetConcept("works made for hire", out var c).Should().BeTrue();
        c.Should().Equal(conceptVec);
    }

    [Fact]
    public void Read_throws_loud_when_cache_missing_a_referenced_vector()
    {
        var root = Path.Combine(Path.GetTempPath(), "lambdarag-snap-tests", Guid.NewGuid().ToString("N"));
        var cache = new FileBackedEmbeddingCache(root, "test-model", 3);

        var store = new InMemorySemanticVectorStore("test-model", 3);
        store.AddConcept("missing", new float[] { 1f, 2f, 3f });

        var snapPath = Path.Combine(root, "snapshot.json");
        SemanticVectorStoreSnapshot.WriteJson(store, snapPath);

        var act = () => SemanticVectorStoreSnapshot.ReadJson(snapPath, cache);
        act.Should().Throw<InvalidOperationException>().WithMessage("*missing*");
    }
}
