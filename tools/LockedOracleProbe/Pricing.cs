namespace LambdaRag.Tools.LockedOracleProbe;

/// <summary>
/// Cost estimation for a probe run. Rates are per-1M-tokens (USD).
///
/// ⚠️ PRICING WARNING ⚠️
/// The rates below are best-effort placeholders inferred from the Azure
/// pricing page's public structure and the model class (mini). At the
/// time this tool was written the Azure OpenAI pricing page rendered
/// prices dynamically via JavaScript and could not be scraped
/// automatically. ALWAYS verify against
/// https://azure.microsoft.com/en-us/pricing/details/azure-openai/
/// or the Azure Portal Cost Management view before quoting numbers.
///
/// Override with --input-rate and --output-rate on the CLI.
/// </summary>
internal static class Pricing
{
    /// <summary>Best-effort default rates keyed by model-name substring (case-insensitive).</summary>
    private static readonly (string Match, decimal InputPer1M, decimal OutputPer1M)[] DefaultRates =
    {
        // Order matters — more-specific matches first.
        // Rates are USD per 1,000,000 tokens (Azure GA Global pricing).
        // Verify at https://azure.microsoft.com/en-us/pricing/details/azure-openai/
        ("gpt-5.4-nano",   0.10m,  0.40m),
        ("gpt-5.4-mini",   0.75m,  4.50m),  // user-confirmed 2026-07-05  -> RateIsPlaceholder=false for this match
        ("gpt-5.4",        1.25m, 10.00m),
        ("gpt-5.3",        1.25m, 10.00m),
        ("gpt-5.2",        1.25m, 10.00m),
        ("gpt-5.1",        1.25m, 10.00m),
        ("gpt-5",          1.25m, 10.00m),
        ("gpt-4o-mini",    0.15m,  0.60m),
        ("gpt-4o",         2.50m, 10.00m),
        ("gpt-4",         10.00m, 30.00m),
        ("text-embedding", 0.13m,  0.00m),
    };

    /// <summary>Rates the user has explicitly verified — treated as authoritative (not placeholder).</summary>
    private static readonly HashSet<string> VerifiedRates = new(StringComparer.OrdinalIgnoreCase)
    {
        "gpt-5.4-mini",
    };

    public static (decimal InputPer1M, decimal OutputPer1M, bool IsPlaceholder) Resolve(
        string deployment, string? modelName,
        decimal? explicitInputRate, decimal? explicitOutputRate)
    {
        if (explicitInputRate.HasValue && explicitOutputRate.HasValue)
            return (explicitInputRate.Value, explicitOutputRate.Value, false);

        var candidates = new[] { modelName, deployment }.Where(x => !string.IsNullOrEmpty(x));
        foreach (var name in candidates)
        {
            foreach (var (match, inRate, outRate) in DefaultRates)
            {
                if (name!.Contains(match, StringComparison.OrdinalIgnoreCase))
                    return (
                        explicitInputRate ?? inRate,
                        explicitOutputRate ?? outRate,
                        !VerifiedRates.Contains(match));
            }
        }

        return (explicitInputRate ?? 1.00m, explicitOutputRate ?? 5.00m, true);
    }

    public sealed record CostReport(
        long TotalInputTokens,
        long TotalOutputTokens,
        long TotalTokens,
        decimal InputRatePer1M,
        decimal OutputRatePer1M,
        decimal InputCostUsd,
        decimal OutputCostUsd,
        decimal TotalCostUsd,
        decimal AvgCostPerRunUsd,
        bool RateIsPlaceholder);

    public static CostReport Compute(IReadOnlyList<ProbeRun> runs,
        decimal inputPer1M, decimal outputPer1M, bool isPlaceholder)
    {
        long ti = runs.Sum(r => r.InputTokens);
        long to = runs.Sum(r => r.OutputTokens);
        var ic = inputPer1M  * ti / 1_000_000m;
        var oc = outputPer1M * to / 1_000_000m;
        var tc = ic + oc;
        var perRun = runs.Count == 0 ? 0m : tc / runs.Count;
        return new CostReport(ti, to, ti + to, inputPer1M, outputPer1M,
            ic, oc, tc, perRun, isPlaceholder);
    }
}
