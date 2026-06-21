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
/// Pillar 6 (#124) — end-to-end semantic-binding tests. Validates that
/// rules with <see cref="SemanticAnchor"/>s see populated bindings, that
/// thresholding behaves as documented, and that zero bindings produce
/// the expected Gap / Fail outcomes without flipping legacy rules.
/// </summary>
public class SemanticBindingTests
{
    private static readonly DeterministicHashEmbedder Embedder = new();

    private static async Task<float[]> EmbedAsync(string text)
        => await Embedder.EmbedAsync(text);

    private static Rule MakeRule(
        string id,
        string lambda,
        IReadOnlyList<SemanticAnchor>? anchors)
        => new(
            Id: id,
            Version: "1.0.0",
            NaturalLanguage: $"{id} semantic-binding test rule.",
            Lambda: lambda,
            AppliesToSchema: new JsonObject { ["type"] = "object" },
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Violation,
            SourceSpan: new SourceSpan("test", 0, 1, null, null),
            EvidenceQuote: "test",
            Metadata: new Dictionary<string, string>())
        {
            Predicate = "true",
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

    private static EvaluationService NewEvaluator(bool withEmbedder = true)
    {
        var matcher = new JsonPathSelectorMatcher(NullLogger<JsonPathSelectorMatcher>.Instance);
        return new EvaluationService(
            matcher,
            NullLogger<EvaluationService>.Instance,
            tokenEmbedder: withEmbedder ? Embedder : null);
    }

    [Fact]
    public async Task Rule_with_matching_anchor_sees_non_empty_bindings()
    {
        // Anchor on "recovery point" with cosine threshold 0 so the
        // hash-based embedder's noisy similarity still binds. The point
        // is to prove the plumbing — accuracy is gated by the benchmark
        // with the real embedder.
        var anchorVec = await EmbedAsync("recovery point objective");
        var rule = MakeRule(
            "TEST-BIND-001",
            "LambdaPrimitives.SemanticBindings(\"rpo\").Count > 0",
            new[]
            {
                new SemanticAnchor(
                    Name: "rpo",
                    AnchorText: "recovery point objective",
                    AnchorEmbedding: anchorVec,
                    Threshold: 0.0,
                    Ngram: new[] { 1, 2 }),
            });
        var ruleset = new RuleSet(
            Id: "rs-test", Version: "1.0.0", Domain: "test",
            PublishedAt: DateTimeOffset.UnixEpoch,
            Rules: new[] { rule },
            Metadata: new Dictionary<string, string>());

        var doc = BuildDoc(("s1", "Our recovery point objective is 4 hours for tier-1 services."));
        var report = await NewEvaluator().EvaluateAsync(ruleset, doc);

        report.Verdicts.Should().ContainSingle();
        var v = report.Verdicts[0];
        v.Outcome.Should().Be(VerdictOutcome.Pass);
        v.SemanticBindings.Should().NotBeNull()
            .And.NotBeEmpty()
            .And.OnlyContain(b => b.Anchor == "rpo");
    }

    [Fact]
    public async Task Anchor_threshold_of_one_yields_no_bindings_and_Fail()
    {
        // Unreachable threshold ⇒ zero bindings ⇒ lambda returns false ⇒ Fail.
        var anchorVec = await EmbedAsync("recovery point objective");
        var rule = MakeRule(
            "TEST-BIND-002",
            "LambdaPrimitives.SemanticBindings(\"rpo\").Count > 0",
            new[]
            {
                new SemanticAnchor("rpo", "recovery point objective", anchorVec, Threshold: 1.0001),
            });
        var ruleset = new RuleSet("rs-t", "1.0.0", "test", DateTimeOffset.UnixEpoch,
            new[] { rule }, new Dictionary<string, string>());
        var doc = BuildDoc(("s1", "Recovery point objective is 4 hours."));

        var report = await NewEvaluator().EvaluateAsync(ruleset, doc);
        var v = report.Verdicts.Single();
        v.Outcome.Should().Be(VerdictOutcome.Fail);
        v.SemanticBindings.Should().BeNull(
            "no bindings produced ⇒ Verdict.SemanticBindings stays null for byte-identity");
    }

    [Fact]
    public async Task Rules_without_anchors_evaluate_unchanged_when_embedder_present()
    {
        // Additive-only guarantee: a rule that does NOT declare anchors
        // must produce the same verdict whether or not an embedder is wired.
        var rule = MakeRule(
            "TEST-BIND-003",
            "input1.text.Contains(\"failover\")",
            anchors: null);
        var ruleset = new RuleSet("rs-t", "1.0.0", "test", DateTimeOffset.UnixEpoch,
            new[] { rule }, new Dictionary<string, string>());
        var doc = BuildDoc(("s1", "Our failover design uses warm standby."));

        var with = (await NewEvaluator(withEmbedder: true).EvaluateAsync(ruleset, doc))
            .Verdicts.Single();
        var without = (await NewEvaluator(withEmbedder: false).EvaluateAsync(ruleset, doc))
            .Verdicts.Single();
        with.Outcome.Should().Be(without.Outcome).And.Be(VerdictOutcome.Pass);
        with.SemanticBindings.Should().BeNull();
        without.SemanticBindings.Should().BeNull();
    }

    [Fact]
    public async Task Anchor_with_no_matching_tokens_yields_empty_list_not_exception()
    {
        var anchorVec = await EmbedAsync("kubernetes orchestration");
        var rule = MakeRule(
            "TEST-BIND-004",
            "LambdaPrimitives.SemanticBindings(\"k8s\").Count == 0",
            new[]
            {
                new SemanticAnchor("k8s", "kubernetes orchestration", anchorVec, Threshold: 0.999),
            });
        var ruleset = new RuleSet("rs-t", "1.0.0", "test", DateTimeOffset.UnixEpoch,
            new[] { rule }, new Dictionary<string, string>());
        var doc = BuildDoc(("s1", "This section has no relevant content."));

        var report = await NewEvaluator().EvaluateAsync(ruleset, doc);
        report.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Pass);
    }

