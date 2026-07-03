using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Facts;
using LambdaRag.Core.Selectors;
using Xunit;

namespace LambdaRag.UnitTests.Facts;

public class FactSchemaFingerprintTests
{
    private static Rule MakeRule(string id = "R-1") => new(
        Id: id,
        Version: "1.0.0",
        NaturalLanguage: "must encrypt",
        Lambda: "input1.text.Contains(\"encrypt\")",
        AppliesToSchema: new JsonObject(),
        Selector: new PathSelector("$"),
        Severity: RuleSeverity.Violation,
        SourceSpan: new SourceSpan("d1", 0, 1, null, null),
        EvidenceQuote: "must encrypt",
        Metadata: new Dictionary<string, string>());

    private static RuleSet MakeRuleSet(FactSchema? schema = null, params Rule[] rules) => new(
        Id: "rs",
        Version: "1.0.0",
        Domain: "test",
        PublishedAt: DateTimeOffset.UnixEpoch,
        Rules: rules.Length == 0 ? new[] { MakeRule() } : rules,
        Metadata: new Dictionary<string, string>())
    {
        FactSchema = schema,
    };

    [Fact]
    public void Rule_Fingerprint_Unchanged_When_Pillar12_Fields_Null()
    {
        var a = MakeRule();
        var b = MakeRule() with { EvaluationMode = null, RequiredFacts = null };
        b.Fingerprint().Value.Should().Be(a.Fingerprint().Value);
    }

    [Fact]
    public void Rule_Fingerprint_Changes_When_EvaluationMode_Set()
    {
        var a = MakeRule();
        var b = MakeRule() with { EvaluationMode = "facts" };
        b.Fingerprint().Value.Should().NotBe(a.Fingerprint().Value);
    }

    [Fact]
    public void Rule_Fingerprint_Changes_When_RequiredFacts_Set()
    {
        var a = MakeRule();
        var b = MakeRule() with { RequiredFacts = new[] { "encryption_declared" } };
        b.Fingerprint().Value.Should().NotBe(a.Fingerprint().Value);
    }

    [Fact]
    public void Rule_Fingerprint_RequiredFacts_Order_Independent()
    {
        var a = MakeRule() with { RequiredFacts = new[] { "a", "b", "c" } };
        var b = MakeRule() with { RequiredFacts = new[] { "c", "a", "b" } };
        b.Fingerprint().Value.Should().Be(a.Fingerprint().Value);
    }

    // ── RequiredFactsAny (batch 3, issue #154) ──

    [Fact]
    public void Rule_Fingerprint_Unchanged_When_RequiredFactsAny_Null()
    {
        var a = MakeRule();
        var b = MakeRule() with { RequiredFactsAny = null };
        b.Fingerprint().Value.Should().Be(a.Fingerprint().Value);
    }

    [Fact]
    public void Rule_Fingerprint_Unchanged_When_RequiredFactsAny_Empty()
    {
        var a = MakeRule();
        var b = MakeRule() with { RequiredFactsAny = Array.Empty<string>() };
        b.Fingerprint().Value.Should().Be(a.Fingerprint().Value);
    }

    [Fact]
    public void Rule_Fingerprint_Changes_When_RequiredFactsAny_Set()
    {
        var a = MakeRule();
        var b = MakeRule() with { RequiredFactsAny = new[] { "iac_managed", "deletion_days" } };
        b.Fingerprint().Value.Should().NotBe(a.Fingerprint().Value);
    }

    [Fact]
    public void Rule_Fingerprint_RequiredFactsAny_Order_Independent()
    {
        var a = MakeRule() with { RequiredFactsAny = new[] { "x", "y", "z" } };
        var b = MakeRule() with { RequiredFactsAny = new[] { "z", "y", "x" } };
        b.Fingerprint().Value.Should().Be(a.Fingerprint().Value);
    }

