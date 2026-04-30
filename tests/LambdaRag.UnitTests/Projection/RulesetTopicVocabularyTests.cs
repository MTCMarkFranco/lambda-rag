using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using LambdaRag.Projection;
using Xunit;

namespace LambdaRag.UnitTests.Projection;

public class RulesetTopicVocabularyTests
{
    private static Rule MakeRule(string id, string predicate) =>
        new Rule(
            Id: id, Version: "1.0.0", NaturalLanguage: id, Lambda: "true",
            AppliesToSchema: new JsonObject(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Violation,
            SourceSpan: new SourceSpan("p", 0, 0, 1, null),
            EvidenceQuote: id,
            Metadata: new Dictionary<string, string>())
        { Predicate = predicate };

    [Fact]
    public void Extract_finds_category_and_primary_topic_literals()
    {
        var rules = new[]
        {
            MakeRule("R1", "input1.category == \"payment_terms\""),
            MakeRule("R2", "input1.primary_topic == \"liability\""),
            MakeRule("R3", "input1.topics.Contains(\"data_protection\")"),
            MakeRule("R4", "input1.topics.HasTopic(\"warranty\")"),
            MakeRule("R5", "true"),
        };

        var topics = RulesetTopicVocabulary.Extract(rules);

        topics.Should().BeEquivalentTo(new[]
        {
            "payment_terms", "liability", "data_protection", "warranty",
        });
    }

    [Fact]
    public void Coverage_reports_missing_and_unused_topics()
    {
        var rules = new[]
        {
            MakeRule("R1", "input1.category == \"payment_terms\""),
            MakeRule("R2", "input1.category == \"made_up_topic\""),
        };
        var map = TopicMapRegistry.Load("contract.v1");

        var cov = RulesetTopicVocabulary.Coverage(rules, map);

        cov.MissingFromMap.Should().Contain("made_up_topic");
        cov.MissingFromMap.Should().NotContain("payment_terms");
        cov.IsFullyCovered.Should().BeFalse();
        cov.Declared.Should().Contain("payment_terms");
    }

    [Fact]
    public void Coverage_clean_when_all_referenced_topics_declared()
    {
        var rules = new[] { MakeRule("R1", "input1.category == \"payment_terms\"") };
        var map = TopicMapRegistry.Load("contract.v1");

        var cov = RulesetTopicVocabulary.Coverage(rules, map);

        cov.MissingFromMap.Should().BeEmpty();
        cov.IsFullyCovered.Should().BeTrue();
    }
}
