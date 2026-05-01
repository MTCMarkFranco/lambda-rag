using System.Text.Json;
using FluentAssertions;
using LambdaRag.Core;
using LambdaRag.Core.Domain;
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
/// Phase-1 corpus regression: iterates every
/// <c>tests/Goldens/corpus/{topic-map}/{doc-id}/</c> triple, runs the full
/// review pipeline against the matching topic-map projector, and asserts
/// the produced <see cref="ComplianceReport"/> matches the checked-in
/// <c>expected-verdict.json</c>.
///
/// Behaviour on a missing golden: the test bootstraps the file and fails
/// loudly with the path. The developer inspects the bootstrapped JSON,
/// commits it if intentional, and re-runs to lock the baseline.
///
/// This is the platform's accuracy backbone — every public-source-grounded
/// rule in the corpus is exercised against every document on every CI run.
/// Drift in the projector, selector, or rules engine that affects any of
/// the verdicts will fail this test before it merges.
/// </summary>
public sealed class CorpusRegression
{
    private readonly ITestOutputHelper _output;
    public CorpusRegression(ITestOutputHelper output) => _output = output;

    private static string CorpusRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "Goldens", "corpus"));

    private static readonly DateTimeOffset FrozenInstant =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Topic-map id used for the projector when we run the corpus document
    /// through the pipeline. The directory name under <c>corpus/</c> selects
    /// the topic map (e.g. <c>gov-architecture</c> →
    /// <c>gov-architecture.v1</c>).
    /// </summary>
    private static string TopicMapIdFor(string vertical) => $"{vertical}.v1";

    public static IEnumerable<object[]> CorpusTriples()
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
    [MemberData(nameof(CorpusTriples))]
    public async Task Document_matches_expected_verdict(string vertical, string docId)
    {
        var verticalDir = Path.Combine(CorpusRoot, vertical);
        var docDir = Path.Combine(verticalDir, docId);
        var rulesetPath = Path.Combine(verticalDir, "ruleset.json");
        var sourcePath = Path.Combine(docDir, "source.md");
        var expectedPath = Path.Combine(docDir, "expected-verdict.json");

        File.Exists(rulesetPath).Should().BeTrue($"ruleset.json must exist at {rulesetPath}");
        File.Exists(sourcePath).Should().BeTrue($"source.md must exist at {sourcePath}");

        var observedJson = await ProduceVerdictJsonAsync(rulesetPath, sourcePath, vertical);

        if (!File.Exists(expectedPath))
        {
            File.WriteAllText(expectedPath, observedJson);
            Assert.Fail(
                $"Golden expected-verdict.json did not exist for {vertical}/{docId}; " +
                $"bootstrapped at {expectedPath}. Inspect, then commit it to lock the " +
                "baseline. Re-run the test to confirm green.");
        }

        var expectedJson = File.ReadAllText(expectedPath);
        if (observedJson != expectedJson)
        {
            // Surface a concise diff in the test output so a CI failure is
            // immediately actionable without opening a debugger.
            _output.WriteLine($"Drift detected for {vertical}/{docId}");
            _output.WriteLine($"Expected file: {expectedPath}");
            _output.WriteLine("--- expected (first 40 lines) ---");
            foreach (var line in expectedJson.Split('\n').Take(40)) _output.WriteLine(line);
            _output.WriteLine("--- observed (first 40 lines) ---");
            foreach (var line in observedJson.Split('\n').Take(40)) _output.WriteLine(line);
        }
        observedJson.Should().Be(expectedJson,
            $"the produced ComplianceReport for {vertical}/{docId} must match the " +
            $"checked-in golden at {expectedPath}; if this drift was intentional, " +
            "delete the golden file and re-run the test to regenerate it.");
    }

    private static async Task<string> ProduceVerdictJsonAsync(
        string rulesetPath,
        string sourcePath,
        string vertical)
    {
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
        var evaluator = sp.GetRequiredService<EvaluationService>();

        // Topic-map-specific projector — directory name under corpus/
        // selects the topic map.
        var topicMap = TopicMapRegistry.Load(TopicMapIdFor(vertical));
        var projector = new DeterministicContractProjector(topicMap);

        var rulesetJson = await File.ReadAllTextAsync(rulesetPath);
        var ruleset = JsonSerializer.Deserialize<RuleSet>(rulesetJson, CanonicalJson.Options)!;

        var parsed = await parsers.ParseAsync(sourcePath);
        var projected = await projector.ProjectAsync(parsed);
        var report = await evaluator.EvaluateAsync(ruleset, projected);

        return JsonSerializer.Serialize(report, CanonicalJson.Options);
    }

    private sealed class FrozenTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
