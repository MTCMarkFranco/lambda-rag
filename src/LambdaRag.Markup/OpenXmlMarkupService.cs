using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;

namespace LambdaRag.Markup;

/// <summary>
/// Applies <see cref="Annotation"/>s to a .docx file by injecting OOXML
/// comments anchored to the matching paragraph spans. Tracked-change
/// insertions / deletions are supported when an annotation's
/// <see cref="Annotation.Kind"/> requests them and the original span text
/// is found inside a single run.
///
/// Determinism guarantees:
///   • Annotations are processed in stable order (Id ordinal).
///   • Comment ids and revision ids are derived from Annotation.Id, not
///     from a counter or timestamp, so two runs over the same inputs
///     produce byte-identical output.
///
/// Anchoring strategy: the canonical text we received from
/// <see cref="LambdaRag.Core.Domain.ParsedDocument"/> is paragraph-aligned,
/// so we walk paragraphs in document order, accumulate their character
/// length, and identify the paragraph that contains the span's start. The
/// comment is anchored to the run that contains the offset inside that
/// paragraph (we split the run at the boundary if necessary). For Insert /
/// Delete we use OOXML <c>w:ins</c> and <c>w:del</c> elements with stable
/// ids and a fixed author/date so the file diffs are reproducible.
/// </summary>
public sealed class OpenXmlMarkupService
{
    /// <summary>Fixed timestamp used for tracked changes — keeps output deterministic.</summary>
    public static readonly DateTime DeterministicTimestamp = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ILogger<OpenXmlMarkupService> _logger;

