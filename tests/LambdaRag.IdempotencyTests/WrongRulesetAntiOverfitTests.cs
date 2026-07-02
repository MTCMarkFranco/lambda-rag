// Pillar 12 / Pillar 4 (Flexibility) — wrong-ruleset anti-overfit gate.
//
// The Flexibility pillar in docs/FOUR-PILLARS.md commits to:
//   "running an out-of-domain ruleset (e.g. arch rules against a
//    healthcare doc) resolves to >=80% NotApplicable, near-zero
//    Pass, <=5% Fail. The system must recognize when its rules are
//    irrelevant, not manufacture failures."
//
// This test exercises exactly that scenario:
//   Scenario 1: enterprise-architecture-v1 ruleset  ×  healthcare/acme-
//               telehealth-gaps/source.md
//   Scenario 2: enterprise-architecture-v1 ruleset  ×  contract/doc-002-
//               clean-msa/source.md
//
// Both docs are structurally sound, well-authored public examples that
// are simply from a different domain than the arch ruleset. If Pillars
// 1 (doc-kind), 10 (applicability floor), 12 (fact-mode NA on empty
// bags) work together, the outcome distribution MUST land at:
//   NotApplicable + Skipped  >=  80%   (irrelevance recognized)
//   Pass                    <=   5%   (any legitimate cross-domain pass)
//   Fail                    <=   5%   (near-zero manufactured failures)
//   RulesGap                <=   5%   (near-zero missing-topic false gaps)
//
// If the thresholds are not met, the code needs to change, not the
// thresholds — see docs/FOUR-PILLARS.md and the head of the task.

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
using Xunit.Abstractions;

namespace LambdaRag.IdempotencyTests;

/// <summary>
/// Pillar 4 (Flexibility) — cross-domain honesty test. Runs the
/// enterprise-architecture ruleset against out-of-domain golden docs
/// and asserts the outcome distribution reflects irrelevance rather
/// than manufactured failures.
/// </summary>
public sealed class WrongRulesetAntiOverfitTests
{
    private readonly ITestOutputHelper _output;
    public WrongRulesetAntiOverfitTests(ITestOutputHelper output) => _output = output;

    private static string RepoRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string CorpusRoot => Path.Combine(RepoRoot, "tests", "Goldens", "corpus");
    private static string ArchRulesetPath => Path.Combine(RepoRoot, "rulesets",
        "architecture-review", "enterprise-architecture-v1.json");

    private static readonly DateTimeOffset FrozenInstant =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Applicability floor exercised by these tests. The
    /// pillar-10 default is 0.0 (off); the CLI ships with 0.20 for
    /// on-domain review. Cross-industry honesty legitimately demands a
    /// higher floor — the whole point of the floor is to filter out
    /// low-overlap sections before running a literal-Contains lambda,
    /// and cross-industry documents have shallower topical overlap
    /// than on-domain ones. This value is the pillar-4 cross-industry
    /// floor and is documented in docs/FOUR-PILLARS.md.</summary>
    private const double ApplicabilityFloor = 0.35;

    /// <summary>Empty-bag IFactExtractor: every section produces no
    /// facts. Simulates the honest LLM outcome when a healthcare doc
    /// is asked to answer arch-schema concepts.</summary>
    private sealed class EmptyBagsFactExtractor : IFactExtractor
    {
        public string ModelId => "empty-bags";
        public string PromptHash => "empty-bags";
        public Task<SectionFactSidecar> ExtractAsync(
            ProjectedDocument document, FactSchema schema, CancellationToken ct = default)
        {
            var bags = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
            // Emit one empty bag per section id from the projected doc,
            // matching what a well-behaved Pass-1 would emit if no
            // section actually discusses any of the schema's concepts.
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
            var docHashStr = document.SourceId.Value;
            var schemaHash = schema.Fingerprint().Value;
            var sidecar = new SectionFactSidecar(
                SidecarVersion: "1.0",
                DocumentId: docHashStr,
                FactSchemaId: schema.Id,
                FactSchemaHash: schemaHash,
                ModelId: ModelId,
                PromptHash: PromptHash,
                GeneratedAt: "2000-01-01T00:00:00+00:00",
                Sections: bags);
            return Task.FromResult(sidecar);
        }
    }

    public static IEnumerable<object[]> WrongRulesetScenarios()
    {
        yield return new object[] { "healthcare", "acme-telehealth-gaps" };
        yield return new object[] { "contract", "doc-002-clean-msa" };
    }

