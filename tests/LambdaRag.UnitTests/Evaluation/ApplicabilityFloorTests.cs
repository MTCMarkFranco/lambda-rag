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
/// Pillar 10 (#152) tests for the lexical applicability floor + rule-level
/// rollup in <see cref="EvaluationService"/>. The floor is opt-in and MUST
/// default to off so pre-Pillar-10 golden masters stay byte-identical; the
/// rule-level rollup is likewise opt-in via <c>emitRuleLevelStats</c> (or
/// implied by <c>applicabilityFloor &gt; 0</c>).
/// </summary>
public class ApplicabilityFloorTests
{
    private static readonly TimeProvider Frozen = new FrozenTime(
        new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class FrozenTime(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private static EvaluationService Build(
        double applicabilityFloor = 0.0,
        bool emitRuleLevelStats = false)
    {
        var matcher = new JsonPathSelectorMatcher(NullLogger<JsonPathSelectorMatcher>.Instance);
        return new EvaluationService(
            matcher,
            NullLogger<EvaluationService>.Instance,
            Frozen,
            applicabilityFloor: applicabilityFloor,
            emitRuleLevelStats: emitRuleLevelStats);
    }

    private static ProjectedDocument Doc(params (string id, string text)[] sections)
    {
        var arr = new JsonArray();
        foreach (var (id, text) in sections)
        {
            arr.Add(new JsonObject
            {
                ["id"] = id,
                ["category"] = "body",
                ["text"] = text,
                ["heading"] = id,
            });
        }
        return new ProjectedDocument(
            ContentHash.OfString("doc-bytes"),
            "test-projector",
            "1.0",
            new JsonObject { ["sections"] = arr },
            new Dictionary<string, SourceSpan>(StringComparer.Ordinal));
    }

    private static Rule MakeBroadRule(
        string id,
        string evidenceQuote,
        string lambda) => new(
        Id: id,
        Version: "1.0.0",
        NaturalLanguage: evidenceQuote,
        Lambda: lambda,
        AppliesToSchema: new JsonObject(),
        Selector: new PathSelector("$.sections[*]"),
        Severity: RuleSeverity.Violation,
        SourceSpan: new SourceSpan("policy", 0, 0, 1, null),
        EvidenceQuote: evidenceQuote,
        Metadata: new Dictionary<string, string>())
    {
        // Mimics the FoundryRuleAuthoringAgent default: no semantic gate,
        // no predicate gate, broad selector. This is the exact shape that
        // motivated the Pillar 10 floor.
        Predicate = "true",
        Applicability = RuleApplicability.Mandatory,
    };

    private static RuleSet Set(params Rule[] rules) => new(
        Id: "rs-test",
        Version: "1.0.0",
        Domain: "test",
        PublishedAt: DateTimeOffset.UnixEpoch,
        Rules: rules,
        Metadata: new Dictionary<string, string>());

    // ── Byte-identity guard ─────────────────────────────────────────────

    [Fact]
    public async Task Floor_off_emits_no_rule_level_fields_byte_identity_preserved()
    {
        var rule = MakeBroadRule(
            "R-01",
            evidenceQuote: "ADRs SHALL be stored under docs/adr/",
            lambda: "input1.text.Contains(\"docs/adr/\")");
        var doc = Doc(
            ("s1", "The architecture uses docs/adr/ for decisions."),
            ("s2", "Latency budget is 200ms end to end."),
            ("s3", "Cost model uses reserved instances only."));

        var report = await Build().EvaluateAsync(Set(rule), doc);

        report.TotalUniqueRules.Should().BeNull("rule-level stats are opt-in");
        report.RulesPassed.Should().BeNull();
        report.RulesFailed.Should().BeNull();
        report.RuleScore.Should().BeNull();
        report.RuleSummaries.Should().BeNull();
        // Verdict count unchanged from pre-Pillar-10 semantics.
        report.Verdicts.Count.Should().Be(3);
    }

    // ── Applicability floor: silent-topic rule ──────────────────────────

    [Fact]
    public async Task Floor_on_silent_topic_becomes_NotApplicable_not_Gap()
    {
        // Rule about AKS pods. Doc has three sections, none mentioning
        // Kubernetes / pods / AKS. Pre-floor: 3 hard Fails (or a Gap-of-1
        // if selector matched nothing). With floor on: no section clears
        // the topic-overlap ratio → single NotApplicable verdict, NOT a
        // Gap (a Mandatory silent Gap would penalise the score for a
        // topic the doc was never scoped to cover).
        var rule = MakeBroadRule(
            "EA-AKS-001",
            evidenceQuote: "Pods SHALL authenticate to cloud APIs using workload identity federation.",
            lambda: "input1.text.Contains(\"workload identity federation\")");
        var doc = Doc(
            ("s1", "The signing budget for contracts is fifteen days."),
            ("s2", "All PII fields are redacted before archival."),
            ("s3", "Auditor sign-off required prior to release."));

        var report = await Build(applicabilityFloor: 0.5, emitRuleLevelStats: true)
            .EvaluateAsync(Set(rule), doc);

        report.Verdicts.Should().ContainSingle();
        var only = report.Verdicts[0];
        only.Outcome.Should().Be(VerdictOutcome.NotApplicable);
        only.ErrorMessage.Should().StartWith("applicability_floor:no_relevant_section");
        report.RulesNotApplicable.Should().Be(1);
        report.RulesGap.Should().Be(0);
        report.RuleScore.Should().Be(1.0, "silent rule was filtered out of the denominator");
    }

    // ── Applicability floor: keeps topic-matched sections ───────────────

    [Fact]
    public async Task Floor_on_topic_matched_section_still_evaluates_lambda()
    {
        // Rule about ADR storage. Two sections: one mentions "adr" and
        // the required "docs/adr/" path (should Pass), one is unrelated
        // (should be gated out by the floor).
        var rule = MakeBroadRule(
            "EA-ADR-001",
            evidenceQuote: "ADRs SHALL be stored in the workload source repository under docs/adr/",
            lambda: "input1.text.Contains(\"docs/adr/\")");
        var doc = Doc(
            ("s-ok", "The workload source repository stores ADRs under docs/adr/."),
            ("s-junk", "Retention policy for backups is thirty days on the primary cluster."));

        var report = await Build(applicabilityFloor: 0.2, emitRuleLevelStats: true)
            .EvaluateAsync(Set(rule), doc);

        // The topic-matched section produced a Pass; the junk section was
        // filtered by the floor and produced NO verdict — so we get one
        // verdict, not two, and the rule's aggregate outcome is Pass.
        report.Verdicts.Should().ContainSingle();
        report.Verdicts[0].Outcome.Should().Be(VerdictOutcome.Pass);
        report.RulesPassed.Should().Be(1);
        report.RulesFailed.Should().Be(0);
        report.RuleScore.Should().Be(1.0);
    }

    // ── Rule-level rollup arithmetic ────────────────────────────────────

    [Fact]
    public async Task RuleSummaries_aggregate_precedence_pass_wins_over_fail()
    {
        // One rule against three sections. Section 1 satisfies the lambda,
        // sections 2 and 3 do not → per-verdict pass rate is 1/3 (bad-
        // looking score), but the rule-level aggregate is Pass (the doc
        // *does* address the requirement, once).
        var rule = MakeBroadRule(
            "R-42",
            evidenceQuote: "docs/adr/",
            lambda: "input1.text.Contains(\"docs/adr/\")");
        var doc = Doc(
            ("s1", "ADRs live under docs/adr/ per policy."),
            ("s2", "ADR templates are in the wiki, not the repo."),
            ("s3", "See the ADR retention section for archival."));

        var report = await Build(applicabilityFloor: 0.0, emitRuleLevelStats: true)
            .EvaluateAsync(Set(rule), doc);

        // Per-verdict: 1 Pass + 2 Fails. Rule-level: 1 Pass, 0 Fails.
        report.Passed.Should().Be(1);
        report.Failed.Should().Be(2);
        report.TotalUniqueRules.Should().Be(1);
        report.RulesPassed.Should().Be(1);
        report.RulesFailed.Should().Be(0);
        report.RuleScore.Should().Be(1.0);
        // Legacy score = 1/(1+2) ≈ 0.333, rule score = 1/1 = 1.0.
        report.Score.Should().BeApproximately(1.0 / 3.0, 1e-9);
    }

    [Fact]
    public async Task RuleSummaries_has_one_entry_per_distinct_rule()
    {
        var r1 = MakeBroadRule("A-01", "alpha", "input1.text.Contains(\"alpha\")");
        var r2 = MakeBroadRule("B-02", "beta", "input1.text.Contains(\"beta\")");
        var doc = Doc(("s1", "alpha only"), ("s2", "beta only"), ("s3", "neither"));

        var report = await Build(emitRuleLevelStats: true).EvaluateAsync(Set(r1, r2), doc);

        report.RuleSummaries.Should().NotBeNull();
        report.RuleSummaries!.Should().HaveCount(2);
        report.RuleSummaries!.Select(s => s.RuleId).Should().BeEquivalentTo(new[] { "A-01", "B-02" });
        var a = report.RuleSummaries!.Single(s => s.RuleId == "A-01");
        a.AggregateOutcome.Should().Be(VerdictOutcome.Pass);
        a.PassCount.Should().Be(1);
        a.FailCount.Should().Be(2);
        a.SectionsEvaluated.Should().Be(3);
    }
}
