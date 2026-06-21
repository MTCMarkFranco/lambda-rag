using System.Text.Json;
using FluentAssertions;
using LambdaRag.Authoring;
using LambdaRag.Authoring.Embeddings;
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
/// One-shot diagnostic harness for the ARB-PSA benchmark. Not a gate.
///
/// Runs the same pipeline as <see cref="ArbPsaBenchmark"/> but with toggles
/// for every Pillar-9 lever (offset, cohesion) and a verbose dump per rule:
///   • outcome, primary topic of matched section, predicate dimension
///   • per-anchor bindings (count, peak cosine, peak token)
///   • whether the section was synthetic-anchor
///
/// Designed to be invoked once per experiment with different settings via
/// environment variables — the existing benchmark wiring is left alone.
///
/// Env vars (read on test entry):
///   ARB_DIAG_OFFSET           — double, default 0.0
///   ARB_DIAG_MIN_EFFECTIVE    — double, default 0.0
///   ARB_DIAG_COHESION         — "1"/"true" to enable soft cohesion
///   ARB_DIAG_MIN_EVIDENCED    — int, default 2
///   ARB_DIAG_DUMP_PATH        — optional file to write the JSON dump
/// </summary>
public sealed class ArbPsaDiagnostics
{
    private readonly ITestOutputHelper _output;
    public ArbPsaDiagnostics(ITestOutputHelper output) => _output = output;

    private static string RepoRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string PsaSamplePath => Path.Combine(
        RepoRoot, "samples", "architecture",
        "Example PSA - 16614 Shipping 360 ARB2 v2.1.pdf");

    private static string RulesetPath => Path.Combine(
        RepoRoot, "rulesets", "architecture-review", "arb-psa.json");

    private const string CliUserSecretsId =
        "lambda-rag-cli-3f1e7b8c-9c2a-4f6e-bf2a-2c5b9c6d4e10";

