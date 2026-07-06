using System.Text.Json;
using System.Text.Json.Serialization;

namespace LambdaRag.Core.Observability;

/// <summary>
/// Issue #180 — append-only observability ledger. One JSONL line per review,
/// keyed on <see cref="RunManifest.RunId"/>. Never edited in place; consumers
/// tail-read.
///
/// <para>Not a fingerprint input. This ledger exists exclusively to enable
/// operational metrics — refusal rate, cost trend, token trend, latency —
/// that the FID Lottery audit flagged as our weakest observability area.</para>
///
/// <para>Cost is estimated post-hoc via <see cref="TokenCostEstimator"/>
/// so pricing changes don't require re-running the review. If the model
/// isn't in the pricing table the entry is emitted with <c>estimatedUsd=null</c>.</para>
/// </summary>
public sealed record RunTelemetryEntry(
    [property: JsonPropertyName("ts")] string TimestampUtc,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("gitSha")] string GitSha,
    [property: JsonPropertyName("engineVersion")] string EngineVersion,
    [property: JsonPropertyName("doc")] TelemetryDoc Doc,
    [property: JsonPropertyName("ruleset")] TelemetryRuleSet RuleSet,
    [property: JsonPropertyName("extractor")] TelemetryExtractor? Extractor,
    [property: JsonPropertyName("tokens")] TelemetryTokens Tokens,
    [property: JsonPropertyName("verdicts")] TelemetryVerdicts Verdicts,
    [property: JsonPropertyName("elapsedMs")] TelemetryElapsed ElapsedMs,
    [property: JsonPropertyName("refusal")] string? Refusal);

public sealed record TelemetryDoc(
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes);

public sealed record TelemetryRuleSet(
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("ruleCount")] int RuleCount);

public sealed record TelemetryExtractor(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("modelSnapshot")] string? ModelSnapshot,
    [property: JsonPropertyName("deployment")] string? Deployment,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("settingsFingerprint")] string SettingsFingerprint);

public sealed record TelemetryTokens(
    [property: JsonPropertyName("in")] long In,
    [property: JsonPropertyName("out")] long Out,
    [property: JsonPropertyName("estimatedUsd")] double? EstimatedUsd);

public sealed record TelemetryVerdicts(
    [property: JsonPropertyName("pass")] int Pass,
    [property: JsonPropertyName("fail")] int Fail,
    [property: JsonPropertyName("gap")] int Gap,
    [property: JsonPropertyName("na")] int Na,
    [property: JsonPropertyName("errored")] int Errored);

public sealed record TelemetryElapsed(
    [property: JsonPropertyName("total")] long Total);

public static class RunTelemetryWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Simple file-lock keyed by absolute path so concurrent CLI invocations
    // don't tear a line. In-proc lock is enough — this is a dev + CI ledger,
    // not multi-host.
    private static readonly Dictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

    private static object LockFor(string path)
    {
        lock (Locks)
        {
            var full = Path.GetFullPath(path);
            if (!Locks.TryGetValue(full, out var l))
            {
                l = new object();
                Locks[full] = l;
            }
            return l;
        }
    }

    /// <summary>Append one JSONL entry.</summary>
    public static void Append(RunTelemetryEntry entry, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var line = JsonSerializer.Serialize(entry, Options);
        lock (LockFor(path))
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }
}

/// <summary>
/// Static pricing table for cost estimation. Kept intentionally tiny — we
/// only estimate cost for models we actually run. Update entries when Azure
/// pricing changes; unknown models return null (surfaced as-is in telemetry).
/// </summary>
public static class TokenCostEstimator
{
    // USD per million tokens. Sourced from Azure OpenAI GA pricing.
    // Add rows as new deployments are wired in.
    private static readonly IReadOnlyDictionary<string, (double inPerM, double outPerM)> Pricing =
        new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase)
        {
            // gpt-5.4-mini — audit-anchor deployment (Locked Oracle Phase 0/1).
            ["gpt-5.4-mini"] = (0.75, 4.50),
        };

    public static double? EstimateUsd(string? deploymentOrModel, long tokensIn, long tokensOut)
    {
        if (string.IsNullOrWhiteSpace(deploymentOrModel)) return null;
        if (!Pricing.TryGetValue(deploymentOrModel, out var rates)) return null;
        return (tokensIn / 1_000_000.0) * rates.inPerM
             + (tokensOut / 1_000_000.0) * rates.outPerM;
    }
}
