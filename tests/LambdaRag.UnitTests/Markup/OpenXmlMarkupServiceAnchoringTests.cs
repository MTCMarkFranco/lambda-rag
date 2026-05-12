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

    [Fact]
    public void Replace_with_multiline_replacement_emits_one_paragraph_per_line_with_cloned_pPr()
    {
        // Option A list preservation: when the rewriter returns text with
        // '\n' separators, the markup engine must emit each line as its
        // own paragraph, cloning bullet/numbering pPr from the clause's
        // start paragraph, so the redline keeps the original list
        // structure instead of collapsing into a single paragraph.
        var docxPath = BuildBulletedSampleDoc();
        try
        {
            // Three bullets, joined by single LF in canonical text.
            // BulletParagraphs[i].Length each, separated by '\n'.
            var b0Len = BulletParagraphs[0].Length;
            var b1Len = BulletParagraphs[1].Length;
            var b2Len = BulletParagraphs[2].Length;
            var clauseStart = 0;
            var clauseLen = b0Len + 1 + b1Len + 1 + b2Len;

            var replace = new Annotation(
                Id: "multiline-bullet-1",
                Kind: AnnotationKind.Replace,
                Span: new SourceSpan(DocumentId, 0, b0Len, null, null),
                Author: "🕵 - Insurance guidance",
                Text: "Restate the insurance minimums as three explicit lines.",
                Replacement: "Commercial general liability: $5M per occurrence.\nProfessional liability: $5M per claim.\nCyber liability: $10M per claim.")
            {
                ClauseSpan = new SourceSpan(DocumentId, clauseStart, clauseLen, null, null),
            };

            var svc = new OpenXmlMarkupService(NullLogger<OpenXmlMarkupService>.Instance);
            svc.Apply(docxPath, docxPath, new[] { replace });

            using var doc = WordprocessingDocument.Open(docxPath, isEditable: false);
            var body = doc.MainDocumentPart!.Document!.Body!;
            var insertedRuns = body.Descendants<InsertedRun>().ToList();
            insertedRuns.Should().HaveCount(3,
                "a 3-line replacement must emit exactly 3 inserted runs " +
                "(one per paragraph) — collapsing to one is the bug.");

            // The first inserted run sits inside the end clause paragraph
            // (existing behaviour). The remaining two must live in brand-
            // new <w:p> siblings that follow the end paragraph.
            var paras = body.Elements<Paragraph>().ToList();
            paras.Count.Should().BeGreaterOrEqualTo(BulletParagraphs.Length + 2,
                "two brand-new paragraphs must be appended for lines 2 and 3");

            // The two new paragraphs must clone the start paragraph's
            // numPr (bullet) so accept preserves the list structure.
            var startNumPr = paras[0].GetFirstChild<ParagraphProperties>()
                ?.GetFirstChild<NumberingProperties>();
            startNumPr.Should().NotBeNull("fixture: bullet sample has numPr on every paragraph");

            // Locate the two inserted-paragraph siblings — they are the
            // ones that contain an InsertedRun AND were not in the
            // original fixture (i.e. their pPr's rPr carries a w:ins).
            var insertedParas = paras
                .Where(p =>
                    p.GetFirstChild<ParagraphProperties>()
                     ?.GetFirstChild<ParagraphMarkRunProperties>()
                     ?.GetFirstChild<Inserted>() is not null)
                .ToList();
            insertedParas.Should().HaveCount(2,
                "lines 2 and 3 must be emitted as tracked-inserted paragraphs " +
                "(pPr/rPr/w:ins so reject removes the whole paragraph)");

            foreach (var ip in insertedParas)
            {
                ip.GetFirstChild<ParagraphProperties>()
                  ?.GetFirstChild<NumberingProperties>()
                  .Should().NotBeNull(
                    "every inserted paragraph must clone numPr from the " +
                    "start paragraph so the bullet survives accept");
                ip.Descendants<InsertedRun>().Should().HaveCount(1);
            }
        }
        finally
        {
            File.Delete(docxPath);
        }
    }

    [Fact]
    public void Replace_inserts_safeguard_space_when_replacement_ends_with_period_against_following_letter()
    {
        // Sentence-spacing safeguard: when the inserted text ends with a
        // sentence-ending punctuation and the very next visible char post-
        // accept would be a letter or digit, the markup engine must emit
        // a tracked-inserted single space at the boundary so the post-
        // accept text reads as two sentences, not one run-together blob.
        const string fixtureParagraph = "Alpha bravo charlie.";
        var docxPath = BuildCustomSampleDoc(new[] { fixtureParagraph });
        try
        {
            const int bravoOffset = 6;          // "Alpha ".Length
            const int bravoWithSpaceLen = 6;    // "bravo " — include the trailing
                                                // space so post-accept would be
                                                // "Alpha BRAVO.charlie." (run-together)
                                                // without the safeguard.

            var replace = new Annotation(
                Id: "spacing-end-1",
                Kind: AnnotationKind.Replace,
                Span: new SourceSpan(DocumentId, bravoOffset, bravoWithSpaceLen, null, null),
                Author: "🕵 - Style guidance",
                Text: "Rewrite mid-sentence so it ends a sentence.",
                Replacement: "BRAVO.");

            var svc = new OpenXmlMarkupService(NullLogger<OpenXmlMarkupService>.Instance);
            svc.Apply(docxPath, docxPath, new[] { replace });

            using var doc = WordprocessingDocument.Open(docxPath, isEditable: false);
            var p = doc.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>().Single();

            // Two InsertedRuns: the rewrite "BRAVO." and the safeguard " ".
            var insertedRuns = p.Descendants<InsertedRun>().ToList();
            insertedRuns.Should().HaveCount(2,
                "the rewrite plus a sentence-spacing safeguard space must produce 2 inserted runs");

            var insertedTexts = insertedRuns
                .Select(ir => string.Concat(ir.Descendants<Text>().Select(t => t.Text)))
                .ToList();
            insertedTexts.Should().BeEquivalentTo(new[] { "BRAVO.", " " },
                "the safeguard space must be exactly one ASCII space");

            // The safeguard space sits AFTER the rewrite InsertedRun so
            // the post-accept paragraph reads "Alpha BRAVO. charlie." not
            // "Alpha  BRAVO.charlie.".
            var rewriteIr = insertedRuns.Single(ir =>
                ir.Descendants<Text>().Any(t => t.Text == "BRAVO."));
            var followingIr = rewriteIr.ElementsAfter().OfType<InsertedRun>().FirstOrDefault();
            followingIr.Should().NotBeNull(
                "the safeguard space must follow the rewrite, not precede it");
            string.Concat(followingIr!.Descendants<Text>().Select(t => t.Text))
                .Should().Be(" ");
        }
        finally
        {
            File.Delete(docxPath);
        }
    }

    [Fact]
    public void Replace_does_not_insert_safeguard_space_when_following_text_already_starts_with_space()
    {
        // Negative case: when the post-accept boundary is already
        // separated (next char is whitespace, or replacement ends in
        // whitespace), no safeguard space is added.
        const string fixtureParagraph = "Alpha bravo charlie.";
        var docxPath = BuildCustomSampleDoc(new[] { fixtureParagraph });
        try
        {
            const int bravoOffset = 6;
            const int bravoLen = 5;

            var replace = new Annotation(
                Id: "spacing-end-noop-1",
                Kind: AnnotationKind.Replace,
                Span: new SourceSpan(DocumentId, bravoOffset, bravoLen, null, null),
                Author: "🕵 - Style guidance",
                Text: "Rewrite without trailing period.",
                Replacement: "BRAVO");   // no trailing period → no run-together

            var svc = new OpenXmlMarkupService(NullLogger<OpenXmlMarkupService>.Instance);
            svc.Apply(docxPath, docxPath, new[] { replace });

            using var doc = WordprocessingDocument.Open(docxPath, isEditable: false);
            var p = doc.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>().Single();
            p.Descendants<InsertedRun>().Should().HaveCount(1,
                "without a sentence-end-against-letter collision, only the rewrite InsertedRun must be emitted");
        }
        finally
        {
            File.Delete(docxPath);
        }
    }

    private static string BuildCustomSampleDoc(IReadOnlyList<string> paragraphTexts)
    {
        var path = Path.Combine(Path.GetTempPath(),
            "lambda-rag-anchor-custom-" + Guid.NewGuid().ToString("N")[..12] + ".docx");
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        foreach (var text in paragraphTexts)
        {
            body.AppendChild(new Paragraph(new Run(new Text(text)
            {
                Space = SpaceProcessingModeValues.Preserve,
            })));
        }
        main.Document.Save();
        return path;
    }

    // Three bulleted paragraphs joined by '\n' in canonical text. Each
    // paragraph carries a <w:numPr> so the fixture mirrors a real
    // bulleted list (the Insurance section bug repro).
    private static readonly string[] BulletParagraphs =
    {
        "Commercial general liability coverage.",
        "Professional liability coverage.",
        "Cyber liability coverage.",
    };

    private static string BuildBulletedSampleDoc()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "lambda-rag-anchor-bullets-" + Guid.NewGuid().ToString("N")[..12] + ".docx");
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        foreach (var text in BulletParagraphs)
        {
            var p = new Paragraph();
            var pPr = new ParagraphProperties(
                new NumberingProperties(
                    new NumberingLevelReference { Val = 0 },
                    new NumberingId { Val = 1 }));
            p.AppendChild(pPr);
            p.AppendChild(new Run(new Text(text)
            {
                Space = SpaceProcessingModeValues.Preserve,
            }));
            body.AppendChild(p);
        }
        main.Document.Save();
        return path;
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
