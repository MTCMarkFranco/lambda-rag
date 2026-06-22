using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LambdaRag.Authoring;
using LambdaRag.Core;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Evaluation;
using LambdaRag.Evaluation.Engine;
using LambdaRag.Parsing;
using LambdaRag.Projection;
using LambdaRag.Projection.Projectors;
using LambdaRag.Selectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace LambdaRag.IdempotencyTests;

/// <summary>
/// Cross-industry accuracy harness.
///
/// Iterates every <c>tests/Goldens/corpus/{vertical}/{doc-id}/</c> triple, runs
/// the full lambda-rag pipeline, and compares the engine's per-rule
/// PASS/FAIL outcome against the LLM-judged ground truth in
/// <c>expected-llm.json</c>. Asserts per-scenario recall / false-positive /
/// F1 gates loaded from an optional <c>scenario.json</c> sitting next to
/// the document (defaults are conservative: recall ≥ 0.85, max FP = 0,
/// min F1 ≥ 0.85).
///
/// Scenarios that lack <c>expected-llm.json</c> are SKIPPED with a clear
/// reason — they are unjudged ground truth, not failing scenarios.
/// Generate the missing goldens with:
///   <c>dotnet run --project tools/LlmGroundTruth -- --vertical &lt;v&gt; --doc &lt;d&gt;</c>
///
/// Idempotency / byte-identity is already covered by <see cref="CorpusRegression"/>.
/// This harness's responsibility is purely "is the engine as accurate as the LLM".
/// </summary>
public sealed class AccuracyHarness
{
    private readonly ITestOutputHelper _output;
    public AccuracyHarness(ITestOutputHelper output) => _output = output;

    private static string RepoRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string CorpusRoot => Path.Combine(RepoRoot, "tests", "Goldens", "corpus");
    private static string LedgerPath => Path.Combine(RepoRoot, "bench-results", "cross-industry-ledger.csv");

    private static readonly DateTimeOffset FrozenInstant =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Default gates — tuned conservatively. Per-scenario overrides live
    // in scenario.json. Keeping defaults strict means a new scenario
    // either meets the bar or surfaces immediately as a calibration task.
    private static readonly ScenarioGates DefaultGates =
        new(RecallMinPct: 0.85, MaxFalsePositives: 0, MinF1: 0.85);

    private static readonly ScenarioEvaluation DefaultEvaluation =
        new(SemanticThresholdOffset: 0.0,
            MinEffectiveSemanticThreshold: 0.0,
            EnforceSoftCohesion: false,
            MinEvidencedAnchors: 2);

