// Issue #159 — Domain-scoped review guardrail.
//
// Lambda-rag does not perform cross-domain evaluation. A caller who
// invokes review with a declared domain that does not match the
// ruleset's authored domain is making a user error, and the engine
// refuses to run — not silently proceeds with potentially nonsensical
// verdicts.
//
// This test file replaces the retired WrongRulesetAntiOverfitTests /
// "cross-domain honesty" ratchet from Pillar 4 (Flexibility, old
// framing). The old framing tried to make the arch ruleset behave
// reasonably against healthcare/contract documents. The new framing:
// that scenario is a user error, guarded at the entry point, and the
// Flexibility pillar is about IN-domain paraphrase-robustness — see
// docs/FOUR-PILLARS.md.
//
// The IN-domain paraphrase harness lives in the unit-test project
// (Phase-C adversarial paraphrase goldens); this file only guards the
// domain-mismatch entry point.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Facts;
using LambdaRag.Evaluation;
using LambdaRag.Evaluation.Engine;
using LambdaRag.Parsing;
using LambdaRag.Projection;
using LambdaRag.Projection.Projectors;
using LambdaRag.Selectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LambdaRag.IdempotencyTests;

public sealed class DomainMismatchGuardrailTests
{
    private static string RepoRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string CorpusRoot => Path.Combine(RepoRoot, "tests", "Goldens", "corpus");
    private static string ArchRulesetPath => Path.Combine(RepoRoot, "rulesets",
        "architecture-review", "architecture-v1.json");

    private static readonly DateTimeOffset FrozenInstant =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Minimal frozen TimeProvider — was inline in the retired
    /// WrongRulesetAntiOverfitTests file; local class here so this
    /// integration test remains self-contained.</summary>
    private sealed class FrozenTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    /// <summary>Empty-bag IFactExtractor. Kept only to build a
    /// full EvaluationService — the test throws before any extraction
    /// actually happens.</summary>
    private sealed class EmptyBagsFactExtractor : IFactExtractor
    {
        public string ModelId => "empty-bags";
        public string PromptHash => "empty-bags";
        public Task<SectionFactSidecar> ExtractAsync(
            ProjectedDocument document, FactSchema schema, CancellationToken ct = default)
        {
            var bags = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
            if (document.Graph["sections"] is JsonArray arr)
            {
                var i = 0;
                foreach (var node in arr)
                {
                    if (node is JsonObject obj)
                    {
                        var id = (string?)obj["id"] ?? $"s_{i:D8}";
                        bags[id] = new Dictionary<string, object?>(StringComparer.Ordinal);
                    }
                    i++;
                }
            }
            return Task.FromResult(new SectionFactSidecar(
                SidecarVersion: "1.0",
                DocumentId: document.SourceId.Value,
                FactSchemaId: schema.Id,
                FactSchemaHash: schema.Fingerprint().Value,
                ModelId: ModelId,
                PromptHash: PromptHash,
                GeneratedAt: "2000-01-01T00:00:00+00:00",
                Sections: bags));
        }
    }

    public static IEnumerable<object[]> MismatchScenarios()
    {
        // (declared-domain, corpus-vertical, doc-id) — the declared
        // domain is what the caller passes to review. All rulesets
        // under test carry Domain="architecture", so any
        // other declared value MUST throw.
        yield return new object[] { "healthcare", "healthcare", "acme-telehealth-gaps" };
        yield return new object[] { "contract",   "contract",   "doc-002-clean-msa" };
    }

    [Theory]
    [MemberData(nameof(MismatchScenarios))]
    public async Task Declaring_Wrong_Domain_Throws_At_Entry_Point(
        string declaredDomain, string vertical, string docId)
    {
        var (evaluator, ruleset, projected) = await BuildEvalHarnessAsync(vertical, docId);

        // Sanity: the ruleset's own domain is NOT the caller-declared one.
        ruleset.Domain.Should().NotBe(declaredDomain,
            "test setup precondition: declared domain must differ from ruleset's authored domain " +
            "for the guardrail to fire");

        var act = async () => await evaluator.EvaluateAsync(
            ruleset, projected, docKind: null, declaredDomain: declaredDomain);

        var ex = await act.Should().ThrowAsync<DomainMismatchException>(
            "Issue #159 — cross-domain review must fail loud at the entry point, not " +
            "silently proceed with potentially nonsensical verdicts.");
        ex.Which.DeclaredDomain.Should().Be(declaredDomain);
        ex.Which.RulesetDomain.Should().Be(ruleset.Domain);
        ex.Which.RulesetId.Should().Be(ruleset.Id);
    }

