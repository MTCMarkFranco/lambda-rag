using FluentAssertions;
using LambdaRag.Authoring;
using LambdaRag.Core.Domain;
using Xunit;

namespace LambdaRag.UnitTests.Authoring;

public class DeterministicMockAuthoringAgentTests
{
    private static SourceSpan AnySpan() => new("chunk.txt", 0, 100, 1, null);

    [Fact]
    public async Task PaymentChunk_EmitsPaymentRule_WithPredicateLambdaRemediationAndEmbedding()
    {
        var agent = new DeterministicMockAuthoringAgent();
        var result = await agent.AuthorAsync(new RuleAuthoringRequest(
            SourceContent: "Customer shall pay all undisputed invoices within 30 days. Net 30 terms apply.",
            Domain: "contract",
            RuleIdPrefix: "RX-",
            SourceSpan: AnySpan()));

        result.Should().HaveCount(1);
        var s = result.Single();
        s.Rule.Id.Should().Be("RX-PAY-001");
        s.Rule.Predicate.Should().Be("input1.category == \"payment_terms\"");
        s.Rule.Lambda.Should().Contain("30 days");
        s.Rule.Remediation.Should().NotBeNullOrEmpty();
        s.Rule.SourceContent.Should().Be("Customer shall pay all undisputed invoices within 30 days. Net 30 terms apply.");
        s.Rule.SourceEmbedding.Should().NotBeNull();
        s.Rule.SourceEmbedding!.Should().HaveCount(32);
        s.Rule.Severity.Should().Be(RuleSeverity.Violation);
        s.Confidence.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task PolicyMentioningAllThreeTopics_EmitsThreeRules_InStableIdOrder()
    {
        var agent = new DeterministicMockAuthoringAgent();
        var result = await agent.AuthorAsync(new RuleAuthoringRequest(
            SourceContent: "Payment net 30. Governing law of Delaware. Personal data protected per GDPR.",
            Domain: "contract",
            RuleIdPrefix: "",
            SourceSpan: AnySpan()));

        result.Should().HaveCount(3);
        result.Select(r => r.Rule.Id).Should().Equal("DPA-001", "GOV-001", "PAY-001");
    }

    [Fact]
    public async Task UnrelatedChunk_ReturnsZeroSuggestions_RatherThanFabricating()
    {
        var agent = new DeterministicMockAuthoringAgent();
        var result = await agent.AuthorAsync(new RuleAuthoringRequest(
            SourceContent: "The fish is purple and likes warm tea.",
            Domain: "contract",
            RuleIdPrefix: "",
            SourceSpan: AnySpan()));

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfidentialityChunk_EmitsConfRule()
    {
        var agent = new DeterministicMockAuthoringAgent();
        var result = await agent.AuthorAsync(new RuleAuthoringRequest(
            SourceContent: "All Confidential Information shall be protected. Obligations survive termination.",
            Domain: "contract",
            RuleIdPrefix: "AC-",
            SourceSpan: AnySpan()));

        result.Should().ContainSingle(s => s.Rule.Id == "AC-CONF-001");
        result.Single(s => s.Rule.Id == "AC-CONF-001").Rule.Predicate
            .Should().Be("input1.category == \"confidentiality\"");
    }

    [Fact]
    public async Task LiabilityCapChunk_EmitsLiabRule_Critical()
    {
        var agent = new DeterministicMockAuthoringAgent();
        var result = await agent.AuthorAsync(new RuleAuthoringRequest(
            SourceContent: "Limitation of liability: total amount paid in twelve months preceding the claim.",
            Domain: "contract",
            RuleIdPrefix: "",
            SourceSpan: AnySpan()));

        var liab = result.Single(s => s.Rule.Id == "LIAB-001");
        liab.Rule.Severity.Should().Be(RuleSeverity.Critical);
        liab.Rule.Predicate.Should().Be("input1.category == \"liability\"");
    }

    [Fact]
    public async Task WarrantyChunk_EmitsWarRule()
    {
        var agent = new DeterministicMockAuthoringAgent();
        var result = await agent.AuthorAsync(new RuleAuthoringRequest(
            SourceContent: "Provider warrants that Services will conform; remedy is to replace within 30 days.",
            Domain: "contract",
            RuleIdPrefix: "",
            SourceSpan: AnySpan()));

        result.Should().ContainSingle(s => s.Rule.Id == "WAR-001");
    }

    [Fact]
    public async Task TerminationChunk_EmitsTrmRule()
    {
        var agent = new DeterministicMockAuthoringAgent();
        var result = await agent.AuthorAsync(new RuleAuthoringRequest(
            SourceContent: "Either party may terminate by giving 60 calendar days prior written notice.",
            Domain: "contract",
            RuleIdPrefix: "",
            SourceSpan: AnySpan()));

        result.Should().ContainSingle(s => s.Rule.Id == "TRM-001");
        result.Single(s => s.Rule.Id == "TRM-001").Rule.Predicate
            .Should().Be("input1.category == \"termination\"");
    }

    [Fact]
    public async Task IpOwnershipChunk_EmitsIpRule()
    {
        var agent = new DeterministicMockAuthoringAgent();
        var result = await agent.AuthorAsync(new RuleAuthoringRequest(
            SourceContent: "All work product is intellectual property assigned to Customer as work for hire.",
            Domain: "contract",
            RuleIdPrefix: "",
            SourceSpan: AnySpan()));

        result.Should().ContainSingle(s => s.Rule.Id == "IP-001");
    }

    [Fact]
    public async Task SameChunk_TwiceProducesEqualRules_IncludingEmbeddings()
    {
        var agent = new DeterministicMockAuthoringAgent();
        var req = new RuleAuthoringRequest(
            SourceContent: "Invoices paid within net 30 days.",
            Domain: "contract",
            RuleIdPrefix: "",
            SourceSpan: AnySpan());

        var a = await agent.AuthorAsync(req);
        var b = await agent.AuthorAsync(req);

        a.Should().HaveCount(1);
        b.Should().HaveCount(1);
        a[0].Rule.Fingerprint().Value.Should().Be(b[0].Rule.Fingerprint().Value);
        a[0].Rule.SourceEmbedding!.Should().BeEquivalentTo(b[0].Rule.SourceEmbedding!);
    }
}
