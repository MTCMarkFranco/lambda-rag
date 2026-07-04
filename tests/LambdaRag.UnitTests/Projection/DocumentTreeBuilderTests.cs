using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Projection;
using Xunit;

namespace LambdaRag.UnitTests.Projection;

/// <summary>
/// Unit tests for <see cref="DocumentTreeBuilder"/> — the offline
/// PageIndex-style hierarchical tree over a ParsedDocument. Every test
/// pins one of the spike's non-negotiable properties:
///   • determinism (same input → same fingerprint)
///   • structural correctness (level-based nesting, offset ranges)
///   • additivity (nothing throws on edge shapes)
/// </summary>
public class DocumentTreeBuilderTests
{
    // ── helpers ───────────────────────────────────────────────────────────

    private static ContentBlock H(string id, int level, string text, int start)
        => new(
            Id: id,
            Kind: ContentBlockKind.Heading,
            Text: text,
            Span: new SourceSpan("doc", start, text.Length, null, null),
            HeadingLevel: level,
            HeadingPath: "/" + text);

    private static ContentBlock P(string id, string text, int start, string headingPath = "/")
        => new(
            Id: id,
            Kind: ContentBlockKind.Paragraph,
            Text: text,
            Span: new SourceSpan("doc", start, text.Length, null, headingPath),
            HeadingLevel: 0,
            HeadingPath: headingPath);

    private static ParsedDocument Doc(int textLength, params ContentBlock[] blocks)
    {
        var src = new SourceDocument(
            Id: ContentHash.OfString($"doc-{textLength}-{blocks.Length}"),
            FileName: "test.md",
            Kind: SourceDocumentKind.Markdown,
            ByteLength: textLength,
            IngestedAt: DateTimeOffset.UnixEpoch);
        var canonical = new string(' ', textLength);
        return new ParsedDocument(src, canonical, blocks, new Dictionary<string, string>());
    }

    // ── determinism ───────────────────────────────────────────────────────

    [Fact]
    public void Build_SameInput_YieldsIdenticalFingerprint()
    {
        var d1 = Doc(100,
            H("h1", 1, "Intro", 0),
            P("p1", "hello", 10),
            H("h2", 2, "Sub", 20),
            P("p2", "world", 30));

        var d2 = Doc(100,
            H("h1", 1, "Intro", 0),
            P("p1", "hello", 10),
            H("h2", 2, "Sub", 20),
            P("p2", "world", 30));

        var t1 = new DocumentTreeBuilder().Build(d1);
        var t2 = new DocumentTreeBuilder().Build(d2);

        t1.Fingerprint.Should().Be(t2.Fingerprint);
        DocumentTreeBuilder.ToJson(t1).Should().Be(DocumentTreeBuilder.ToJson(t2));
    }

    [Fact]
    public void Build_DifferentContent_ChangesFingerprint()
    {
        var d1 = Doc(100, H("h1", 1, "Intro", 0), P("p1", "hello", 10));
        var d2 = Doc(100, H("h1", 1, "IntroX", 0), P("p1", "hello", 10));

        var t1 = new DocumentTreeBuilder().Build(d1);
        var t2 = new DocumentTreeBuilder().Build(d2);

        t1.Fingerprint.Should().NotBe(t2.Fingerprint);
    }

    // ── structure ─────────────────────────────────────────────────────────

    [Fact]
    public void Build_FlatHeadings_ProducesFlatChildren()
    {
        var doc = Doc(100,
            H("h1", 1, "A", 0),
            P("p1", "aa", 5),
            H("h2", 1, "B", 20),
            P("p2", "bb", 25));

        var tree = new DocumentTreeBuilder().Build(doc);

        tree.Root.HeadingLevel.Should().Be(0);
        tree.Root.Children.Should().HaveCount(2);
        tree.Root.Children[0].Title.Should().Be("A");
        tree.Root.Children[1].Title.Should().Be("B");
        tree.Root.Children[0].Children.Should().BeEmpty();
        tree.Root.Children[1].Children.Should().BeEmpty();
    }

    [Fact]
    public void Build_NestedHeadings_ProducesNestedChildren()
    {
        var doc = Doc(200,
            H("h1", 1, "Chapter", 0),
            P("p1", "intro", 10),
            H("h2", 2, "Section", 30),
            P("p2", "body", 45),
            H("h3", 3, "Sub", 60),
            P("p3", "leaf", 70));

        var tree = new DocumentTreeBuilder().Build(doc);

        tree.Root.Children.Should().HaveCount(1);
        var chapter = tree.Root.Children[0];
        chapter.Title.Should().Be("Chapter");
        chapter.HeadingLevel.Should().Be(1);
        chapter.Children.Should().HaveCount(1);

        var section = chapter.Children[0];
        section.Title.Should().Be("Section");
        section.HeadingLevel.Should().Be(2);
        section.Children.Should().HaveCount(1);

        var sub = section.Children[0];
        sub.Title.Should().Be("Sub");
        sub.HeadingLevel.Should().Be(3);
        sub.Children.Should().BeEmpty();
    }

