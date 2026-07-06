using System.Text;
using System.Text.Json;

namespace LambdaRag.Tools.LockedOracleProbe;

internal static class ReportWriter
{
    public static async Task WriteAsync(
        string outDir,
        Metrics.Report metrics,
        Pricing.CostReport cost,
        IReadOnlyList<ProbeRun> runs,
        string endpoint,
        string deployment,
        string documentId,
        int n)
    {
        Directory.CreateDirectory(outDir);
        await WriteJsonAsync(outDir, metrics, cost, runs, endpoint, deployment, documentId, n).ConfigureAwait(false);
        await WriteMarkdownAsync(outDir, metrics, cost, runs, endpoint, deployment, documentId, n).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(
        string outDir, Metrics.Report m, Pricing.CostReport cost, IReadOnlyList<ProbeRun> runs,
        string endpoint, string deployment, string documentId, int n)
    {
        var payload = new
        {
            probe = "locked-oracle-phase-0",
            issue = "https://github.com/MTCMarkFranco/lambda-rag/issues/175",
            document_id = documentId,
            schema_version = SchemaText.SchemaVersion,
            system_prompt_version = SchemaText.SystemPromptVersion,
            endpoint,
            deployment,
            n,
            timestamp_utc = DateTime.UtcNow.ToString("O"),
            verdict = Metrics.ClassifyVerdict(m),
            metrics = m,
            cost,
            per_run = runs.Select(r => new
            {
                index = r.Index,
                latency_ms = r.LatencyMs,
                raw_sha256 = r.RawSha256,
                canonical_sha256 = r.CanonicalSha256,
                system_fingerprint = r.SystemFingerprint,
                model = r.ModelName,
                input_tokens = r.InputTokens,
                output_tokens = r.OutputTokens,
                error = r.Error,
            }).ToList(),
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        await File.WriteAllTextAsync(Path.Combine(outDir, "probe-report.json"), json)
            .ConfigureAwait(false);
    }

    private static async Task WriteMarkdownAsync(
        string outDir, Metrics.Report m, Pricing.CostReport cost, IReadOnlyList<ProbeRun> runs,
        string endpoint, string deployment, string documentId, int n)
    {
        var verdict = Metrics.ClassifyVerdict(m);
        var sb = new StringBuilder();
        sb.AppendLine("# Locked Oracle — Phase 0 Empirical Probe Report");
        sb.AppendLine();
        sb.AppendLine($"**Issue:** https://github.com/MTCMarkFranco/lambda-rag/issues/175  ");
        sb.AppendLine($"**Timestamp (UTC):** {DateTime.UtcNow:O}  ");
        sb.AppendLine($"**Endpoint:** `{endpoint}`  ");
        sb.AppendLine($"**Deployment:** `{deployment}`  ");
        sb.AppendLine($"**Document:** `{documentId}`  ");
        sb.AppendLine($"**Schema version:** `{SchemaText.SchemaVersion}`  ");
        sb.AppendLine($"**System-prompt version:** `{SchemaText.SystemPromptVersion}`  ");
        sb.AppendLine($"**N (runs):** {n}");
        sb.AppendLine();
        sb.AppendLine($"## Verdict: **{verdict}**");
        sb.AppendLine();
        sb.AppendLine(verdict switch
        {
            "GREEN" => "≥99% canonical-JSON identity — **Locked Oracle Pattern (#175) is justified as-is.** " +
                       "Cache-miss idempotency meets the target. Proceed to Phase 1 (projector interface + cache).",
            "AMBER" => "95–98% canonical-JSON identity — **Locked Oracle Pattern is viable but requires the " +
                       "N=3 majority-vote fallback for all rules (not just `idempotencyClass: strict`).** " +
                       "Update the spec before Phase 1.",
            _       => "<95% canonical-JSON identity — **The 99% relaxation is insufficient.** The FID-Lottery " +
                       "framework does not translate cleanly to autoregressive LLM inference on this endpoint. " +
                       "Close #175 as negative result, or investigate a different backend (self-hosted, " +
                       "different provider, deterministic-inference mode).",
        });
        sb.AppendLine();
        sb.AppendLine("## Headline metrics");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Total runs | {m.TotalRuns} |");
        sb.AppendLine($"| Successful runs | {m.SuccessfulRuns} |");
        sb.AppendLine($"| Failed runs | {m.FailedRuns} |");
        sb.AppendLine($"| **Raw byte-identity** | **{m.RawByteIdentityPct:F1}%** ({m.UniqueRawResponses.Count} unique response hashes) |");
        sb.AppendLine($"| **Canonical-JSON identity** | **{m.CanonicalJsonIdentityPct:F1}%** ({m.UniqueCanonicalResponses.Count} unique canonical hashes) |");
        sb.AppendLine($"| Avg latency | {m.AvgLatencyMs:F0} ms |");
        sb.AppendLine($"| P95 latency | {m.P95LatencyMs:F0} ms |");
        sb.AppendLine();

        sb.AppendLine("## Token usage & cost");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Total input tokens | {cost.TotalInputTokens:N0} |");
        sb.AppendLine($"| Total output tokens | {cost.TotalOutputTokens:N0} |");
        sb.AppendLine($"| Total tokens | {cost.TotalTokens:N0} |");
        sb.AppendLine($"| Input rate | ${cost.InputRatePer1M:F4} / 1M tokens |");
        sb.AppendLine($"| Output rate | ${cost.OutputRatePer1M:F4} / 1M tokens |");
        sb.AppendLine($"| Input cost | ${cost.InputCostUsd:F4} USD |");
        sb.AppendLine($"| Output cost | ${cost.OutputCostUsd:F4} USD |");
        sb.AppendLine($"| **Total cost** | **${cost.TotalCostUsd:F4} USD** |");
        sb.AppendLine($"| Avg cost per run | ${cost.AvgCostPerRunUsd:F6} USD |");
        sb.AppendLine();
        if (cost.RateIsPlaceholder)
        {
            sb.AppendLine("⚠️  **Rates are best-effort placeholders.** The Azure OpenAI pricing page renders");
            sb.AppendLine("prices dynamically and cannot be scraped programmatically. Verify against");
            sb.AppendLine("https://azure.microsoft.com/en-us/pricing/details/azure-openai/ or the Azure");
            sb.AppendLine("Portal Cost Management view. Override with `--input-rate` and `--output-rate`");
            sb.AppendLine("(USD per 1M tokens).");
            sb.AppendLine();
        }

        sb.AppendLine("## Provider metadata — shard/model distribution");
        sb.AppendLine();
        sb.AppendLine("Azure OpenAI's `system_fingerprint` identifies the backend model checkpoint/shard. Multiple fingerprints = we hit multiple shards (good — the result is representative of real traffic). Single fingerprint = all N runs served by one shard (result may not generalize).");
        sb.AppendLine();
        if (m.SystemFingerprintDistribution.Count == 0)
        {
            sb.AppendLine("_No `system_fingerprint` returned by the endpoint._");
        }
        else
        {
            sb.AppendLine("| system_fingerprint | Count |");
            sb.AppendLine("|---|---|");
            foreach (var kv in m.SystemFingerprintDistribution.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"| `{kv.Key}` | {kv.Value} |");
        }
        sb.AppendLine();
        if (m.ModelDistribution.Count > 0)
        {
            sb.AppendLine("| Reported model | Count |");
            sb.AppendLine("|---|---|");
            foreach (var kv in m.ModelDistribution.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"| `{kv.Key}` | {kv.Value} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Per-field modal agreement");        sb.AppendLine();
        sb.AppendLine("Each field's agreement rate: fraction of successful runs whose value equals the modal (most common) value.");
        sb.AppendLine();
        sb.AppendLine("| Field | Modal value | Agreement |");
        sb.AppendLine("|---|---|---|");
        foreach (var kv in m.PerFieldAgreementPct)
        {
            var modal = m.ModalFieldValues.GetValueOrDefault(kv.Key, "?");
            sb.AppendLine($"| `{kv.Key}` | `{Escape(modal)}` | {kv.Value:F1}% |");
        }
        sb.AppendLine();

        sb.AppendLine("## Interpretation (per FID-Lottery mapping in issue #175)");
        sb.AppendLine();
        sb.AppendLine("- **Raw byte-identity gap** (100% − raw%) is a superset of all drift: whitespace, ordering, and value drift combined.");
        sb.AppendLine("- **Canonical-JSON identity gap** (100% − canonical%) isolates *semantic* drift. This is the number that matters for the Locked Oracle cache-miss gate (issue #175, Gate B).");
        sb.AppendLine("- **Per-field agreement < 100%** for any field indicates that field is flip-prone. Rules that depend on flip-prone fields must be annotated `idempotencyClass: \"strict\"` (N=3 majority vote).");
        sb.AppendLine();

        if (m.UniqueCanonicalResponses.Count > 1)
        {
            sb.AppendLine("## Divergent canonical responses");
            sb.AppendLine();
            sb.AppendLine("| Canonical SHA-256 (12) | Count | Example run |");
            sb.AppendLine("|---|---|---|");
            foreach (var kv in m.UniqueCanonicalResponses.OrderByDescending(kv => kv.Value))
            {
                var example = runs.FirstOrDefault(r => r.CanonicalSha256 == kv.Key);
                sb.AppendLine($"| `{kv.Key[..12]}` | {kv.Value} | run-{example?.Index:d3} |");
            }
            sb.AppendLine();
            sb.AppendLine("Inspect divergent runs in `runs/run-NNN.json`.");
            sb.AppendLine();
        }

        if (m.FailedRuns > 0)
        {
            sb.AppendLine("## Failed runs");
            sb.AppendLine();
            foreach (var r in runs.Where(r => r.Error is not null))
                sb.AppendLine($"- run-{r.Index:d3}: {r.Error}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Generated by `tools/LockedOracleProbe`. See `README.md` in that folder for how to reproduce.");

        await File.WriteAllTextAsync(Path.Combine(outDir, "probe-report.md"), sb.ToString())
            .ConfigureAwait(false);
    }

    private static string Escape(string s) => s.Replace("|", "\\|").Replace("`", "'");
}
