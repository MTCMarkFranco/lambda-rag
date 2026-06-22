using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Authoring;
using LambdaRag.Core;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Selectors;
using LambdaRag.Core.Semantic;
using LambdaRag.Evaluation.Engine;
using LambdaRag.Selectors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LambdaRag.UnitTests.Evaluation;

/// <summary>
/// Pillar 9 — soft cohesion post-filter ported from
/// policy-compiler-spike v0.1.1. The filter demotes Pass→NotApplicable
/// when fewer than <c>minEvidencedAnchors</c> of a rule's anchors
/// actually produced bindings on a section.
///
/// These tests prove:
///   • Default-off behaviour: byte-identity vs the legacy code path.
///   • When enabled with the default minEvidencedAnchors=2:
///       — a Pass driven by only 1 of 2 anchors is demoted (FP killer).
///       — a Pass driven by both anchors is kept.
///       — single-anchor rules are exempt from the gate.
///       — Fail outcomes are never demoted (genuine gaps still surface).
/// </summary>
public class SoftCohesionPostFilterTests
{
    private static readonly DeterministicHashEmbedder Embedder = new();

    private static async Task<float[]> EmbedAsync(string text)
        => await Embedder.EmbedAsync(text);

    private static EvaluationService NewEvaluator(bool enforceSoftCohesion)
    {
        var matcher = new JsonPathSelectorMatcher(NullLogger<JsonPathSelectorMatcher>.Instance);
        return new EvaluationService(
            matcher,
            NullLogger<EvaluationService>.Instance,
            tokenEmbedder: Embedder,
            enforceSoftCohesion: enforceSoftCohesion);
    }

    private static Rule MakeRule(
        string id,
        string lambda,
        IReadOnlyList<SemanticAnchor> anchors,
        string predicate = "true")
        => new(
            Id: id,
            Version: "1.0.0",
            NaturalLanguage: $"{id} soft-cohesion test rule.",
            Lambda: lambda,
            AppliesToSchema: new JsonObject { ["type"] = "object" },
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Violation,
            SourceSpan: new SourceSpan("test", 0, 1, null, null),
            EvidenceQuote: "test",
            Metadata: new Dictionary<string, string>())
        {
            Predicate = predicate,
            Applicability = RuleApplicability.Mandatory,
            SemanticAnchors = anchors,
        };

    private static ProjectedDocument BuildDoc(params (string id, string text)[] sections)
    {
        var arr = new JsonArray();
        foreach (var (id, text) in sections)
        {
            arr.Add(new JsonObject
            {
                ["id"] = id,
                ["heading"] = id,
                ["heading_path"] = "/" + id,
                ["category"] = "test",
                ["text"] = text,
                ["text_char_start"] = 0L,
            });
        }
        var graph = new JsonObject
        {
            ["doc_kind"] = "test",
            ["sections"] = arr,
        };
        return new ProjectedDocument(
            SourceId: ContentHash.OfString("test"),
            ProjectorId: "test", ProjectorVersion: "1.0",
            Graph: graph,
            SpanMap: new Dictionary<string, SourceSpan>());
    }

