using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;

namespace LambdaRag.Parsing;

/// <summary>
/// Parses DOCX files into a canonical <see cref="ParsedDocument"/>.
///
/// Strategy: walks every top-level element of the document body.
/// Paragraph elements are classified by their Word paragraph style
/// (Heading1–Heading6 → Heading; ListParagraph → ListItem;
/// everything else → Paragraph). Table cells are flattened into
/// <see cref="ContentBlockKind.TableCell"/> blocks.
/// All offsets are tracked into the accumulating canonical text
/// (page number is null because Word reflows on render).
/// </summary>
public sealed class DocxParser : IDocumentParser
{
    private const string ParserId = "docx-parser";
    private const string ParserVersion = "1.0.0";

    public bool CanParse(SourceDocument source) => source.Kind == SourceDocumentKind.Docx;

    public Task<ParsedDocument> ParseAsync(string filePath, CancellationToken ct = default)
    {
        var source = SourceDocument.FromFile(filePath, ParsingHelpers.FileIngestedAt(filePath));
        var canonical = new StringBuilder();
        var blocks = new List<ContentBlock>();
        var headingStack = new List<(int Level, string Text)>();

        using var doc = WordprocessingDocument.Open(filePath, isEditable: false);
        var body = doc.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("DOCX has no document body.");

        foreach (var element in body.Elements<OpenXmlElement>())
        {
            ct.ThrowIfCancellationRequested();

            if (element is Paragraph para)
            {
                ProcessParagraph(para, source, canonical, blocks, headingStack);
            }
            else if (element is Table table)
            {
                ProcessTable(table, source, canonical, blocks);
            }
        }

        var metadata = new Dictionary<string, string>
        {
            ["parser_id"] = ParserId,
            ["parser_version"] = ParserVersion,
        };

        return Task.FromResult(
            new ParsedDocument(source, canonical.ToString(), blocks, metadata));
    }

    // ── Paragraph ─────────────────────────────────────────────────────────

    private static void ProcessParagraph(
        Paragraph para,
        SourceDocument source,
        StringBuilder canonical,
        List<ContentBlock> blocks,
        List<(int Level, string Text)> headingStack)
    {
        var text = ParsingHelpers.NormalizeInlineParagraph(para.InnerText);
        if (string.IsNullOrWhiteSpace(text)) return;

        var styleId = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value
                      ?? string.Empty;

        var (kind, headingLevel) = ClassifyStyle(styleId);

        if (kind == ContentBlockKind.Heading)
            ParsingHelpers.PushHeading(headingStack, headingLevel, text);

        var headingPath = ParsingHelpers.BuildHeadingPath(headingStack);
        var charStart = canonical.Length;
        canonical.Append(text);
        canonical.Append('\n');

        var span = new SourceSpan(
            source.Id.Value, charStart, text.Length, null, headingPath);

        blocks.Add(new ContentBlock(
            ParsingHelpers.BlockId(charStart), kind, text, span,
            headingLevel, headingPath));
    }

    // ── Table ─────────────────────────────────────────────────────────────

    private static void ProcessTable(
        Table table,
        SourceDocument source,
        StringBuilder canonical,
        List<ContentBlock> blocks)
    {
        int rowIdx = 0;
        foreach (var row in table.Elements<TableRow>())
        {
            int colIdx = 0;
            foreach (var cell in row.Elements<TableCell>())
            {
                var cellText = ParsingHelpers.NormalizeInlineParagraph(cell.InnerText);
                if (!string.IsNullOrWhiteSpace(cellText))
                {
                    var charStart = canonical.Length;
                    canonical.Append(cellText);
                    canonical.Append('\n');

                    // Encode row/col into HeadingPath since ContentBlock has no metadata bag.
                    var path = $"/table/r{rowIdx}/c{colIdx}";
                    var span = new SourceSpan(
                        source.Id.Value, charStart, cellText.Length, null, path);

                    blocks.Add(new ContentBlock(
                        ParsingHelpers.BlockId(charStart),
                        ContentBlockKind.TableCell, cellText, span, 0, path));
                }
                colIdx++;
            }
            rowIdx++;
        }
    }

    // ── Style classification ──────────────────────────────────────────────

    private static (ContentBlockKind Kind, int HeadingLevel) ClassifyStyle(string styleId)
    {
        // Heading1..Heading6 (no space) OR "Heading 1".."Heading 6"
        var compact = styleId.Replace(" ", "");
        if (compact.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = compact["Heading".Length..];
            if (int.TryParse(suffix, out var lvl) && lvl >= 1 && lvl <= 9)
                return (ContentBlockKind.Heading, lvl);
        }

        if (compact.Equals("ListParagraph", StringComparison.OrdinalIgnoreCase))
            return (ContentBlockKind.ListItem, 0);

        return (ContentBlockKind.Paragraph, 0);
    }
}
