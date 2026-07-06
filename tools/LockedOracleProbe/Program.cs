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
var outDir = Path.Combine(outRoot, $"probe-{stamp}");
var runDir = Path.Combine(outDir, "runs");

Console.WriteLine("Locked Oracle Probe — Phase 0");
Console.WriteLine($"  Endpoint:   {endpoint}");
Console.WriteLine($"  Deployment: {deployment}");
Console.WriteLine($"  N:          {n}");
Console.WriteLine($"  Document:   {ProbeDocument.DocumentId}  (~{ProbeDocument.Text.Length} chars)");
Console.WriteLine($"  Out:        {outDir}");
Console.WriteLine();
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

var runner = new ProbeRunner(chat, n, runDir);
Console.WriteLine($"Running {n} probes sequentially...");
Console.WriteLine();

var runs = await runner.RunAllAsync().ConfigureAwait(false);

Console.WriteLine();
Console.WriteLine("Computing metrics...");
var metrics = Metrics.Compute(runs);
var verdict = Metrics.ClassifyVerdict(metrics);

await ReportWriter.WriteAsync(outDir, metrics, runs, endpoint, deployment, n)
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
