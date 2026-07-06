namespace LambdaRag.Tools.LockedOracleProbe;

/// <summary>
/// The three metrics that characterize LLM cache-miss idempotency for
/// the Locked Oracle Pattern (issue #175):
///
///  1. Raw byte-identity — % of runs whose raw response bytes equal the
///     modal (most common) response. The paper-faithful analog of FID
///     between-seed σ.
///
///  2. Canonical-JSON identity — same as (1) but after parsing and
///     re-serializing with a canonical writer. Isolates whitespace and
///     property-ordering drift from semantic drift.
///
///  3. Per-field modal-agreement — for each field, % of runs whose value
///     equals the modal value across the sample. Tells us WHICH fields
///     are flip-prone.
/// </summary>
internal static class Metrics
{
    public sealed record Report(
        int TotalRuns,
        int SuccessfulRuns,
        int FailedRuns,
        double RawByteIdentityPct,
        double CanonicalJsonIdentityPct,
        Dictionary<string, double> PerFieldAgreementPct,
        Dictionary<string, string> ModalFieldValues,
        Dictionary<string, int> UniqueRawResponses,
        Dictionary<string, int> UniqueCanonicalResponses,
        Dictionary<string, int> SystemFingerprintDistribution,
        Dictionary<string, int> ModelDistribution,
        double AvgLatencyMs,
        double P95LatencyMs);

    public static Report Compute(IReadOnlyList<ProbeRun> runs)
    {
        var ok = runs.Where(r => r.Error is null && r.Parsed is not null).ToList();
        var total = runs.Count;
        var okCount = ok.Count;
        var failed = total - okCount;

        // 1. Raw byte identity
        var rawHashCounts = ok
            .GroupBy(r => r.RawSha256)
            .ToDictionary(g => g.Key, g => g.Count());
        var modalRawCount = rawHashCounts.Count == 0 ? 0 : rawHashCounts.Values.Max();
        var rawPct = okCount == 0 ? 0.0 : 100.0 * modalRawCount / okCount;

        // 2. Canonical JSON identity
        var canonHashCounts = ok
            .Where(r => r.CanonicalSha256 is not null)
            .GroupBy(r => r.CanonicalSha256!)
            .ToDictionary(g => g.Key, g => g.Count());
        var modalCanonCount = canonHashCounts.Count == 0 ? 0 : canonHashCounts.Values.Max();
        var canonPct = okCount == 0 ? 0.0 : 100.0 * modalCanonCount / okCount;

        // 3. Per-field modal agreement
        var fieldExtractors =
            new (string Name, Func<StructuredFacts, string?> Get)[]
            {
                ("system_name",                    f => f.SystemName),
                ("encryption_in_transit_enabled",  f => f.EncryptionInTransitEnabled?.ToString().ToLowerInvariant()),
                ("encryption_at_rest_enabled",     f => f.EncryptionAtRestEnabled?.ToString().ToLowerInvariant()),
                ("authentication_method",          f => f.AuthenticationMethod?.ToLowerInvariant()),
                ("data_residency_region",          f => f.DataResidencyRegion),
            };

        var perField = new Dictionary<string, double>();
        var modalValues = new Dictionary<string, string>();
        foreach (var (name, get) in fieldExtractors)
        {
            var values = ok.Select(r => get(r.Parsed!) ?? "<null>").ToList();
            if (values.Count == 0) { perField[name] = 0.0; modalValues[name] = "<no-data>"; continue; }

            var groups = values.GroupBy(v => v)
                .OrderByDescending(g => g.Count())
                .ToList();
            var modal = groups[0];
            perField[name] = 100.0 * modal.Count() / values.Count;
            modalValues[name] = modal.Key;
        }

        // Provider metadata distribution
        var fpDist = runs
            .Where(r => r.SystemFingerprint is not null)
            .GroupBy(r => r.SystemFingerprint!)
            .ToDictionary(g => g.Key, g => g.Count());
        var modelDist = runs
            .Where(r => r.ModelName is not null)
            .GroupBy(r => r.ModelName!)
            .ToDictionary(g => g.Key, g => g.Count());

        // Latency
        var latencies = runs.Select(r => (double)r.LatencyMs).OrderBy(x => x).ToList();
        var avg = latencies.Count == 0 ? 0 : latencies.Average();
        var p95 = latencies.Count == 0 ? 0 : latencies[(int)Math.Min(latencies.Count - 1, Math.Floor(latencies.Count * 0.95))];

        return new Report(
            TotalRuns: total,
            SuccessfulRuns: okCount,
            FailedRuns: failed,
            RawByteIdentityPct: rawPct,
            CanonicalJsonIdentityPct: canonPct,
            PerFieldAgreementPct: perField,
            ModalFieldValues: modalValues,
            UniqueRawResponses: rawHashCounts,
            UniqueCanonicalResponses: canonHashCounts,
            SystemFingerprintDistribution: fpDist,
            ModelDistribution: modelDist,
            AvgLatencyMs: avg,
            P95LatencyMs: p95);
    }

    /// <summary>
    /// Applies the Locked Oracle (issue #175) three-tier verdict:
    ///   ≥99 canonical-JSON identity  → GREEN  (Locked Oracle justified as-is)
    ///   95–98 canonical-JSON identity → AMBER (majority-vote fallback required)
    ///   &lt;95 canonical-JSON identity  → RED   (99% relaxation insufficient)
    /// </summary>
    public static string ClassifyVerdict(Report r)
    {
        var c = r.CanonicalJsonIdentityPct;
        return c switch
        {
            >= 99.0 => "GREEN",
            >= 95.0 => "AMBER",
            _       => "RED",
        };
    }
}
