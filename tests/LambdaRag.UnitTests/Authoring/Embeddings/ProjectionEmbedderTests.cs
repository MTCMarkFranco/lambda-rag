using System.Linq;
using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Authoring;
using LambdaRag.Authoring.Embeddings;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Semantic;
using Xunit;

namespace LambdaRag.UnitTests.Authoring.Embeddings;

public class ProjectionEmbedderTests
{
    [Fact]
    public void EnumerateSections_yields_each_id_and_text_pair()
    {
        var graph = JsonNode.Parse("""
        {
          "sections": [
            { "id": "s1", "text": "Alpha", "category": "a" },
            { "id": "s2", "text": "Beta",  "category": "b" }
          ]
        }
        """)!;
        var hits = ProjectionEmbedder.EnumerateSections(graph).ToList();
        hits.Should().BeEquivalentTo(new[] { ("s1", "Alpha"), ("s2", "Beta") });
    }

    [Fact]
    public async Task EmbedSectionsAsync_populates_one_vector_per_section_id()
    {
        var doc = MakeDoc(("s1", "first"), ("s2", "second"));
        var store = new InMemorySemanticVectorStore("test", 32);
        var embedder = new DeterministicHashEmbedder();
        var sut = new ProjectionEmbedder(embedder);

        await sut.EmbedSectionsAsync(doc, store);

        store.TryGetSection("s1", out _).Should().BeTrue();
        store.TryGetSection("s2", out _).Should().BeTrue();
    }

    [Fact]
    public async Task EmbedSectionsAsync_skips_blank_text_sections()
    {
        var doc = MakeDoc(("s1", "real text"), ("s2", "   "));
        var store = new InMemorySemanticVectorStore("test", 32);
        await new ProjectionEmbedder(new DeterministicHashEmbedder()).EmbedSectionsAsync(doc, store);

        store.TryGetSection("s1", out _).Should().BeTrue();
        store.TryGetSection("s2", out _).Should().BeFalse();
    }

    [Fact]
    public async Task EmbedSectionsAsync_is_idempotent_byte_for_byte()
    {
        var doc = MakeDoc(("s1", "Provider shall maintain ISO 27001 controls."));
        var embedder = new DeterministicHashEmbedder();

        var s1 = new InMemorySemanticVectorStore("t", 32);
        var s2 = new InMemorySemanticVectorStore("t", 32);
        await new ProjectionEmbedder(embedder).EmbedSectionsAsync(doc, s1);
        await new ProjectionEmbedder(embedder).EmbedSectionsAsync(doc, s2);

        s1.TryGetSection("s1", out var v1).Should().BeTrue();
        s2.TryGetSection("s1", out var v2).Should().BeTrue();
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
}
