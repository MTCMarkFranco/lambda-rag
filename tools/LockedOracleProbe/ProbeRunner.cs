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
    string? Error);

internal sealed class ProbeRunner
{
    private readonly IChatClient _chat;
    private readonly int _n;
    private readonly string _runDir;

    public ProbeRunner(IChatClient chat, int n, string runDir)
    {
        _chat = chat;
        _n = n;
        _runDir = runDir;
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
                Console.WriteLine($"ok  ({run.LatencyMs,5} ms)  sha={run.RawSha256[..12]}");
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

            var options = new ChatOptions
            {
                Temperature = 0.0f,
                TopP = 1.0f,
                Seed = 42, // pinned; provider may or may not honor
                MaxOutputTokens = 512,
                ResponseFormat = ChatResponseFormat.Json,
            };

            var response = await _chat.GetResponseAsync(messages, options, ct)
                .ConfigureAwait(false);
            sw.Stop();

            var raw = response.Text ?? string.Empty;
            var rawSha = Sha256(raw);

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
                sw.ElapsedMilliseconds, error);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeRun(index, string.Empty, string.Empty, null, null, null,
                sw.ElapsedMilliseconds, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string BuildUserMessage() =>
        "Document id: " + ProbeDocument.DocumentId + "\n\n" +
        "Document text:\n" + ProbeDocument.Text + "\n\n" +
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
