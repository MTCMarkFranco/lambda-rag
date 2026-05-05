using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Authoring;
using LambdaRag.Authoring.Validation;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using Xunit;

namespace LambdaRag.UnitTests.Authoring.Validation;

public class RuleSelfValidatorTests
{
    /// <summary>
    /// Tiny embedder that returns caller-supplied unit vectors verbatim
    /// (no re-normalisation). The test helper builds vectors so that
    /// cosine(concept, text) is exactly the value the test wants — much
    /// easier to reason about than fighting hash-derived pseudo-vectors.
    /// </summary>
    private sealed class CannedEmbedder : IRuleEmbedder
    {
        private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);
        public int Dimensions => 2;
        public string EmbedderId => "canned/2";

        public CannedEmbedder MapWithCosine(string text, double cosineToConcept)
        {
            var c = (float)cosineToConcept;
            var s = (float)Math.Sqrt(Math.Max(0, 1 - cosineToConcept * cosineToConcept));
            _vectors[text] = new[] { c, s };
            return this;
        }

        public CannedEmbedder MapConcept(string concept)
        {
            _vectors[concept] = new float[] { 1, 0 };
            return this;
        }

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult(_vectors[text]);
    }

    private static Rule MakeRule(string id, string lambda, RuleExamples? ex = null) => new(
        Id: id,
        Version: "1",
        NaturalLanguage: "stub",
        Lambda: lambda,
        AppliesToSchema: new JsonObject(),
        Selector: new PathSelector("$"),
        Severity: RuleSeverity.Violation,
        SourceSpan: SourceSpan.Unknown,
        EvidenceQuote: "stub",
        Metadata: new Dictionary<string, string>())
    { Examples = ex };

    [Fact]
    public async Task CleanSeparation_Accepts_AndCalibratedThresholdIsMidpoint()
    {
        const string concept = "encryption-at-rest";
        var emb = new CannedEmbedder()
            .MapConcept(concept)
            .MapWithCosine("pos1", 0.95)
            .MapWithCosine("pos2", 0.90)
            .MapWithCosine("neg1", 0.20)
            .MapWithCosine("neg2", 0.30);

        var rule = MakeRule("R1",
            lambda: $"SemanticFunctions.MatchesAnyMeaning(x, \"{concept}\", 0.55)",
            ex: new RuleExamples(new[] { "pos1", "pos2" }, new[] { "neg1", "neg2" }));

        var r = await new RuleSelfValidator(emb).ValidateAsync(rule);

        r.Accepted.Should().BeTrue();
        r.RejectionReason.Should().BeNull();
        r.MinPositive.Should().BeApproximately(0.90, 1e-5);
        r.MaxNegative.Should().BeApproximately(0.30, 1e-5);
        r.Margin.Should().BeApproximately(0.60, 1e-5);
        r.CalibratedThreshold.Should().BeApproximately(0.60, 1e-5);
    }

    [Fact]
    public async Task Overlap_Rejects()
    {
        const string concept = "encryption";
        var emb = new CannedEmbedder()
            .MapConcept(concept)
            .MapWithCosine("pos", 0.50)
            .MapWithCosine("neg", 0.70);

        var rule = MakeRule("R2",
            lambda: $"SemanticFunctions.MatchesAnyMeaning(x, \"{concept}\", 0.55)",
            ex: new RuleExamples(new[] { "pos" }, new[] { "neg" }));

        var r = await new RuleSelfValidator(emb).ValidateAsync(rule);

        r.Accepted.Should().BeFalse();
        r.RejectionReason.Should().Contain("Negative");
    }

    [Fact]
    public async Task ThinMargin_BelowEpsilon_Rejects()
    {
        const string concept = "encryption";
        var emb = new CannedEmbedder()
            .MapConcept(concept)
            .MapWithCosine("pos", 0.62)
            .MapWithCosine("neg", 0.60);

        var rule = MakeRule("R3",
            lambda: $"SemanticFunctions.MatchesAnyMeaning(x, \"{concept}\", 0.55)",
            ex: new RuleExamples(new[] { "pos" }, new[] { "neg" }));

        var r = await new RuleSelfValidator(emb, epsilon: 0.05).ValidateAsync(rule);

        r.Accepted.Should().BeFalse();
        r.Margin.Should().BeLessThan(0.05);
        r.RejectionReason.Should().Contain("Margin");
    }

    [Fact]
    public async Task NoExamples_ThrowsInvalidOperation()
    {
        var rule = MakeRule("R4",
            lambda: "SemanticFunctions.MatchesAnyMeaning(x, \"x\", 0.5)");
        var v = new RuleSelfValidator(new CannedEmbedder());
        await Assert.ThrowsAsync<InvalidOperationException>(() => v.ValidateAsync(rule));
    }

    [Fact]
    public async Task NoConcepts_ThrowsInvalidOperation()
    {
        var rule = MakeRule("R5",
            lambda: "input1.value > 0",
            ex: new RuleExamples(new[] { "p" }, new[] { "n" }));
        var v = new RuleSelfValidator(new CannedEmbedder());
        await Assert.ThrowsAsync<InvalidOperationException>(() => v.ValidateAsync(rule));
    }

    [Fact]
    public async Task Determinism_SameInputs_ProducesSameThreshold()
    {
        var rule = MakeRule("R6",
            lambda: "SemanticFunctions.MatchesAnyMeaning(x, \"data retention | data deletion\", 0.5)",
            ex: new RuleExamples(
                Positive: new[] { "the provider deletes customer data within 30 days of contract end.", "data is purged after the retention period expires." },
                Negative: new[] { "all employees must complete annual security training.", "use multi-factor authentication for admin consoles." }));

        var v = new RuleSelfValidator(new DeterministicHashEmbedder());
        var r1 = await v.ValidateAsync(rule);
        var r2 = await v.ValidateAsync(rule);

        r1.MinPositive.Should().Be(r2.MinPositive);
        r1.MaxNegative.Should().Be(r2.MaxNegative);
        r1.CalibratedThreshold.Should().Be(r2.CalibratedThreshold);
        r1.Accepted.Should().Be(r2.Accepted);
    }
}
