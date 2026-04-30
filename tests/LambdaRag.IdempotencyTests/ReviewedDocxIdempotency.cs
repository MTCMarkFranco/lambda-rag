using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using LambdaRag.Core;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Evaluation;
using LambdaRag.Evaluation.Engine;
using LambdaRag.Markup;
using LambdaRag.Parsing;
using LambdaRag.Projection;
using LambdaRag.Selectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LambdaRag.IdempotencyTests;

/// <summary>
/// Markup-mode golden-master idempotency proof.
///
/// Two complementary checks:
///
///   1. <c>InnerParts_AreByteIdentical_AcrossTwoRuns</c> — runs the full
///      review-with-markup pipeline twice against the same input and
///      asserts every inner OOXML part of <c>reviewed.docx</c> is
///      byte-identical between runs. This is the core determinism claim.
///
///   2. <c>InnerParts_MatchGoldenHashes</c> — compares the SHA-256 hashes
///      of every inner OOXML part to a checked-in golden file
///      (<c>reviewed-docx-golden.json</c>). This catches *unintentional*
///      drift across code changes — e.g. an OOXML SDK upgrade silently
///      reorders attributes, or someone inadvertently introduces a
///      non-deterministic id, timestamp, or relationship id.
///
/// Notes:
///   - Outer .docx SHA is intentionally NOT compared. ZIP central-directory
///     entries embed per-entry write timestamps which are not under our
///     control and cannot be made stable in a portable way. The legal /
///     audit claim is that every inner part — the actual OOXML content —
///     is reproducible, and that is what these tests prove.
///   - We use <see cref="ZipFile.OpenRead"/>, not <c>Expand-Archive</c>,
///     because the latter mishandles <c>[Content_Types].xml</c> on
///     Windows.
///   - The sample.docx input is generated deterministically inside the
///     test from <c>samples/contracts/contract.md</c>. No checked-in
///     binary input is required.
/// </summary>
public sealed class ReviewedDocxIdempotency
{
    private static string SamplesRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "contracts"));

    private static string GoldenRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "Goldens", "reviewed-docx"));

    private static readonly DateTime DeterministicAuthored =
        new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task InnerParts_AreByteIdentical_AcrossTwoRuns()
    {
        using var work1 = new TempDir();
        using var work2 = new TempDir();

        var docx1 = await ProduceReviewedDocxAsync(work1.Path);
        var docx2 = await ProduceReviewedDocxAsync(work2.Path);

        var hashes1 = HashInnerParts(docx1);
        var hashes2 = HashInnerParts(docx2);

        hashes1.Should().BeEquivalentTo(hashes2,
            "every inner OOXML part of reviewed.docx must be byte-identical " +
            "between two clean runs of the same inputs");
        hashes1.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task InnerParts_MatchGoldenHashes()
    {
        using var work = new TempDir();
        var docx = await ProduceReviewedDocxAsync(work.Path);
        var observed = HashInnerParts(docx);

        Directory.CreateDirectory(GoldenRoot);
        var goldenPath = Path.Combine(GoldenRoot, "reviewed-docx-golden.json");

        if (!File.Exists(goldenPath))
        {
            // First-run bootstrap: emit the golden so the developer can
            // inspect it and commit. Fail loudly so a missing golden is
            // never silently treated as "everything matches".
            var bootstrap = JsonSerializer.Serialize(
                observed,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(goldenPath, bootstrap);
            Assert.Fail(
                $"Golden master did not exist; bootstrapped at {goldenPath}. " +
                "Inspect, then commit it to lock the markup output for the future.");
        }

        var goldenJson = File.ReadAllText(goldenPath);
        var golden = JsonSerializer.Deserialize<SortedDictionary<string, string>>(goldenJson)!;

        observed.Should().BeEquivalentTo(golden,
            "the SHA-256 of every inner OOXML part of reviewed.docx must " +
            "match the checked-in golden — any drift here means an " +
            "unintended source of non-determinism crept into the markup " +
            $"pipeline. Golden file: {goldenPath}");
    }

    // ---------------------------------------------------------------
    // Pipeline
    // ---------------------------------------------------------------

    private static async Task<string> ProduceReviewedDocxAsync(string workDir)
    {
        var sourceDocx = Path.Combine(workDir, "sample.docx");
        BuildDeterministicSampleDocx(
            sourceDocx,
            File.ReadAllText(Path.Combine(SamplesRoot, "contract.md")));

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<TimeProvider>(new FrozenTimeProvider(
            new DateTimeOffset(DeterministicAuthored)));
        services
            .AddLambdaRagParsing()
            .AddLambdaRagProjection()
            .AddLambdaRagSelectors()
            .AddLambdaRagEvaluation()
            .AddLambdaRagMarkup();

        await using var sp = services.BuildServiceProvider();
        var parsers = sp.GetRequiredService<ParserRegistry>();
        var projector = sp.GetRequiredService<IDocumentProjector>();
        var evaluator = sp.GetRequiredService<EvaluationService>();
        var markup = sp.GetRequiredService<OpenXmlMarkupService>();

        var rulesetJson = await File.ReadAllTextAsync(
            Path.Combine(SamplesRoot, "ruleset.json"));
        var ruleset = JsonSerializer.Deserialize<RuleSet>(
            rulesetJson, CanonicalJson.Options)!;

        var parsed = await parsers.ParseAsync(sourceDocx);
        var projected = await projector.ProjectAsync(parsed);
        var report = await evaluator.EvaluateAsync(ruleset, projected);

        var ruleLookup = ruleset.Rules.ToDictionary(r => r.Id, r => r, StringComparer.Ordinal);
        var annotations = AnnotationFactory.FromReport(report, ruleLookup).ToList();
        var gapsSummary = AnnotationFactory.BuildGapsSummary(report, ruleLookup);
        if (gapsSummary is not null) annotations.Insert(0, gapsSummary);

        var reviewed = Path.Combine(workDir, "reviewed.docx");
        markup.Apply(sourceDocx, reviewed, annotations);
        return reviewed;
    }

    // ---------------------------------------------------------------
    // Sample.docx generator (deterministic)
    // ---------------------------------------------------------------

    private static void BuildDeterministicSampleDocx(string path, string markdown)
    {
        // Trivial markdown→OOXML: split on lines, lines starting with '#'
        // become Heading paragraphs, blanks are skipped, everything else is
        // a Body paragraph. Run order, paragraph order, ids — all derived
        // from line ordinals. No timestamps. No GUIDs.
        using (var doc = WordprocessingDocument.Create(
            path, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);

            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (string.IsNullOrWhiteSpace(line)) continue;
                var heading = 0;
                while (heading < line.Length && line[heading] == '#') heading++;
                var text = heading > 0 ? line[heading..].TrimStart() : line;

                var pPr = heading switch
                {
                    1 => new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                    2 => new ParagraphProperties(new ParagraphStyleId { Val = "Heading2" }),
                    3 => new ParagraphProperties(new ParagraphStyleId { Val = "Heading3" }),
                    _ => null
                };

                var paragraph = pPr is null
                    ? new Paragraph(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }))
                    : new Paragraph(pPr, new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
                body.Append(paragraph);
            }

            main.Document.Save();
        }

        // OpenXml SDK auto-generates a random relationship id when
        // AddMainDocumentPart() is called. Rewrite the package-root .rels
        // to use a pinned id so the source .docx is byte-deterministic.
        PinPackageRels(path);
    }

    /// <summary>
    /// Rewrites the package-root <c>_rels/.rels</c> entry of a .docx so the
    /// main-document relationship uses a stable id (<c>rIdMainDocument</c>),
    /// regardless of what the OpenXml SDK randomly chose at create time.
    /// </summary>
    private static void PinPackageRels(string docxPath)
    {
        const string PinnedId = "rIdMainDocument";
        const string RelsEntry = "_rels/.rels";

        // Read all entries.
        var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        using (var read = ZipFile.OpenRead(docxPath))
        {
            foreach (var e in read.Entries)
            {
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                entries[e.FullName] = ms.ToArray();
            }
        }

        if (!entries.TryGetValue(RelsEntry, out var relsBytes)) return;
        var relsXml = System.Text.Encoding.UTF8.GetString(relsBytes);

        // Find every <Relationship .../> element targeting document.xml
        // and pin its Id attribute. Order of attributes inside a tag is
        // up to the SDK and varies between SDK versions, so do an XML
        // parse rather than a regex on attribute order.
        var rewritten = System.Text.RegularExpressions.Regex.Replace(
            relsXml,
            "<Relationship\\b[^>]*?/>",
            m =>
            {
                var elem = m.Value;
                if (!System.Text.RegularExpressions.Regex.IsMatch(
                        elem, "Target=\"[^\"]*document\\.xml\"",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return elem;
                return System.Text.RegularExpressions.Regex.Replace(
                    elem,
                    "Id=\"[^\"]+\"",
                    "Id=\"" + PinnedId + "\"",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            });

        if (rewritten == relsXml) return;
        entries[RelsEntry] = System.Text.Encoding.UTF8.GetBytes(rewritten);

        // Rewrite the .docx with the patched entry. Order entries by name
        // so the central directory is stable too (only inner content is
        // covered by the determinism claim, but stable order is harmless).
        File.Delete(docxPath);
        using var write = ZipFile.Open(docxPath, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            var entry = write.CreateEntry(name, CompressionLevel.Optimal);
            // Pin the entry timestamp too — does not affect inner-part
            // hashes, but makes the zip's central directory more stable.
            entry.LastWriteTime = new DateTimeOffset(DeterministicAuthored);
            using var es = entry.Open();
            es.Write(bytes, 0, bytes.Length);
        }
    }

    // ---------------------------------------------------------------
    // Inner-parts hashing (NOT outer ZIP)
    // ---------------------------------------------------------------

    private static SortedDictionary<string, string> HashInnerParts(string docxPath)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using var zip = ZipFile.OpenRead(docxPath);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var sha = SHA256.HashData(ms.ToArray());
            result[entry.FullName] = Convert.ToHexString(sha).ToLowerInvariant();
        }
        return result;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "lambda-rag-tests-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    private sealed class FrozenTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
