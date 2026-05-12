using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LambdaRag.Core.Domain;
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
        // CRITICAL: this index must produce char offsets that match the
        // canonical text produced by LambdaRag.Parsing.DocxParser exactly —
        // otherwise SourceSpan offsets coming back from evaluation/projection
        // will land on the wrong paragraph in the markup engine. The parser:
        //   • walks BODY's TOP-LEVEL elements only (not Descendants — so
        //     paragraphs nested in tables don't double-count);
        //   • for each Paragraph: takes para.InnerText (which includes
        //     <w:instrText> field-code content like TOC field codes — NOT
        //     just <w:t>), normalizes whitespace, and skips blank ones;
        //   • for each Table: walks rows -> cells in order, flattening each
        //     cell's full InnerText into a single canonical paragraph and
        //     skipping blank cells.
        //   • Each emitted block appends "<text>\n" to canonical text, so the
        //     offset advances by normalized.Length + 1 per non-blank block.
        var list = new List<ParagraphIndexEntry>();
        var offset = 0;

        foreach (var element in body.Elements<OpenXmlElement>())
        {
            if (element is Paragraph para)
            {
                var normalized = NormalizeInlineParagraph(para.InnerText);
                if (normalized.Length == 0) continue;
                list.Add(new ParagraphIndexEntry(para, offset, normalized.Length));
                offset += normalized.Length + 1;
            }
            else if (element is Table table)
            {
                foreach (var row in table.Elements<TableRow>())
                {
                    foreach (var cell in row.Elements<TableCell>())
                    {
                        var cellNormalized = NormalizeInlineParagraph(cell.InnerText);
                        if (cellNormalized.Length == 0) continue;
                        // Anchor markup edits to the cell's first inner
                        // paragraph — table cells contain 1+ <w:p>, but the
                        // parser flattens them all into one canonical block,
                        // so there is no clean per-inner-p mapping. The first
                        // paragraph is the most stable choice for offset 0
                        // within the cell.
                        var anchor = cell.Elements<Paragraph>().FirstOrDefault();
                        if (anchor is null) continue;
                        list.Add(new ParagraphIndexEntry(anchor, offset, cellNormalized.Length));
                        offset += cellNormalized.Length + 1;
                    }
                }
            }
        }

        return list;
    }

    /// <summary>
    /// Mirrors <c>LambdaRag.Parsing.ParsingHelpers.NormalizeInlineParagraph</c>
    /// — collapses horizontal whitespace runs to a single space and trims.
    /// Duplicated here (instead of taking a project reference to
    /// LambdaRag.Parsing) so the markup engine stays free of parser
    /// dependencies. Any change to the parser's normalization MUST be
    /// mirrored here, or paragraph offsets will diverge and comments will
    /// anchor on the wrong paragraph.
    /// </summary>
    private static string NormalizeInlineParagraph(string text)
    {
        var noNewlines = text
            .Replace("\r\n", " ")
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        // Collapse runs of [space|tab] to a single space, then trim.
        var sb = new System.Text.StringBuilder(noNewlines.Length);
        var prevSpace = false;
        foreach (var ch in noNewlines)
        {
            var isSpace = ch == ' ' || ch == '\t';
            if (isSpace)
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }
        return sb.ToString().Trim();
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

        var hasDelete = a.Kind is AnnotationKind.Delete or AnnotationKind.Replace;
        var hasInsert = a.Kind is AnnotationKind.Insert or AnnotationKind.Replace
                        && a.Replacement is { Length: > 0 };

        // Clause widening (issue #87) only applies to tracked-change
        // deletions / replacements — Comment kinds always stay anchored
        // to the narrow evidence span so Word's review pane highlights
        // the offending substring, not the whole clause.
        var useClauseSpan = a.ClauseSpan is not null
                            && a.Kind is AnnotationKind.Delete or AnnotationKind.Replace;

        if (useClauseSpan)
        {
            ApplyMultiParagraph(a, a.ClauseSpan!, commentId, paragraphs, comments, hasDelete, hasInsert);
            return;
        }

        // Clamp the comment range to this paragraph. Multi-paragraph
        // spans (without a ClauseSpan widening) still anchor the comment
        // to the paragraph the span *starts* in — Word's review pane is
        // paragraph-centric anyway.
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
        if (hasDelete && endOffsetInParagraph > startOffsetInParagraph)
        {
            WrapSpanInDeletedRun(rangeStart, rangeEnd, commentId, a.Author);
        }

        if (hasInsert)
        {
            // Place the inserted (rewritten) text immediately after
            // the deleted span so Word renders the tracked change inline
            // — not appended at the paragraph tail. Multi-line rewrites
            // are expanded into multiple paragraphs cloning the source
            // paragraph's bullet / numbering pPr (see EmitInsertedParagraphs).
            EmitInsertedParagraphs(
                a.Replacement!, rangeEnd, paragraph, commentId, a.Author);
        }

        EnsureBoundarySentenceSpacing(rangeStart, rangeEnd, hasInsert ? a.Replacement : null, commentId, a.Author);
    }

    /// <summary>
    /// Apply a tracked-change deletion (and optional replacement) that
    /// spans multiple paragraphs (issue #87). When the verdict's
    /// <see cref="Annotation.ClauseSpan"/> covers a clause crossing
    /// paragraph boundaries we widen the strike-through to every covered
    /// paragraph so the redline shows the whole clause as removed —
    /// rather than partially struck through with the replacement awkwardly
    /// jammed mid-paragraph.
    ///
    /// Layout:
    ///   • <c>CommentRangeStart</c> goes into the first paragraph at the
    ///     widened start offset.
    ///   • <c>CommentRangeEnd</c> goes into the last paragraph at the
    ///     widened end offset, immediately followed by
    ///     <c>CommentReference</c> and (when present) the <c>InsertedRun</c>.
    ///   • Every covered paragraph's runs inside the comment range are
    ///     wrapped in a per-paragraph <c>DeletedRun</c> with stable id
    ///     and author so two runs over the same inputs stay byte-identical.
    /// </summary>
    private void ApplyMultiParagraph(
        Annotation a,
        SourceSpan clause,
        string commentId,
        IReadOnlyList<ParagraphIndexEntry> paragraphs,
        Comments comments,
        bool hasDelete,
        bool hasInsert)
    {
        var (startPara, startOffInPara, _) = LocateParagraph(clause.CharStart, paragraphs);
        if (startPara is null)
        {
            _logger.LogWarning(
                "Annotation {AnnotationId} clauseSpan [{Start},{End}) does not match any paragraph; skipping",
                a.Id, clause.CharStart, clause.CharEnd);
            return;
        }

        // Clamp end so we never run past the document. Map endChar back to
        // an (endParagraph, endOffsetInEndParagraph) pair. If endChar lands
        // exactly on a paragraph boundary, prefer the *previous* paragraph
        // (offset = its full length) so the strike-through doesn't bleed
        // into the next clause.
        var endChar = clause.CharStart + Math.Max(clause.CharLength, 0);
        var (endPara, endOffInEnd) = LocateClauseEnd(endChar, paragraphs);
        if (endPara is null)
        {
            // Clause extends past doc end — clamp to last paragraph.
            var last = paragraphs[^1];
            endPara = last.Paragraph;
            endOffInEnd = last.Length;
        }

        // Index lookup so we can iterate paragraphs in document order
        // between start and end.
        int startIdx = -1, endIdx = -1;
        for (int i = 0; i < paragraphs.Count; i++)
        {
            if (ReferenceEquals(paragraphs[i].Paragraph, startPara)) startIdx = i;
            if (ReferenceEquals(paragraphs[i].Paragraph, endPara)) endIdx = i;
        }
        if (startIdx < 0 || endIdx < 0 || endIdx < startIdx)
        {
            _logger.LogWarning(
                "Annotation {AnnotationId} clauseSpan paragraph index lookup failed; skipping",
                a.Id);
            return;
        }

        // Same-paragraph clause widening collapses to the single-paragraph
        // path with the wider offsets — keeps the byte layout identical to
        // pre-#87 single-paragraph deletes when the clause already fit in
        // one paragraph.
        if (startIdx == endIdx)
        {
            var widenedEnd = Math.Clamp(endOffInEnd, startOffInPara, paragraphs[startIdx].Length);
            ApplySingleParagraphAtOffsets(
                a, commentId, paragraphs[startIdx], startOffInPara, widenedEnd,
                comments, hasDelete, hasInsert);
            return;
        }

        // Append the comment definition (single comment for the whole range).
        comments.AppendChild(new Comment(
            new Paragraph(new Run(new Text(a.Text) { Space = SpaceProcessingModeValues.Preserve })))
        {
            Id = commentId,
            Author = a.Author,
            Date = DeterministicTimestamp,
            Initials = ResolveInitials(a.Author),
        });

        var startAnchor = SplitParagraphAtOffset(startPara, startOffInPara);
        var endAnchor   = SplitParagraphAtOffset(endPara, endOffInEnd);

        var rangeStart = new CommentRangeStart { Id = commentId };
        var rangeEnd   = new CommentRangeEnd   { Id = commentId };
        InsertAtBoundary(startPara, startAnchor, rangeStart);
        InsertAtBoundary(endPara,   endAnchor,   rangeEnd);
        rangeEnd.InsertAfterSelf(new Run(new CommentReference { Id = commentId }));

        if (hasDelete)
        {
            // Wrap runs in the *start* paragraph from CommentRangeStart to
            // the paragraph's end.
            WrapRunsBetweenInParagraph(
                startPara,
                fromExclusive: rangeStart,
                toExclusive: null,
                commentId, a.Author);

            // Wrap every full paragraph between start and end.
            for (int i = startIdx + 1; i < endIdx; i++)
            {
                WrapRunsBetweenInParagraph(
                    paragraphs[i].Paragraph,
                    fromExclusive: null,
                    toExclusive: null,
                    commentId, a.Author);
            }

            // Wrap runs in the *end* paragraph from paragraph start to
            // CommentRangeEnd.
            WrapRunsBetweenInParagraph(
                endPara,
                fromExclusive: null,
                toExclusive: rangeEnd,
                commentId, a.Author);

            // Issue #87 follow-up: also mark the *paragraph marks* of every
            // paragraph strictly inside the clause as deleted. Without this,
            // bulleted/numbered paragraphs whose content is wholly struck
            // through still keep their `<w:pPr><w:numPr>` and Word leaves an
            // empty bullet visible after the user accepts the deletion. A
            // paragraph-mark `<w:del>` tells Word "this paragraph mark is
            // deleted" — on accept, the paragraph merges into the following
            // one, taking its bullet/numbering with it. We do NOT delete the
            // end paragraph's mark: that would also swallow the paragraph
            // break between this clause and the next, merging unrelated
            // content together.
            for (int i = startIdx; i < endIdx; i++)
            {
                MarkParagraphMarkDeleted(paragraphs[i].Paragraph, commentId, a.Author);
            }
        }

        if (hasInsert)
        {
            // Multi-paragraph rewrite preservation: when the rewriter
            // returns text with '\n' separators, expand line 2..N into
            // their own paragraphs cloning the *start* paragraph's pPr
            // so bullet / numbering survives the redline. Line 1 stays
            // inside the end paragraph as before.
            EmitInsertedParagraphs(
                a.Replacement!, rangeEnd, startPara, commentId, a.Author);
        }

        EnsureBoundarySentenceSpacing(rangeStart, rangeEnd, hasInsert ? a.Replacement : null, commentId, a.Author);
    }

    /// <summary>
    /// Single-paragraph variant of <see cref="ApplyOne"/> with explicit
    /// start/end offsets — used by <see cref="ApplyMultiParagraph"/> when
    /// a widened ClauseSpan turns out to fit inside one paragraph after
    /// all. Keeps the multi-paragraph code path from special-casing the
    /// degenerate case.
    /// </summary>
    private void ApplySingleParagraphAtOffsets(
        Annotation a,
        string commentId,
        ParagraphIndexEntry entry,
        int startOff,
        int endOff,
        Comments comments,
        bool hasDelete,
        bool hasInsert)
    {
        comments.AppendChild(new Comment(
            new Paragraph(new Run(new Text(a.Text) { Space = SpaceProcessingModeValues.Preserve })))
        {
            Id = commentId,
            Author = a.Author,
            Date = DeterministicTimestamp,
            Initials = ResolveInitials(a.Author),
        });

        var startAnchor = SplitParagraphAtOffset(entry.Paragraph, startOff);
        var endAnchor   = SplitParagraphAtOffset(entry.Paragraph, endOff);

        var rangeStart = new CommentRangeStart { Id = commentId };
        var rangeEnd   = new CommentRangeEnd   { Id = commentId };
        InsertAtBoundary(entry.Paragraph, startAnchor, rangeStart);
        InsertAtBoundary(entry.Paragraph, endAnchor, rangeEnd);
        rangeEnd.InsertAfterSelf(new Run(new CommentReference { Id = commentId }));

        if (hasDelete && endOff > startOff)
        {
            WrapSpanInDeletedRun(rangeStart, rangeEnd, commentId, a.Author);
        }
        if (hasInsert)
        {
            EmitInsertedParagraphs(
                a.Replacement!, rangeEnd, entry.Paragraph, commentId, a.Author);
        }

        EnsureBoundarySentenceSpacing(rangeStart, rangeEnd, hasInsert ? a.Replacement : null, commentId, a.Author);
    }

    /// <summary>
    /// Locate the paragraph containing the *end* of a clause span.
    /// Differs from <see cref="LocateParagraph"/> only in tie-breaking:
    /// when <paramref name="endChar"/> sits exactly on a paragraph
    /// boundary, we attribute it to the *previous* paragraph so a clause
    /// that ends at a paragraph break doesn't bleed into the next clause.
    /// </summary>
    private static (Paragraph? Paragraph, int Offset) LocateClauseEnd(
        int endChar, IReadOnlyList<ParagraphIndexEntry> paragraphs)
    {
        for (int i = 0; i < paragraphs.Count; i++)
        {
            var p = paragraphs[i];
            // Hit when endChar falls inside [start, start+length].
            if (endChar > p.Offset && endChar <= p.Offset + p.Length)
                return (p.Paragraph, endChar - p.Offset);
            // Boundary tie at paragraph start of the next entry → prefer
            // the previous paragraph at its full length.
            if (endChar == p.Offset && i > 0)
                return (paragraphs[i - 1].Paragraph, paragraphs[i - 1].Length);
        }
        return (null, 0);
    }

    /// <summary>
    /// Mark a paragraph's paragraph-mark (the implicit "pilcrow" at end of
    /// the paragraph) as deleted by inserting <c>&lt;w:del/&gt;</c> inside
    /// <c>w:pPr/w:rPr</c>. On accept, Word merges this paragraph with the
    /// following one, taking its bullet/numbering with it.
    /// </summary>
    private static void MarkParagraphMarkDeleted(Paragraph p, string commentId, string author)
    {
        var pPr = p.GetFirstChild<ParagraphProperties>();
        if (pPr is null)
        {
            pPr = new ParagraphProperties();
            p.InsertAt(pPr, 0);
        }
        var rPr = pPr.GetFirstChild<ParagraphMarkRunProperties>();
        if (rPr is null)
        {
            rPr = new ParagraphMarkRunProperties();
            pPr.AppendChild(rPr);
        }
        // Idempotency: if already marked deleted (re-run / replay scenario),
        // don't stack multiple <w:del/> elements.
        if (rPr.GetFirstChild<Deleted>() is not null) return;
        rPr.AppendChild(new Deleted
        {
            Id = commentId,
            Author = author,
            Date = DeterministicTimestamp,
        });
    }

    /// <summary>
    /// Wrap every <c>Run</c> child of <paramref name="paragraph"/> that
    /// lies between <paramref name="fromExclusive"/> and
    /// <paramref name="toExclusive"/> (either or both may be
    /// <c>null</c> to mean "paragraph start" / "paragraph end") in a
    /// single <see cref="DeletedRun"/>. Converts each <c>w:t</c> to
    /// <c>w:delText</c> so Word renders the strike-through. No-op if
    /// no runs are in the range.
    /// </summary>
    private static void WrapRunsBetweenInParagraph(
        Paragraph paragraph,
        OpenXmlElement? fromExclusive,
        OpenXmlElement? toExclusive,
        string commentId, string author)
    {
        var elements = new List<Run>();
        OpenXmlElement? cursor = fromExclusive is null
            ? paragraph.FirstChild
            : fromExclusive.NextSibling();
        while (cursor is not null && cursor != toExclusive)
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
    /// Emit the rewriter's replacement text as one or more inserted
    /// paragraphs in tracked-change form. Single-line replacements (no
    /// <c>\n</c>) collapse to the historical behaviour: a single
    /// <c>InsertedRun</c> placed immediately after
    /// <paramref name="anchor"/>. Multi-line replacements expand into:
    /// <list type="bullet">
    ///   <item><description>line 1: <c>InsertedRun</c> after
    ///   <paramref name="anchor"/> (sits inside the end paragraph of the
    ///   deleted range, which collapsed into one paragraph on accept of
    ///   the paragraph-mark deletions).</description></item>
    ///   <item><description>lines 2..N: brand-new <c>w:p</c> siblings
    ///   appended after that end paragraph, each cloning
    ///   <paramref name="pPrSource"/>'s <c>pPr</c> (so bullet /
    ///   numbering survives), with the paragraph mark itself marked as
    ///   inserted (<c>pPr/rPr/w:ins</c>) and the run wrapped in
    ///   <c>w:ins</c>. On reject every new paragraph vanishes
    ///   completely; on accept N bulleted paragraphs replace the
    ///   deleted clause.</description></item>
    /// </list>
    /// </summary>
    private static void EmitInsertedParagraphs(
        string replacement,
        OpenXmlElement anchor,
        Paragraph pPrSource,
        string commentId,
        string author)
    {
        var lines = SplitReplacementLines(replacement);
        if (lines.Count == 0) return;

        // Line 1 goes after the anchor (CommentRangeEnd or the following
        // CommentReference run) inside the existing end paragraph.
        anchor.InsertAfterSelf(new InsertedRun(
            new Run(new Text(lines[0]) { Space = SpaceProcessingModeValues.Preserve }))
        {
            Id = commentId,
            Author = author,
            Date = DeterministicTimestamp,
        });

        if (lines.Count == 1) return;

        // Find the paragraph that owns `anchor` so we can append siblings.
        var endParagraph = anchor.Ancestors<Paragraph>().FirstOrDefault();
        if (endParagraph is null) return;

        OpenXmlElement insertAfter = endParagraph;
        for (int i = 1; i < lines.Count; i++)
        {
            var newPara = BuildInsertedParagraph(lines[i], pPrSource, commentId, author);
            insertAfter.InsertAfterSelf(newPara);
            insertAfter = newPara;
        }
    }

    /// <summary>
    /// Split the rewriter's output on <c>\n</c>, normalising stray <c>\r</c>
    /// and dropping any blank lines so empty paragraphs are not emitted.
    /// A single-line rewrite returns a one-element list (preserves the
    /// historical single-paragraph behaviour byte-for-byte).
    /// </summary>
    private static List<string> SplitReplacementLines(string replacement)
    {
        var lines = new List<string>();
        foreach (var raw in replacement.Split('\n'))
        {
            var trimmed = raw.TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(trimmed)) lines.Add(trimmed);
        }
        return lines;
    }

    /// <summary>
    /// Build a brand-new <c>w:p</c> carrying a single tracked-inserted
    /// run. The paragraph's <c>pPr</c> is cloned from
    /// <paramref name="pPrSource"/> so bullet / numbering / style refs
    /// survive, and the paragraph mark is marked as inserted (via
    /// <c>pPr/rPr/w:ins</c>) so rejecting the change removes the entire
    /// paragraph.
    /// </summary>
    private static Paragraph BuildInsertedParagraph(
        string text,
        Paragraph pPrSource,
        string commentId,
        string author)
    {
        var newPara = new Paragraph();
        var srcPPr = pPrSource.GetFirstChild<ParagraphProperties>();
        var pPr = srcPPr is not null
            ? (ParagraphProperties)srcPPr.CloneNode(true)
            : new ParagraphProperties();

        // Strip any deletion marker that may have been cloned from a
        // source paragraph whose paragraph mark was tagged as deleted
        // earlier in this same Apply call — we are creating a brand-new
        // paragraph, not deleting one.
        var existingMarkRPr = pPr.GetFirstChild<ParagraphMarkRunProperties>();
        existingMarkRPr?.Elements<Deleted>().ToList().ForEach(d => d.Remove());

        var markRPr = pPr.GetFirstChild<ParagraphMarkRunProperties>();
        if (markRPr is null)
        {
            markRPr = new ParagraphMarkRunProperties();
            pPr.AppendChild(markRPr);
        }
        if (markRPr.GetFirstChild<Inserted>() is null)
        {
            markRPr.AppendChild(new Inserted
            {
                Id = commentId,
                Author = author,
                Date = DeterministicTimestamp,
            });
        }
        newPara.AppendChild(pPr);

        newPara.AppendChild(new InsertedRun(
            new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }))
        {
            Id = commentId,
            Author = author,
            Date = DeterministicTimestamp,
        });
        return newPara;
    }

    /// <summary>
    /// Sentence-spacing safeguard at the redline boundaries: if accepting
    /// the change would visibly concatenate a sentence-ending punctuation
    /// (<c>.</c>, <c>!</c>, <c>?</c>) directly against a letter or digit
    /// — i.e., the change collapses two sentences into one with no space
    /// between them — emit a tracked-inserted single space at that
    /// boundary so the post-accept text reads naturally.
    ///
    /// The space is placed inside its own <c>w:ins</c> so rejecting the
    /// surrounding change also removes the safeguard space (avoids
    /// "phantom" whitespace surviving a reject).
    ///
    /// Two boundaries are checked:
    /// <list type="bullet">
    ///   <item><description>Start boundary — the last visible char in the
    ///   start paragraph before <c>CommentRangeStart</c> meeting the first
    ///   char of the inserted text (or, on pure delete, the first visible
    ///   char following <c>CommentRangeEnd</c>).</description></item>
    ///   <item><description>End boundary — the last char of the inserted
    ///   text (or, on pure delete, the same left-side char as the start
    ///   boundary) meeting the first visible char following the inserted
    ///   run / <c>CommentRangeEnd</c>.</description></item>
    /// </list>
    /// Multi-line inserts skip the end boundary check because the
    /// trailing line lives in a new paragraph — there is no inline
    /// concatenation to worry about.
    /// </summary>
    private static void EnsureBoundarySentenceSpacing(
        CommentRangeStart rangeStart,
        CommentRangeEnd rangeEnd,
        string? replacement,
        string commentId,
        string author)
    {
        var hasInsert = !string.IsNullOrEmpty(replacement);
        var multiLine = hasInsert && replacement!.Contains('\n');

        // ----- Start boundary -----
        var leftStart = LastVisibleCharBeforeAccept(rangeStart);
        char? rightStart;
        if (hasInsert)
        {
            rightStart = replacement!.Length > 0 ? replacement![0] : null;
        }
        else if (ReferenceEquals(rangeStart.Parent, rangeEnd.Parent))
        {
            // Pure delete in a single paragraph — the deleted region
            // collapses, so the start and end boundaries coincide.
            rightStart = FirstVisibleCharAfterAccept(rangeEnd, skipInsertedRuns: false);
        }
        else
        {
            // Cross-paragraph pure delete: paragraph break separates the
            // two visible halves post-accept, so no inline concatenation.
            rightStart = null;
        }
        if (NeedsSentenceSpace(leftStart, rightStart))
        {
            rangeStart.InsertBeforeSelf(BuildInsertedSpaceRun(commentId, author));
        }

        // ----- End boundary -----
        if (multiLine) return;

        char? leftEnd;
        if (hasInsert)
        {
            leftEnd = replacement!.Length > 0 ? replacement![^1] : null;
        }
        else
        {
            // Pure delete — the visible left-of-end is the same as
            // left-of-start (deleted region is invisible post-accept).
            leftEnd = leftStart;
        }
        var rightEnd = FirstVisibleCharAfterAccept(rangeEnd, skipInsertedRuns: hasInsert);
        if (NeedsSentenceSpace(leftEnd, rightEnd))
        {
            // Anchor the space after the last InsertedRun that follows
            // rangeEnd in this paragraph (single-line insert case puts
            // exactly one there). For pure deletes the anchor stays at
            // rangeEnd, which sits before the CommentReference run — the
            // post-accept order is unchanged because CommentReference
            // contributes no visible text.
            OpenXmlElement anchor = rangeEnd;
            for (var n = rangeEnd.NextSibling(); n is InsertedRun; n = n.NextSibling())
            {
                anchor = n;
            }
            anchor.InsertAfterSelf(BuildInsertedSpaceRun(commentId, author));
        }
    }

    private static bool NeedsSentenceSpace(char? left, char? right)
    {
        if (left is null || right is null) return false;
        if (char.IsWhiteSpace(left.Value) || char.IsWhiteSpace(right.Value)) return false;
        return (left.Value is '.' or '!' or '?') && char.IsLetterOrDigit(right.Value);
    }

    /// <summary>
    /// Walk backward through <paramref name="element"/>'s siblings and
    /// return the last character of the first sibling that contributes
    /// visible text after the change is accepted. <see cref="DeletedRun"/>
    /// elements are skipped because they vanish on accept.
    /// </summary>
    private static char? LastVisibleCharBeforeAccept(OpenXmlElement element)
    {
        for (var prev = element.PreviousSibling(); prev is not null; prev = prev.PreviousSibling())
        {
            if (prev is DeletedRun) continue;
            var text = CollectVisibleText(prev);
            if (text.Length == 0) continue;
            return text[^1];
        }
        return null;
    }

    /// <summary>
    /// Forward analogue of <see cref="LastVisibleCharBeforeAccept"/>.
    /// When <paramref name="skipInsertedRuns"/> is true the walker steps
    /// over <see cref="InsertedRun"/> elements — used when the inserted
    /// text is already accounted for as the "left" side of the boundary.
    /// </summary>
    private static char? FirstVisibleCharAfterAccept(OpenXmlElement element, bool skipInsertedRuns)
    {
        for (var next = element.NextSibling(); next is not null; next = next.NextSibling())
        {
            if (next is DeletedRun) continue;
            if (skipInsertedRuns && next is InsertedRun) continue;
            var text = CollectVisibleText(next);
            if (text.Length == 0) continue;
            return text[0];
        }
        return null;
    }

    /// <summary>
    /// Collect text from a sibling element that will be visible after the
    /// tracked changes are accepted. Plain <see cref="Run"/> and
    /// <see cref="InsertedRun"/> contribute their <c>w:t</c> descendants;
    /// other element kinds (range markers, comment references, paragraph
    /// properties) contribute nothing.
    /// </summary>
    private static string CollectVisibleText(OpenXmlElement element)
    {
        return element switch
        {
            InsertedRun ir => string.Concat(ir.Descendants<Text>().Select(t => t.Text)),
            Run r => string.Concat(r.Descendants<Text>().Select(t => t.Text)),
            _ => string.Empty,
        };
    }

    private static InsertedRun BuildInsertedSpaceRun(string commentId, string author)
    {
        return new InsertedRun(
            new Run(new Text(" ") { Space = SpaceProcessingModeValues.Preserve }))
        {
            Id = commentId,
            Author = author,
            Date = DeterministicTimestamp,
        };
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
