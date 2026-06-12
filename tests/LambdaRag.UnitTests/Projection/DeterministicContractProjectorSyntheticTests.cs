using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Selectors;
using LambdaRag.Core.Semantic;
using LambdaRag.Projection.Projectors;
using Xunit;

namespace LambdaRag.UnitTests.Projection;

/// <summary>
/// Pillar 7.B (#130) — covers the anchor-driven synthetic-child-section
/// post-pass added to <see cref="DeterministicContractProjector"/>.
///
/// Determinism contract: same inputs → same projection bytes, regardless
/// of whether the post-pass fired. Off path (no ruleset, no embedder,
/// no anchors, every target topic already present) must be observably
/// indistinguishable from the parameterless ctor.
/// </summary>
public sealed class DeterministicContractProjectorSyntheticTests
{
    // ─── deterministic stub embedder ───────────────────────────────────

    private sealed class StubEmbedder : ITokenEmbedder
    {
        private readonly Dictionary<string, float[]> _table;
        private readonly float[] _fallback;
        public int CallCount { get; private set; }

        public StubEmbedder(int dim, IDictionary<string, float[]>? table = null, float[]? fallback = null)
        {
            Dimensions = dim;
            _table = table is null
                ? new Dictionary<string, float[]>(StringComparer.Ordinal)
                : new Dictionary<string, float[]>(table, StringComparer.Ordinal);
            _fallback = fallback ?? new float[dim];
        }

