using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LambdaRag.Authoring;
using LambdaRag.Authoring.Embeddings;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Semantic;
using Xunit;

namespace LambdaRag.UnitTests.Authoring.Embeddings;

/// <summary>
/// Tests for issue #69: <see cref="JitEmbeddingSemanticVectorStore"/> must
/// silently embed sections that the projector skipped (or that arrived
/// later than projection time) instead of throwing
/// <c>"no precomputed vector for section ..."</c> at evaluation time.
///
/// The decorator preserves determinism by delegating to the same
/// <see cref="IRuleEmbedder"/> the rest of the pipeline uses; replay-only
/// scenarios simply do not wrap their store, so the loud-fail behaviour of
/// <see cref="InMemorySemanticVectorStore"/> remains intact for audit.
/// </summary>
public class JitEmbeddingSemanticVectorStoreTests
{
    [Fact]
    public void TryGetSection_returns_inner_vector_without_calling_embedder_when_present()
    {
        var inner = new InMemorySemanticVectorStore("test", 32);
        var presentVec = new float[32];
        presentVec[0] = 1f;
        inner.AddSection("s1", presentVec);

        var counter = new CountingEmbedder(new DeterministicHashEmbedder());
        var sut = new JitEmbeddingSemanticVectorStore(inner, counter);
        sut.RegisterSectionText("s1", "should not be embedded — already present");

        sut.TryGetSection("s1", out var vec).Should().BeTrue();
        vec.Should().BeSameAs(presentVec);
        counter.CallCount.Should().Be(0);
        sut.JitEmbedCount.Should().Be(0);
    }

    [Fact]
    public void TryGetSection_jit_embeds_when_inner_misses_and_text_registered()
    {
        var inner = new InMemorySemanticVectorStore("test", 32);
        var counter = new CountingEmbedder(new DeterministicHashEmbedder());
        var sut = new JitEmbeddingSemanticVectorStore(inner, counter);
        sut.RegisterSectionText("s_jit", "Architecture must address shared responsibility model.");

        sut.TryGetSection("s_jit", out var vec).Should().BeTrue();
        vec.Should().NotBeNull();
        vec.Count.Should().Be(32);
        counter.CallCount.Should().Be(1);
        sut.JitEmbedCount.Should().Be(1);

        // Subsequent lookups read from the inner store and do not re-embed.
        sut.TryGetSection("s_jit", out var vec2).Should().BeTrue();
        vec2.Should().BeSameAs(vec);
        counter.CallCount.Should().Be(1);
    }

    [Fact]
    public void TryGetSection_returns_false_when_text_not_registered()
    {
        var inner = new InMemorySemanticVectorStore("test", 32);
        var counter = new CountingEmbedder(new DeterministicHashEmbedder());
        var sut = new JitEmbeddingSemanticVectorStore(inner, counter);

        sut.TryGetSection("s_unknown", out var vec).Should().BeFalse();
        vec.Should().BeNull();
        counter.CallCount.Should().Be(0);
        sut.JitEmbedCount.Should().Be(0);
    }

    [Fact]
    public void TryGetSection_returns_false_when_text_is_blank()
    {
        var inner = new InMemorySemanticVectorStore("test", 32);
        var counter = new CountingEmbedder(new DeterministicHashEmbedder());
        var sut = new JitEmbeddingSemanticVectorStore(inner, counter);
        sut.RegisterSectionText("s_blank", "   ");

        sut.TryGetSection("s_blank", out _).Should().BeFalse();
        counter.CallCount.Should().Be(0);
    }

    [Fact]
    public void RegisterSectionTexts_picks_up_every_id_text_pair_from_projection()
    {
        var doc = MakeDoc(("s1", "alpha"), ("s2", "beta"));
        var inner = new InMemorySemanticVectorStore("test", 32);
        var sut = new JitEmbeddingSemanticVectorStore(inner, new DeterministicHashEmbedder());
        sut.RegisterSectionTexts(doc);

        sut.TryGetSection("s1", out _).Should().BeTrue();
        sut.TryGetSection("s2", out _).Should().BeTrue();
    }

