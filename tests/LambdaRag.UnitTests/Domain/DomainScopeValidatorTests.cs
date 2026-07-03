using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using Xunit;

namespace LambdaRag.UnitTests.Domain;

/// <summary>
/// Unit tests for <see cref="DomainScopeValidator"/> — the entry-point
/// guardrail for issue #159 (domain-scoped review).
/// </summary>
public class DomainScopeValidatorTests
{
    private static RuleSet MakeRuleSet(string domain) => new(
        Id: "rs-test",
        Version: "1.0.0",
        Domain: domain,
        PublishedAt: DateTimeOffset.UnixEpoch,
        Rules: new[]
        {
            new Rule(
                Id: "R1", Version: "1.0.0",
                NaturalLanguage: "n", Lambda: "true",
                AppliesToSchema: new JsonObject(),
                Selector: new PathSelector("$"),
                Severity: RuleSeverity.Violation,
                SourceSpan: new SourceSpan("d1", 0, 1, null, null),
                EvidenceQuote: "e",
                Metadata: new Dictionary<string, string>()),
        },
        Metadata: new Dictionary<string, string>());

    [Fact]
    public void Null_Declared_Domain_Is_Silent_Pass()
    {
        var rs = MakeRuleSet("enterprise-architecture");
        var act = () => DomainScopeValidator.RequireMatch(null, rs);
        act.Should().NotThrow();
    }

    [Fact]
    public void Whitespace_Declared_Domain_Is_Silent_Pass()
    {
        var rs = MakeRuleSet("enterprise-architecture");
        var act = () => DomainScopeValidator.RequireMatch("   ", rs);
        act.Should().NotThrow();
    }

    [Fact]
    public void Matching_Domain_Passes()
    {
        var rs = MakeRuleSet("enterprise-architecture");
        var act = () => DomainScopeValidator.RequireMatch("enterprise-architecture", rs);
        act.Should().NotThrow();
    }

    [Fact]
    public void Case_Insensitive_Match_Passes()
    {
        var rs = MakeRuleSet("enterprise-architecture");
        var act = () => DomainScopeValidator.RequireMatch("Enterprise-Architecture", rs);
        act.Should().NotThrow();
    }

    [Fact]
    public void Mismatched_Domain_Throws_DomainMismatchException()
    {
        var rs = MakeRuleSet("enterprise-architecture");
        var act = () => DomainScopeValidator.RequireMatch("healthcare", rs);

        var ex = act.Should().Throw<DomainMismatchException>().Which;
        ex.DeclaredDomain.Should().Be("healthcare");
        ex.RulesetDomain.Should().Be("enterprise-architecture");
        ex.RulesetId.Should().Be("rs-test");
        ex.Message.Should().Contain("healthcare");
        ex.Message.Should().Contain("enterprise-architecture");
        ex.Message.Should().Contain("rs-test");
    }

    [Fact]
    public void Null_RuleSet_Throws_ArgumentNullException()
    {
        var act = () => DomainScopeValidator.RequireMatch("x", null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
