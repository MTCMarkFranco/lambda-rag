using System.Security.Cryptography;
using FluentAssertions;
using LambdaRag.Core;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Evaluation;
using LambdaRag.Evaluation.Engine;
using LambdaRag.Parsing;
using LambdaRag.Projection;
using LambdaRag.Selectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LambdaRag.IdempotencyTests;

/// <summary>
/// Golden-master idempotency tests: run the full review pipeline twice
/// against the bundled sample contract and assert SHA-256 equality of the
/// canonical-JSON report. This is the legal-scrutiny proof that the
/// runtime is deterministic.
/// </summary>
public sealed class ReviewPipelineIdempotency
{
    private static string SamplesRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "contracts"));

    [Fact]
    public async Task Two_runs_produce_byte_identical_report()
    {
        var report1 = await RunOnceAsync();
        var report2 = await RunOnceAsync();
        Hash(report1).Should().Be(Hash(report2));
    }

    [Fact]
    public async Task Verdict_ids_are_stable_across_runs()
    {
        var report1 = await RunOnceAsync();
        var report2 = await RunOnceAsync();

        var ids1 = string.Join(",", System.Text.RegularExpressions.Regex.Matches(report1, "\"id\":\\s*\"([^\"]+)\"").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value));
        var ids2 = string.Join(",", System.Text.RegularExpressions.Regex.Matches(report2, "\"id\":\\s*\"([^\"]+)\"").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value));
        ids1.Should().Be(ids2);
    }

    private static async Task<string> RunOnceAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<TimeProvider>(new FrozenTimeProvider(
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        services
            .AddLambdaRagParsing()
            .AddLambdaRagProjection()
            .AddLambdaRagSelectors()
            .AddLambdaRagEvaluation();

        await using var sp = services.BuildServiceProvider();
        var parsers = sp.GetRequiredService<ParserRegistry>();
        var projector = sp.GetRequiredService<IDocumentProjector>();
        var evaluator = sp.GetRequiredService<EvaluationService>();

        var rulesetJson = await File.ReadAllTextAsync(Path.Combine(SamplesRoot, "ruleset.json"));
        var ruleset = System.Text.Json.JsonSerializer.Deserialize<RuleSet>(rulesetJson, CanonicalJson.Options)!;

        var parsed = await parsers.ParseAsync(Path.Combine(SamplesRoot, "contract.md"));
        var projected = await projector.ProjectAsync(parsed);
        var report = await evaluator.EvaluateAsync(ruleset, projected);

        return System.Text.Json.JsonSerializer.Serialize(report, CanonicalJson.Options);
    }

    private static string Hash(string s)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }

    private sealed class FrozenTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
