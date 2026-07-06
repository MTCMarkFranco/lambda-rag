using System.Text.Json;
using System.Text.Json.Serialization;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Core.Observability;

/// <summary>
/// Issue #179 (FID Lottery audit follow-up) — per-review replay ledger.
///
/// <para>
/// A single JSON artifact emitted alongside <c>report.json</c> that ties
/// together every input needed to reproduce the review: doc hash, ruleset
/// fingerprint, fact-extractor settings, model snapshot, git SHA, engine
/// version. Reproducibility fields participate in <see cref="RunId"/>;
/// ledger-only fields (<see cref="TimestampUtc"/>, <see cref="Elapsed"/>,
/// token totals) do not, so that identical inputs yield identical
/// <see cref="RunId"/> across re-runs.
/// </para>
///
/// <para>Consumed by <c>run-telemetry.jsonl</c> (issue #180) which stores a
/// summary row keyed on <see cref="RunId"/>.</para>
/// </summary>
public sealed record RunManifest(
    string ManifestVersion,
    string RunId,
    string TimestampUtc,
    RunManifestEngine Engine,
    RunManifestInput Input,
    RunManifestRuleSet RuleSet,
    RunManifestFacts? Facts,
    RunManifestVerdicts Verdicts,
    RunManifestElapsed Elapsed,
    string? Refusal);

public sealed record RunManifestEngine(
    string Version,
    string GitSha,
    string AssemblyVersion);

public sealed record RunManifestInput(
    string DocPath,
    string DocHash,
    string DocKind,
    string DeclaredDomain);

public sealed record RunManifestRuleSet(
    string Path,
    string Id,
    string Version,
    string Fingerprint,
    int RuleCount);

public sealed record RunManifestFacts(
    string ExtractorKind,
    string ModelId,
    string? ModelSnapshot,
    string? DeploymentId,
    string? Region,
    string PromptHash,
    string PromptVersion,
    string SettingsFingerprint,
    int SectionsTotal,
    long TokensIn,
    long TokensOut);

public sealed record RunManifestVerdicts(
    int Pass,
    int Fail,
    int Gap,
    int Na,
    int Errored,
    int Total,
    double Score);

public sealed record RunManifestElapsed(
    long TotalMs);

/// <summary>
/// Deterministic writer for <see cref="RunManifest"/>. Uses canonical JSON
/// so byte-identical inputs produce byte-identical artifacts (excluding the
/// ledger-only fields).
/// </summary>
public static class RunManifestIO
{
    public const string CurrentVersion = "1.0.0";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Compose a deterministic <see cref="RunManifest.RunId"/> from the
    /// reproducibility-relevant fields. Two invocations with identical
    /// inputs return the same runId even across days.
    /// </summary>
    public static string ComposeRunId(
        string engineVersion,
        string gitSha,
        string docHash,
        string rulesetFingerprint,
        string? factsSettingsFingerprint,
        string? factsPromptHash)
        => ContentHash.Compose(
            "run-manifest-v1",
            engineVersion ?? string.Empty,
            gitSha ?? string.Empty,
            docHash ?? string.Empty,
            rulesetFingerprint ?? string.Empty,
            factsSettingsFingerprint ?? "no-facts",
            factsPromptHash ?? "no-facts").Value;

    public static string Serialize(RunManifest manifest)
        => JsonSerializer.Serialize(manifest, Options);

    public static void Write(RunManifest manifest, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Serialize(manifest));
    }
}
