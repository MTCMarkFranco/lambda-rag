using System.Text;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using CoreSpan = LambdaRag.Core.Domain.SourceSpan;
using MdSourceSpan = Markdig.Syntax.SourceSpan;

namespace LambdaRag.Parsing;

/// <summary>
/// Parses Markdown files into a canonical <see cref="ParsedDocument"/>.
///
/// Strategy: the source file is normalised to LF and parsed with Markdig.
/// The resulting AST is walked top-down; each block's
/// <see cref="Block.Span"/> gives character offsets directly into the
/// normalised source text, which becomes the <see cref="ParsedDocument.CanonicalText"/>.
///
/// Block types mapped:
///   HeadingBlock      → Heading    (Level = block.Level)
///   ParagraphBlock    → Paragraph
///   ListItemBlock     → ListItem   (visited recursively inside ListBlock)
///   FencedCodeBlock   → CodeBlock
///   All others        → skipped
/// </summary>
public sealed class MarkdownParser : IDocumentParser
{
    private const string ParserId = "md-parser";
    private const string ParserVersion = "1.0.0";

    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().Build();

    public bool CanParse(SourceDocument source) => source.Kind == SourceDocumentKind.Markdown;

    public Task<ParsedDocument> ParseAsync(string filePath, CancellationToken ct = default)
    {
        var source = SourceDocument.FromFile(filePath, ParsingHelpers.FileIngestedAt(filePath));

        // Normalise to LF so Markdig spans are into the same string we expose.
        var text = File.ReadAllText(filePath, Encoding.UTF8)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');

        var mdDoc = Markdown.Parse(text, Pipeline);

        var blocks = new List<ContentBlock>();
        var headingStack = new List<(int Level, string Text)>();

        foreach (var block in mdDoc)
        {
            ct.ThrowIfCancellationRequested();
            WalkBlock(block, source, text, blocks, headingStack);
        }

        var metadata = new Dictionary<string, string>
        {
            ["parser_id"] = ParserId,
            ["parser_version"] = ParserVersion,
        };

        return Task.FromResult(
            new ParsedDocument(source, text, blocks, metadata));
    }

    // ── Block walker ──────────────────────────────────────────────────────

    private static void WalkBlock(
        Block block,
        SourceDocument source,
        string text,
        List<ContentBlock> blocks,
        List<(int Level, string Text)> headingStack)
    {
        switch (block)
        {
            case HeadingBlock heading:
                EmitHeading(heading, source, text, blocks, headingStack);
                break;

            case ParagraphBlock para:
                EmitParagraph(para, source, text, blocks, headingStack);
                break;

            case ListBlock list:
                foreach (var child in list)
                    WalkBlock(child, source, text, blocks, headingStack);
                break;

            case ListItemBlock listItem:
                EmitListItem(listItem, source, text, blocks, headingStack);
                break;

            case FencedCodeBlock codeBlock:
                EmitCodeBlock(codeBlock, source, text, blocks, headingStack);
                break;

            case ContainerBlock container:
                // Generic container (e.g. block-quote) — recurse into children.
                foreach (var child in container)
                    WalkBlock(child, source, text, blocks, headingStack);
                break;
        }
    }

    // ── Emitters ─────────────────────────────────────────────────────────

    private static void EmitHeading(
        HeadingBlock heading,
        SourceDocument source,
        string text,
        List<ContentBlock> blocks,
        List<(int Level, string Text)> headingStack)
    {
        var content = ExtractInlineText(heading.Inline);
        var normalized = ParsingHelpers.NormalizeInlineParagraph(content);
        if (string.IsNullOrWhiteSpace(normalized)) return;

        ParsingHelpers.PushHeading(headingStack, heading.Level, normalized);
        var headingPath = ParsingHelpers.BuildHeadingPath(headingStack);

        var span = MakeSpan(source, heading.Span, null, headingPath);
        blocks.Add(new ContentBlock(
            ParsingHelpers.BlockId(heading.Span.Start),
            ContentBlockKind.Heading, normalized, span,
            heading.Level, headingPath));
    }

    private static void EmitParagraph(
        ParagraphBlock para,
        SourceDocument source,
        string text,
        List<ContentBlock> blocks,
        List<(int Level, string Text)> headingStack)
    {
        var content = ExtractInlineText(para.Inline);
        var normalized = ParsingHelpers.NormalizeInlineParagraph(content);
        if (string.IsNullOrWhiteSpace(normalized)) return;

        var headingPath = ParsingHelpers.BuildHeadingPath(headingStack);
        var span = MakeSpan(source, para.Span, null, headingPath);
        blocks.Add(new ContentBlock(
            ParsingHelpers.BlockId(para.Span.Start),
            ContentBlockKind.Paragraph, normalized, span,
            0, headingPath));
    }

    private static void EmitListItem(
        ListItemBlock listItem,
        SourceDocument source,
        string text,
        List<ContentBlock> blocks,
        List<(int Level, string Text)> headingStack)
    {
        // Prefer text from the first child paragraph; fall back to raw span.
        string content = string.Empty;
        foreach (var child in listItem)
        {
            if (child is ParagraphBlock childPara)
            {
                content = ExtractInlineText(childPara.Inline);
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            // Fallback: extract from source span and strip leading marker chars.
            content = text.Substring(listItem.Span.Start, listItem.Span.Length)
                .TrimStart('-', '*', '+', ' ', '\t');
        }

        var normalized = ParsingHelpers.NormalizeInlineParagraph(content);
        if (string.IsNullOrWhiteSpace(normalized)) return;

        var headingPath = ParsingHelpers.BuildHeadingPath(headingStack);
        var span = MakeSpan(source, listItem.Span, null, headingPath);
        blocks.Add(new ContentBlock(
            ParsingHelpers.BlockId(listItem.Span.Start),
            ContentBlockKind.ListItem, normalized, span,
            0, headingPath));
    }

    private static void EmitCodeBlock(
        FencedCodeBlock codeBlock,
        SourceDocument source,
        string text,
        List<ContentBlock> blocks,
        List<(int Level, string Text)> headingStack)
    {
        // Extract raw span from source; strip the opening and closing fence lines.
        var raw = text.Substring(codeBlock.Span.Start, codeBlock.Span.Length);
        var lines = raw.Split('\n');
        var codeLines = lines.Length > 2
            ? lines[1..^1]
            : lines;
        var codeText = string.Join("\n", codeLines).TrimEnd('\n');

        if (string.IsNullOrWhiteSpace(codeText)) return;

        var headingPath = ParsingHelpers.BuildHeadingPath(headingStack);
        var span = MakeSpan(source, codeBlock.Span, null, headingPath);
        blocks.Add(new ContentBlock(
            ParsingHelpers.BlockId(codeBlock.Span.Start),
            ContentBlockKind.CodeBlock, codeText, span,
            0, headingPath));
    }

    // ── Inline text extraction ────────────────────────────────────────────

    private static string ExtractInlineText(ContainerInline? container)
    {
        if (container is null) return string.Empty;
        var sb = new StringBuilder();
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    sb.Append(lit.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case ContainerInline child:
                    sb.Append(ExtractInlineText(child));
                    break;
            }
        }
        return sb.ToString();
    }

    // ── Span helper ───────────────────────────────────────────────────────

    private static CoreSpan MakeSpan(
        SourceDocument source,
        MdSourceSpan mdSpan,
        int? pageNumber,
        string headingPath)
        => new CoreSpan(
            source.Id.Value,
            mdSpan.Start,
            mdSpan.Length,
            pageNumber,
            headingPath);
}