    [Fact]
    public void Build_LevelJumpBack_PopsIntermediateLevels()
    {
        // # A → ## B → ### C → # D  — D is a sibling of A at root level.
        var doc = Doc(200,
            H("h1", 1, "A", 0),
            H("h2", 2, "B", 10),
            H("h3", 3, "C", 20),
            H("h4", 1, "D", 30));

        var tree = new DocumentTreeBuilder().Build(doc);

        tree.Root.Children.Should().HaveCount(2);
        tree.Root.Children[0].Title.Should().Be("A");
        tree.Root.Children[1].Title.Should().Be("D");
        tree.Root.Children[0].Children.Should().HaveCount(1);
        tree.Root.Children[0].Children[0].Title.Should().Be("B");
    }

    [Fact]
    public void Build_SkippedLevel_StillNestsUnderNearestOpen()
    {
        // # A → ### C  (skips level 2). C nests directly under A.
        var doc = Doc(100,
            H("h1", 1, "A", 0),
            H("h2", 3, "C", 10));

        var tree = new DocumentTreeBuilder().Build(doc);

        tree.Root.Children.Should().HaveCount(1);
        tree.Root.Children[0].Children.Should().HaveCount(1);
        tree.Root.Children[0].Children[0].Title.Should().Be("C");
        tree.Root.Children[0].Children[0].HeadingLevel.Should().Be(3);
    }

    // ── offsets & block assignment ────────────────────────────────────────

    [Fact]
    public void Build_NodeOffsets_CoverContiguousRanges()
    {
        var doc = Doc(100,
            H("h1", 1, "A", 0),
            P("p1", "aa", 5),
            H("h2", 1, "B", 30));

        var tree = new DocumentTreeBuilder().Build(doc);

        var a = tree.Root.Children[0];
        var b = tree.Root.Children[1];

        a.StartOffset.Should().Be(0);
        a.EndOffset.Should().Be(30);   // ends where B begins
        b.StartOffset.Should().Be(30);
        b.EndOffset.Should().Be(100);  // ends at doc length
        tree.Root.StartOffset.Should().Be(0);
        tree.Root.EndOffset.Should().Be(100);
    }

    [Fact]
    public void Build_HeadingBlockBelongsToItsOwnNode()
    {
        var doc = Doc(100,
            H("h1", 1, "A", 0),
            P("p1", "aa", 5),
            P("p2", "bb", 15));

        var tree = new DocumentTreeBuilder().Build(doc);

        var a = tree.Root.Children[0];
        a.BlockIds.Should().ContainInOrder("h1", "p1", "p2");
        tree.Root.BlockIds.Should().BeEmpty(); // no preamble
    }

    [Fact]
    public void Build_PreambleBlocks_AttachToRoot()
    {
        var doc = Doc(100,
            P("p0", "preamble", 0),
            H("h1", 1, "A", 20),
            P("p1", "aa", 25));

        var tree = new DocumentTreeBuilder().Build(doc);

        tree.Root.BlockIds.Should().ContainInOrder("p0");
        tree.Root.Children[0].BlockIds.Should().ContainInOrder("h1", "p1");
    }

    // ── edge cases ────────────────────────────────────────────────────────

    [Fact]
    public void Build_EmptyDocument_YieldsRootOnly()
    {
        var doc = Doc(0);

        var tree = new DocumentTreeBuilder().Build(doc);

        tree.Root.Children.Should().BeEmpty();
        tree.Root.BlockIds.Should().BeEmpty();
        tree.Root.StartOffset.Should().Be(0);
        tree.Root.EndOffset.Should().Be(0);
    }

    [Fact]
    public void Build_HeadinglessDocument_AllBlocksInRoot()
    {
        var doc = Doc(50,
            P("p1", "para one", 0),
            P("p2", "para two", 20));

        var tree = new DocumentTreeBuilder().Build(doc);

        tree.Root.Children.Should().BeEmpty();
        tree.Root.BlockIds.Should().ContainInOrder("p1", "p2");
    }

    // ── node-id stability ─────────────────────────────────────────────────

    [Fact]
    public void NodeIds_AreOffsetDerivedAndPredictable()
    {
        var doc = Doc(100,
            H("h1", 1, "A", 0),
            H("h2", 1, "B", 42));

        var tree = new DocumentTreeBuilder().Build(doc);

        tree.Root.NodeId.Should().Be("n_00000000");
        tree.Root.Children[0].NodeId.Should().Be("n_00000000");
        tree.Root.Children[1].NodeId.Should().Be("n_0000002a"); // 42 hex
    }

    // ── JSON output ───────────────────────────────────────────────────────

    [Fact]
    public void ToJson_ProducesStableIndentedJson()
    {
        var doc = Doc(50,
            H("h1", 1, "Hello", 0),
            P("p1", "world", 10));

        var tree = new DocumentTreeBuilder().Build(doc);
        var json = DocumentTreeBuilder.ToJson(tree);

        // Round-trip parseability + expected top-level shape.
        var node = JsonNode.Parse(json)!.AsObject();
        node["builder_id"]!.GetValue<string>().Should().Be("heading-tree");
        node["builder_version"]!.GetValue<string>().Should().Be("0.1.0");
        node["fingerprint"]!.GetValue<string>().Should().StartWith("lr1:");
        node["root"]!.AsObject()["children"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public void ToJson_IsDeterministic_ByteIdentical_AcrossRuns()
    {
        var doc = Doc(50, H("h1", 1, "Hello", 0), P("p1", "world", 10));

        var a = DocumentTreeBuilder.ToJson(new DocumentTreeBuilder().Build(doc));
        var b = DocumentTreeBuilder.ToJson(new DocumentTreeBuilder().Build(doc));

        a.Should().Be(b);
    }
}
