using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Markup;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LambdaRag.UnitTests.Markup;

/// <summary>
/// Anchoring tests for <see cref="OpenXmlMarkupService"/> — covers the
/// "comment on the wrong paragraph" bug fixed when we removed the
/// fallthrough to <c>paragraphs[^1]</c> and started splitting runs at
/// the exact character offset of each span.
/// </summary>
public sealed class OpenXmlMarkupServiceAnchoringTests
{
    private const string DocumentId = "test-doc";

    // Paragraphs are joined by single LF in the canonical text — same
    // contract <see cref="OpenXmlMarkupService"/>'s BuildParagraphIndex
    // mirrors. Mid-paragraph offsets must anchor inside the correct
    // paragraph, not the paragraph tail.
    private static readonly string[] Paragraphs =
    {
        "Section 1. Introduction text.",          // [0..29]   total 29 chars
        "Section 2. Liability and indemnity.",    // [30..64]  total 35 chars
        "Section 3. Cyber-liability coverage.",   // [65..100] total 36 chars
    };

    [Fact]
    public void Comment_anchors_to_paragraph_containing_charStart_not_paragraph_tail()
    {
        var docxPath = BuildSampleDoc();
        try
        {
            // Span starts mid-paragraph in paragraph #2 (the
            // cyber-liability clause). Before the fix, this anchored to
            // the trailing paragraph because LocateParagraph fell
            // through to paragraphs[^1] on any miss and the comment
            // ended up at the wrong paragraph in practice. We pick an
            // offset that *does* land inside p2 to lock the correct
            // behavior in place.
            const int CyberLiabilityClauseStart = 65 + 11;   // "Section 3. ".Length == 11; "Cyber" begins here
            const int CyberLiabilityClauseLength = 15;       // "Cyber-liability".Length

            var annotation = new Annotation(
                Id: "anchor-test-1",
                Kind: AnnotationKind.Comment,
                Span: new SourceSpan(DocumentId, CyberLiabilityClauseStart, CyberLiabilityClauseLength, null, null),
                Author: "🕵 - Insurance guidance",
                Text: "Cyber-liability minimums missing.",
                Replacement: null);

            var svc = new OpenXmlMarkupService(NullLogger<OpenXmlMarkupService>.Instance);
            svc.Apply(docxPath, docxPath, new[] { annotation });

            using var doc = WordprocessingDocument.Open(docxPath, isEditable: false);
            var body = doc.MainDocumentPart!.Document!.Body!;
            var paras = body.Descendants<Paragraph>().ToList();

            // The comment range must be inside paragraph #2 (the cyber
            // clause), NOT in paragraph #3 (the tail) which is what the
            // pre-fix behavior produced.
            var p2 = paras[2];
            p2.Descendants<CommentRangeStart>().Should().HaveCount(1,
                "the comment range must anchor inside the paragraph " +
                "that contains the span's start character");
            p2.Descendants<CommentRangeEnd>().Should().HaveCount(1);

            // Wrong-paragraph regression: no CommentRange* should leak
            // into any other paragraph.
            foreach (var (p, i) in paras.Select((p, i) => (p, i)))
            {
                if (i == 2) continue;
                p.Descendants<CommentRangeStart>().Should().BeEmpty(
                    $"paragraph #{i} must NOT receive the comment range");
                p.Descendants<CommentRangeEnd>().Should().BeEmpty(
                    $"paragraph #{i} must NOT receive the comment range");
            }
        }
        finally
        {
            File.Delete(docxPath);
        }
    }

    [Fact]
    public void Span_outside_any_paragraph_is_skipped_not_pinned_to_last_paragraph()
    {
        var docxPath = BuildSampleDoc();
        try
        {
            var totalLen = Paragraphs.Sum(p => p.Length) + (Paragraphs.Length - 1);
            var outOfRange = new Annotation(
                Id: "anchor-test-out-of-range",
                Kind: AnnotationKind.Comment,
                Span: new SourceSpan(DocumentId, totalLen + 5000, 10, null, null),
                Author: "🕵 - Compliance guidance",
                Text: "stale offset from a different document version",
                Replacement: null);

            var svc = new OpenXmlMarkupService(NullLogger<OpenXmlMarkupService>.Instance);
            svc.Apply(docxPath, docxPath, new[] { outOfRange });

            using var doc = WordprocessingDocument.Open(docxPath, isEditable: false);
            var body = doc.MainDocumentPart!.Document!.Body!;
            body.Descendants<CommentRangeStart>().Should().BeEmpty(
                "out-of-range spans must be skipped — falling through to " +
                "paragraphs[^1] is exactly the wrong-paragraph bug we fixed");

            var commentsPart = doc.MainDocumentPart.WordprocessingCommentsPart;
            (commentsPart?.Comments?.Elements<Comment>().Count() ?? 0)
                .Should().Be(0, "we also do not emit a comment definition " +
                                "for a span we could not anchor");
        }
        finally
        {
            File.Delete(docxPath);
        }
    }

