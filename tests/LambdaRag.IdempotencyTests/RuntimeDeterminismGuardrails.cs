using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using LambdaRag.Core;
using LambdaRag.Core.Abstractions;
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

namespace LambdaRag.IdempotencyTests;

/// <summary>
/// Runtime determinism guardrails (issue #74).
///
/// Protects the boundary established in #72: AI Search and other cloud
/// services may be used at AUTHORING time only. The runtime evaluation
/// path must remain cloud-free, byte-identical replay-safe, and free of
/// banned package references in its transitive graph.
///
/// These tests are intentionally defensive — they should never fail under
/// normal development. A failure here means a PR has accidentally crossed
/// the determinism boundary and should be rejected.
/// </summary>
public sealed class RuntimeDeterminismGuardrails
{
    /// <summary>
    /// Package ids that must not appear in the transitive dependency
    /// graph of any runtime evaluation project. Authoring / indexing
    /// projects are explicitly out of scope.
    /// </summary>
    private static readonly string[] BannedPackages =
    {
        "Azure.Search.Documents",
        "Microsoft.SemanticKernel.Connectors.AzureAISearch",
    };

    /// <summary>
    /// Projects that participate in <see cref="EvaluationService.EvaluateAsync"/>.
    /// LambdaRag.Cli and LambdaRag.Api are intentionally excluded — they
    /// host both authoring and review verbs, and a finer-grained split is
    /// tracked separately.
    /// </summary>
    private static readonly string[] RuntimeProjects =
    {
        "LambdaRag.Core",
        "LambdaRag.Evaluation",
        "LambdaRag.Selectors",
        "LambdaRag.Projection",
        "LambdaRag.Parsing",
        "LambdaRag.Markup",
        "LambdaRag.Persistence",
    };

    public static IEnumerable<object[]> RuntimeProjectsData() =>
        RuntimeProjects.Select(p => new object[] { p });

    private static string RepoRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string CorpusRoot => Path.Combine(RepoRoot, "tests", "Goldens", "corpus");

    private static readonly DateTimeOffset FrozenInstant =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Guardrail #1 — package reference: assert no runtime project's
    /// resolved NuGet graph contains a banned AI Search package.
    /// </summary>
    [Theory]
    [MemberData(nameof(RuntimeProjectsData))]
    public void Runtime_project_does_not_reference_banned_package(string project)
    {
        var assetsPath = Path.Combine(RepoRoot, "src", project, "obj", "project.assets.json");
        File.Exists(assetsPath).Should().BeTrue(
            $"{assetsPath} should exist — run `dotnet restore` first. " +
            "If the layout has moved, update RepoRoot in this guardrail.");

        var assets = File.ReadAllText(assetsPath);
        foreach (var banned in BannedPackages)
        {
            // project.assets.json keys library entries as "<id>/<version>";
            // matching the trailing slash avoids false hits on substrings.
            assets.Should().NotContain(
                $"\"{banned}/",
                $"runtime project {project} must not transitively reference {banned} " +
                $"— AI Search packages are authoring-only (issue #72/#74).");
        }
    }

    /// <summary>
    /// Guardrail #2 — runtime assembly load: after running a real
    /// evaluation, no banned assembly should be loaded into the test
    /// AppDomain. This catches accidental dynamic loads / type-forwarders
    /// even when the static graph is clean.
    /// </summary>
    [Fact]
    public async Task No_banned_assembly_is_loaded_after_evaluation()
    {
        // Snapshot the AppDomain BEFORE running evaluation so the assertion
        // measures what evaluation itself causes, not what other test classes
        // in the same xUnit process may have legitimately loaded (e.g. the
        // authoring-time ARB-PSA benchmarks). Process-global state would
        // otherwise make this test order-dependent.
        var before = new HashSet<string>(
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetName().Name ?? string.Empty),
            StringComparer.Ordinal);

        await RunEvaluationAgainstFirstCorpusDocAsync();

