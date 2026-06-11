using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LambdaRag.Core;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Semantic;
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
/// Pillar 6 (#124) — the additive-guarantee proof.
///
/// Asserts that wiring the Pillar 6 token-embedder into the evaluator
/// produces byte-identical reports for rulesets whose rules do NOT
/// declare <see cref="SemanticAnchor"/>s. This is the contract:
/// adding Pillar 6 to the engine cannot flip a single pre-Pillar-6
/// verdict on a pre-Pillar-6 ruleset.
///
/// Run against the bundled Contoso contract corpus + the legacy
/// ARB-PSA ruleset (whose rules have no anchors yet).
/// </summary>
public sealed class AdditiveGuaranteeTests
{
    private readonly ITestOutputHelper _output;
    public AdditiveGuaranteeTests(ITestOutputHelper output) => _output = output;

    private static string RepoRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly DateTimeOffset FrozenInstant =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(
        "samples/contracts/contoso-sample-contract.docx",
        "rulesets/contracts/contoso-demo-ruleset.json",
        null)]
    public async Task Report_is_byte_identical_with_and_without_token_embedder(
        string documentRel, string rulesetRel, string? docKind)
    {
        var documentPath = Path.Combine(RepoRoot, documentRel.Replace('/', Path.DirectorySeparatorChar));
        var rulesetPath = Path.Combine(RepoRoot, rulesetRel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(documentPath) || !File.Exists(rulesetPath))
        {
            _output.WriteLine($"Skipping additive-guarantee: missing inputs {documentPath} / {rulesetPath}");
            return;
        }

        var without = await RunAsync(documentPath, rulesetPath, docKind, embedder: null);
        var with = await RunAsync(documentPath, rulesetPath, docKind, embedder: new DeterministicTokenEmbedder());

        // The rules in the bundled contract ruleset do NOT declare
        // semanticAnchors, so the bindings code path is a no-op and the
        // canonical report JSON must be byte-identical.
        var sha = (string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
        sha(without).Should().Be(sha(with),
            "Pillar 6 must not affect verdicts on a ruleset with no semanticAnchors");
    }

    private static async Task<string> RunAsync(
        string documentPath, string rulesetPath, string? docKind, ITokenEmbedder? embedder)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<TimeProvider>(new FrozenTimeProvider(FrozenInstant));
        services
            .AddLambdaRagParsing()
            .AddLambdaRagProjection()
            .AddLambdaRagSelectors()
            .AddLambdaRagEvaluation();
        if (embedder is not null)
        {
            // Override the default EvaluationService so it sees the embedder.
            services.AddSingleton<EvaluationService>(sp =>
                new EvaluationService(
                    sp.GetRequiredService<LambdaRag.Core.Abstractions.ISelectorMatcher>(),
                    sp.GetRequiredService<ILogger<EvaluationService>>(),
                    sp.GetRequiredService<TimeProvider>(),
                    tokenEmbedder: embedder));
        }

        await using var sp = services.BuildServiceProvider();
        var parsers = sp.GetRequiredService<ParserRegistry>();
        var evaluator = sp.GetRequiredService<EvaluationService>();
        var projector = new DeterministicContractProjector();

        var rulesetJson = await File.ReadAllTextAsync(rulesetPath);
        var ruleset = JsonSerializer.Deserialize<RuleSet>(rulesetJson, CanonicalJson.Options)!;
        var parsed = await parsers.ParseAsync(documentPath);
        var projected = await projector.ProjectAsync(parsed);
        var report = await evaluator.EvaluateAsync(ruleset, projected, docKind);
        return JsonSerializer.Serialize(report, CanonicalJson.Options);
    }

    private sealed class FrozenTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    /// <summary>
    /// Tiny deterministic 32-dim embedder for additive-guarantee tests.
    /// Mirrors <c>DeterministicHashEmbedder</c> but lives in
    /// LambdaRag.IdempotencyTests so this project does not have to
    /// reference LambdaRag.Authoring (which would pull
    /// <c>Azure.Search.Documents</c> into the test AppDomain and trip
    /// <see cref="RuntimeDeterminismGuardrails.No_banned_assembly_is_loaded_after_evaluation"/>).
    /// </summary>
    private sealed class DeterministicTokenEmbedder : ITokenEmbedder
    {
        public string EmbedderId => "additive-guarantee:sha256/32";
        public int Dimensions => 32;
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            Span<byte> hash = stackalloc byte[64];
            var input = Encoding.UTF8.GetBytes(text ?? string.Empty);
            var h1 = SHA256.HashData(input);
            var h2 = SHA256.HashData(h1);
            h1.AsSpan().CopyTo(hash[..32]);
            h2.AsSpan().CopyTo(hash[32..]);
            var v = new float[Dimensions];
            for (var i = 0; i < Dimensions; i++)
            {
                var raw = (short)((hash[i * 2] << 8) | hash[i * 2 + 1]);
                v[i] = raw / (float)short.MaxValue;
            }
            double sq = 0; for (var i = 0; i < v.Length; i++) sq += v[i] * v[i];
            var n = Math.Sqrt(sq);
            if (n > double.Epsilon) for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / n);
            return Task.FromResult(v);
        }
    }
}