    [Fact]
    public async Task SemanticBindings_primitive_returns_empty_when_no_scope_active()
    {
        // Calling the primitive outside an evaluation must return empty,
        // not throw — lets unit tests safely compose with the primitive.
        var result = await Task.FromResult(LambdaPrimitives.SemanticBindings("anything"));
        result.Should().BeEmpty();
    }

    // ─── Pillar 9 — threshold offset calibration lever ──────────────────

    [Fact]
    public async Task ThresholdOffset_default_is_byte_identical_to_legacy_path()
    {
        // Default-zero offset MUST reproduce the exact same bindings the
        // pre-Pillar-9 code emitted. This is the "safe by default" guarantee
        // that protects every golden master.
        var anchorVec = await EmbedAsync("recovery point objective");
        var rule = MakeRule(
            "TEST-BIND-OFFSET-DEFAULT",
            "LambdaPrimitives.SemanticBindings(\"rpo\").Count > 0",
            new[]
            {
                new SemanticAnchor(
                    Name: "rpo",
                    AnchorText: "recovery point objective",
                    AnchorEmbedding: anchorVec,
                    Threshold: 0.0,
                    Ngram: new[] { 1, 2 }),
            });
        var ruleset = new RuleSet(
            "rs-t", "1.0.0", "test", DateTimeOffset.UnixEpoch,
            new[] { rule }, new Dictionary<string, string>());
        var doc = BuildDoc(("s1", "Our recovery point objective is 4 hours."));

        var matcher = new JsonPathSelectorMatcher(NullLogger<JsonPathSelectorMatcher>.Instance);
        var defaultEvaluator = new EvaluationService(
            matcher, NullLogger<EvaluationService>.Instance,
            tokenEmbedder: Embedder);
        var explicitZero = new EvaluationService(
            matcher, NullLogger<EvaluationService>.Instance,
            tokenEmbedder: Embedder,
            semanticThresholdOffset: 0.0);

        var a = await defaultEvaluator.EvaluateAsync(ruleset, doc);
        var b = await explicitZero.EvaluateAsync(ruleset, doc);

        a.Verdicts.Single().SemanticBindings.Should().BeEquivalentTo(
            b.Verdicts.Single().SemanticBindings,
            "default ctor and explicit-zero offset must produce identical bindings");
    }