    [Fact]
    public void Replace_inserts_rewrite_adjacent_to_deleted_span_not_at_paragraph_end()
    {
        var docxPath = BuildSampleDoc();
        try
        {
            // Replace the word "indemnity" inside paragraph #1.
            const string TargetWord = "indemnity";
            var p1Text = Paragraphs[1];
            var wordIndex = p1Text.IndexOf(TargetWord, StringComparison.Ordinal);
            wordIndex.Should().BeGreaterThan(-1, "sanity: fixture text must contain the target word");

            var p1Offset = Paragraphs[0].Length + 1;   // paragraphs joined by LF
            var replace = new Annotation(
                Id: "replace-test-1",
                Kind: AnnotationKind.Replace,
                Span: new SourceSpan(DocumentId, p1Offset + wordIndex, TargetWord.Length, null, null),
                Author: "🕵 - Legal guidance",
                Text: "Replace 'indemnity' with 'indemnification'.",
                Replacement: "indemnification");

            var svc = new OpenXmlMarkupService(NullLogger<OpenXmlMarkupService>.Instance);
            svc.Apply(docxPath, docxPath, new[] { replace });

            using var doc = WordprocessingDocument.Open(docxPath, isEditable: false);
            var body = doc.MainDocumentPart!.Document!.Body!;
            var paras = body.Descendants<Paragraph>().ToList();
            var p1 = paras[1];

            // The deleted text must contain "indemnity" wrapped in
            // a w:del with w:delText (proper tracked-change form).
            var deletedTexts = p1.Descendants<DeletedText>()
                .Select(d => d.Text)
                .ToArray();
            deletedTexts.Should().Contain(TargetWord,
                "the spanned text must be wrapped as a tracked-change deletion, " +
                "not left in place beside a sibling [deleted] placeholder");

            // The InsertedRun (w:ins) must carry the replacement and sit
            // *between* the comment range end and any other paragraph
            // content — i.e. adjacent to where the deletion occurred,
            // not appended at the paragraph tail.
            var insertedRuns = p1.Descendants<InsertedRun>().ToList();
            insertedRuns.Should().HaveCount(1);
            insertedRuns[0].Descendants<Text>()
                .Select(t => t.Text)
                .Should().Contain("indemnification");

            // Adjacency check: the InsertedRun must follow the
            // CommentRangeEnd directly (modulo the CommentReference run
            // that always follows the range end). Anything appended to
            // the paragraph end would push it past unrelated runs.
            var rangeEnd = p1.Descendants<CommentRangeEnd>().Single();
            var afterRangeEnd = rangeEnd.ElementsAfter()
                .OfType<OpenXmlElement>()
                .Take(3)
                .ToList();
            afterRangeEnd.OfType<InsertedRun>().Should().NotBeEmpty(
                "the rewrite must sit immediately after the deleted span, " +
                "not at the paragraph tail");
        }
        finally
        {
            File.Delete(docxPath);
        }
    }

