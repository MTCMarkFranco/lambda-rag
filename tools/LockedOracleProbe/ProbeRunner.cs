using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LambdaRag.Tools.LockedOracleProbe;

internal sealed record ProbeRun(
    int Index,
    string RawResponse,
    string RawSha256,
    string? CanonicalJson,
    string? CanonicalSha256,
    StructuredFacts? Parsed,
    long LatencyMs,
    string? Error,
    string? SystemFingerprint,
    string? ModelName);

internal sealed record ProbeOptions(
    float? Temperature,
    float? TopP,
    long? Seed,
    bool JsonMode,
    int? MaxOutputTokens);

internal sealed class ProbeRunner
{
    private readonly IChatClient _chat;
    private readonly int _n;
    private readonly string _runDir;
    private readonly ProbeOptions _opts;
    private readonly string _documentId;
    private readonly string _documentText;

    public ProbeRunner(IChatClient chat, int n, string runDir, ProbeOptions opts,
        string documentId, string documentText)
    {
        _chat = chat;
        _n = n;
        _runDir = runDir;
        _opts = opts;
        _documentId = documentId;
        _documentText = documentText;
        Directory.CreateDirectory(_runDir);
    }

    public async Task<List<ProbeRun>> RunAllAsync(CancellationToken ct = default)
    {
        var results = new List<ProbeRun>(_n);
        var userMessage = BuildUserMessage();

        for (var i = 0; i < _n; i++)
        {
            Console.Write($"  run {i + 1,3}/{_n} ... ");
            var run = await RunOneAsync(i, userMessage, ct).ConfigureAwait(false);
            results.Add(run);
            await File.WriteAllTextAsync(
                Path.Combine(_runDir, $"run-{i:d3}.json"),
                run.RawResponse,
                ct).ConfigureAwait(false);

            if (run.Error != null)
                Console.WriteLine($"ERROR ({run.LatencyMs} ms): {run.Error}");
            else
                Console.WriteLine($"ok  ({run.LatencyMs,5} ms)  sha={run.RawSha256[..12]}  fp={run.SystemFingerprint ?? "<none>"}");
        }

        return results;
    }

    private async Task<ProbeRun> RunOneAsync(int index, string userMessage, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SchemaText.SystemPrompt),
                new(ChatRole.User,   userMessage),
            };

            var options = new ChatOptions();
            if (_opts.Temperature.HasValue)     options.Temperature = _opts.Temperature.Value;
            if (_opts.TopP.HasValue)            options.TopP = _opts.TopP.Value;
            if (_opts.Seed.HasValue)            options.Seed = _opts.Seed.Value;
            if (_opts.MaxOutputTokens.HasValue) options.MaxOutputTokens = _opts.MaxOutputTokens.Value;
            if (_opts.JsonMode)                 options.ResponseFormat = ChatResponseFormat.Json;

            var response = await _chat.GetResponseAsync(messages, options, ct)
                .ConfigureAwait(false);
            sw.Stop();

            var raw = response.Text ?? string.Empty;
            var rawSha = Sha256(raw);

            // Extract system_fingerprint and model from the raw representation
            // (Azure OpenAI SDK exposes these on ChatCompletion). If the
            // wrapper doesn't provide them we degrade gracefully to null.
            var (fingerprint, modelName) = TryExtractProviderMetadata(response);

            string? canonical = null;
            string? canonicalSha = null;
            StructuredFacts? parsed = null;
            string? error = null;

            try
            {
                parsed = JsonSerializer.Deserialize<StructuredFacts>(raw);
                if (parsed != null)
                {
                    canonical = CanonicalizeJson(parsed);
                    canonicalSha = Sha256(canonical);
                }
            }
            catch (JsonException jx)
            {
                error = "parse: " + jx.Message;
            }

            return new ProbeRun(index, raw, rawSha, canonical, canonicalSha, parsed,
                sw.ElapsedMilliseconds, error, fingerprint, modelName);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var msg = ex.GetType().Name + ": " + ex.Message;
            if (ex.InnerException is not null)
                msg += " | inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message;
            return new ProbeRun(index, string.Empty, string.Empty, null, null, null,
                sw.ElapsedMilliseconds, msg, null, null);
        }
    }

    private static (string? Fingerprint, string? Model) TryExtractProviderMetadata(ChatResponse response)
    {
        // Best-effort. Different providers expose these differently.
        // The OpenAI ChatCompletion type has SystemFingerprint and Model fields.
        // We reflect on the raw representation to avoid a hard SDK dependency
        // on OpenAI-specific types here.
        try
        {
            var raw = response.RawRepresentation;
            if (raw is null) return (null, null);

            var t = raw.GetType();
            string? fp = null;
            string? model = null;

            var fpProp = t.GetProperty("SystemFingerprint") ?? t.GetProperty("system_fingerprint");
            if (fpProp is not null)
                fp = fpProp.GetValue(raw) as string;

            var modelProp = t.GetProperty("Model") ?? t.GetProperty("model");
            if (modelProp is not null)
                model = modelProp.GetValue(raw)?.ToString();

            return (fp, model);
        }
        catch
        {
            return (null, null);
        }
    }

    private string BuildUserMessage() =>
        "Document id: " + _documentId + "\n\n" +
        "Document text:\n" + _documentText + "\n\n" +
        "Extract the facts.";

    private static string CanonicalizeJson(StructuredFacts facts)
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        };
        return JsonSerializer.Serialize(facts, opts);
    }

    private static string Sha256(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