    public static IEnumerable<object[]> Scenarios()
    {
        if (!Directory.Exists(CorpusRoot)) yield break;
        foreach (var verticalDir in Directory.EnumerateDirectories(CorpusRoot)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var vertical = Path.GetFileName(verticalDir);
            var rulesetPath = Path.Combine(verticalDir, "ruleset.json");
            if (!File.Exists(rulesetPath)) continue;
            foreach (var docDir in Directory.EnumerateDirectories(verticalDir)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                var docId = Path.GetFileName(docDir);
                var sourcePath = Path.Combine(docDir, "source.md");
                if (!File.Exists(sourcePath)) continue;
                yield return new object[] { vertical, docId };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Engine_meets_per_scenario_accuracy_gates(string vertical, string docId)
    {
        var verticalDir = Path.Combine(CorpusRoot, vertical);
        var docDir = Path.Combine(verticalDir, docId);
        var rulesetPath = Path.Combine(verticalDir, "ruleset.json");
        var sourcePath = Path.Combine(docDir, "source.md");
        var llmGroundTruthPath = Path.Combine(docDir, "expected-llm.json");
        var scenarioCfgPath = Path.Combine(docDir, "scenario.json");

        if (!File.Exists(llmGroundTruthPath))
        {
            // Treat missing ground truth as Skip, not Fail. Adding a new
            // scenario should not break CI before someone has run the
            // LLM generator. The summary in test output tells the dev
            // exactly how to unblock.
            _output.WriteLine(
                $"SKIP {vertical}/{docId}: no expected-llm.json. " +
                $"Generate with: dotnet run --project tools/LlmGroundTruth " +
                $"-- --vertical {vertical} --doc {docId}");
            return;
        }

        var groundTruth = LoadGroundTruth(llmGroundTruthPath);
        var scenarioCfg = LoadScenarioConfig(scenarioCfgPath);

        var report = await RunPipelineAsync(
            rulesetPath, sourcePath, vertical, scenarioCfg.Evaluation);

        var metrics = ComputeMetrics(report, groundTruth);
        AppendLedger(vertical, docId, scenarioCfg, metrics);
        EmitScoreboard(vertical, docId, scenarioCfg, metrics, groundTruth);

        var gates = scenarioCfg.Gates;
        using (new FluentAssertions.Execution.AssertionScope())
        {
            metrics.Recall.Should().BeGreaterThanOrEqualTo(gates.RecallMinPct,
                $"recall gate ({gates.RecallMinPct:P0}) for {vertical}/{docId}");
            metrics.FalsePositives.Should().BeLessThanOrEqualTo(gates.MaxFalsePositives,
                $"FP gate (≤{gates.MaxFalsePositives}) for {vertical}/{docId}");
            metrics.F1.Should().BeGreaterThanOrEqualTo(gates.MinF1,
                $"F1 gate ({gates.MinF1:F2}) for {vertical}/{docId}");
        }
    }

    // ── pipeline ───────────────────────────────────────────────────────

    private static async Task<ComplianceReport> RunPipelineAsync(
        string rulesetPath,
        string sourcePath,
        string vertical,
        ScenarioEvaluation evalCfg)
    {
        var configuration = BuildBenchmarkConfiguration();

        // Load the ruleset first so we can decide whether to wire in
        // LambdaRag.Authoring. Authoring transitively pulls Azure.Search.Documents
        // and the RuntimeDeterminismGuardrails guardrail forbids that assembly
        // from being loaded into the test AppDomain when no anchored rules
        // are present (issue #74).
        var rulesetJson = await File.ReadAllTextAsync(rulesetPath);
        var ruleset = JsonSerializer.Deserialize<RuleSet>(rulesetJson, CanonicalJson.Options)!;
        var hasAnchoredRules = ruleset.Rules.Any(r =>
            r.SemanticAnchors is { Count: > 0 });

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<TimeProvider>(new FrozenTimeProvider(FrozenInstant));
        services.AddSingleton<IConfiguration>(configuration);
        services
            .AddLambdaRagParsing()
            .AddLambdaRagProjection()
            .AddLambdaRagSelectors()
            .AddLambdaRagEvaluation();
        if (hasAnchoredRules)
            RegisterAuthoring(services, configuration);

        await using var sp = services.BuildServiceProvider();
        var parsers = sp.GetRequiredService<ParserRegistry>();

        var topicMap = TopicMapRegistry.Load($"{vertical}.v1");

        // Mirror ArbPsaBenchmark's anchor-aware wiring. If the ruleset
        // carries semanticAnchors we build a projector + evaluator that
        // can resolve them; otherwise the simple anchor-free path is fine
        // and avoids paying for the embedder.

        IDocumentProjector projector;
        EvaluationService evaluator;

        if (hasAnchoredRules)
        {
            (projector, evaluator) = BuildAnchoredPipeline(sp, topicMap, ruleset, evalCfg);
        }
        else
        {
            projector = new DeterministicContractProjector(topicMap);
            evaluator = sp.GetRequiredService<EvaluationService>();
        }

        var parsed = await parsers.ParseAsync(sourcePath);
        var projected = await projector.ProjectAsync(parsed);
        return await evaluator.EvaluateAsync(ruleset, projected);
    }

    /// <summary>
    /// Wraps <c>AddLambdaRagAuthoring</c> in a helper method so the JIT only
    /// resolves the LambdaRag.Authoring assembly (and its transitive
    /// Azure.Search.Documents dependency) when an anchored ruleset actually
    /// runs. Required to keep the anchor-free corpus path cloud-free per
    /// RuntimeDeterminismGuardrails (issue #74).
    /// </summary>
    private static void RegisterAuthoring(IServiceCollection services, IConfiguration configuration)
        => services.AddLambdaRagAuthoring(configuration);

    /// <summary>
    /// Isolates all references to <see cref="IRuleEmbedder"/> (and any other
    /// LambdaRag.Authoring type) in a separate method so the JIT only resolves
    /// the Authoring assembly when an anchored ruleset is actually run.
    /// Keeps Azure.Search.Documents out of the AppDomain for the common
    /// anchor-free path the RuntimeDeterminismGuardrails enforces (issue #74).
    /// </summary>
    private static (IDocumentProjector projector, EvaluationService evaluator) BuildAnchoredPipeline(
        IServiceProvider sp,
        TopicMap topicMap,
        RuleSet ruleset,
        ScenarioEvaluation evalCfg)
    {
        var ruleEmbedder = sp.GetRequiredService<IRuleEmbedder>();
        var projector = new DeterministicContractProjector(
            topicMap,
            ruleSet: ruleset,
            ruleEmbedder: ruleEmbedder,
            syntheticCosineThreshold: 0.30,
            logger: sp.GetService<ILogger<DeterministicContractProjector>>());
        var evaluator = new EvaluationService(
            sp.GetRequiredService<ISelectorMatcher>(),
            sp.GetRequiredService<ILogger<EvaluationService>>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ICandidateRuleFilter>(),
            vectorStore: null,
            tokenEmbedder: ruleEmbedder,
            semanticThresholdOffset: evalCfg.SemanticThresholdOffset,
            minEffectiveSemanticThreshold: evalCfg.MinEffectiveSemanticThreshold,
            enforceSoftCohesion: evalCfg.EnforceSoftCohesion,
            minEvidencedAnchors: evalCfg.MinEvidencedAnchors);
        return (projector, evaluator);
    }

    // ── ground truth + config IO ───────────────────────────────────────

    private sealed class LlmGroundTruth
    {
        public string SchemaVersion { get; init; } = "1.0.0";
        public string Model { get; init; } = "";
        public string RulesetId { get; init; } = "";
        public string RulesetVersion { get; init; } = "";
        public string RulesetFingerprint { get; init; } = "";
        public string DocumentHash { get; init; } = "";
        public string DocumentPath { get; init; } = "";
        public Dictionary<string, LlmRuleVerdict> PerRule { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class LlmRuleVerdict
    {
        public string Verdict { get; init; } = "FAIL";
        public string Evidence { get; init; } = "";
        public string Reason { get; init; } = "";
    }

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private static LlmGroundTruth LoadGroundTruth(string path)
        => JsonSerializer.Deserialize<LlmGroundTruth>(File.ReadAllText(path), ReadOpts)
           ?? throw new InvalidDataException($"unreadable expected-llm.json at {path}");

    private sealed record ScenarioConfig(ScenarioEvaluation Evaluation, ScenarioGates Gates);

    private sealed record ScenarioEvaluation(
        double SemanticThresholdOffset,
        double MinEffectiveSemanticThreshold,
        bool EnforceSoftCohesion,
        int MinEvidencedAnchors);

    private sealed record ScenarioGates(
        double RecallMinPct,
        int MaxFalsePositives,
        double MinF1);

    private static ScenarioConfig LoadScenarioConfig(string path)
    {
        if (!File.Exists(path))
            return new ScenarioConfig(DefaultEvaluation, DefaultGates);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var eval = DefaultEvaluation;
        if (root.TryGetProperty("evaluation", out var evalEl))
        {
            eval = new ScenarioEvaluation(
                SemanticThresholdOffset: GetDouble(evalEl, "semantic_threshold_offset", eval.SemanticThresholdOffset),
                MinEffectiveSemanticThreshold: GetDouble(evalEl, "min_effective_semantic_threshold", eval.MinEffectiveSemanticThreshold),
                EnforceSoftCohesion: GetBool(evalEl, "enforce_soft_cohesion", eval.EnforceSoftCohesion),
                MinEvidencedAnchors: GetInt(evalEl, "min_evidenced_anchors", eval.MinEvidencedAnchors));
        }

        var gates = DefaultGates;
        if (root.TryGetProperty("gates", out var gatesEl))
        {
            gates = new ScenarioGates(
                RecallMinPct: GetDouble(gatesEl, "recall_min_pct", gates.RecallMinPct),
                MaxFalsePositives: GetInt(gatesEl, "max_false_positives", gates.MaxFalsePositives),
                MinF1: GetDouble(gatesEl, "min_f1", gates.MinF1));
        }
        return new ScenarioConfig(eval, gates);
    }

    private static double GetDouble(JsonElement el, string name, double fallback)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
    private static int GetInt(JsonElement el, string name, int fallback)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
    private static bool GetBool(JsonElement el, string name, bool fallback)
        => el.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : fallback;

    // ── metrics ────────────────────────────────────────────────────────

    private sealed record AccuracyMetrics(
        int TruePositives,
        int FalsePositives,
        int FalseNegatives,
        int TrueNegatives,
        int NotApplicableSkipped,
        int UnknownRulesInGroundTruth,
        double Recall,
        double Precision,
        double F1,
        List<string> FalsePositiveRules,
        List<string> FalseNegativeRules);

    private static AccuracyMetrics ComputeMetrics(ComplianceReport report, LlmGroundTruth groundTruth)
    {
        var enginePassRules = new HashSet<string>(
            report.Verdicts.Where(v => v.Outcome == VerdictOutcome.Pass).Select(v => v.RuleId),
            StringComparer.Ordinal);

        int tp = 0, fp = 0, fn = 0, tn = 0, na = 0, unknown = 0;
        var fpRules = new List<string>();
        var fnRules = new List<string>();

        foreach (var (ruleId, judgement) in groundTruth.PerRule)
        {
            var enginePassed = enginePassRules.Contains(ruleId);
            switch (judgement.Verdict)
            {
                case "PASS":
                    if (enginePassed) tp++;
                    else { fn++; fnRules.Add(ruleId); }
                    break;
                case "FAIL":
                    if (enginePassed) { fp++; fpRules.Add(ruleId); }
                    else tn++;
                    break;
                case "NOT_APPLICABLE":
                    // Excluded from accuracy metrics — if the engine
                    // ALSO didn't pass, that's the consistent answer; if
                    // it did pass we don't penalise (the LLM may have
                    // been overly cautious). We just count it.
                    na++;
                    break;
                default:
                    unknown++;
                    break;
            }
        }

        var recallDenom = tp + fn;
        var precDenom = tp + fp;
        var recall = recallDenom == 0 ? 1.0 : (double)tp / recallDenom;
        var precision = precDenom == 0 ? 1.0 : (double)tp / precDenom;
        var f1 = (recall + precision) == 0 ? 0.0 : 2 * recall * precision / (recall + precision);

        return new AccuracyMetrics(
            tp, fp, fn, tn, na, unknown, recall, precision, f1, fpRules, fnRules);
    }

    // ── output ─────────────────────────────────────────────────────────

    private void EmitScoreboard(
        string vertical, string docId,
        ScenarioConfig cfg, AccuracyMetrics m,
        LlmGroundTruth gt)
    {
        _output.WriteLine($"── {vertical}/{docId} ─────────────────────────────────");
        _output.WriteLine($"  ruleset:  {gt.RulesetId} v{gt.RulesetVersion}");
        _output.WriteLine($"  judge:    {gt.Model}");
        _output.WriteLine($"  eval:     offset={cfg.Evaluation.SemanticThresholdOffset:F2} " +
                          $"cohesion={cfg.Evaluation.EnforceSoftCohesion}");
        _output.WriteLine($"  results:  TP={m.TruePositives} FP={m.FalsePositives} " +
                          $"FN={m.FalseNegatives} TN={m.TrueNegatives} N/A={m.NotApplicableSkipped}");
        _output.WriteLine($"  metrics:  recall={m.Recall:P1}  precision={m.Precision:P1}  F1={m.F1:F3}");
        _output.WriteLine($"  gates:    recall≥{cfg.Gates.RecallMinPct:P0} " +
                          $"FP≤{cfg.Gates.MaxFalsePositives} F1≥{cfg.Gates.MinF1:F2}");
        if (m.FalsePositiveRules.Count > 0)
            _output.WriteLine($"  FP rules: {string.Join(", ", m.FalsePositiveRules)}");
        if (m.FalseNegativeRules.Count > 0)
            _output.WriteLine($"  FN rules: {string.Join(", ", m.FalseNegativeRules)}");
    }

    private static readonly object LedgerLock = new();

    private static void AppendLedger(
        string vertical, string docId, ScenarioConfig cfg, AccuracyMetrics m)
    {
        try
        {
            var dir = Path.GetDirectoryName(LedgerPath)!;
            Directory.CreateDirectory(dir);
            var newFile = !File.Exists(LedgerPath);
            lock (LedgerLock)
            {
                using var sw = new StreamWriter(LedgerPath, append: true, Encoding.UTF8);
                if (newFile)
                {
                    sw.WriteLine("timestamp_utc,vertical,doc_id,offset,cohesion,tp,fp,fn,tn,na,recall,precision,f1,passes_gates");
                }
                var passesGates = m.Recall >= cfg.Gates.RecallMinPct
                                  && m.FalsePositives <= cfg.Gates.MaxFalsePositives
                                  && m.F1 >= cfg.Gates.MinF1;
                sw.WriteLine(string.Join(",",
                    DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    vertical,
                    docId,
                    cfg.Evaluation.SemanticThresholdOffset.ToString("F2", CultureInfo.InvariantCulture),
                    cfg.Evaluation.EnforceSoftCohesion ? "1" : "0",
                    m.TruePositives, m.FalsePositives, m.FalseNegatives, m.TrueNegatives,
                    m.NotApplicableSkipped,
                    m.Recall.ToString("F3", CultureInfo.InvariantCulture),
                    m.Precision.ToString("F3", CultureInfo.InvariantCulture),
                    m.F1.ToString("F3", CultureInfo.InvariantCulture),
                    passesGates ? "1" : "0"));
            }
        }
        catch
        {
            // Ledger is best-effort observability — never fail a test on
            // a ledger write error (disk full, permissions, race).
        }
    }

    // ── plumbing ───────────────────────────────────────────────────────

    private const string CliUserSecretsId =
        "lambda-rag-cli-3f1e7b8c-9c2a-4f6e-bf2a-2c5b9c6d4e10";

    private static IConfiguration BuildBenchmarkConfiguration()
        => new ConfigurationBuilder()
            .AddUserSecrets(userSecretsId: CliUserSecretsId, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

    private sealed class FrozenTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
