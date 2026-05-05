using System.Linq;
using FluentAssertions;
using LambdaRag.Authoring;
using LambdaRag.Authoring.Embeddings;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using System.Text.Json.Nodes;
using Xunit;

namespace LambdaRag.UnitTests.Authoring.Embeddings;

public class RuleSetEmbedderTests
{
    [Fact]
    public void ExtractConcepts_pulls_ContainsMeaning_concept_verbatim()
    {
        var lambda =
            "input1.text.Contains(\"Contoso\") && " +
            "SemanticFunctions.ContainsMeaning(input1.id, \"works made for hire\", 0.78)";
        var concepts = RuleSetEmbedder.ExtractConcepts(lambda).ToList();
        concepts.Should().BeEquivalentTo(new[] { "works made for hire" });
    }

    [Fact]
    public void ExtractConcepts_splits_MatchesAnyMeaning_pipe_payload()
    {
        var lambda =
            "SemanticFunctions.MatchesAnyMeaning(input1.id, \"works made for hire|hereby assigns|vests in customer\", 0.78)";
        var concepts = RuleSetEmbedder.ExtractConcepts(lambda).ToList();
        concepts.Should().BeEquivalentTo(new[]
        {
            "works made for hire",
            "hereby assigns",
            "vests in customer",
        });
    }

    [Fact]
    public void ExtractConcepts_handles_mixed_calls_and_unrelated_text()
    {
        var lambda =
            "input1.text_features.dollar_for_gcl >= 5000000 && " +
            "(SemanticFunctions.ContainsMeaning(input1.id, \"general commercial liability\", 0.78) || " +
            "SemanticFunctions.MatchesAnyMeaning(input1.id, \"per occurrence|aggregate\", 0.7))";
        var concepts = RuleSetEmbedder.ExtractConcepts(lambda).ToList();
        concepts.Should().BeEquivalentTo(new[]
        {
            "general commercial liability",
            "per occurrence",
            "aggregate",
        });
    }

    [Fact]
    public async Task EmbedAsync_creates_one_concept_per_extracted_literal_and_one_rule_description()
    {
        var ruleset = MakeRuleSet(
            (id: "R1",
             nl: "Liability cap must include the standard carve-outs.",
             lambda: "SemanticFunctions.MatchesAnyMeaning(input1.id, \"gross negligence|wilful misconduct\", 0.78)"),
            (id: "R2",
             nl: "Confidentiality clause must define a survival period.",
             lambda: "SemanticFunctions.ContainsMeaning(input1.id, \"survival period\", 0.78)"));

        var embedder = new DeterministicHashEmbedder();
        var sut = new RuleSetEmbedder(embedder);

        var store = await sut.EmbedAsync(ruleset);

        store.TryGetConcept(RuleSetEmbedder.RuleDescriptionKey("R1"), out _).Should().BeTrue();
        store.TryGetConcept(RuleSetEmbedder.RuleDescriptionKey("R2"), out _).Should().BeTrue();
        store.TryGetConcept("gross negligence", out _).Should().BeTrue();
        store.TryGetConcept("wilful misconduct", out _).Should().BeTrue();
        store.TryGetConcept("survival period", out _).Should().BeTrue();
    }

    [Fact]
    public async Task EmbedAsync_is_idempotent_across_repeated_calls()
    {
        var ruleset = MakeRuleSet(
            (id: "R1",
             nl: "Indemnification must cover IP infringement claims.",
             lambda: "SemanticFunctions.ContainsMeaning(input1.id, \"intellectual property infringement\", 0.78)"));
        var embedder = new DeterministicHashEmbedder();
        var sut = new RuleSetEmbedder(embedder);

        var store1 = await sut.EmbedAsync(ruleset);
        var store2 = await sut.EmbedAsync(ruleset);

        store1.TryGetConcept(RuleSetEmbedder.RuleDescriptionKey("R1"), out var v1).Should().BeTrue();
        store2.TryGetConcept(RuleSetEmbedder.RuleDescriptionKey("R1"), out var v2).Should().BeTrue();
        v1.ToArray().Should().BeEquivalentTo(v2.ToArray());

        store1.TryGetConcept("intellectual property infringement", out var c1).Should().BeTrue();
        store2.TryGetConcept("intellectual property infringement", out var c2).Should().BeTrue();
        c1.ToArray().Should().BeEquivalentTo(c2.ToArray());
    }

    [Fact]
    public async Task EmbedAsync_dedupes_concepts_referenced_by_multiple_rules()
    {
        var ruleset = MakeRuleSet(
            (id: "R1",
             nl: "Rule one.",
             lambda: "SemanticFunctions.ContainsMeaning(input1.id, \"governing law\", 0.78)"),
            (id: "R2",
             nl: "Rule two.",
             lambda: "SemanticFunctions.ContainsMeaning(input1.id, \"governing law\", 0.78)"));

        var embedder = new CountingEmbedder();
        var sut = new RuleSetEmbedder(embedder);

        await sut.EmbedAsync(ruleset);

        embedder.CallsByText.Should().ContainKey("governing law")
            .WhoseValue.Should().Be(1, "duplicate concept literal embedded once");
    }

    private static RuleSet MakeRuleSet(params (string id, string nl, string lambda)[] rules) =>
        new(
            Id: "rs_test",
            Version: "1.0.0",
            Domain: "test",
            PublishedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Rules: rules.Select(r => new Rule(
                Id: r.id,
                Version: "1.0.0",
                NaturalLanguage: r.nl,
                Lambda: r.lambda,
                AppliesToSchema: new JsonObject(),
                Selector: new PathSelector("$.sections[*]"),
                Severity: RuleSeverity.Violation,
                SourceSpan: new SourceSpan("test", 0, 1, 1, null),
                EvidenceQuote: string.Empty,
                Metadata: new Dictionary<string, string>())).ToList(),
            Metadata: new Dictionary<string, string>());

    private sealed class CountingEmbedder : IRuleEmbedder
    {
        private readonly DeterministicHashEmbedder _inner = new();
        public Dictionary<string, int> CallsByText { get; } = new(StringComparer.Ordinal);

        public int Dimensions => _inner.Dimensions;
        public string EmbedderId => _inner.EmbedderId;

        public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            CallsByText[text] = CallsByText.TryGetValue(text, out var n) ? n + 1 : 1;
            return await _inner.EmbedAsync(text, ct);
        }
    }
}