    [Fact]
    public void Replace_with_ClauseSpan_strikes_through_every_covered_paragraph_and_inserts_once()
    {
        // Issue #87 — when a verdict carries a ClauseSpan that crosses
        // paragraph boundaries the markup engine must strike through
        // the full clause (both paragraphs) and emit the rewrite
        // exactly once, immediately after the comment range end.
        var docxPath = BuildSampleDoc();
        try
        {
            // Narrow evidence: the word "Liability" in paragraph #1.
            const int p1Start = 30;                   // Paragraphs[0].Length + 1
            const int liabilityOffsetInP1 = 11;       // "Section 2. ".Length
            const int liabilityLen = 9;               // "Liability".Length

            // Clause: from "Liability" in paragraph #1 through the end
            // of paragraph #2 ("Cyber-liability coverage.").
            var clauseStart = p1Start + liabilityOffsetInP1;
            var clauseEnd = 65 + Paragraphs[2].Length;   // end of paragraph #2
            var clauseLen = clauseEnd - clauseStart;

            var replace = new Annotation(
                Id: "replace-multi-1",
                Kind: AnnotationKind.Replace,
                Span: new SourceSpan(DocumentId, clauseStart, liabilityLen, null, null),
                Author: "🕵 - Legal guidance",
                Text: "Tighten the liability and cyber clauses.",
                Replacement: "Liability, indemnity, and cyber-liability coverage are governed by Schedule A.")
            {
                ClauseSpan = new SourceSpan(DocumentId, clauseStart, clauseLen, null, null),
            };

            var svc = new OpenXmlMarkupService(NullLogger<OpenXmlMarkupService>.Instance);
            svc.Apply(docxPath, docxPath, new[] { replace });

            using var doc = WordprocessingDocument.Open(docxPath, isEditable: false);
            var body = doc.MainDocumentPart!.Document!.Body!;
            var paras = body.Descendants<Paragraph>().ToList();

            // CommentRangeStart in paragraph #1, CommentRangeEnd in
            // paragraph #2 — covering the whole clause.
            paras[1].Descendants<CommentRangeStart>().Should().HaveCount(1,
                "the comment range must START in the paragraph containing the clause start");
            paras[2].Descendants<CommentRangeEnd>().Should().HaveCount(1,
                "the comment range must END in the paragraph containing the clause end");

            // Strike-through must reach BOTH paragraphs — that's the
            // whole point of clause widening. Pre-#87 only paragraph #1
            // would be struck through.
            paras[1].Descendants<DeletedRun>().Should().NotBeEmpty(
                "paragraph #1 must contain a tracked-change deletion for the clause-start portion");
            paras[2].Descendants<DeletedRun>().Should().NotBeEmpty(
                "paragraph #2 must contain a tracked-change deletion for the clause-end portion");

            // The InsertedRun must appear EXACTLY ONCE across the whole
            // body — duplicating it per paragraph would render the
            // replacement multiple times in Word.
            var insertedRuns = body.Descendants<InsertedRun>().ToList();
            insertedRuns.Should().HaveCount(1,
                "the rewrite is a single replacement for the whole clause, not per paragraph");
            insertedRuns[0].Descendants<Text>()
                .Select(t => t.Text)
                .Should().Contain("Liability, indemnity, and cyber-liability coverage are governed by Schedule A.");

            // And it must sit in paragraph #2 (where the clause ends),
            // not paragraph #1 (where the comment range started).
            paras[2].Descendants<InsertedRun>().Should().HaveCount(1,
                "the rewrite must appear after the CommentRangeEnd in the LAST clause paragraph");

            // Issue #87 bullet-deletion follow-up: paragraph marks of every
            // paragraph strictly *inside* the clause must be marked deleted
            // so Word merges them on accept (taking any bullet/numbering
            // with them). The LAST clause paragraph's mark must NOT be
            // deleted — that would merge clause content into the next
            // unrelated paragraph.
            paras[1].Descendants<ParagraphMarkRunProperties>()
                .SelectMany(r => r.Descendants<Deleted>())
                .Should().HaveCount(1,
                    "paragraph #1's mark must be deleted so its bullet/numbering vanishes on accept");
            paras[2].Descendants<ParagraphMarkRunProperties>()
                .SelectMany(r => r.Descendants<Deleted>())
                .Should().BeEmpty(
                    "paragraph #2 is the LAST clause paragraph — deleting its mark would merge into unrelated content");
        }
        finally
        {
            File.Delete(docxPath);
        }
    }

    /// <summary>
    /// Builds a 3-paragraph .docx whose paragraph text matches
    /// <see cref="Paragraphs"/>. Each paragraph contains a single run
    /// with a single text element so the canonical char index is
    /// trivially predictable for the test offsets above.
    /// </summary>
    private static string BuildSampleDoc()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "lambda-rag-anchor-" + Guid.NewGuid().ToString("N")[..12] + ".docx");
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        foreach (var text in Paragraphs)
        {
            body.AppendChild(new Paragraph(new Run(new Text(text)
            {
                Space = SpaceProcessingModeValues.Preserve,
            })));
        }
        main.Document.Save();
        return path;
    }
}
