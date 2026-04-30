using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Selectors;
using LambdaRag.Evaluation.Engine;
using LambdaRag.Selectors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LambdaRag.UnitTests.Evaluation;

/// <summary>
/// End-to-end tests for predicate-gated evaluation. Selector returns
/// candidates; predicate is the *applicability gate* that decides which
/// candidates the lambda runs against. No LLM is involved at any step.
/// </summary>
public class RulePredicateGateTests
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

    private static ProjectedDocument Doc(params (string id, string category, string text)[] sections)
    {
        var arr = new JsonArray();
        foreach (var (id, cat, text) in sections)
        {
            arr.Add(new JsonObject
            {
                ["id"] = id,
                ["category"] = cat,
                ["text"] = text,
                ["heading"] = id,
            });
        }
        var graph = new JsonObject { ["sections"] = arr };
        return new ProjectedDocument(
            ContentHash.OfString("doc-bytes"),
            "test-projector",
            "1.0",
            graph,
            new Dictionary<string, SourceSpan>(StringComparer.Ordinal));
    }

    private static Rule MakeRule(
        string id,
        string predicate,
        string lambda,
        string? remediation = null) => new(
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
    {
        Predicate = predicate,
        Remediation = remediation,
    };

    private static RuleSet RuleSet(params Rule[] rules) => new(
        Id: "rs-test",
        Version: "1.0.0",
        Domain: "contract",
        PublishedAt: DateTimeOffset.UnixEpoch,
        Rules: rules,
        Metadata: new Dictionary<string, string>());

    [Fact]
    public async Task PredicateFiltersOutNonMatchingSections_PassingSectionEmitsPass()
    {
        // Selector returns ALL sections; predicate gates to category=payment_terms only.
        var rule = MakeRule(
            "PAY-001",
            predicate: "input1.category == \"payment_terms\"",
            lambda: "input1.text.Contains(\"30 days\")");
        var doc = Doc(
            ("s1", "governing_law", "Delaware applies."),
            ("s2", "payment_terms", "Pay in 30 days."),
            ("s3", "privacy", "GDPR applies."));

        var report = await Build().EvaluateAsync(RuleSet(rule), doc);

        // Only the gated section produced a real verdict; no per-section
        // NotApplicable noise from sections the predicate filtered out.
        report.Verdicts.Should().HaveCount(1);
        report.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Pass);
        report.Verdicts.Single().MatchedSectionId.Should().Be("s2");
        report.Verdicts.Single().PredicateText.Should().Be("input1.category == \"payment_terms\"");
    }

    [Fact]
    public async Task PredicateMatchesNoSection_EmitsExactlyOneGap_ForMandatoryRule()
    {
        // Default Applicability is Mandatory → no matching section means
        // the document is silently failing to address this rule.
        var rule = MakeRule(
            "PAY-001",
            predicate: "input1.category == \"payment_terms\"",
            lambda: "true");
        var doc = Doc(
            ("s1", "governing_law", "x"),
            ("s2", "privacy", "y"));

        var report = await Build().EvaluateAsync(RuleSet(rule), doc);

        report.Verdicts.Should().HaveCount(1);
        report.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Gap);
        report.Gaps.Should().Be(1);
    }

    [Fact]
    public async Task LambdaFails_AndRemediationTemplate_ProducesRewriteText()
    {
        var rule = MakeRule(
            "PAY-001",
            predicate: "input1.category == \"payment_terms\"",
            lambda: "input1.text.Contains(\"30 days\")",
            remediation: "Replace the {section.heading} clause: pay within 30 days.");
        var doc = Doc(("p1", "payment_terms", "Pay in 60 days."));

        var report = await Build().EvaluateAsync(RuleSet(rule), doc);

        report.Verdicts.Should().HaveCount(1);
        var v = report.Verdicts.Single();
        v.Outcome.Should().Be(VerdictOutcome.Fail);
        v.RemediationText.Should().Be("Replace the p1 clause: pay within 30 days.");
    }

    [Fact]
    public async Task DefaultPredicateTrue_AppliesToEverySelectorMatch()
    {
        // Predicate omitted (defaults to "true") — every selector match is evaluated.
        var rule = new Rule(
            Id: "ALL-001",
            Version: "1.0.0",
            NaturalLanguage: "Every section must mention 'agreement'.",
            Lambda: "input1.text.Contains(\"agreement\")",
            AppliesToSchema: new JsonObject(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Violation,
            SourceSpan: new SourceSpan("policy", 0, 0, 1, null),
            EvidenceQuote: "agreement",
            Metadata: new Dictionary<string, string>());
        var doc = Doc(
            ("a", "x", "this agreement holds."),
            ("b", "y", "no match here."));

        var report = await Build().EvaluateAsync(RuleSet(rule), doc);

        report.Verdicts.Should().HaveCount(2);
        report.Verdicts.Count(v => v.Outcome == VerdictOutcome.Pass).Should().Be(1);
        report.Verdicts.Count(v => v.Outcome == VerdictOutcome.Fail).Should().Be(1);
    }

    [Fact]
    public async Task PredicateChange_ChangesVerdictId_ButNotLambdaResult()
    {
        // Two rules with same lambda + same matched section, but different
        // predicate → different verdict id. Locks in the audit-trail story.
        var docPayment = Doc(("p1", "payment_terms", "Pay in 30 days."));
        var ruleA = MakeRule("PAY", "input1.category == \"payment_terms\"", "input1.text.Contains(\"30 days\")");
        var ruleB = ruleA with { } /* same record */;
        // Override predicate on ruleB by creating a new init.
        var ruleB2 = new Rule(
            Id: ruleB.Id, Version: ruleB.Version, NaturalLanguage: ruleB.NaturalLanguage,
            Lambda: ruleB.Lambda, AppliesToSchema: ruleB.AppliesToSchema, Selector: ruleB.Selector,
            Severity: ruleB.Severity, SourceSpan: ruleB.SourceSpan, EvidenceQuote: ruleB.EvidenceQuote,
            Metadata: ruleB.Metadata)
        {
            Predicate = "true",
        };

        var reportA = await Build().EvaluateAsync(RuleSet(ruleA), docPayment);
        var reportB = await Build().EvaluateAsync(RuleSet(ruleB2), docPayment);

        reportA.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Pass);
        reportB.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Pass);
        reportA.Verdicts.Single().Id.Should().NotBe(reportB.Verdicts.Single().Id);
    }
}
