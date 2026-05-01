using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Selectors;
using LambdaRag.Evaluation.Engine;
using LambdaRag.Projection.Projectors;
using LambdaRag.Selectors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LambdaRag.UnitTests.Evaluation;

/// <summary>
/// Genericness contract: the engine evaluates lambdas over the
/// <c>text_features</c> sub-graph regardless of the calling domain. We
/// prove this by constructing a <em>completely synthetic, non-contract,
/// non-Contoso</em> ruleset and a synthetic projected document, then
/// evaluating end-to-end. If a future change accidentally hardcodes
/// behaviour against Contoso rule ids, contract topics, or specific keyword
/// sets, these tests fail.
///
/// This is the test the user asked for: "make sure they genuinely pass
/// in a generic fashion."
/// </summary>
public class GenericTextFeaturesEvaluationTests
{
    private static readonly TimeProvider Frozen = new TestFrozenTimeProvider(
        new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class TestFrozenTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private static EvaluationService Build()
    {
        var matcher = new JsonPathSelectorMatcher(NullLogger<JsonPathSelectorMatcher>.Instance);
        return new EvaluationService(matcher, NullLogger<EvaluationService>.Instance, Frozen);
    }

    /// <summary>
    /// Builds a section node with text_features pre-populated by the
    /// real extractor — the same code path as the projector.
    /// </summary>
    private static JsonObject Section(string id, string category, string text, params string[] topics)
    {
        var topicsArr = new JsonArray();
        foreach (var t in topics) topicsArr.Add(t);
        return new JsonObject
        {
            ["id"] = id,
            ["heading"] = id,
            ["category"] = category,
            ["primary_topic"] = category,
            ["topics"] = topicsArr,
            ["is_operative_for_topic"] = true,
            ["text"] = text,
            ["text_features"] = TextFeatureExtractor.Extract(text),
        };
    }

    private static ProjectedDocument Doc(params JsonObject[] sections)
    {
        var arr = new JsonArray();
        foreach (var s in sections) arr.Add(s);
        return new ProjectedDocument(
            ContentHash.OfString("doc-bytes"),
            "test-projector",
            "1.0",
            new JsonObject { ["sections"] = arr },
            new Dictionary<string, SourceSpan>(StringComparer.Ordinal));
    }

    private static Rule Rule(string id, string predicate, string lambda) => new(
        Id: id,
        Version: "1.0.0",
        NaturalLanguage: $"Rule {id}",
        Lambda: lambda,
        AppliesToSchema: new JsonObject(),
        Selector: new PathSelector("$.sections[*]"),
        Severity: RuleSeverity.Violation,
        SourceSpan: new SourceSpan("policy", 0, 0, 1, null),
        EvidenceQuote: id,
        Metadata: new Dictionary<string, string>())
    { Predicate = predicate };

    private static RuleSet Ruleset(string id, params Rule[] rules) => new(
        Id: id,
        Version: "1.0.0",
        Domain: "synthetic-vendor-policy",  // *not* "contract"
        PublishedAt: DateTimeOffset.UnixEpoch,
        Rules: rules,
        Metadata: new Dictionary<string, string>());

    [Fact]
    public async Task Generic_dollar_threshold_rule_works_on_arbitrary_domain()
    {
        // Synthetic vendor policy unrelated to Contoso: minimum project bond
        // must be >= $2,500,000.
        var rule = Rule(
            "VENDOR-BOND-MIN",
            predicate: "input1.topics.Contains(\"financial_assurance\") && input1.text_features.dollar_amounts.Count > 0",
            lambda: "input1.text_features.dollar_max >= 2500000");

        var s1 = Section("s1", "fin", "Bidder shall post a $1,000,000 bond.", "financial_assurance");
        var s2 = Section("s2", "fin", "Top-tier bidders post a $5,000,000 bond.", "financial_assurance");
        var s3 = Section("s3", "fin", "No bond required.", "other");

        var report = await Build().EvaluateAsync(
            Ruleset("rs-vendor-x", rule),
            Doc(s1, s2, s3));

        // Both s1 and s2 match predicate (have dollar_amounts and the
        // financial_assurance topic). s3 does not have the topic.
        report.Verdicts.Should().HaveCount(2);
        report.Verdicts.Single(v => v.MatchedSectionId == "s1").Outcome.Should().Be(VerdictOutcome.Fail);   // 1M < 2.5M
        report.Verdicts.Single(v => v.MatchedSectionId == "s2").Outcome.Should().Be(VerdictOutcome.Pass);   // 5M >= 2.5M
    }

    [Fact]
    public async Task Generic_day_count_rule_works_on_arbitrary_domain()
    {
        // Synthetic permitting policy: response window must be <= 60 days.
        var rule = Rule(
            "PERMIT-RESPONSE-MAX",
            predicate: "input1.topics.Contains(\"permit_response\") && input1.text_features.day_counts.Count > 0",
            lambda: "input1.text_features.day_count_max <= 60");

        var report = await Build().EvaluateAsync(
            Ruleset("rs-municipal", rule),
            Doc(
                Section("s1", "permit", "Applicant must respond within 45 calendar days.", "permit_response"),
                Section("s2", "permit", "Late applicants get a 120-day cure window.", "permit_response")));

        var s1 = report.Verdicts.Single(v => v.MatchedSectionId == "s1");
        var s2 = report.Verdicts.Single(v => v.MatchedSectionId == "s2");
        s1.Outcome.Should().Be(VerdictOutcome.Pass);   // 45 <= 60
        s2.Outcome.Should().Be(VerdictOutcome.Fail);   // 120 > 60
    }

    [Fact]
    public async Task Generic_percent_threshold_rule_works_on_arbitrary_domain()
    {
        // Synthetic safety standard: minimum recyclable content must be >= 30%.
        var rule = Rule(
            "ESG-RECYCLED-MIN",
            predicate: "input1.topics.Contains(\"esg_content\") && input1.text_features.percent_values.Count > 0",
            lambda: "input1.text_features.percent_max >= 30");

        var report = await Build().EvaluateAsync(
            Ruleset("rs-esg", rule),
            Doc(
                Section("s1", "esg", "Packaging contains 35% recycled material.", "esg_content"),
                Section("s2", "esg", "Substrate contains 12% recycled material.", "esg_content")));

        var s1 = report.Verdicts.Single(v => v.MatchedSectionId == "s1");
        var s2 = report.Verdicts.Single(v => v.MatchedSectionId == "s2");
        s1.Outcome.Should().Be(VerdictOutcome.Pass);
        s2.Outcome.Should().Be(VerdictOutcome.Fail);
    }

    [Fact]
    public async Task Predicate_using_text_features_count_does_not_throw_on_empty_section()
    {
        // A common defensive pattern in rule authoring: gate on
        // `text_features.X.Count > 0` so the lambda can safely compare
        // against the *_max scalar. Sections without any extracted values
        // must NOT raise an Error verdict — they should simply not apply.
        var rule = Rule(
            "DEFENSIVE",
            predicate: "input1.text_features.dollar_amounts.Count > 0",
            lambda: "input1.text_features.dollar_max >= 100");

        var report = await Build().EvaluateAsync(
            Ruleset("rs-defensive", rule),
            Doc(
                Section("s1", "x", "No dollar amounts here at all.", "any"),
                Section("s2", "x", "Bond of $200.", "any")));

        // s1 fails the predicate so no verdict is emitted for it.
        // Exactly one verdict for s2; no Errors anywhere.
        report.Verdicts.Should().HaveCount(1);
        report.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Pass);
        report.Errored.Should().Be(0);
    }
}

