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
/// normalise whitespace, and apply lightweight heading heuristics.
///
/// Heading classification (v1.1.0):
///   • ALL-CAPS short line, OR
///   • numeric outline prefix "N.M.K" that passes the strict
///     <see cref="ParsingHelpers.LooksLikeNumberedHeading"/> test (short
///     enough, capitalised, no mid-body sentence boundary).
///
/// v1.1.0 note: font-size heuristic is disabled — the previous implementation
/// used a page-wide <c>Max()</c> font size, mis-classifying every short
/// segment on any page containing a large-font element as a heading.
/// Restoring font-size detection requires per-segment font tracking (see
/// follow-up issue).
/// </summary>
public sealed class PdfParser : IDocumentParser
{
    private const string ParserId = "pdf-parser";
    private const string ParserVersion = "1.1.0";

    public bool CanParse(SourceDocument source) => source.Kind == SourceDocumentKind.Pdf;

    public Task<ParsedDocument> ParseAsync(string filePath, CancellationToken ct = default)
    {
        var source = SourceDocument.FromFile(filePath, ParsingHelpers.FileIngestedAt(filePath));
        var canonical = new StringBuilder();
        var blocks = new List<ContentBlock>();
        var headingStack = new List<(int Level, string Text)>();

        using var pdf = PdfDocument.Open(filePath);

        for (int pageNum = 1; pageNum <= pdf.NumberOfPages; pageNum++)
        {
            ct.ThrowIfCancellationRequested();
            var page = pdf.GetPage(pageNum);
            var pageText = page.Text;

            foreach (var segment in ParsingHelpers.SplitIntoParagraphs(pageText))
            {
                var normalized = ParsingHelpers.NormalizeInlineParagraph(segment);
                if (string.IsNullOrWhiteSpace(normalized)) continue;

                var charStart = canonical.Length;
                canonical.Append(normalized);
                canonical.Append('\n');

                var kind = ClassifySegment(normalized);
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

    private static ContentBlockKind ClassifySegment(string text)
    {
        if (ParsingHelpers.IsAllCapsHeading(text)) return ContentBlockKind.Heading;
        if (ParsingHelpers.LooksLikeNumberedHeading(text)) return ContentBlockKind.Heading;
        return ContentBlockKind.Paragraph;
    }

    private static int ComputeHeadingLevel(string text)
    {
        int depth = ParsingHelpers.NumericPrefixDepth(text);
        return depth > 0 ? depth : 1;
    }
}
