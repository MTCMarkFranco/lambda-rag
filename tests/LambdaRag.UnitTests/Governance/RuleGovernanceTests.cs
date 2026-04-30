using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Cli;
using LambdaRag.Core;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using Xunit;

namespace LambdaRag.UnitTests.Governance;

/// <summary>
/// Tests for the rule governance tooling: ruleset diff and overlay
/// application. These are the legal-defensibility primitives — disabling
/// or annotating a rule must always produce an attributable, fingerprinted
/// audit trail and must never silently mutate the underlying ruleset.
/// </summary>
public class RuleGovernanceTests
{
    private static Rule MakeRule(string id, string predicate = "true", string lambda = "true",
        RuleApplicability applicability = RuleApplicability.Mandatory,
        RuleSeverity severity = RuleSeverity.Violation) =>
        new(
            Id: id,
            Version: "1.0.0",
            NaturalLanguage: $"Rule {id}",
            Lambda: lambda,
            AppliesToSchema: new JsonObject(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: severity,
            SourceSpan: new SourceSpan("policy", 0, 0, 1, null),
            EvidenceQuote: id,
            Metadata: new Dictionary<string, string>())
        { Predicate = predicate, Applicability = applicability };

    private static RuleSet MakeSet(string version, params Rule[] rules) => new(
        Id: "rs-test",
        Version: version,
        Domain: "contract",
        PublishedAt: DateTimeOffset.UnixEpoch,
        Rules: rules,
        Metadata: new Dictionary<string, string>());

    // ────────────────────────────────────────────────────────────────────
    // Diff
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Diff_DetectsAddedRemovedAndModifiedRules()
    {
        var oldRs = MakeSet("1.0.0",
            MakeRule("PAY-001"),
            MakeRule("LIAB-001"),
            MakeRule("WARR-001", lambda: "input1.text != null"));

        var newRs = MakeSet("1.1.0",
            MakeRule("PAY-001"),
            MakeRule("WARR-001", lambda: "input1.text.Length > 0"),
            MakeRule("DPA-001"));

        var diff = RulesCommand.ComputeDiff(oldRs, newRs);

        diff.Added.Should().BeEquivalentTo(new[] { "DPA-001" });
        diff.Removed.Should().BeEquivalentTo(new[] { "LIAB-001" });
        diff.Unchanged.Should().BeEquivalentTo(new[] { "PAY-001" });
        diff.Changed.Should().HaveCount(1);
        diff.Changed[0].RuleId.Should().Be("WARR-001");
        diff.Changed[0].ChangedFields.Should().Contain("lambda");
        diff.Changed[0].FromFingerprint.Should().NotBe(diff.Changed[0].ToFingerprint);
    }

    [Fact]
    public void Diff_DetectsApplicabilityAndSeverityChanges()
    {
        var oldRs = MakeSet("1.0.0",
            MakeRule("PAY-001", applicability: RuleApplicability.Mandatory, severity: RuleSeverity.Violation));
        var newRs = MakeSet("1.0.0",
            MakeRule("PAY-001", applicability: RuleApplicability.Optional, severity: RuleSeverity.Suggestion));

        var diff = RulesCommand.ComputeDiff(oldRs, newRs);

        diff.Changed.Should().HaveCount(1);
        diff.Changed[0].ChangedFields.Should().Contain("applicability");
        diff.Changed[0].ChangedFields.Should().Contain("severity");
    }

    // ────────────────────────────────────────────────────────────────────
    // Overlay apply
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Overlay_DisablesNamedRulesAndPreservesOthers()
    {
        var ruleset = MakeSet("1.0.0", MakeRule("R-1"), MakeRule("R-2"), MakeRule("R-3"));
        var overlay = new RuleOverlay(
            RuleSetId: ruleset.Id, RuleSetVersion: ruleset.Version,
            CreatedAt: DateTimeOffset.UnixEpoch,
            Disabled: [new DisabledRule("R-2", "superseded by side-letter", DateTimeOffset.UnixEpoch)],
            Annotations: []);

        var result = OverlayApplier.Apply(ruleset, overlay);

        result.RuleSet.Rules.Select(r => r.Id).Should().BeEquivalentTo(new[] { "R-1", "R-3" });
        result.Audit.DisabledCount.Should().Be(1);
        result.Audit.Disabled[0].Reason.Should().Be("superseded by side-letter");
        result.UnknownRuleIds.Should().BeEmpty();
    }

    [Fact]
    public void Overlay_RejectsBindingMismatch()
    {
        var ruleset = MakeSet("1.0.0", MakeRule("R-1"));
        var overlay = new RuleOverlay(
            RuleSetId: ruleset.Id, RuleSetVersion: "9.9.9",
            CreatedAt: DateTimeOffset.UnixEpoch,
            Disabled: [], Annotations: []);

        Action act = () => OverlayApplier.Apply(ruleset, overlay);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*9.9.9*");
    }

    [Fact]
    public void Overlay_FlagsUnknownRuleIdsButStillApplies()
    {
        var ruleset = MakeSet("1.0.0", MakeRule("R-1"));
        var overlay = new RuleOverlay(
            RuleSetId: ruleset.Id, RuleSetVersion: ruleset.Version,
            CreatedAt: DateTimeOffset.UnixEpoch,
            Disabled: [new DisabledRule("R-DOES-NOT-EXIST", "stale entry", DateTimeOffset.UnixEpoch)],
            Annotations: [new RuleAnnotation("R-1", "see clause 7.2", DateTimeOffset.UnixEpoch)]);

        var result = OverlayApplier.Apply(ruleset, overlay);

        result.UnknownRuleIds.Should().Contain("R-DOES-NOT-EXIST");
        result.RuleSet.Rules.Should().HaveCount(1);
        result.Audit.AnnotatedCount.Should().Be(1);
        result.Audit.DisabledCount.Should().Be(0);
    }

    [Fact]
    public void Overlay_FingerprintIsStableAcrossInstanceConstructions()
    {
        var a = new RuleOverlay("rs", "1.0.0", DateTimeOffset.UnixEpoch,
            Disabled: [new DisabledRule("R-1", "reason", DateTimeOffset.UnixEpoch)],
            Annotations: [new RuleAnnotation("R-2", "note", DateTimeOffset.UnixEpoch)]);
        var b = new RuleOverlay("rs", "1.0.0", DateTimeOffset.MaxValue,
            Disabled: [new DisabledRule("R-1", "reason", DateTimeOffset.MaxValue)],
            Annotations: [new RuleAnnotation("R-2", "note", DateTimeOffset.MaxValue)]);

        a.Fingerprint().Should().Be(b.Fingerprint());
    }
}