    [Fact]
    public async Task ThresholdOffset_loosens_gate_and_recovers_borderline_bindings()
    {
        // Pillar 9 lever — when the author threshold is calibrated for one
        // embedder cosine register but the runtime uses another, lowering
        // the effective threshold via offset recovers borderline matches.
        // Hash embedder cosines are noisy and small, so we pick a high
        // author threshold that yields zero bindings, then prove that an
        // offset large enough to drop the effective threshold recovers
        // them.
        var anchorVec = await EmbedAsync("recovery point objective");
        var rule = MakeRule(
            "TEST-BIND-OFFSET-LOOSEN",
            "LambdaPrimitives.SemanticBindings(\"rpo\").Count > 0",
            new[]
            {
                new SemanticAnchor(
                    Name: "rpo",
                    AnchorText: "recovery point objective",
                    AnchorEmbedding: anchorVec,
                    Threshold: 0.95,
                    Ngram: new[] { 1, 2 }),
            });
        var ruleset = new RuleSet(
            "rs-t", "1.0.0", "test", DateTimeOffset.UnixEpoch,
            new[] { rule }, new Dictionary<string, string>());
        var doc = BuildDoc(("s1", "Our recovery point objective is 4 hours."));

        var matcher = new JsonPathSelectorMatcher(NullLogger<JsonPathSelectorMatcher>.Instance);
        var strict = new EvaluationService(
            matcher, NullLogger<EvaluationService>.Instance,
            tokenEmbedder: Embedder,
            semanticThresholdOffset: 0.0);
        var loosened = new EvaluationService(
            matcher, NullLogger<EvaluationService>.Instance,
            tokenEmbedder: Embedder,
            semanticThresholdOffset: 0.95);

        var strictReport = await strict.EvaluateAsync(ruleset, doc);
        var loosenedReport = await loosened.EvaluateAsync(ruleset, doc);

        strictReport.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Fail,
            "strict 0.95 threshold should not bind on hash-embedder noise");
        loosenedReport.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Pass,
            "loosened effective threshold should recover the borderline binding");
    }

    [Fact]
    public async Task ThresholdOffset_respects_min_effective_threshold_floor()
    {
        // A very large offset must not drive the effective threshold
        // below the configured floor — guards against accidentally turning
        // a semantic gate into "everything matches".
        var anchorVec = await EmbedAsync("kubernetes orchestration");
        var rule = MakeRule(
            "TEST-BIND-OFFSET-FLOOR",
            "LambdaPrimitives.SemanticBindings(\"k8s\").Count > 0",
            new[]
            {
                new SemanticAnchor(
                    Name: "k8s",
                    AnchorText: "kubernetes orchestration",
                    AnchorEmbedding: anchorVec,
                    Threshold: 0.30,
                    Ngram: new[] { 1, 2 }),
            });
        var ruleset = new RuleSet(
            "rs-t", "1.0.0", "test", DateTimeOffset.UnixEpoch,
            new[] { rule }, new Dictionary<string, string>());
        // Section text intentionally has nothing to do with the anchor.
        var doc = BuildDoc(("s1", "The quick brown fox jumps over the lazy dog."));

        var matcher = new JsonPathSelectorMatcher(NullLogger<JsonPathSelectorMatcher>.Instance);
        // Offset of 5.0 would drive effective threshold to 0.30-5.0=-4.7,
        // which (without a floor) admits every cosine in [0, 1]. The 0.99
        // floor is set high enough that even the hash embedder's noise
        // cannot cross it, so the gate still rejects unrelated text.
        var floored = new EvaluationService(
            matcher, NullLogger<EvaluationService>.Instance,
            tokenEmbedder: Embedder,
            semanticThresholdOffset: 5.0,
            minEffectiveSemanticThreshold: 0.99);

        var report = await floored.EvaluateAsync(ruleset, doc);
        report.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Fail,
            "min effective threshold floor (0.99) must clamp the offset so semantic gating still rejects unrelated text");
    }

    [Fact]
    public void ThresholdOffset_negative_offset_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SemanticBindingResolver(Embedder, thresholdOffset: -0.01));
    }

    [Fact]
    public void ThresholdOffset_NaN_offset_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SemanticBindingResolver(Embedder, thresholdOffset: double.NaN));
    }
}