        public string EmbedderId => "stub";
        public int Dimensions { get; }

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            CallCount++;
            if (_table.TryGetValue(text, out var v))
                return Task.FromResult((float[])v.Clone());
            return Task.FromResult((float[])_fallback.Clone());
        }
    }

    private sealed class ThrowingEmbedder : ITokenEmbedder
    {
        public string EmbedderId => "throwing";
        public int Dimensions => 4;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => throw new InvalidOperationException("intentional");
    }

    // ─── helpers ───────────────────────────────────────────────────────

    private static ParsedDocument BuildDoc(params (string Heading, string Body)[] sections)
    {
        var blocks = new List<ContentBlock>();
        var offset = 0;
        var i = 0;
        foreach (var (h, b) in sections)
        {
            blocks.Add(new ContentBlock(
                Id: $"h{i}",
                Kind: ContentBlockKind.Heading,
                Text: h,
                Span: new SourceSpan("doc", offset, h.Length, 1, "/"),
                HeadingLevel: 1,
                HeadingPath: "/" + h));
            offset += h.Length;
            blocks.Add(new ContentBlock(
                Id: $"p{i}",
                Kind: ContentBlockKind.Paragraph,
                Text: b,
                Span: new SourceSpan("doc", offset, b.Length, 1, "/" + h),
                HeadingLevel: 0,
                HeadingPath: "/" + h));
            offset += b.Length;
            i++;
        }
        var src = new SourceDocument(
            Id: ContentHash.OfBytes(System.Text.Encoding.UTF8.GetBytes(
                string.Join("|", sections))),
            FileName: "test.md",
            Kind: SourceDocumentKind.Markdown,
            ByteLength: offset,
            IngestedAt: DateTimeOffset.UnixEpoch);
        return new ParsedDocument(src, "", blocks, new Dictionary<string, string>());
    }

    private static Rule MakeAnchoredRule(
        string id,
        string targetTopic,
        string anchorName,
        float[] anchorEmbedding,
        double threshold = 0.78)
    {
        return new Rule(
            Id: id,
            Version: "1.0.0",
            NaturalLanguage: $"{id} natural language",
            Lambda: "true",
            AppliesToSchema: new JsonObject(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Deviation,
            SourceSpan: new SourceSpan("ruleset", 0, 1, 1, "/"),
            EvidenceQuote: "",
            Metadata: new Dictionary<string, string>())
        {
            Predicate = $"LambdaPrimitives.HasTopic(input1, \"{targetTopic}\")",
            SemanticAnchors = new[]
            {
                new SemanticAnchor(
                    Name: anchorName,
                    AnchorText: $"{targetTopic} anchor",
                    AnchorEmbedding: anchorEmbedding,
                    Threshold: threshold),
            },
        };
    }

    private static RuleSet MakeRuleSet(params Rule[] rules)
        => new(
            Id: "test-ruleset",
            Version: "1.0.0",
            Domain: "test",
            PublishedAt: DateTimeOffset.UnixEpoch,
            Rules: rules,
            Metadata: new Dictionary<string, string>());

    private static IEnumerable<JsonObject> SectionsOf(ProjectedDocument proj)
        => proj.Graph["sections"]!.AsArray().OfType<JsonObject>();

    private static IReadOnlyList<JsonObject> SyntheticOf(ProjectedDocument proj)
        => SectionsOf(proj)
            .Where(s => s["is_synthetic_anchor"]?.GetValue<bool>() == true)
            .ToList();

    // ─── tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task NoRuleSet_NoSyntheticSections_OutputByteIdenticalToLegacy()
    {
        var legacy = new DeterministicContractProjector();
        var withNulls = new DeterministicContractProjector(
            legacy.TopicMap, ruleSet: null, ruleEmbedder: null);

        var doc = BuildDoc(
            ("Limitation of Liability", "Liability shall be capped at fees paid in twelve months."));

        var a = (await legacy.ProjectAsync(doc)).Graph.ToJsonString();
        var b = (await withNulls.ProjectAsync(doc)).Graph.ToJsonString();
        b.Should().Be(a);
    }

    [Fact]
    public async Task RuleSetWithoutAnchors_NoSyntheticSections()
    {
        var ruleNoAnchor = new Rule(
            Id: "r-noanchor",
            Version: "1.0.0",
            NaturalLanguage: "no anchor",
            Lambda: "true",
            AppliesToSchema: new JsonObject(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Deviation,
            SourceSpan: new SourceSpan("ruleset", 0, 1, 1, "/"),
            EvidenceQuote: "",
            Metadata: new Dictionary<string, string>())
        {
            Predicate = "LambdaPrimitives.HasTopic(input1, \"orphan_topic\")",
        };
        var rs = MakeRuleSet(ruleNoAnchor);
        var stub = new StubEmbedder(dim: 4, fallback: new float[] { 1, 0, 0, 0 });

        var projector = new DeterministicContractProjector(
            new DeterministicContractProjector().TopicMap, rs, stub);
        var proj = await projector.ProjectAsync(BuildDoc(("Heading", "Body text.")));

        SyntheticOf(proj).Should().BeEmpty();
        stub.CallCount.Should().Be(0); // no anchored rules → never embedded
    }

    [Fact]
    public async Task NoEmbedder_NoSyntheticSections()
    {
        var rs = MakeRuleSet(MakeAnchoredRule(
            "r1", "orphan_topic", "a1", new float[] { 1, 0, 0, 0 }));

        var projector = new DeterministicContractProjector(
            new DeterministicContractProjector().TopicMap, rs, ruleEmbedder: null);

        var proj = await projector.ProjectAsync(BuildDoc(("H", "Body text.")));
        SyntheticOf(proj).Should().BeEmpty();
    }

    [Fact]
    public async Task TopicAlreadyPresentAsPrimary_SkipsEmission()
    {
        // "liability" is already the primary topic of the only section,
        // so the post-pass must skip it even when an embedder + anchored
        // rule for "liability" exist.
        var rs = MakeRuleSet(MakeAnchoredRule(
            "r1", "liability", "a1", new float[] { 1, 0, 0, 0 }));
        var stub = new StubEmbedder(dim: 4);

        var projector = new DeterministicContractProjector(
            new DeterministicContractProjector().TopicMap, rs, stub);

        var proj = await projector.ProjectAsync(BuildDoc(
            ("Limitation of Liability",
             "Liability shall be capped at fees paid in twelve months.")));

        SyntheticOf(proj).Should().BeEmpty();
        stub.CallCount.Should().Be(0); // no candidate topics → never embedded
    }

    [Fact]
    public async Task CosineAboveThreshold_TopicAbsent_EmitsExactlyOneSynthetic()
    {
        var bodyVec = new float[] { 1, 0, 0, 0 };
        var anchorVec = new float[] { 1, 0, 0, 0 }; // cosine = 1.0
        var rs = MakeRuleSet(MakeAnchoredRule(
            "r1", "orphan_topic", "anchor_alpha", anchorVec));

        var stub = new StubEmbedder(dim: 4,
            table: new Dictionary<string, float[]>(StringComparer.Ordinal)
            {
                ["Body about something semantic."] = bodyVec,
            });

        var projector = new DeterministicContractProjector(
            new DeterministicContractProjector().TopicMap, rs, stub,
            syntheticCosineThreshold: 0.30);

        var proj = await projector.ProjectAsync(BuildDoc(
            ("Other Heading", "Body about something semantic.")));

        var synth = SyntheticOf(proj);
        synth.Should().HaveCount(1);
        var s = synth[0];
        s["id"]!.GetValue<string>().Should().Be("s_synthetic_orphan_topic_0001");
        s["primary_topic"]!.GetValue<string>().Should().Be("orphan_topic");
        s["category"]!.GetValue<string>().Should().Be("orphan_topic");
        s["topics"]!.AsArray().Select(x => x!.GetValue<string>())
            .Should().Equal("orphan_topic");
        s["synthetic_anchor"]!.GetValue<string>().Should().Be("anchor_alpha");
        s["synthetic_from"]!.GetValue<string>().Should().Be("s_00000000");
        s["is_operative_for_topic"]!.GetValue<bool>().Should().BeFalse();
        var scored = s["topic_scores"]!.AsObject()["orphan_topic"]!.GetValue<double>();
        scored.Should().BeApproximately(0.9, 0.0001); // promoted to heading-tier confidence
        s["synthetic_cosine"]!.GetValue<double>().Should().BeApproximately(1.0, 0.0001);
    }

    [Fact]
    public async Task CosineBelowThreshold_NoSyntheticEmitted()
    {
        // Orthogonal body and anchor → cosine 0 → below 0.30 threshold.
        var rs = MakeRuleSet(MakeAnchoredRule(
            "r1", "orphan_topic", "a1", new float[] { 1, 0, 0, 0 }));
        var stub = new StubEmbedder(dim: 4,
            table: new Dictionary<string, float[]>(StringComparer.Ordinal)
            {
                ["Body about something semantic."] = new float[] { 0, 1, 0, 0 },
            });

        var projector = new DeterministicContractProjector(
            new DeterministicContractProjector().TopicMap, rs, stub,
            syntheticCosineThreshold: 0.30);

        var proj = await projector.ProjectAsync(BuildDoc(
            ("Other Heading", "Body about something semantic.")));

        SyntheticOf(proj).Should().BeEmpty();
    }

    [Fact]
    public async Task EmbedderThrows_GracefulNoOp()
    {
        var rs = MakeRuleSet(MakeAnchoredRule(
            "r1", "orphan_topic", "a1", new float[] { 1, 0, 0, 0 }));

        var projector = new DeterministicContractProjector(
            new DeterministicContractProjector().TopicMap, rs, new ThrowingEmbedder());

        var proj = await projector.ProjectAsync(BuildDoc(
            ("Other Heading", "Body about something semantic.")));

        SyntheticOf(proj).Should().BeEmpty();
    }

    [Fact]
    public async Task SyntheticEmission_IsDeterministic_AcrossRepeatedRuns()
    {
        // Re-project ten times; the JSON bytes for the section list must
        // be identical every time, with the synthetic id stable.
        var bodyVec = new float[] { 1, 0, 0, 0 };
        var anchorVec = new float[] { 1, 0, 0, 0 };
        var rs = MakeRuleSet(MakeAnchoredRule(
            "r1", "orphan_topic", "anchor_alpha", anchorVec));
        var stub = new StubEmbedder(dim: 4,
            table: new Dictionary<string, float[]>(StringComparer.Ordinal)
            {
                ["Body about something semantic."] = bodyVec,
            });

        var projector = new DeterministicContractProjector(
            new DeterministicContractProjector().TopicMap, rs, stub);

        string? baseline = null;
        for (var i = 0; i < 10; i++)
        {
            var proj = await projector.ProjectAsync(BuildDoc(
                ("Other Heading", "Body about something semantic.")));
            var bytes = proj.Graph["sections"]!.ToJsonString();
            baseline ??= bytes;
            bytes.Should().Be(baseline);
        }
    }
}