        var addedByEvaluation = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? string.Empty)
            .Where(name => !before.Contains(name))
            .ToArray();

        foreach (var banned in BannedPackages)
        {
            addedByEvaluation.Should().NotContain(
                a => a.StartsWith(banned, StringComparison.Ordinal),
                $"no banned assembly ({banned}*) should be loaded by EvaluateAsync " +
                "— evaluation must be cloud-free.");
        }
    }

    /// <summary>
    /// Guardrail #3 — replay byte-identity across the full golden corpus.
    /// Extends the existing single-document idempotency check to every
    /// corpus triple, hashing the canonicalised report payload so JSON
    /// pretty-print drift cannot mask a real change.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusTriples))]
    public async Task Corpus_document_produces_byte_identical_report_across_two_runs(
        string vertical, string docId)
    {
        var verticalDir = Path.Combine(CorpusRoot, vertical);
        var rulesetPath = Path.Combine(verticalDir, "ruleset.json");
        var sourcePath = Path.Combine(verticalDir, docId, "source.md");

        File.Exists(rulesetPath).Should().BeTrue($"ruleset must exist at {rulesetPath}");
        File.Exists(sourcePath).Should().BeTrue($"source must exist at {sourcePath}");

        var first = await ProduceVerdictJsonAsync(rulesetPath, sourcePath, vertical);
        var second = await ProduceVerdictJsonAsync(rulesetPath, sourcePath, vertical);

        Hash(first).Should().Be(
            Hash(second),
            $"two consecutive evaluations of {vertical}/{docId} must produce " +
            "byte-identical canonical-JSON reports.");
    }

    public static IEnumerable<object[]> CorpusTriples()
    {
        if (!Directory.Exists(CorpusRoot)) yield break;
        foreach (var verticalDir in Directory.EnumerateDirectories(CorpusRoot)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var vertical = Path.GetFileName(verticalDir);
            if (!File.Exists(Path.Combine(verticalDir, "ruleset.json"))) continue;

            foreach (var docDir in Directory.EnumerateDirectories(verticalDir)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!File.Exists(Path.Combine(docDir, "source.md"))) continue;
                yield return new object[] { vertical, Path.GetFileName(docDir) };
            }
        }
    }

    /// <summary>
    /// Guardrail #4 — snapshot pull determinism. Will be enabled once
    /// <c>lambda-rag ruleset pull --version &lt;hash&gt;</c> ships under
    /// issue #72. Tracked here so the placeholder fails loudly if anyone
    /// flips the skip without delivering the CLI.
    /// </summary>
    [Fact(Skip = "Blocked on #72: ruleset pull CLI not yet implemented")]
    public void Snapshot_pull_is_byte_identical_across_runs()
    {
        // Once #72 lands, exercise:
        //   lambda-rag ruleset pull --index <name> --version <hash> --out <a>
        //   lambda-rag ruleset pull --index <name> --version <hash> --out <b>
        // and assert sha256(<a>/**) == sha256(<b>/**).
        Assert.Fail("Implement when #72 ships the snapshot pull CLI.");
    }

    private static async Task RunEvaluationAgainstFirstCorpusDocAsync()
    {
        var firstTriple = CorpusTriples().FirstOrDefault();
        if (firstTriple is null)
        {
            // Fall back to the shared sample contract used by ReviewPipelineIdempotency.
            var samplesRoot   = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "contracts"));
            var rulesetsRoot  = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "rulesets", "contracts"));
            await ProduceVerdictJsonAsync(
                Path.Combine(rulesetsRoot, "ruleset.json"),
                Path.Combine(samplesRoot, "contract.md"),
                vertical: "contract");
            return;
        }

        var vertical = (string)firstTriple[0];
        var docId = (string)firstTriple[1];
        await ProduceVerdictJsonAsync(
            Path.Combine(CorpusRoot, vertical, "ruleset.json"),
            Path.Combine(CorpusRoot, vertical, docId, "source.md"),
            vertical);
    }

    private static async Task<string> ProduceVerdictJsonAsync(
        string rulesetPath, string sourcePath, string vertical)
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

        var topicMapId = $"{vertical}.v1";
        IDocumentProjector projector;
        try
        {
            var topicMap = TopicMapRegistry.Load(topicMapId);
            projector = new DeterministicContractProjector(topicMap);
        }
        catch
        {
            // If no per-vertical topic map exists (e.g. the bundled samples),
            // fall back to the DI-resolved default projector.
            projector = sp.GetRequiredService<IDocumentProjector>();
        }

        var rulesetJson = await File.ReadAllTextAsync(rulesetPath);
        var ruleset = JsonSerializer.Deserialize<RuleSet>(rulesetJson, CanonicalJson.Options)!;

        var parsed = await parsers.ParseAsync(sourcePath);
        var projected = await projector.ProjectAsync(parsed);
        var report = await evaluator.EvaluateAsync(ruleset, projected);

        return JsonSerializer.Serialize(report, CanonicalJson.Options);
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