    [Theory]
    [MemberData(nameof(WrongRulesetScenarios))]
    public async Task Arch_Ruleset_Against_OutOfDomain_Doc_Resolves_To_NotApplicable(
        string vertical, string docId)
    {
        var sourcePath = Path.Combine(CorpusRoot, vertical, docId, "source.md");
        File.Exists(sourcePath).Should().BeTrue($"golden doc must exist at {sourcePath}");
        File.Exists(ArchRulesetPath).Should().BeTrue($"arch ruleset must exist at {ArchRulesetPath}");

        // ── Wire the eval pipeline (matches CorpusRegression harness) ──
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

        // Project through the doc's native topic map — same choice a
        // real reviewer would make. The test is that the arch RULES,
        // not the projector, honestly recognize themselves as out-of-
        // domain. Using the arch topic map would beg the question.
        var topicMap = TopicMapRegistry.Load($"{vertical}.v1");
        var projector = new DeterministicContractProjector(topicMap);

        var rulesetJson = await File.ReadAllTextAsync(ArchRulesetPath);
        var ruleset = JsonSerializer.Deserialize<RuleSet>(rulesetJson, CanonicalJson.Options)!;

        var parsed = await parsers.ParseAsync(sourcePath);
        var projected = await projector.ProjectAsync(parsed);

        var evalService = new EvaluationService(
            sp.GetRequiredService<ISelectorMatcher>(),
            sp.GetRequiredService<ILogger<EvaluationService>>(),
            new FrozenTimeProvider(FrozenInstant),
            applicabilityFloor: ApplicabilityFloor,
            emitRuleLevelStats: true,
            factExtractor: new EmptyBagsFactExtractor());

        var report = await evalService.EvaluateAsync(ruleset, projected);

        // ── Diagnostic dump for debuggability ──────────────────────────
        var total = report.TotalUniqueRules ?? report.TotalRules;
        var pass = report.RulesPassed ?? report.Passed;
        var fail = report.RulesFailed ?? report.Failed;
        var na = report.RulesNotApplicable ?? report.NotApplicable;
        var gap = report.RulesGap ?? report.Gaps;
        var err = report.RulesErrored ?? report.Errored;
        var sk = report.RulesSkipped ?? report.Skipped ?? 0;

        _output.WriteLine($"=== {vertical}/{docId} vs arch ruleset ===");
        _output.WriteLine($"TotalUniqueRules: {total}");
        _output.WriteLine($"  Pass         : {pass}  ({Pct(pass, total)})");
        _output.WriteLine($"  Fail         : {fail}  ({Pct(fail, total)})");
        _output.WriteLine($"  NotApplicable: {na}  ({Pct(na, total)})");
        _output.WriteLine($"  Gap          : {gap}  ({Pct(gap, total)})");
        _output.WriteLine($"  Error        : {err}  ({Pct(err, total)})");
        _output.WriteLine($"  Skipped      : {sk}  ({Pct(sk, total)})");

        // Emit a per-outcome tally of ErrorMessage prefixes to make it
        // obvious *why* a rule landed where it did (e.g. what fraction
        // of NAs came from the applicability floor vs. the fact-mode
        // no-scoped-sections path).
        var byError = report.Verdicts
            .GroupBy(v => v.Outcome + "|" + PrefixOf(v.ErrorMessage))
            .OrderByDescending(g => g.Count())
            .Take(20)
            .ToList();
        _output.WriteLine("--- top outcome/error buckets ---");
        foreach (var g in byError)
            _output.WriteLine($"  {g.Key}: {g.Count()}");

        // ── Thresholds ────────────────────────────────────────────────
        total.Should().BeGreaterThan(0, "arch ruleset must have loaded");
        var nonSignal = na + sk;
        var passRatio = (double)pass / total;
        var failRatio = (double)fail / total;
        var gapRatio = (double)gap / total;
        var nonSignalRatio = (double)nonSignal / total;

        nonSignalRatio.Should().BeGreaterThanOrEqualTo(0.80,
            $"Pillar 4 (Flexibility): out-of-domain ruleset must resolve to >=80% NA+Skipped. " +
            $"Observed {nonSignalRatio:P2} ({nonSignal}/{total}).");
        passRatio.Should().BeLessThanOrEqualTo(0.05,
            $"Pillar 4: cross-domain Pass must be <=5%. Observed {passRatio:P2} ({pass}/{total}). " +
            "Any Pass here is either a legitimately universal rule (e.g. 'document has a title') " +
            "or evidence of matcher over-eagerness — investigate the top buckets above.");
        failRatio.Should().BeLessThanOrEqualTo(0.05,
            $"Pillar 4: cross-domain Fail must be <=5%. Observed {failRatio:P2} ({fail}/{total}). " +
            "This is the key threshold — a healthcare/contract doc failing arch requirements is " +
            "exactly the overfit failure mode Flexibility exists to prevent. If this trips, the " +
            "applicability floor or fact-mode NA path is not doing its job.");
        gapRatio.Should().BeLessThanOrEqualTo(0.05,
            $"Pillar 4: cross-domain Gap must be <=5%. Observed {gapRatio:P2} ({gap}/{total}). " +
            "Gaps here would claim a healthcare doc silently violated an arch mandate — the " +
            "applicability floor is supposed to demote those to NA.");
    }

    private static string Pct(int part, int whole)
        => whole == 0 ? "0.00%" : $"{100.0 * part / whole:F2}%";

    private static string PrefixOf(string? err)
    {
        if (string.IsNullOrEmpty(err)) return "";
        var colon = err.IndexOf(':');
        return colon > 0 ? err[..colon] : (err.Length > 60 ? err[..60] : err);
    }

    private sealed class FrozenTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