    [Fact]
    public void Rule_Fingerprint_Distinguishes_RequiredFacts_From_RequiredFactsAny()
    {
        // AND-gate on {a,b} must not hash identically to OR-gate on {a,b}.
        var a = MakeRule() with { RequiredFacts = new[] { "a", "b" } };
        var b = MakeRule() with { RequiredFactsAny = new[] { "a", "b" } };
        b.Fingerprint().Value.Should().NotBe(a.Fingerprint().Value);
    }

    [Fact]
    public void RuleSet_Fingerprint_Unchanged_When_FactSchema_Null()
    {
        var a = MakeRuleSet();
        var b = MakeRuleSet(schema: null);
        b.Fingerprint().Value.Should().Be(a.Fingerprint().Value);
    }

    [Fact]
    public void RuleSet_Fingerprint_Changes_When_FactSchema_Set()
    {
        var schema = new FactSchema("s", "1", new[]
        {
            new FactConcept("encryption_declared", FactType.Boolean, "encrypt?"),
        });
        var a = MakeRuleSet();
        var b = MakeRuleSet(schema);
        b.Fingerprint().Value.Should().NotBe(a.Fingerprint().Value);
    }

    [Fact]
    public void FactSchema_Fingerprint_Concept_Order_Independent()
    {
        var s1 = new FactSchema("s", "1", new[]
        {
            new FactConcept("a", FactType.Boolean, "d"),
            new FactConcept("b", FactType.Integer, "d"),
        });
        var s2 = new FactSchema("s", "1", new[]
        {
            new FactConcept("b", FactType.Integer, "d"),
            new FactConcept("a", FactType.Boolean, "d"),
        });
        s2.Fingerprint().Value.Should().Be(s1.Fingerprint().Value);
    }

    [Fact]
    public void FactSchema_Fingerprint_EnumValues_Order_Independent()
    {
        var s1 = new FactSchema("s", "1", new[]
        {
            new FactConcept("algo", FactType.Enum, "d") { EnumValues = new[] { "A", "B", "C" } },
        });
        var s2 = new FactSchema("s", "1", new[]
        {
            new FactConcept("algo", FactType.Enum, "d") { EnumValues = new[] { "C", "A", "B" } },
        });
        s2.Fingerprint().Value.Should().Be(s1.Fingerprint().Value);
    }

    [Fact]
    public void FactSchema_Fingerprint_Distinguishes_Type_Change()
    {
        var s1 = new FactSchema("s", "1", new[] { new FactConcept("a", FactType.Boolean, "d") });
        var s2 = new FactSchema("s", "1", new[] { new FactConcept("a", FactType.Integer, "d") });
        s2.Fingerprint().Value.Should().NotBe(s1.Fingerprint().Value);
    }

    [Fact]
    public void FactSchema_Fingerprint_Distinguishes_Version()
    {
        var s1 = new FactSchema("s", "1", new[] { new FactConcept("a", FactType.Boolean, "d") });
        var s2 = new FactSchema("s", "2", new[] { new FactConcept("a", FactType.Boolean, "d") });
        s2.Fingerprint().Value.Should().NotBe(s1.Fingerprint().Value);
    }

    [Fact]
    public void FactSchema_Fingerprint_Distinguishes_Normalizer()
    {
        var s1 = new FactSchema("s", "1", new[] { new FactConcept("a", FactType.Duration, "d") });
        var s2 = new FactSchema("s", "1", new[] { new FactConcept("a", FactType.Duration, "d") { Normalizer = "duration-iso8601" } });
        s2.Fingerprint().Value.Should().NotBe(s1.Fingerprint().Value);
    }

    [Fact]
    public void RuleSet_Fingerprint_Deterministic_Across_Calls()
    {
        var schema = new FactSchema("s", "1", new[] { new FactConcept("a", FactType.Boolean, "d") });
        var rs = MakeRuleSet(schema);
        rs.Fingerprint().Value.Should().Be(rs.Fingerprint().Value);
    }
}
