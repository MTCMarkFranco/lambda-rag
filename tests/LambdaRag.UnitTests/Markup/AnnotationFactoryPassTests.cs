using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Selectors;
using LambdaRag.Markup;
using Xunit;

namespace LambdaRag.UnitTests.Markup;

/// <summary>
/// Covers <see cref="AnnotationFactory.BuildPassAnnotations"/> — the opt-in
/// positive-confirmation pathway used by <c>review --annotate-pass</c>.
/// </summary>
public class AnnotationFactoryPassTests
{
    private static Rule MakeRule(string id, string statement) => new(
        Id: id,
        Version: "1.0.0",
        NaturalLanguage: statement,
        Lambda: "true",
        AppliesToSchema: new JsonObject(),
        Selector: new PathSelector("$.sections[*]"),
        Severity: RuleSeverity.Violation,
        SourceSpan: new SourceSpan("doc", 0, 0, 1, null),
        EvidenceQuote: statement,
        Metadata: new Dictionary<string, string>());

    private static Verdict MakeVerdict(string ruleId, VerdictOutcome outcome, int charStart = 100) => new(
        Id: ContentHash.Compose("verdict", ruleId, outcome.ToString(), charStart.ToString()).Value,
        RuleId: ruleId,
        RuleSetVersion: "1.0.0",
        Outcome: outcome,
        LambdaText: "true",
        EvaluatedInput: new JsonObject(),
        SourceSpan: new SourceSpan("doc", charStart, 50, 1, null),
        ErrorMessage: null,
        EvidenceQuotes: Array.Empty<string>(),
        EvaluatedAt: DateTimeOffset.UnixEpoch);

    private static ComplianceReport MakeReport(IReadOnlyList<Verdict> verdicts) => new(
        DocumentId: ContentHash.OfString("doc"),
        RuleSetId: "rs",
        RuleSetVersion: "1.0.0",
        RuleSetFingerprint: ContentHash.OfString("fp"),
        ProjectorId: "contract",
        ProjectorVersion: "1.0.0",
        Score: 1.0,
        TotalRules: verdicts.Count,
        Passed: verdicts.Count(v => v.Outcome == VerdictOutcome.Pass),
        Failed: verdicts.Count(v => v.Outcome == VerdictOutcome.Fail),
        NotApplicable: 0,
        Errored: 0,
        Verdicts: verdicts,
        GeneratedAt: DateTimeOffset.UnixEpoch);

    [Fact]
    public void BuildPassAnnotations_emits_one_comment_per_pass_verdict()
    {
        var rules = new Dictionary<string, Rule>
        {
            ["A"] = MakeRule("A", "Statement A."),
            ["B"] = MakeRule("B", "Statement B."),
        };
        var report = MakeReport(new[]
        {
            MakeVerdict("A", VerdictOutcome.Pass, 100),
            MakeVerdict("B", VerdictOutcome.Pass, 200),
        });

        var annotations = AnnotationFactory.BuildPassAnnotations(report, rules).ToList();

        annotations.Should().HaveCount(2);
        annotations.Should().AllSatisfy(a =>
        {
            a.Kind.Should().Be(AnnotationKind.Comment);
            a.Text.Should().StartWith("\u2713 Passed: ");
            a.Replacement.Should().BeNull();
            a.Author.Should().Be(AnnotationFactory.Author);
        });
        annotations[0].Text.Should().Contain("Statement A.");
        annotations[1].Text.Should().Contain("Statement B.");
    }

    [Fact]
    public void BuildPassAnnotations_skips_non_pass_outcomes()
    {
        var rules = new Dictionary<string, Rule> { ["A"] = MakeRule("A", "S") };
        var report = MakeReport(new[]
        {
            MakeVerdict("A", VerdictOutcome.Fail),
            MakeVerdict("A", VerdictOutcome.Gap),
            MakeVerdict("A", VerdictOutcome.NotApplicable),
            MakeVerdict("A", VerdictOutcome.Error),
        });

        AnnotationFactory.BuildPassAnnotations(report, rules).Should().BeEmpty();
    }

    [Fact]
    public void BuildPassAnnotations_anchors_to_verdict_source_span()
    {
        var rules = new Dictionary<string, Rule> { ["A"] = MakeRule("A", "S") };
        var verdict = MakeVerdict("A", VerdictOutcome.Pass, charStart: 4242);
        var report = MakeReport(new[] { verdict });

        var annot = AnnotationFactory.BuildPassAnnotations(report, rules).Single();

        annot.Span.Should().BeEquivalentTo(verdict.SourceSpan);
    }

    [Fact]
    public void BuildPassAnnotations_id_is_stable_across_runs()
    {
        var rules = new Dictionary<string, Rule> { ["A"] = MakeRule("A", "S") };
        var report = MakeReport(new[] { MakeVerdict("A", VerdictOutcome.Pass) });

        var run1 = AnnotationFactory.BuildPassAnnotations(report, rules).Single();
        var run2 = AnnotationFactory.BuildPassAnnotations(report, rules).Single();

        run1.Id.Should().Be(run2.Id, "pass annotation ids must derive deterministically " +
            "from the verdict id so reviewed.docx remains byte-identical across runs");
    }

    [Fact]
    public void BuildPassAnnotations_id_does_not_collide_with_FromReport_ids()
    {
        // A rule that produces a Fail verdict and a Pass verdict for the same
        // ruleId on different sections must yield distinct annotation ids;
        // otherwise OpenXmlMarkupService would treat them as duplicates.
        var rules = new Dictionary<string, Rule> { ["A"] = MakeRule("A", "S") };
        var pass = MakeVerdict("A", VerdictOutcome.Pass, charStart: 100);
        var fail = MakeVerdict("A", VerdictOutcome.Fail, charStart: 500);
        var report = MakeReport(new[] { pass, fail });

        var passId = AnnotationFactory.BuildPassAnnotations(report, rules).Single().Id;
        var failId = AnnotationFactory.FromReport(report, rules).Single().Id;

        passId.Should().NotBe(failId);
    }

    [Fact]
    public void BuildPassAnnotations_falls_back_to_rule_id_when_rule_lookup_misses()
    {
        var emptyRules = new Dictionary<string, Rule>();
        var report = MakeReport(new[] { MakeVerdict("ORPHAN", VerdictOutcome.Pass) });

        var annot = AnnotationFactory.BuildPassAnnotations(report, emptyRules).Single();

        annot.Text.Should().Contain("ORPHAN");
    }
}
