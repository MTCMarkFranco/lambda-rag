using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using LambdaRag.Indexing.Signatures;
using Xunit;

namespace LambdaRag.UnitTests.Indexing;

public class PredicateSignatureExtractorTests
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

    [Fact]
    public void TruePredicate_BecomesUniversal()
    {
        var sig = PredicateSignatureExtractor.Extract(MakeRule("R1", "true"));
        sig.Universal.Should().BeTrue();
        sig.Equalities.Should().BeEmpty();
        sig.Containments.Should().BeEmpty();
    }

    [Fact]
    public void EmptyPredicate_BecomesUniversal()
    {
        var sig = PredicateSignatureExtractor.Extract(MakeRule("R1", ""));
        sig.Universal.Should().BeTrue();
    }

    [Fact]
    public void EqualityPredicate_ExtractedExactly()
    {
        var sig = PredicateSignatureExtractor.Extract(
            MakeRule("R1", "input1.category == \"payment_terms\""));
        sig.Universal.Should().BeFalse();
        sig.Equalities.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new EqualityConstraint("input1.category", "payment_terms"));
    }

    [Fact]
    public void EqualityPredicateReversed_ExtractedExactly()
    {
        var sig = PredicateSignatureExtractor.Extract(
            MakeRule("R1", "\"payment_terms\" == input1.category"));
        sig.Equalities.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new EqualityConstraint("input1.category", "payment_terms"));
    }

    [Fact]
    public void ContainsPredicate_ExtractedExactly()
    {
        var sig = PredicateSignatureExtractor.Extract(
            MakeRule("R1", "input1.text.Contains(\"GDPR\")"));
        sig.Containments.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ContainsConstraint("input1.text", "GDPR"));
    }

    [Fact]
    public void NestedPath_PrependsInput1()
    {
        var sig = PredicateSignatureExtractor.Extract(
            MakeRule("R1", "input1.party.role == \"vendor\""));
        sig.Equalities.Single().FieldPath.Should().Be("input1.party.role");
    }

    [Fact]
    public void UnparseablePredicate_FallsBackToUniversal()
    {
        var sig = PredicateSignatureExtractor.Extract(
            MakeRule("R1", "ComplexFunc(input1.x) > 42"));
        sig.Universal.Should().BeTrue();
    }

    [Fact]
    public void Extraction_IsByteIdentical_GivenSamePredicate()
    {
        var rule = MakeRule("R1", "input1.category == \"a\" && input1.text.Contains(\"b\")");
        var first = PredicateSignatureExtractor.Extract(rule);
        var second = PredicateSignatureExtractor.Extract(rule);
        first.Should().BeEquivalentTo(second, opts => opts.WithStrictOrdering());
    }
}
