using System.Text;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace LambdaRag.Parsing;

/// <summary>
/// Parses PDF files into a canonical <see cref="ParsedDocument"/>.
///
/// Strategy: for each page, <see cref="ContentOrderTextExtractor"/> produces
/// reading-order text; we split on blank lines to detect paragraph boundaries,
/// normalise whitespace, and apply lightweight heading heuristics
/// (ALL-CAPS short line, or a numeric outline prefix such as "1.2.3.").
/// Font-size-based heading detection is applied when words are extractable.
/// </summary>
public sealed class PdfParser : IDocumentParser
{
    private const string ParserId = "pdf-parser";
    private const string ParserVersion = "1.0.0";

    public bool CanParse(SourceDocument source) => source.Kind == SourceDocumentKind.Pdf;

    public Task<ParsedDocument> ParseAsync(string filePath, CancellationToken ct = default)
    {
        var source = SourceDocument.FromFile(filePath, ParsingHelpers.FileIngestedAt(filePath));
        var canonical = new StringBuilder();
        var blocks = new List<ContentBlock>();
        var headingStack = new List<(int Level, string Text)>();

        using var pdf = PdfDocument.Open(filePath);

        // First pass: compute median font size across all pages for heading detection.
        var allFontSizes = CollectFontSizes(pdf);
        double medianFontSize = allFontSizes.Count > 0
            ? allFontSizes[allFontSizes.Count / 2]
            : 12.0;

        for (int pageNum = 1; pageNum <= pdf.NumberOfPages; pageNum++)
        {
            ct.ThrowIfCancellationRequested();
            var page = pdf.GetPage(pageNum);
            var pageText = page.Text;

            // Build a lookup: approximate leading font size per line of text on this page.
            var lineFontSizes = BuildLineFontSizes(page);

            foreach (var segment in ParsingHelpers.SplitIntoParagraphs(pageText))
            {
                var normalized = ParsingHelpers.NormalizeInlineParagraph(segment);
                if (string.IsNullOrWhiteSpace(normalized)) continue;

                var charStart = canonical.Length;
                canonical.Append(normalized);
                canonical.Append('\n');

                var kind = ClassifySegment(normalized, lineFontSizes, medianFontSize);
                var headingLevel = kind == ContentBlockKind.Heading
                    ? ComputeHeadingLevel(normalized)
                    : 0;

                if (kind == ContentBlockKind.Heading)
                    ParsingHelpers.PushHeading(headingStack, headingLevel, normalized);

                var headingPath = ParsingHelpers.BuildHeadingPath(headingStack);
                var span = new SourceSpan(
                    source.Id.Value, charStart, normalized.Length, pageNum, headingPath);

                blocks.Add(new ContentBlock(
                    ParsingHelpers.BlockId(charStart), kind, normalized, span,
                    headingLevel, headingPath));
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

    // ── Helpers ───────────────────────────────────────────────────────────

    private static List<double> CollectFontSizes(PdfDocument pdf)
    {
        var sizes = new List<double>();
        for (int p = 1; p <= pdf.NumberOfPages; p++)
        {
            foreach (var letter in pdf.GetPage(p).Letters)
                sizes.Add(letter.FontSize);
        }
        sizes.Sort();
        return sizes;
    }

    private static Dictionary<int, double> BuildLineFontSizes(Page page)
    {
        // Map each text line (approximated by Y-bucket) to its max font size.
        var buckets = new Dictionary<int, double>();
        foreach (var letter in page.Letters)
        {
            int yBucket = (int)Math.Round(letter.GlyphRectangle.Bottom);
            if (!buckets.TryGetValue(yBucket, out var cur) || letter.FontSize > cur)
                buckets[yBucket] = letter.FontSize;
        }
        return buckets;
    }

    private static ContentBlockKind ClassifySegment(
        string text,
        Dictionary<int, double> lineFontSizes,
        double medianFontSize)
    {
        if (ParsingHelpers.IsAllCapsHeading(text)) return ContentBlockKind.Heading;
        if (ParsingHelpers.NumericPrefixPattern().IsMatch(text)) return ContentBlockKind.Heading;

        // Font-size heuristic: if we have line-level font info and the segment's
        // typical size is 20%+ larger than the document median, treat as heading.
        if (lineFontSizes.Count > 0 && medianFontSize > 0)
        {
            // We can't trivially map from text back to y-coordinates, so use the
            // largest font-size bucket that exceeds the threshold as an indicator.
            double maxSize = lineFontSizes.Values.Max();
            if (maxSize >= medianFontSize * 1.2 && text.Length <= 200)
                return ContentBlockKind.Heading;
        }

        return ContentBlockKind.Paragraph;
    }

    private static int ComputeHeadingLevel(string text)
    {
        int depth = ParsingHelpers.NumericPrefixDepth(text);
        return depth > 0 ? depth : 1;
    }
}
