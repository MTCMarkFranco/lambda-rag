using Azure.AI.OpenAI;
using Azure.Identity;
using LambdaRag.Tools.LockedOracleProbe;
using Microsoft.Extensions.AI;

// =============================================================================
// Locked Oracle Pattern — Phase 0 Empirical Probe
// Issue: https://github.com/MTCMarkFranco/lambda-rag/issues/175
//
// Fires the SAME (system prompt, schema, document) N times at a pinned
// Azure OpenAI deployment under temperature=0 + pinned seed. Measures
// three progressively-relaxed identity metrics (raw bytes, canonical
// JSON, per-field modal agreement) and emits a GREEN/AMBER/RED verdict
// against the 99% target from #175.
//
// This tool is a throwaway spike. Do not depend on its exit codes or
// output shape from production code.
// =============================================================================

var flags = ParseFlags(args);

int n = int.TryParse(flags.GetValueOrDefault("n"), out var pn) ? pn : 100;
string outRoot = flags.GetValueOrDefault("out") ?? Path.Combine("out", "locked-oracle-probe");
string docName = flags.GetValueOrDefault("document") ?? "default";

(string docId, string docText) doc;
try { doc = Documents.Get(docName); }
catch (ArgumentException ax) { Console.Error.WriteLine("ERROR: " + ax.Message); return 64; }

// Deterministic-inference knobs. Reasoning-class models (gpt-5.x, o-series)
// often reject non-default temperature, seed, and json_object response_format.
// When any of these is disabled we cannot claim source ⑤ (sampling noise)
// is pinned — the probe still runs and still measures response variance,
// but the reported number is drift+sampling, not drift alone.
float? temperature = flags.TryGetValue("temperature", out var tv) && float.TryParse(tv, out var tf) ? tf
    : flags.ContainsKey("no-temperature") ? (float?)null : 0.0f;
float? topP = flags.ContainsKey("no-top-p") ? (float?)null : 1.0f;
long? seed = flags.TryGetValue("seed", out var sv) && long.TryParse(sv, out var sl) ? sl
    : flags.ContainsKey("no-seed") ? (long?)null : 42L;
bool jsonMode = !flags.ContainsKey("no-json-mode");
int? maxTokens = flags.TryGetValue("max-tokens", out var mv) && int.TryParse(mv, out var mi) ? mi
    : flags.ContainsKey("no-max-tokens") ? (int?)null : 512;

decimal? inRate  = flags.TryGetValue("input-rate",  out var ir) && decimal.TryParse(ir, out var ird) ? ird : (decimal?)null;
decimal? outRate = flags.TryGetValue("output-rate", out var or) && decimal.TryParse(or, out var ord) ? ord : (decimal?)null;

var probeOpts = new ProbeOptions(temperature, topP, seed, jsonMode, maxTokens);

string endpoint = flags.GetValueOrDefault("endpoint")
    ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
    ?? string.Empty;
string deployment = flags.GetValueOrDefault("deployment")
    ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")
    ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_MINI_DEPLOYMENT")
    ?? "gpt-4o-mini";

if (string.IsNullOrWhiteSpace(endpoint))
{
    Console.Error.WriteLine("ERROR: AZURE_OPENAI_ENDPOINT not set (or pass --endpoint).");
    Console.Error.WriteLine("Example:");
    Console.Error.WriteLine("  $env:AZURE_OPENAI_ENDPOINT = 'https://<name>.cognitiveservices.azure.com/'");
    Console.Error.WriteLine("  $env:AZURE_OPENAI_DEPLOYMENT = 'gpt-4o-mini'");
    Console.Error.WriteLine("  az login   # DefaultAzureCredential is used for auth");
    return 64;
}

var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
var outDir = Path.Combine(outRoot, $"probe-{docName}-{stamp}");
var runDir = Path.Combine(outDir, "runs");

Console.WriteLine("Locked Oracle Probe — Phase 0");
Console.WriteLine($"  Endpoint:      {endpoint}");
Console.WriteLine($"  Deployment:    {deployment}");
Console.WriteLine($"  N:             {n}");
Console.WriteLine($"  Document:      {doc.docId}  (~{doc.docText.Length} chars)  [--document {docName}]");
Console.WriteLine($"  Out:           {outDir}");
Console.WriteLine($"  Temperature:   {(probeOpts.Temperature?.ToString("F1") ?? "<model default>")}");
Console.WriteLine($"  Top-P:         {(probeOpts.TopP?.ToString("F1") ?? "<model default>")}");
Console.WriteLine($"  Seed:          {(probeOpts.Seed?.ToString() ?? "<unpinned>")}");
Console.WriteLine($"  JSON mode:     {probeOpts.JsonMode}");
Console.WriteLine($"  Max tokens:    {(probeOpts.MaxOutputTokens?.ToString() ?? "<model default>")}");
Console.WriteLine();
if (probeOpts.Temperature is null || probeOpts.Seed is null)
{
    Console.WriteLine("⚠️  Sampling noise (randomness source ⑤ in the FID-Lottery paper) is NOT pinned.");
    Console.WriteLine("   Any measured variance is drift + sampling combined, not drift alone.");
    Console.WriteLine("   The GREEN/AMBER/RED verdict still reflects real-world Locked Oracle behavior,");
    Console.WriteLine("   but cannot be attributed to hardware drift specifically.");
    Console.WriteLine();
}
Console.WriteLine("Auth via DefaultAzureCredential (Entra ID). Ensure `az login` succeeded.");
Console.WriteLine();

