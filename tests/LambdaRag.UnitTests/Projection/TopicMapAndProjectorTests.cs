using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Projection;
using LambdaRag.Projection.Projectors;
using Xunit;

namespace LambdaRag.UnitTests.Projection;

public class TopicMapAndProjectorTests
{
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
            Id: ContentHash.OfBytes(System.Text.Encoding.UTF8.GetBytes(string.Join("|", sections))),
            FileName: "test.md",
            Kind: SourceDocumentKind.Markdown,
            ByteLength: offset,
            IngestedAt: DateTimeOffset.UnixEpoch);
        return new ParsedDocument(src, "", blocks, new Dictionary<string, string>());
    }

    [Fact]
    public void DefaultTopicMap_LoadsFromEmbeddedResource()
    {
        var p = new DeterministicContractProjector();
        p.TopicMap.Domain.Should().Be("contract");
        p.TopicMap.Topics.Should().NotBeEmpty();
        p.TopicMap.Axes.Should().ContainKey("jurisdiction");
    }

    [Fact]
    public async Task PrimaryTopic_ClassifiesByHeading_AsBefore()
    {
        var p = new DeterministicContractProjector();
        var doc = BuildDoc(
            ("Limitation of Liability", "Liability shall be capped at fees paid in twelve months."));
        var proj = await p.ProjectAsync(doc);
        var section = proj.Graph["sections"]!.AsArray()[0]!.AsObject();
        section["primary_topic"]!.GetValue<string>().Should().Be("liability");
        section["category"]!.GetValue<string>().Should().Be("liability"); // back-compat alias
    }

    [Fact]
    public async Task JurisdictionAxis_AddsTopicTagWithoutBecomingPrimary()
    {
        var p = new DeterministicContractProjector();
        var doc = BuildDoc(
            ("Limitation of Liability", "cap"),
            ("Hungary", "Supplement Terms and Conditions section 6 titled \"Limitation of liability\" with the following: ..."));
        var proj = await p.ProjectAsync(doc);
        var hungary = proj.Graph["sections"]!.AsArray()[1]!.AsObject();
        var topics = hungary["topics"]!.AsArray().Select(t => t!.GetValue<string>()).ToList();
        topics.Should().Contain("jurisdiction:hungary");
        hungary["primary_topic"]!.GetValue<string>().Should().Be("liability"); // inherited
    }

    [Fact]
    public async Task AmendmentXref_InheritsPrimaryTopic_FromReferencedSection()
    {
        var p = new DeterministicContractProjector();
        var doc = BuildDoc(
            ("Warranties.", "Microsoft warrants Services."),
            ("Australia", "Supplement Terms and Conditions section 4 titled \"Warranties\" with the following consumer-remedies clause."));
        var proj = await p.ProjectAsync(doc);
        var aus = proj.Graph["sections"]!.AsArray()[1]!.AsObject();
        aus["primary_topic"]!.GetValue<string>().Should().Be("warranty");
        aus["inherited_from"]!.GetValue<string>().Should().Be("s_00000000");
    }

    [Fact]
    public async Task UnknownSection_IsSurfaced_NotSilentlyBucketed()
    {
        var p = new DeterministicContractProjector();
        var doc = BuildDoc(
            ("Cover Page", "This is the cover boilerplate with no rule shape."));
        var proj = await p.ProjectAsync(doc);
        proj.Graph["unknown_sections"]!.AsArray().Should().HaveCount(1);
        var sec = proj.Graph["sections"]!.AsArray()[0]!.AsObject();
        sec["primary_topic"]!.GetValue<string>().Should().Be("unknown");
    }

    [Fact]
    public async Task CustomTopicMap_OverridesDefault_ForNewDomains()
    {
        var json = """
        {
          "domain": "architecture-review",
          "version": "0.1.0",
          "topics": [
            { "id": "scalability", "keywords": ["scalability", "scale", "load"] },
            { "id": "security",    "keywords": ["security", "threat model"] }
          ],
          "axes": {},
          "amendmentPatterns": []
        }
        """;
        var map = TopicMap.LoadFromJson(json);
        var p = new DeterministicContractProjector(map);
        var doc = BuildDoc(("Scalability concerns", "must scale to 1M req/s"));
        var proj = await p.ProjectAsync(doc);
        var sec = proj.Graph["sections"]!.AsArray()[0]!.AsObject();
        sec["primary_topic"]!.GetValue<string>().Should().Be("scalability");
        proj.Graph["topic_map"]!.GetValue<string>().Should().Be("architecture-review@0.1.0");
    }

    [Fact]
    public async Task SameInputs_TwoProjections_AreByteEqual()
    {
        var p = new DeterministicContractProjector();
        var doc = BuildDoc(
            ("Warranties.", "Provider warrants Services for 30 days."),
            ("Australia", "Supplement section 4 titled \"Warranties\" with consumer-remedies clause."),
            ("Hungary", "Supplement section 6 titled \"Limitation of liability\" with negotiated terms."));
        var a = await p.ProjectAsync(doc);
        var b = await p.ProjectAsync(doc);
        a.Graph.ToJsonString().Should().Be(b.Graph.ToJsonString());
    }

    // -----------------------------------------------------------------
    // #44 — Vocabulary-density tie-break for same-topic sections.
    //
    // When a contract mentions a topic twice — once near the top with only
    // a heading reference, and again later with the operative obligation —
    // the projector must flag the *richer* section as operative so rule
    // authors can target it via
    //   predicate: input1.primary_topic == "X" && input1.is_operative_for_topic
    // This pair of tests pins that behaviour.
    // -----------------------------------------------------------------

    [Fact]
    public async Task IsOperativeForTopic_FlagsDensestSection_WhenTopicAppearsTwice()
    {
        var p = new DeterministicContractProjector();
        var doc = BuildDoc(
            // Sparse early heading mention — classifies as payment_terms via
            // heading keyword "payment", but body has no real obligation.
            ("Payment terms",
             "See Schedule A for fee details. Cross-reference clause 12."),
            // Operative section much later — heading still matches, but the
            // body is dense with payment-vocabulary keywords.
            ("Services payment terms",
             "Customer agrees to pay all fees within 30 calendar days of invoice. " +
             "Late payment of fees accrues interest. All fees and compensation " +
             "shall be invoiced monthly. Payment shall be made by wire."));
        var proj = await p.ProjectAsync(doc);
        var sections = proj.Graph["sections"]!.AsArray();

        var sparse = sections[0]!.AsObject();
        var dense = sections[1]!.AsObject();
        sparse["primary_topic"]!.GetValue<string>().Should().Be("payment_terms");
        dense["primary_topic"]!.GetValue<string>().Should().Be("payment_terms");

        sparse["is_operative_for_topic"]!.GetValue<bool>().Should().BeFalse(
            "the early heading-only mention has no operative obligation language");
        dense["is_operative_for_topic"]!.GetValue<bool>().Should().BeTrue(
            "the later section with rich payment-vocabulary density carries the obligation");

        dense["topic_density"]!.GetValue<double>().Should()
            .BeGreaterThan(sparse["topic_density"]!.GetValue<double>());
    }

    [Fact]
    public async Task IsOperativeForTopic_IsTrue_OnSoleSectionForTopic()
    {
        var p = new DeterministicContractProjector();
        var doc = BuildDoc(("Limitation of Liability",
            "Liability shall be capped at fees paid in twelve months."));
        var proj = await p.ProjectAsync(doc);
        var section = proj.Graph["sections"]!.AsArray()[0]!.AsObject();
        section["is_operative_for_topic"]!.GetValue<bool>().Should().BeTrue(
            "a topic with exactly one section is by definition operative there");
    }

    [Fact]
    public async Task IsOperativeForTopic_NeverFlagsUnknownSections()
    {
        var p = new DeterministicContractProjector();
        var doc = BuildDoc(("Random Heading", "Body with no recognized topic vocabulary."));
        var proj = await p.ProjectAsync(doc);
        var section = proj.Graph["sections"]!.AsArray()[0]!.AsObject();
        section["primary_topic"]!.GetValue<string>().Should().Be("unknown");
        section["is_operative_for_topic"]!.GetValue<bool>().Should().BeFalse(
            "the synthetic 'unknown' bucket is not a real topic and must never " +
            "carry an operative section");
    }
}
