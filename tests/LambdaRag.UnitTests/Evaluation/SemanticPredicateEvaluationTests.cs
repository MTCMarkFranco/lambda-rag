using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Authoring.Dsl;
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
/// Proves that <c>SemanticFunctions.ContainsMeaning(...)</c> emitted by the
/// fluent <see cref="Lambda"/> DSL resolves correctly through the full
/// RulesEngine pipeline (CustomTypes registration + ambient
/// <see cref="VectorStoreAccessor"/>) and produces deterministic verdicts.
///
/// No remote calls are made: vectors are pre-populated in an
/// <see cref="InMemorySemanticVectorStore"/> so the entire path is offline
/// and replay-safe.
/// </summary>
public class SemanticPredicateEvaluationTests
{
    private static readonly TimeProvider Frozen = new TestFrozenTimeProvider(
        new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class TestFrozenTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private static EvaluationService Build(ISemanticVectorStore store)
    {
        var matcher = new JsonPathSelectorMatcher(NullLogger<JsonPathSelectorMatcher>.Instance);
        return new EvaluationService(
            matcher,
            NullLogger<EvaluationService>.Instance,
            Frozen,
            candidateFilter: null,
            vectorStore: store);
    }

    private static ProjectedDocument Doc(params (string id, string text)[] sections)
    {
        var arr = new JsonArray();
        foreach (var (id, text) in sections)
        {
            arr.Add(new JsonObject
            {
                ["id"] = id,
                ["category"] = "ip",
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

    private static Rule SemanticRule(string id, string lambda) => new(
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
        Predicate = "true",
    };

    private static RuleSet RuleSet(params Rule[] rules) => new(
        Id: "rs-semantic",
        Version: "1.0.0",
        Domain: "contract",
        PublishedAt: DateTimeOffset.UnixEpoch,
        Rules: rules,
        Metadata: new Dictionary<string, string>());

    [Fact]
    public async Task ContainsMeaning_CosineAboveThreshold_PassesThroughRulesEngine()
    {
        // Identical 3-dim vector for section + concept → cosine = 1.0 ≥ 0.78 → true.
        var v = new float[] { 1f, 0f, 0f };
        var store = new InMemorySemanticVectorStore("test:fake-3d", 3);
        store.AddSection("s1", v);
        store.AddConcept("works made for hire", v);

        var lambda = Lambda.Section().ContainsMeaning("works made for hire").ToExpression();
        // Sanity-check the DSL emitted the prefixed form RulesEngine expects.
        lambda.Should().Contain("SemanticFunctions.ContainsMeaning(input1.id");

        var report = await Build(store).EvaluateAsync(
            RuleSet(SemanticRule("IP-WFH", lambda)),
            Doc(("s1", "All inventions are deemed works made for hire.")));

        report.Verdicts.Should().HaveCount(1);
        report.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Pass);
        report.Verdicts.Single().MatchedSectionId.Should().Be("s1");
    }

    [Fact]
    public async Task ContainsMeaning_CosineBelowThreshold_FailsThroughRulesEngine()
    {
        // Orthogonal vectors → cosine = 0 < 0.78 → false → Fail (lambda returned false).
        var store = new InMemorySemanticVectorStore("test:fake-3d", 3);
        store.AddSection("s1", new float[] { 1f, 0f, 0f });
        store.AddConcept("works made for hire", new float[] { 0f, 1f, 0f });

        var lambda = Lambda.Section().ContainsMeaning("works made for hire").ToExpression();

        var report = await Build(store).EvaluateAsync(
            RuleSet(SemanticRule("IP-WFH", lambda)),
            Doc(("s1", "Unrelated clause about jurisdiction.")));

        report.Verdicts.Should().HaveCount(1);
        report.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Fail);
    }

    [Fact]
    public async Task MatchesAny_FirstConceptMatches_ReturnsTrue()
    {
        var store = new InMemorySemanticVectorStore("test:fake-3d", 3);
        store.AddSection("s1", new float[] { 1f, 0f, 0f });
        store.AddConcept("works made for hire", new float[] { 1f, 0f, 0f });   // cosine 1
        store.AddConcept("hereby assigns",     new float[] { 0f, 1f, 0f });   // cosine 0

        var lambda = Lambda.Section()
            .MatchesAny("works made for hire", "hereby assigns")
            .ToExpression();
        lambda.Should().Contain("SemanticFunctions.MatchesAnyMeaning");

        var report = await Build(store).EvaluateAsync(
            RuleSet(SemanticRule("IP-WFH-ANY", lambda)),
            Doc(("s1", "Works made for hire.")));

        report.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Pass);
    }

    [Fact]
    public async Task MissingConceptVector_SurfacesAsErrorVerdict_NotSilentFail()
    {
        // Vector missing → SemanticFunctions throws → engine surfaces as Error,
        // which is the loud-failure semantics we want for replay safety.
        var store = new InMemorySemanticVectorStore("test:fake-3d", 3);
        store.AddSection("s1", new float[] { 1f, 0f, 0f });
        // intentionally NOT adding a concept vector

        var lambda = Lambda.Section().ContainsMeaning("works made for hire").ToExpression();

        var report = await Build(store).EvaluateAsync(
            RuleSet(SemanticRule("IP-WFH", lambda)),
            Doc(("s1", "Some text.")));

        var verdict = report.Verdicts.Single();
        verdict.Outcome.Should().Be(VerdictOutcome.Error);
        verdict.ErrorMessage.Should().Contain("works made for hire");
    }

    [Fact]
    public async Task TwoIdenticalRuns_ProduceIdenticalVerdicts()
    {
        var store = new InMemorySemanticVectorStore("test:fake-3d", 3);
        store.AddSection("s1", new float[] { 1f, 0f, 0f });
        store.AddConcept("indemnification", new float[] { 0.9f, 0.1f, 0f });

        var lambda = Lambda.Section().ContainsMeaning("indemnification", threshold: 0.5).ToExpression();
        var ruleSet = RuleSet(SemanticRule("IDM", lambda));
        var doc = Doc(("s1", "indemnification clause."));

        var a = await Build(store).EvaluateAsync(ruleSet, doc);
        var b = await Build(store).EvaluateAsync(ruleSet, doc);

        a.Verdicts.Single().Outcome.Should().Be(b.Verdicts.Single().Outcome);
        a.Verdicts.Single().MatchedSectionId.Should().Be(b.Verdicts.Single().MatchedSectionId);
    }
}