IChatClient chat;
try
{
    var azure = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential());
    chat = azure.GetChatClient(deployment).AsIChatClient();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to construct chat client: {ex.Message}");
    return 65;
}

var runner = new ProbeRunner(chat, n, runDir, probeOpts, doc.docId, doc.docText);
Console.WriteLine($"Running {n} probes sequentially...");
Console.WriteLine();

var runs = await runner.RunAllAsync().ConfigureAwait(false);

Console.WriteLine();
Console.WriteLine("Computing metrics...");
var metrics = Metrics.Compute(runs);
var verdict = Metrics.ClassifyVerdict(metrics);

// --- Token usage + cost ---
var reportedModel = runs.FirstOrDefault(r => r.ModelName is not null)?.ModelName;
var (inR, outR, isPlaceholder) = Pricing.Resolve(deployment, reportedModel, inRate, outRate);
var cost = Pricing.Compute(runs, inR, outR, isPlaceholder);

await ReportWriter.WriteAsync(outDir, metrics, cost, runs, endpoint, deployment, n)
    .ConfigureAwait(false);

Console.WriteLine();
Console.WriteLine("========================================================================");
Console.WriteLine($"  VERDICT: {verdict}");
Console.WriteLine("========================================================================");
Console.WriteLine($"  Raw byte-identity:       {metrics.RawByteIdentityPct,6:F1}%   ({metrics.UniqueRawResponses.Count} unique)");
Console.WriteLine($"  Canonical-JSON identity: {metrics.CanonicalJsonIdentityPct,6:F1}%   ({metrics.UniqueCanonicalResponses.Count} unique)");
Console.WriteLine($"  Successful runs:         {metrics.SuccessfulRuns}/{metrics.TotalRuns}");
Console.WriteLine();
Console.WriteLine("  Per-field modal agreement:");
foreach (var kv in metrics.PerFieldAgreementPct)
    Console.WriteLine($"    {kv.Key,-34} {kv.Value,6:F1}%   modal={metrics.ModalFieldValues[kv.Key]}");
Console.WriteLine();
Console.WriteLine($"  Unique system_fingerprints: {metrics.SystemFingerprintDistribution.Count}");
foreach (var kv in metrics.SystemFingerprintDistribution.OrderByDescending(x => x.Value))
    Console.WriteLine($"    {kv.Key,-40} {kv.Value,4}");
Console.WriteLine();
Console.WriteLine("  Token usage:");
Console.WriteLine($"    Input tokens:            {cost.TotalInputTokens,10:N0}");
Console.WriteLine($"    Output tokens:           {cost.TotalOutputTokens,10:N0}");
Console.WriteLine($"    Total tokens:            {cost.TotalTokens,10:N0}");
Console.WriteLine();
Console.WriteLine($"  Cost (USD, rates {(cost.RateIsPlaceholder ? "PLACEHOLDER ⚠️" : "explicit")}):");
Console.WriteLine($"    Input rate:              ${cost.InputRatePer1M,10:F4} / 1M tokens");
Console.WriteLine($"    Output rate:             ${cost.OutputRatePer1M,10:F4} / 1M tokens");
Console.WriteLine($"    Input cost:              ${cost.InputCostUsd,10:F4}");
Console.WriteLine($"    Output cost:             ${cost.OutputCostUsd,10:F4}");
Console.WriteLine($"    Total cost:              ${cost.TotalCostUsd,10:F4}");
Console.WriteLine($"    Cost per run:            ${cost.AvgCostPerRunUsd,10:F6}");
if (cost.RateIsPlaceholder)
{
    Console.WriteLine();
    Console.WriteLine("  ⚠️  Rates above are best-effort placeholders. Verify against:");
    Console.WriteLine("     https://azure.microsoft.com/en-us/pricing/details/azure-openai/");
    Console.WriteLine("     Override with --input-rate <usd-per-1M> --output-rate <usd-per-1M>");
}
Console.WriteLine();
Console.WriteLine($"  Report: {Path.Combine(outDir, "probe-report.md")}");
Console.WriteLine();

return verdict switch
{
    "GREEN" => 0,
    "AMBER" => 10, // non-zero but distinct from RED, so CI can gate differently
    _       => 20,
};

static Dictionary<string, string> ParseFlags(string[] args)
{
    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length
            && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            map[args[i][2..]] = args[i + 1];
            i++;
        }
    }
    return map;
}