    public OpenXmlMarkupService(ILogger<OpenXmlMarkupService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Derive the two-character author initials shown in Word's review
    /// pane from the comment author string. Falls back to <c>"LR"</c>
    /// (lambda-rag) when the author has no usable letters. Pure-code so
    /// the output stays deterministic.
    /// </summary>
    public static string ResolveInitials(string author)
    {
        if (string.IsNullOrWhiteSpace(author)) return "LR";
        // Strip the "🕵 - " prefix and any leading non-letter characters,
        // then take the first letter of the first two whitespace-separated
        // tokens (e.g. "Legal guidance" → "LG"). Single-token labels
        // double up the first letter (e.g. "Compliance" → "CC").
        var stripped = author;
        var dashIdx = stripped.IndexOf("- ", StringComparison.Ordinal);
        if (dashIdx >= 0 && dashIdx < 6) stripped = stripped[(dashIdx + 2)..];
        var tokens = stripped
            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(char.IsLetter).ToArray()))
            .Where(t => t.Length > 0)
            .ToArray();
        if (tokens.Length == 0) return "LR";
        if (tokens.Length == 1)
        {
            var t = tokens[0];
            var c = char.ToUpperInvariant(t[0]);
            return new string(c, 2);
        }
        return $"{char.ToUpperInvariant(tokens[0][0])}{char.ToUpperInvariant(tokens[1][0])}";
    }

    /// <summary>
    /// Apply annotations to <paramref name="sourcePath"/> and write the
    /// reviewed document to <paramref name="targetPath"/>.
    /// </summary>
    public void Apply(string sourcePath, string targetPath, IEnumerable<Annotation> annotations)
    {
        if (!string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            File.Copy(sourcePath, targetPath, overwrite: true);

        var ordered = annotations
            .OrderBy(a => a.Span.CharStart)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToList();

        using var doc = WordprocessingDocument.Open(targetPath, isEditable: true);
        var main = doc.MainDocumentPart
            ?? throw new InvalidOperationException("DOCX has no MainDocumentPart");
        var body = main.Document?.Body
            ?? throw new InvalidOperationException("DOCX has no Body");

        var commentsPart = main.WordprocessingCommentsPart
            ?? main.AddNewPart<WordprocessingCommentsPart>("rIdLambdaRagComments");
        commentsPart.Comments ??= new Comments();

        // Build an index of paragraphs with running char offsets.
        var paragraphs = BuildParagraphIndex(body);

        var stableCounter = 0;
        foreach (var annotation in ordered)
        {
            stableCounter++;
            // Stable comment id derived from annotation id, but OOXML wants
            // a non-negative integer so we use the running counter — within
            // a single Apply call the counter is deterministic because the
            // annotations are in stable order.
            var commentId = stableCounter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                ApplyOne(annotation, commentId, paragraphs, commentsPart.Comments);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply annotation {AnnotationId} at span [{Start},{End})",
                    annotation.Id, annotation.Span.CharStart, annotation.Span.CharEnd);
            }
        }

        commentsPart.Comments.Save();
        main.Document.Save();
    }

    private static List<ParagraphIndexEntry> BuildParagraphIndex(Body body)
    {
        var list = new List<ParagraphIndexEntry>();
        var offset = 0;
        foreach (var p in body.Descendants<Paragraph>())
        {
            var text = string.Concat(p.Descendants<Text>().Select(t => t.Text));
            list.Add(new ParagraphIndexEntry(p, offset, text.Length));
            // Mirrors the canonical-text contract of our parsers: paragraphs
            // are joined by a single LF.
            offset += text.Length + 1;
        }
        return list;
    }

    private void ApplyOne(
        Annotation a,
        string commentId,
        IReadOnlyList<ParagraphIndexEntry> paragraphs,
        Comments comments)
    {
        var (paragraph, startOffsetInParagraph, paragraphLength) =
            LocateParagraph(a.Span.CharStart, paragraphs);
        if (paragraph is null)
        {
            // Span does not fall within any paragraph's character range.
            // Skip rather than misanchor to the last paragraph — that
            // produced the "comment on the wrong paragraph" bug we are
            // fixing here.
            _logger.LogWarning(
                "Annotation {AnnotationId} span [{Start},{End}) does not match any paragraph; skipping",
                a.Id, a.Span.CharStart, a.Span.CharEnd);
            return;
        }

        // Clamp the comment range to this paragraph. Multi-paragraph
        // spans still anchor the comment to the paragraph the span
        // *starts* in — Word's review pane is paragraph-centric anyway.
        var endOffsetInParagraph = Math.Clamp(
            startOffsetInParagraph + Math.Max(a.Span.CharLength, 0),
            startOffsetInParagraph,
            paragraphLength);

        // Append the comment definition.
        comments.AppendChild(new Comment(
            new Paragraph(new Run(new Text(a.Text) { Space = SpaceProcessingModeValues.Preserve })))
        {
            Id = commentId,
            Author = a.Author,
            Date = DeterministicTimestamp,
            Initials = ResolveInitials(a.Author),
        });

        // Split runs at the start and end of the span so we can insert
        // CommentRangeStart / CommentRangeEnd at the exact character
        // boundary. SplitParagraphAtOffset returns the run that lies
        // immediately *before* the boundary (null = boundary is at
        // position 0 of the paragraph).
        var startAnchor = SplitParagraphAtOffset(paragraph, startOffsetInParagraph);
        var endAnchor   = SplitParagraphAtOffset(paragraph, endOffsetInParagraph);

        var rangeStart = new CommentRangeStart { Id = commentId };
        var rangeEnd   = new CommentRangeEnd   { Id = commentId };

        InsertAtBoundary(paragraph, startAnchor, rangeStart);
        InsertAtBoundary(paragraph, endAnchor, rangeEnd);
        rangeEnd.InsertAfterSelf(new Run(new CommentReference { Id = commentId }));

        // Tracked-change deletion: wrap the runs that span
        // [startOffset, endOffset) inside a single DeletedRun and
        // convert their Text elements to DeletedText so Word shows
        // the strike-through. With zero-length spans (e.g. the gaps
        // summary anchored at char 0) we have nothing to delete.
        var hasDelete  = a.Kind is AnnotationKind.Delete or AnnotationKind.Replace;
        var hasInsert  = a.Kind is AnnotationKind.Insert or AnnotationKind.Replace
                         && a.Replacement is { Length: > 0 };

        if (hasDelete && endOffsetInParagraph > startOffsetInParagraph)
        {
            WrapSpanInDeletedRun(rangeStart, rangeEnd, commentId, a.Author);
        }

        if (hasInsert)
        {
            // Place the inserted (rewritten) text immediately after
            // the deleted span so Word renders the tracked change inline
            // — not appended at the paragraph tail.
            rangeEnd.InsertAfterSelf(new InsertedRun(
                new Run(new Text(a.Replacement!) { Space = SpaceProcessingModeValues.Preserve }))
            {
                Id = commentId,
                Author = a.Author,
                Date = DeterministicTimestamp,
            });
        }
    }

    /// <summary>
    /// Insert <paramref name="newElement"/> at the paragraph offset
    /// represented by <paramref name="boundaryRun"/>:
    /// <c>null</c> means "before the first run", otherwise "immediately
    /// after the given run".
    /// </summary>
    private static void InsertAtBoundary(Paragraph paragraph, Run? boundaryRun, OpenXmlElement newElement)
    {
        if (boundaryRun is null)
        {
            var first = paragraph.Elements<Run>().FirstOrDefault();
            if (first is null) paragraph.AppendChild(newElement);
            else first.InsertBeforeSelf(newElement);
        }
        else
        {
            boundaryRun.InsertAfterSelf(newElement);
        }
    }

    /// <summary>
    /// Walks the paragraph's runs in document order, locates the run
    /// that contains the paragraph-relative <paramref name="offset"/>,
    /// and splits the run + its <c>w:t</c> element so the offset
    /// becomes an element boundary. Returns the run that lies
    /// immediately *before* the boundary, or <c>null</c> when the
    /// boundary is at position 0 of the paragraph (caller inserts
    /// before the first run in that case). Offsets past the paragraph
    /// end clamp to the last run.
    /// </summary>
    private static Run? SplitParagraphAtOffset(Paragraph paragraph, int offset)
    {
        if (offset <= 0) return null;

        var runs = paragraph.Elements<Run>().ToList();
        var acc = 0;
        foreach (var run in runs)
        {
            var runLen = run.Elements<Text>().Sum(t => t.Text?.Length ?? 0);
            if (acc + runLen < offset)
            {
                acc += runLen;
                continue;
            }
            if (acc + runLen == offset)
            {
                return run; // boundary lies right at this run's end
            }

            // Boundary is strictly inside this run — split it.
            var into = offset - acc;
            var tAcc = 0;
            foreach (var t in run.Elements<Text>().ToList())
            {
                var tLen = t.Text?.Length ?? 0;
                if (tAcc + tLen < into)
                {
                    tAcc += tLen;
                    continue;
                }
                var splitAt = into - tAcc;
                return SplitRunAtTextOffset(run, t, splitAt);
            }
            return run;
        }
        return runs.LastOrDefault();
    }

    /// <summary>
    /// Splits <paramref name="run"/> so the character at
    /// <c>text[splitAt]</c> becomes the first character of a new run
    /// inserted immediately after <paramref name="run"/>. Run
    /// properties are cloned onto the new run. Returns the original
    /// run (= the element before the split boundary).
    /// </summary>
    private static Run SplitRunAtTextOffset(Run run, Text text, int splitAt)
    {
        var content = text.Text ?? string.Empty;
        Text? rightStart;
        if (splitAt <= 0)
        {
            // Split happens before this Text → the right side starts at `text`.
            rightStart = text;
        }
        else if (splitAt >= content.Length)
        {
            // Split happens at end of this Text → the right side starts
            // with whatever follows `text` inside `run`. If `text` is
            // the last child, there is nothing to split off and we just
            // return the run unchanged.
            rightStart = text.NextSibling() as Text
                         ?? (Text?)text.ElementsAfter().FirstOrDefault();
            if (rightStart is null) return run;
        }
        else
        {
            var leftPart  = content[..splitAt];
            var rightPart = content[splitAt..];
            text.Text  = leftPart;
            text.Space = SpaceProcessingModeValues.Preserve;
            rightStart = new Text(rightPart) { Space = SpaceProcessingModeValues.Preserve };
            text.InsertAfterSelf(rightStart);
        }

        var newRun = new Run();
        if (run.RunProperties is { } rPr)
        {
            newRun.RunProperties = (RunProperties)rPr.CloneNode(true);
        }

        // Move rightStart and every later sibling element into newRun.
        OpenXmlElement? cursor = rightStart;
        while (cursor is not null)
        {
            var next = cursor.NextSibling();
            cursor.Remove();
            newRun.AppendChild(cursor);
            cursor = next;
        }

        run.InsertAfterSelf(newRun);
        return run;
    }

    /// <summary>
    /// Walks every run between <paramref name="rangeStart"/> and
    /// <paramref name="rangeEnd"/> (exclusive on both ends), converts
    /// each <c>w:t</c> to <c>w:delText</c>, wraps the runs in a single
    /// <c>w:del</c> (DeletedRun), and clears the range. Preserves run
    /// properties so the tracked-change deletion keeps the original
    /// formatting in Word's strike-through view.
    /// </summary>
    private static void WrapSpanInDeletedRun(
        CommentRangeStart rangeStart, CommentRangeEnd rangeEnd,
        string commentId, string author)
    {
        var elements = new List<OpenXmlElement>();
        OpenXmlElement? cursor = rangeStart.NextSibling();
        while (cursor is not null && cursor != rangeEnd)
        {
            var next = cursor.NextSibling();
            if (cursor is Run r) elements.Add(r);
            cursor = next;
        }
        if (elements.Count == 0) return;

        var del = new DeletedRun
        {
            Id = commentId,
            Author = author,
            Date = DeterministicTimestamp,
        };
        // Insert the DeletedRun in place of the first element it consumes.
        elements[0].InsertBeforeSelf(del);
        foreach (var r in elements)
        {
            r.Remove();
            foreach (var t in r.Descendants<Text>().ToList())
            {
                var dt = new DeletedText(t.Text ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve };
                t.Parent?.ReplaceChild(dt, t);
            }
            del.AppendChild(r);
        }
    }

    /// <summary>
    /// Locate the paragraph whose canonical character range contains
    /// <paramref name="charStart"/>. Returns <c>null</c> on miss — we
    /// deliberately do *not* fall back to the last paragraph because
    /// that produces "comment on the wrong paragraph" anchoring (the
    /// motivating bug for this change).
    /// </summary>
    private static (Paragraph? Paragraph, int Offset, int Length) LocateParagraph(
        int charStart, IReadOnlyList<ParagraphIndexEntry> paragraphs)
    {
        foreach (var p in paragraphs)
        {
            if (charStart >= p.Offset && charStart <= p.Offset + p.Length)
                return (p.Paragraph, charStart - p.Offset, p.Length);
        }
        return (null, 0, 0);
    }

    private sealed record ParagraphIndexEntry(Paragraph Paragraph, int Offset, int Length);
}
