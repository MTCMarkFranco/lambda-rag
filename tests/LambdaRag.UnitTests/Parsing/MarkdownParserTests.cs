using FluentAssertions;
using LambdaRag.Core;
using LambdaRag.Core.Domain;
using LambdaRag.Parsing;
using Xunit;

namespace LambdaRag.UnitTests.Parsing;

public sealed class MarkdownParserTests : IDisposable
{
    private const string MarkdownContent =
        "# Test Heading\n\nThis is a paragraph of text.\n";

    // Write to a stable path derived from content hash so the file's
    // LastWriteTime is identical across both parse calls in the same test run.
    private readonly string _testFilePath;

    public MarkdownParserTests()
    {
        _testFilePath = Path.Combine(
            Path.GetTempPath(),
            $"lambda-rag-md-test-{Guid.NewGuid():N}.md");
        File.WriteAllText(_testFilePath, MarkdownContent);
    }

    public void Dispose()
    {
        if (File.Exists(_testFilePath))
            File.Delete(_testFilePath);
    }

    [Fact]
    public async Task ParseAsync_TinyMarkdown_ReturnsHeadingThenParagraph()
    {
        var parser = new MarkdownParser();
        var result = await parser.ParseAsync(_testFilePath);

        result.Blocks.Should().HaveCountGreaterThanOrEqualTo(2);

        var heading = result.Blocks.First(b => b.Kind == ContentBlockKind.Heading);
        heading.HeadingLevel.Should().Be(1);
        heading.Text.Should().Be("Test Heading");
        heading.Id.Should().StartWith("b");

        var paragraph = result.Blocks.First(b => b.Kind == ContentBlockKind.Paragraph);
        paragraph.Text.Should().Contain("paragraph of text");

        // Heading must appear before paragraph in block order.
        result.Blocks.ToList().IndexOf(heading)
            .Should().BeLessThan(result.Blocks.ToList().IndexOf(paragraph));
    }

    [Fact]
    public async Task ParseAsync_TwiceSameFile_ProducesIdenticalDocuments()
    {
        var parser = new MarkdownParser();

        var first = await parser.ParseAsync(_testFilePath);
        var second = await parser.ParseAsync(_testFilePath);

        var json1 = CanonicalJson.Serialize(first);
        var json2 = CanonicalJson.Serialize(second);

        json1.Should().Be(json2,
            "same bytes + same parser version must produce byte-identical ParsedDocument");
    }

    [Fact]
    public async Task ParseAsync_BlockSpansPointIntoCanonicalText()
    {
        var parser = new MarkdownParser();
        var result = await parser.ParseAsync(_testFilePath);

        foreach (var block in result.Blocks)
        {
            var spanText = result.CanonicalText.Substring(
                block.Span.CharStart, block.Span.CharLength);

            spanText.Should().NotBeNullOrEmpty(
                $"block {block.Id} span should point into non-empty text");
        }
    }

    [Fact]
    public async Task ParseAsync_MetadataContainsParserIdAndVersion()
    {
        var parser = new MarkdownParser();
        var result = await parser.ParseAsync(_testFilePath);

        result.Metadata.Should().ContainKey("parser_id")
            .WhoseValue.Should().Be("md-parser");
        result.Metadata.Should().ContainKey("parser_version")
            .WhoseValue.Should().Be("1.0.0");
    }
}