    private static readonly DateTimeOffset FrozenInstant =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Dump_per_rule_diagnostics()
    {
        if (!File.Exists(PsaSamplePath))
        {
            _output.WriteLine($"PSA sample missing at {PsaSamplePath} — skipping diagnostics.");
            return;
        }

        var offset = ParseDouble("ARB_DIAG_OFFSET", 0.0);
        var minEff = ParseDouble("ARB_DIAG_MIN_EFFECTIVE", 0.0);
        var cohesion = ParseBool("ARB_DIAG_COHESION", false);
        var minEvidenced = ParseInt("ARB_DIAG_MIN_EVIDENCED", 2);
        var dumpPath = Environment.GetEnvironmentVariable("ARB_DIAG_DUMP_PATH");

        _output.WriteLine(
            $"Diag config: offset={offset} minEffective={minEff} cohesion={cohesion} minEvidenced={minEvidenced}");

        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(userSecretsId: CliUserSecretsId, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<TimeProvider>(new FrozenTimeProvider(FrozenInstant));
        services.AddSingleton<IConfiguration>(configuration);
        services
            .AddLambdaRagParsing()
            .AddLambdaRagProjection()
            .AddLambdaRagSelectors()
            .AddLambdaRagEvaluation()
            .AddLambdaRagAuthoring(configuration);

        await using var sp = services.BuildServiceProvider();
        var parsers = sp.GetRequiredService<ParserRegistry>();
        var ruleEmbedder = sp.GetRequiredService<IRuleEmbedder>();

        var topicMap = TopicMapRegistry.Load("arb-psa.v1");
        var rulesetJson = await File.ReadAllTextAsync(RulesetPath);
        var ruleset = JsonSerializer.Deserialize<RuleSet>(rulesetJson, CanonicalJson.Options)!;

        var projector = new DeterministicContractProjector(
            topicMap,
            ruleSet: ruleset,
            ruleEmbedder: ruleEmbedder,
            syntheticCosineThreshold: 0.30,
            logger: sp.GetService<ILogger<DeterministicContractProjector>>());

        var parsed = await parsers.ParseAsync(PsaSamplePath);
        var docKind = DocKindResolver.Resolve(
            explicitKind: "arb-psa", path: PsaSamplePath, parsed: parsed);
        var projected = await projector.ProjectAsync(parsed);

        var evaluator = new EvaluationService(
            sp.GetRequiredService<ISelectorMatcher>(),
            sp.GetRequiredService<ILogger<EvaluationService>>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ICandidateRuleFilter>(),
            vectorStore: null,
            tokenEmbedder: ruleEmbedder,
            semanticThresholdOffset: offset,
            minEffectiveSemanticThreshold: minEff,
            enforceSoftCohesion: cohesion,
            minEvidencedAnchors: minEvidenced);

        var report = await evaluator.EvaluateAsync(ruleset, projected, docKind);

        // anchor counts per rule (from the ruleset, since report verdicts
        // strip anchor metadata after evaluation).
        var anchorsByRule = ruleset.Rules.ToDictionary(
            r => r.Id,
            r => r.SemanticAnchors?.Select(a => a.Name).ToList() ?? new List<string>(),
            StringComparer.Ordinal);

        var dump = new List<Dictionary<string, object?>>();
        foreach (var v in report.Verdicts.OrderBy(v => v.RuleId, StringComparer.Ordinal))
        {
            var primaryTopic = v.EvaluatedInput?["category"]?.GetValue<string?>();
            var isSynthetic = v.EvaluatedInput?["is_synthetic_anchor"]?.GetValue<bool?>() ?? false;
            var sectionPath = v.EvaluatedInput?["path"]?.GetValue<string?>();
            var dim = ExtractDim(v.PredicateText);
            var anchors = anchorsByRule.TryGetValue(v.RuleId, out var a) ? a : new List<string>();

            // group bindings by anchor for evidence/peak cosine
            var bindings = v.SemanticBindings ?? Array.Empty<BindingRecord>();
            var perAnchor = anchors.ToDictionary(
                an => an,
                an => bindings.Where(b => string.Equals(b.Anchor, an, StringComparison.Ordinal)).ToList(),
                StringComparer.Ordinal);

            dump.Add(new Dictionary<string, object?>
            {
                ["rule_id"] = v.RuleId,
                ["dimension"] = dim,
                ["outcome"] = v.Outcome.ToString(),
                ["section_path"] = sectionPath,
                ["primary_topic"] = primaryTopic,
                ["is_synthetic_anchor"] = isSynthetic,
                ["anchor_count"] = anchors.Count,
                ["evidenced_anchor_count"] = perAnchor.Count(kv => kv.Value.Count > 0),
                ["anchors"] = perAnchor.Select(kv => new Dictionary<string, object?>
                {
                    ["name"] = kv.Key,
                    ["binding_count"] = kv.Value.Count,
                    ["peak_score"] = kv.Value.Count > 0 ? kv.Value.Max(b => b.Cosine) : (double?)null,
                    ["peak_token"] = kv.Value.Count > 0
                        ? kv.Value.OrderByDescending(b => b.Cosine).First().Matched
                        : null,
                }).ToList(),
                ["error"] = v.ErrorMessage,
            });
        }

        // PASS-by-dimension summary, computed with the FIXED extractor.
        var passDims = dump.Where(d => string.Equals((string?)d["outcome"], "Pass", StringComparison.Ordinal))
            .Select(d => (string?)d["dimension"])
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        _output.WriteLine($"PASS dimensions ({passDims.Count}): {string.Join(", ", passDims)}");

        // Per-rule one-liner table.
        _output.WriteLine("rule_id | dim | outcome | primary_topic | synth | ev/anchors | peak");
        foreach (var d in dump)
        {
            var anchorList = (List<Dictionary<string, object?>>)d["anchors"]!;
            var peak = anchorList.Count == 0
                ? "-"
                : anchorList.Max(a => (double?)a["peak_score"]) is { } p
                    ? p.ToString("0.000")
                    : "-";
            _output.WriteLine(
                $"{d["rule_id"]} | {d["dimension"]} | {d["outcome"]} | {d["primary_topic"]} | " +
                $"{d["is_synthetic_anchor"]} | {d["evidenced_anchor_count"]}/{d["anchor_count"]} | peak={peak}");
        }

        if (!string.IsNullOrEmpty(dumpPath))
        {
            var json = JsonSerializer.Serialize(new
            {
                config = new
                {
                    offset, minEffective = minEff, cohesion, minEvidenced,
                    timestamp = DateTime.UtcNow,
                },
                pass_dimensions = passDims,
                rules = dump,
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dumpPath, json);
            _output.WriteLine($"Wrote diagnostic JSON to {dumpPath}");
        }

        // Not a gate.
        true.Should().BeTrue();
    }

    private static double ParseDouble(string env, double dflt)
        => double.TryParse(Environment.GetEnvironmentVariable(env), out var d) ? d : dflt;
    private static int ParseInt(string env, int dflt)
        => int.TryParse(Environment.GetEnvironmentVariable(env), out var i) ? i : dflt;
    private static bool ParseBool(string env, bool dflt)
    {
        var v = Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrEmpty(v)) return dflt;
        return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly System.Text.RegularExpressions.Regex HasTopicRegex =
        new(@"HasTopic\s*\(\s*input1\s*,\s*""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string? ExtractDim(string? predicate)
    {
        if (string.IsNullOrEmpty(predicate)) return null;
        var m = HasTopicRegex.Match(predicate);
        return m.Success ? m.Groups[1].Value : null;
    }

    private sealed class FrozenTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