    [Fact]
    public void RegisterSectionTexts_falls_back_to_heading_when_body_text_is_blank()
    {
        // Heading-only sections (e.g. "8. Implementation View" with no body
        // text) must still resolve through the JIT path — see #69.
        var graph = (JsonObject)JsonNode.Parse("""
        {
          "sections": [
            { "id": "s_body", "heading": "Body section", "text": "real body text" },
            { "id": "s_head_only", "heading": "8. Implementation View", "text": "" }
          ]
        }
        """)!;
        var doc = new ProjectedDocument(
            SourceId: ContentHash.OfString("test-doc"),
            ProjectorId: "test-projector",
            ProjectorVersion: "1.0.0",
            Graph: graph,
            SpanMap: new Dictionary<string, SourceSpan>());

        var counter = new CountingEmbedder(new DeterministicHashEmbedder());
        var sut = new JitEmbeddingSemanticVectorStore(new InMemorySemanticVectorStore("t", 32), counter);
        sut.RegisterSectionTexts(doc);

        sut.TryGetSection("s_body", out _).Should().BeTrue();
        sut.TryGetSection("s_head_only", out _).Should().BeTrue();
        counter.CallCount.Should().Be(2);
    }

    [Fact]
    public void Concept_lookups_passthrough_to_inner_store()
    {
        var inner = new InMemorySemanticVectorStore("test", 32);
        var conceptVec = new float[32];
        conceptVec[5] = 0.5f;
        inner.AddConcept("c1", conceptVec);

        var sut = new JitEmbeddingSemanticVectorStore(inner, new DeterministicHashEmbedder());
        sut.TryGetConcept("c1", out var vec).Should().BeTrue();
        vec.Should().BeSameAs(conceptVec);
        sut.TryGetConcept("c-missing", out _).Should().BeFalse();
    }

    [Fact]
    public void Inmemory_store_alone_still_throws_for_missing_sections_in_replay_mode()
    {
        // Replay-only deployments do NOT wrap their store with the JIT
        // decorator, so missing vectors must continue to surface as a
        // hard failure (audit signal that the snapshot is out of sync).
        var bareStore = new InMemorySemanticVectorStore("test", 32);
        bareStore.TryGetSection("s_missing", out _).Should().BeFalse();
    }

    [Fact]
    public void Jit_embed_is_byte_identical_across_runs_for_same_text()
    {
        var embedder = new DeterministicHashEmbedder();

        var s1 = new JitEmbeddingSemanticVectorStore(new InMemorySemanticVectorStore("t", 32), embedder);
        s1.RegisterSectionText("s_idem", "shared responsibility model");
        s1.TryGetSection("s_idem", out var v1).Should().BeTrue();

        var s2 = new JitEmbeddingSemanticVectorStore(new InMemorySemanticVectorStore("t", 32), embedder);
        s2.RegisterSectionText("s_idem", "shared responsibility model");
        s2.TryGetSection("s_idem", out var v2).Should().BeTrue();

        v1.ToArray().Should().BeEquivalentTo(v2.ToArray());
    }

    private static ProjectedDocument MakeDoc(params (string id, string text)[] sections)
    {
        var arr = new JsonArray();
        foreach (var (id, text) in sections)
            arr.Add(new JsonObject { ["id"] = id, ["text"] = text });
        var graph = new JsonObject { ["sections"] = arr };
        return new ProjectedDocument(
            SourceId: ContentHash.OfString("test-doc"),
            ProjectorId: "test-projector",
            ProjectorVersion: "1.0.0",
            Graph: graph,
            SpanMap: new Dictionary<string, SourceSpan>());
    }

    private sealed class CountingEmbedder : IRuleEmbedder
    {
        private readonly IRuleEmbedder _inner;
        public int CallCount { get; private set; }
        public CountingEmbedder(IRuleEmbedder inner) => _inner = inner;
        public int Dimensions => _inner.Dimensions;
        public string EmbedderId => _inner.EmbedderId;
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            CallCount++;
            return _inner.EmbedAsync(text, ct);
        }
    }
}
