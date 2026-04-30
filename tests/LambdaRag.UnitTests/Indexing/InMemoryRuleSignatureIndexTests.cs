using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using LambdaRag.Indexing.InMemory;
using Xunit;

namespace LambdaRag.UnitTests.Indexing;

public class InMemoryRuleSignatureIndexTests
{
    private static Rule MakeRule(string id, string predicate) => new(
        Id: id,
        Version: "1.0.0",
        NaturalLanguage: id,
        Lambda: "true",
        AppliesToSchema: new JsonObject(),
        Selector: new PathSelector("$.sections[*]"),
        Severity: RuleSeverity.Violation,
        SourceSpan: new SourceSpan("p", 0, 0, 1, null),
        EvidenceQuote: id,
        Metadata: new Dictionary<string, string>())
    {
        Predicate = predicate,
    };

    private static RuleSet RuleSet(params Rule[] rules) => new(
        Id: "rs", Version: "1", Domain: "contract",
        PublishedAt: DateTimeOffset.UnixEpoch, Rules: rules,
        Metadata: new Dictionary<string, string>());

    [Fact]
    public void Lookup_AlwaysIncludesUniversalRules()
    {
        var idx = new InMemoryRuleSignatureIndex();
        idx.Build(RuleSet(
            MakeRule("UNI-001", "true"),
            MakeRule("PAY-001", "input1.category == \"payment_terms\"")));

        var section = new JsonObject { ["category"] = "governing_law" };
        var hits = idx.Lookup(section);

        hits.Should().Contain("UNI-001");
        hits.Should().NotContain("PAY-001");
    }

    [Fact]
    public void Lookup_NarrowsByEqualityConstraint()
    {
        var idx = new InMemoryRuleSignatureIndex();
        idx.Build(RuleSet(
            MakeRule("PAY-001", "input1.category == \"payment_terms\""),
            MakeRule("GOV-001", "input1.category == \"governing_law\""),
            MakeRule("DPA-001", "input1.category == \"privacy\"")));

        var hits = idx.Lookup(new JsonObject { ["category"] = "payment_terms" });

        hits.Should().BeEquivalentTo(new[] { "PAY-001" });
    }

    [Fact]
    public void Lookup_IsStrictSupersetOfPredicateMatch()
    {
        var idx = new InMemoryRuleSignatureIndex();
        idx.Build(RuleSet(
            MakeRule("UNI", "true"),
            MakeRule("PAY", "input1.category == \"payment_terms\""),
            MakeRule("GOV", "input1.category == \"governing_law\"")));

        var pay = idx.Lookup(new JsonObject { ["category"] = "payment_terms" });
        var gov = idx.Lookup(new JsonObject { ["category"] = "governing_law" });
        var other = idx.Lookup(new JsonObject { ["category"] = "privacy" });

        pay.Should().BeEquivalentTo(new[] { "PAY", "UNI" });
        gov.Should().BeEquivalentTo(new[] { "GOV", "UNI" });
        other.Should().BeEquivalentTo(new[] { "UNI" });
    }

    [Fact]
    public void Lookup_OrderIsDeterministic()
    {
        var idx = new InMemoryRuleSignatureIndex();
        idx.Build(RuleSet(
            MakeRule("Z", "true"),
            MakeRule("A", "true"),
            MakeRule("M", "true")));

        var hits = idx.Lookup(new JsonObject { ["category"] = "x" });
        hits.Should().Equal("A", "M", "Z");
    }

    [Fact]
    public void Build_IsIdempotent()
    {
        var idx = new InMemoryRuleSignatureIndex();
        var rs = RuleSet(MakeRule("R1", "input1.category == \"a\""));
        idx.Build(rs);
        var first = idx.Lookup(new JsonObject { ["category"] = "a" }).ToArray();
        idx.Build(rs);
        var second = idx.Lookup(new JsonObject { ["category"] = "a" }).ToArray();
        first.Should().Equal(second);
    }
}