    [Theory]
    [MemberData(nameof(MismatchScenarios))]
    public async Task Null_DeclaredDomain_Inherits_And_Does_Not_Throw(
        string _declared, string vertical, string docId)
    {
        _ = _declared; // Theory data-shape parity; null-inherit path doesn't use it.
        // When the caller doesn't pass --domain, we inherit the
        // ruleset's authored domain (decision 1c). This must succeed
        // regardless of the document's actual topical vertical — the
        // ruleset owns the domain, not the document.
        var (evaluator, ruleset, projected) = await BuildEvalHarnessAsync(vertical, docId);

        var act = async () => await evaluator.EvaluateAsync(
            ruleset, projected, docKind: null, declaredDomain: null);
        await act.Should().NotThrowAsync(
            "null declaredDomain silently inherits the ruleset's domain — this is the " +
            "documented default behavior and must remain backward compatible.");
    }

    [Fact]
    public async Task Matching_DeclaredDomain_Does_Not_Throw()
    {
        var (evaluator, ruleset, projected) = await BuildEvalHarnessAsync("healthcare", "acme-telehealth-gaps");

        var act = async () => await evaluator.EvaluateAsync(
            ruleset, projected, docKind: null, declaredDomain: ruleset.Domain);
        await act.Should().NotThrowAsync(
            "explicit declaredDomain equal to the ruleset's authored domain must be accepted.");
    }

    [Fact]
    public async Task DeclaredDomain_Match_Is_Case_Insensitive()
    {
        var (evaluator, ruleset, projected) = await BuildEvalHarnessAsync("healthcare", "acme-telehealth-gaps");

        var act = async () => await evaluator.EvaluateAsync(
            ruleset, projected, docKind: null, declaredDomain: ruleset.Domain.ToUpperInvariant());
        await act.Should().NotThrowAsync(
            "domain comparison uses OrdinalIgnoreCase — capitalization must not matter.");
    }

    private async Task<(EvaluationService, RuleSet, ProjectedDocument)> BuildEvalHarnessAsync(
        string vertical, string docId)
    {
        var sourcePath = Path.Combine(CorpusRoot, vertical, docId, "source.md");
        File.Exists(sourcePath).Should().BeTrue($"golden doc must exist at {sourcePath}");
        File.Exists(ArchRulesetPath).Should().BeTrue($"arch ruleset must exist at {ArchRulesetPath}");

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<TimeProvider>(new FrozenTimeProvider(FrozenInstant));
        services
            .AddLambdaRagParsing()
            .AddLambdaRagProjection()
            .AddLambdaRagSelectors()
            .AddLambdaRagEvaluation();
        await using var sp = services.BuildServiceProvider();
        var parsers = sp.GetRequiredService<ParserRegistry>();

        var topicMap = TopicMapRegistry.Load($"{vertical}.v1");
        var projector = new DeterministicContractProjector(topicMap);

        var rulesetJson = await File.ReadAllTextAsync(ArchRulesetPath);
        var ruleset = JsonSerializer.Deserialize<RuleSet>(rulesetJson, CanonicalJson.Options)!;

        var docText = await File.ReadAllTextAsync(sourcePath);
        var parsed = await parsers.ParseAsync(sourcePath);
        var projected = await projector.ProjectAsync(parsed);

        var selector = sp.GetRequiredService<ISelectorMatcher>();
        var evaluator = new EvaluationService(
            selector,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EvaluationService>.Instance,
            sp.GetRequiredService<TimeProvider>(),
            factExtractor: new EmptyBagsFactExtractor());

        return (evaluator, ruleset, projected);
    }
}