    [Fact]
    public async Task Default_off_preserves_legacy_Pass_with_only_one_evidenced_anchor()
    {
        // Two anchors, but only one will bind the body text. With the
        // gate OFF (default), the lambda's "any anchor evidenced" still
        // produces Pass — byte-identity with the pre-Pillar-9 code path.
        var rpoVec = await EmbedAsync("recovery point objective");
        var k8sVec = await EmbedAsync("kubernetes orchestration");
        var rule = MakeRule(
            "TEST-COHESION-OFF",
            "LambdaPrimitives.SemanticBindings(\"rpo\").Count > 0 "
            + "|| LambdaPrimitives.SemanticBindings(\"k8s\").Count > 0",
            new[]
            {
                new SemanticAnchor("rpo", "recovery point objective", rpoVec, Threshold: 0.0, Ngram: new[] { 1, 2 }),
                new SemanticAnchor("k8s", "kubernetes orchestration", k8sVec, Threshold: 0.999),
            });
        var ruleset = new RuleSet("rs-t", "1.0.0", "test", DateTimeOffset.UnixEpoch,
            new[] { rule }, new Dictionary<string, string>());
        var doc = BuildDoc(("s1", "Our recovery point objective is 4 hours."));

        var report = await NewEvaluator(enforceSoftCohesion: false)
            .EvaluateAsync(ruleset, doc);
        report.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Pass,
            "default-off cohesion must not change pre-Pillar-9 outcomes");
    }

    [Fact]
    public async Task Enabled_demotes_Pass_to_NotApplicable_when_only_one_of_two_anchors_evidenced()
    {
        // Same setup as the default-off test but with the gate ON. Only
        // the rpo anchor binds; the k8s anchor's threshold is unreachable.
        // → 1 evidenced of 2 → demote.
        var rpoVec = await EmbedAsync("recovery point objective");
        var k8sVec = await EmbedAsync("kubernetes orchestration");
        var rule = MakeRule(
            "TEST-COHESION-DEMOTE",
            "LambdaPrimitives.SemanticBindings(\"rpo\").Count > 0 "
            + "|| LambdaPrimitives.SemanticBindings(\"k8s\").Count > 0",
            new[]
            {
                new SemanticAnchor("rpo", "recovery point objective", rpoVec, Threshold: 0.0, Ngram: new[] { 1, 2 }),
                new SemanticAnchor("k8s", "kubernetes orchestration", k8sVec, Threshold: 0.999),
            });
        var ruleset = new RuleSet("rs-t", "1.0.0", "test", DateTimeOffset.UnixEpoch,
            new[] { rule }, new Dictionary<string, string>());
        var doc = BuildDoc(("s1", "Our recovery point objective is 4 hours."));

        var report = await NewEvaluator(enforceSoftCohesion: true)
            .EvaluateAsync(ruleset, doc);
        report.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.NotApplicable,
            "soft cohesion must demote a Pass driven by only 1 of 2 anchors");
    }

    [Fact]
    public async Task Enabled_keeps_Pass_when_both_of_two_anchors_evidenced()
    {
        // Two reachable anchors, both bind on body text mentioning both
        // surface forms. → 2 of 2 evidenced → gate satisfied → Pass kept.
        var rpoVec = await EmbedAsync("recovery point objective");
        var rtoVec = await EmbedAsync("recovery time objective");
        var rule = MakeRule(
            "TEST-COHESION-KEEP",
            "LambdaPrimitives.SemanticBindings(\"rpo\").Count > 0 "
            + "|| LambdaPrimitives.SemanticBindings(\"rto\").Count > 0",
            new[]
            {
                new SemanticAnchor("rpo", "recovery point objective", rpoVec, Threshold: 0.0, Ngram: new[] { 1, 2 }),
                new SemanticAnchor("rto", "recovery time objective", rtoVec, Threshold: 0.0, Ngram: new[] { 1, 2 }),
            });
        var ruleset = new RuleSet("rs-t", "1.0.0", "test", DateTimeOffset.UnixEpoch,
            new[] { rule }, new Dictionary<string, string>());
        var doc = BuildDoc(("s1",
            "Our recovery point objective is 4 hours and the recovery time objective is 8 hours."));

        var report = await NewEvaluator(enforceSoftCohesion: true)
            .EvaluateAsync(ruleset, doc);
        report.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Pass,
            "Pass must be kept when both anchors are evidenced");
    }

    [Fact]
    public async Task Enabled_does_not_apply_to_single_anchor_rules()
    {
        // Single-anchor rules cannot fail a "≥ 2 evidenced" check.
        // The gate must skip them so Pass outcomes remain Pass.
        var rpoVec = await EmbedAsync("recovery point objective");
        var rule = MakeRule(
            "TEST-COHESION-SINGLE",
            "LambdaPrimitives.SemanticBindings(\"rpo\").Count > 0",
            new[]
            {
                new SemanticAnchor("rpo", "recovery point objective", rpoVec, Threshold: 0.0, Ngram: new[] { 1, 2 }),
            });
        var ruleset = new RuleSet("rs-t", "1.0.0", "test", DateTimeOffset.UnixEpoch,
            new[] { rule }, new Dictionary<string, string>());
        var doc = BuildDoc(("s1", "Our recovery point objective is 4 hours."));

        var report = await NewEvaluator(enforceSoftCohesion: true)
            .EvaluateAsync(ruleset, doc);
        report.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Pass,
            "single-anchor rules must be exempt from the cohesion gate");
    }

    [Fact]
    public async Task Enabled_never_demotes_Fail_to_NotApplicable()
    {
        // A Fail signals "section did not address the requirement". We
        // must preserve Fail so genuine gaps still surface — the gate is
        // strictly a precision tool, not a recall hider.
        var rpoVec = await EmbedAsync("recovery point objective");
        var k8sVec = await EmbedAsync("kubernetes orchestration");
        var rule = MakeRule(
            "TEST-COHESION-FAIL",
            // Lambda asks for both anchors but only rpo binds → false → Fail.
            "LambdaPrimitives.SemanticBindings(\"rpo\").Count > 0 "
            + "&& LambdaPrimitives.SemanticBindings(\"k8s\").Count > 0",
            new[]
            {
                new SemanticAnchor("rpo", "recovery point objective", rpoVec, Threshold: 0.0, Ngram: new[] { 1, 2 }),
                new SemanticAnchor("k8s", "kubernetes orchestration", k8sVec, Threshold: 0.999),
            });
        var ruleset = new RuleSet("rs-t", "1.0.0", "test", DateTimeOffset.UnixEpoch,
            new[] { rule }, new Dictionary<string, string>());
        var doc = BuildDoc(("s1", "Our recovery point objective is 4 hours."));

        var report = await NewEvaluator(enforceSoftCohesion: true)
            .EvaluateAsync(ruleset, doc);
        report.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Fail,
            "Fail outcomes must survive the cohesion gate — recall is sacred");
    }
}
