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
        var report = await RunWrongRulesetAsync(vertical, docId, ApplicabilityFloor);
        DumpDiagnostics(report, $"{vertical}/{docId} vs arch @ floor {ApplicabilityFloor}");

        var (total, pass, fail, na, gap, _, sk) = TallyOutcomes(report);
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

    // ────────────────────────────────────────────────────────────────
    // Issue #154 — Production-floor (0.20) ratchet gate.
    //
    // The floor-0.35 test above proves the ARCHITECTURE generalizes:
    // fact-mode rules resolve to NA on empty bags, and the applicability
    // filter at 0.35 sieves out the noisy classic-lambda leakers.
    // But 0.20 is the ship default (in-domain review floor) and at 0.20
    // the ~344 remaining classic-lambda rules (still using Contains(...)
    // predicates) leak Fail on out-of-domain docs. Measured healthcare
    // fail ≈ 9.3% today.
    //
    // This test is a RATCHET, not a fixed gate. It captures today's
    // ceiling as the acceptance threshold so:
    //   1) The number can only go DOWN as classic rules migrate to
    //      fact-mode. Any regression fails loud.
    //   2) The ceiling is tightened in-band whenever we cross a
    //      milestone (see CHANGELOG for the tightening ledger).
    //   3) The diagnostic output ranks classic-lambda rules by Fail
    //      contribution — this is Phase 2 evidence for which concepts
    //      to add to `factSchema` next. Evidence-driven conversion,
    //      not vocabulary-driven (Flexibility principle).
    //
    // If you tighten these ceilings without shipping the corresponding
    // classic→fact-mode conversions, you're moving the goalposts.
    // Update CHANGELOG.md [Unreleased] instead.
    // ────────────────────────────────────────────────────────────────
    private const double ProductionApplicabilityFloor = 0.20;

    public static IEnumerable<object[]> WrongRulesetScenariosAtProductionFloor()
    {
        // scenario, ceilingFailRatio — ratchet ledger (see CHANGELOG.md
        // "Flexibility ratchet gate" entry). Tighten only when classic
        // rules migrate to fact-mode; never raise to make a build green.
        //   2026-07-02: initial ceiling 0.10 (healthcare 9.34%, contract 4.67%)
        //   2026-07-02: batch 1 — EA-DATA-018 + EA-DATA-019 → fact-mode
        //               (healthcare 8.79% ≤ 0.09, contract 4.12% ≤ 0.05)
        //   2026-07-02: batch 2 — EA-CICD-011 + EA-IAM-023 → fact-mode
        //               (healthcare 8.24% ≤ 0.085, contract 3.57% ≤ 0.04)
        //   2026-07-02: batch 3 — RequiredFactsAny primitive + EA-SECR-007
        //               (healthcare 8.24% ≤ 0.085, contract 3.30% ≤ 0.036)
        //   2026-07-02: batch 4 — EA-IAC-015 (console_action_migration_days)
        //               (healthcare 7.97% ≤ 0.083, contract 3.02% ≤ 0.034)
        yield return new object[] { "healthcare", "acme-telehealth-gaps", 0.083 };
        yield return new object[] { "contract",   "doc-002-clean-msa",    0.034 };
    }

    [Theory]
    [MemberData(nameof(WrongRulesetScenariosAtProductionFloor))]
    public async Task Arch_Ruleset_Against_OutOfDomain_Doc_At_Production_Floor_Ratchet(
        string vertical, string docId, double ceilingFailRatio)
    {
        var report = await RunWrongRulesetAsync(vertical, docId, ProductionApplicabilityFloor);
        DumpDiagnostics(report, $"{vertical}/{docId} vs arch @ floor {ProductionApplicabilityFloor} (RATCHET)");
        DumpTopFailingRules(report, ruleset: null, topN: 25);

        var (total, _, fail, _, _, _, _) = TallyOutcomes(report);
        var failRatio = (double)fail / total;

        failRatio.Should().BeLessThanOrEqualTo(ceilingFailRatio,
            $"Issue #154 ratchet: production-floor Fail% must not regress above {ceilingFailRatio:P0}. " +
            $"Observed {failRatio:P2} ({fail}/{total}). " +
            "This ceiling tightens only when classic-lambda rules migrate to fact-mode (see " +
            "docs/FOUR-PILLARS.md + CHANGELOG). Do not raise it to make a failing build green.");
    }

    // ────────────────────────────────────────────────────────────────
    // Shared eval harness.
    // ────────────────────────────────────────────────────────────────
    private async Task<ComplianceReport> RunWrongRulesetAsync(
        string vertical, string docId, double floor)
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

        var parsed = await parsers.ParseAsync(sourcePath);
        var projected = await projector.ProjectAsync(parsed);

        var evalService = new EvaluationService(
            sp.GetRequiredService<ISelectorMatcher>(),
            sp.GetRequiredService<ILogger<EvaluationService>>(),
            new FrozenTimeProvider(FrozenInstant),
            applicabilityFloor: floor,
            emitRuleLevelStats: true,
            factExtractor: new EmptyBagsFactExtractor());

        return await evalService.EvaluateAsync(ruleset, projected);
    }

    private static (int total, int pass, int fail, int na, int gap, int err, int sk)
        TallyOutcomes(ComplianceReport report)
    {
        var total = report.TotalUniqueRules ?? report.TotalRules;
        var pass = report.RulesPassed ?? report.Passed;
        var fail = report.RulesFailed ?? report.Failed;
        var na = report.RulesNotApplicable ?? report.NotApplicable;
        var gap = report.RulesGap ?? report.Gaps;
        var err = report.RulesErrored ?? report.Errored;
        var sk = report.RulesSkipped ?? report.Skipped ?? 0;
        return (total, pass, fail, na, gap, err, sk);
    }

    private void DumpDiagnostics(ComplianceReport report, string header)
    {
        var (total, pass, fail, na, gap, err, sk) = TallyOutcomes(report);
        _output.WriteLine($"=== {header} ===");
        _output.WriteLine($"TotalUniqueRules: {total}");
        _output.WriteLine($"  Pass         : {pass}  ({Pct(pass, total)})");
        _output.WriteLine($"  Fail         : {fail}  ({Pct(fail, total)})");
        _output.WriteLine($"  NotApplicable: {na}  ({Pct(na, total)})");
        _output.WriteLine($"  Gap          : {gap}  ({Pct(gap, total)})");
        _output.WriteLine($"  Error        : {err}  ({Pct(err, total)})");
        _output.WriteLine($"  Skipped      : {sk}  ({Pct(sk, total)})");

        var byError = report.Verdicts
            .GroupBy(v => v.Outcome + "|" + PrefixOf(v.ErrorMessage))
            .OrderByDescending(g => g.Count())
            .Take(20)
            .ToList();
        _output.WriteLine("--- top outcome/error buckets ---");
        foreach (var g in byError)
            _output.WriteLine($"  {g.Key}: {g.Count()}");
    }

    // Phase 2 diagnostic (Issue #154): rank classic-lambda rules by Fail
    // contribution on out-of-domain docs. Feeds the concept-picking for
    // schema expansion. Do NOT read this list and add concepts because
    // *any single doc* surfaced them; look for concepts that appear
    // across BOTH scenarios (arch-vs-healthcare AND arch-vs-contract)
    // before promoting to schema (Flexibility principle).
    private void DumpTopFailingRules(ComplianceReport report, RuleSet? ruleset, int topN)
    {
        var failing = report.Verdicts
            .Where(v => v.Outcome == VerdictOutcome.Fail)
            .GroupBy(v => v.RuleId)
            .OrderByDescending(g => g.Count())
            .Take(topN)
            .ToList();

        _output.WriteLine($"--- top-{topN} failing classic-lambda rules (Phase 2 evidence for issue #154) ---");
        foreach (var g in failing)
        {
            var first = g.First();
            var msg = first.ErrorMessage ?? "";
            if (msg.Length > 120) msg = msg[..120] + "…";
            _output.WriteLine($"  [{g.Count()}x] {g.Key}  |  {msg}");
        }
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
