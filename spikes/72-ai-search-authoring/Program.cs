using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.AI.OpenAI;
using Azure.Identity;
using Json.Schema;
using Microsoft.Extensions.AI;

namespace LambdaRag.Spikes.AISearchAuthoring;

/// <summary>
/// Spike harness for issue #72.
///
/// Runs the rule-extraction system prompt (samples/authoring/rule-extraction.system-prompt.md)
/// against every ARB policy chunk in policies/arb/policies.json, validates each
/// LLM response against samples/authoring/rule-extraction.schema.json, and writes
/// a per-chunk JSON file plus a comparison-report markdown to out/spike-72/.
///
/// Goal: prove the prompt + schema design holds up before we wire a full
/// AI Search skillset. If it does, the same prompt + schema gets dropped
/// into the GenAI prompt skill verbatim.
///
/// Required environment (or pass --endpoint / --deployment):
///   AZURE_OPENAI_ENDPOINT          e.g. https://my-foundry.cognitiveservices.azure.com/
///   AZURE_OPENAI_MINI_DEPLOYMENT   defaults to gpt-4o-mini
///
/// Auth: DefaultAzureCredential (Entra ID — same pattern as SynopsizeCommand).
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var f = ParseFlags(args);
        var endpoint = f.GetValueOrDefault("endpoint")
            ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var deployment = f.GetValueOrDefault("deployment")
            ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_MINI_DEPLOYMENT")
            ?? "gpt-4o-mini";
        var max = int.TryParse(f.GetValueOrDefault("max"), out var m) ? m : int.MaxValue;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Console.Error.WriteLine(
                "AZURE_OPENAI_ENDPOINT is required (or pass --endpoint).");
            return 64;
        }

        var repoRoot = FindRepoRoot();
        var policiesPath = Path.Combine(repoRoot, "policies", "arb", "policies.json");
        var schemaPath = Path.Combine(repoRoot, "samples", "authoring", "rule-extraction.schema.json");
        var promptPath = Path.Combine(repoRoot, "samples", "authoring", "rule-extraction.system-prompt.md");
        var arbRulesetPath = Path.Combine(repoRoot, "samples", "contracts", "arb-ruleset.json");
        var outDir = Path.Combine(repoRoot, "out", "spike-72");
        Directory.CreateDirectory(outDir);

        var systemPrompt = await File.ReadAllTextAsync(promptPath);
        var schema = JsonSchema.FromText(await File.ReadAllTextAsync(schemaPath));
        var policies = JsonSerializer.Deserialize<List<PolicyChunk>>(
            await File.ReadAllTextAsync(policiesPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("policies.json failed to parse");

        var arbRuleset = JsonNode.Parse(await File.ReadAllTextAsync(arbRulesetPath))!.AsObject();
        var handAuthoredById = arbRuleset["rules"]!.AsArray()
            .Cast<JsonObject>()
            .ToDictionary(
                r => (string)r["id"]!,
                r => r,
                StringComparer.Ordinal);

        Console.WriteLine($"Endpoint: {endpoint}");
        Console.WriteLine($"Model:    {deployment}");
        Console.WriteLine($"Policies: {policies.Count}");
        Console.WriteLine($"Out:      {outDir}");
        Console.WriteLine();

        IChatClient chat = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
            .GetChatClient(deployment)
            .AsIChatClient();

        var results = new List<SpikeResult>();
        var i = 0;
        foreach (var p in policies.Take(max))
        {
            i++;
            var ordinal = i.ToString("D2");
            var slug = Slug(p.Header);
            var stem = $"ARB-{ordinal}-{slug}";
            Console.Write($"[{i:D2}/{policies.Count}] {stem,-60} ");

            try
            {
                var userPrompt = BuildUserPrompt(p, "architecture-review", "arb-cloud-security-directive", i - 1);
                var resp = await chat.GetResponseAsync(
                    new[]
                    {
                        new ChatMessage(ChatRole.System, systemPrompt),
                        new ChatMessage(ChatRole.User, userPrompt),
                    },
                    new ChatOptions
                    {
                        // JSON mode — guarantees the response is parseable JSON.
                        // Full json_schema mode is preferred at production
                        // (AI Search GenAI prompt skill) where we'd bind the
                        // schema directly, but JSON mode is enough for the spike.
                        ResponseFormat = ChatResponseFormat.Json,
                        Temperature = 0,
                    });

                var raw = resp.Text ?? string.Empty;
                var json = ExtractJson(raw);
                var node = json is null ? null : JsonNode.Parse(json);

                var validationErrors = new List<string>();
                if (node is null)
                {
                    validationErrors.Add("response did not contain parseable JSON");
                }
                else
                {
                    foreach (var obj in EnumerateObjects(node))
                    {
                        var element = JsonSerializer.SerializeToElement(obj);
                        var result = schema.Evaluate(element, new EvaluationOptions
                        {
                            OutputFormat = OutputFormat.List,
                        });
                        if (!result.IsValid)
                        {
                            CollectErrors(result, validationErrors);
                        }
                    }
                }

                var perChunkPath = Path.Combine(outDir, $"{stem}.json");
                await File.WriteAllTextAsync(perChunkPath, json ?? raw);

                var status = validationErrors.Count == 0 ? "OK" : "INVALID";
                Console.WriteLine(status);
                if (validationErrors.Count > 0)
                {
                    foreach (var e in validationErrors.Take(3))
                        Console.WriteLine($"      - {e}");
                }

                results.Add(new SpikeResult(stem, p.Header, perChunkPath, validationErrors, node));
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR");
                Console.WriteLine($"      {ex.GetType().Name}: {ex.Message}");
                results.Add(new SpikeResult(stem, p.Header, "", new List<string> { ex.Message }, null));
            }
        }

        await WriteComparisonReportAsync(
            Path.Combine(outDir, "comparison.md"),
            results,
            handAuthoredById);

        var valid = results.Count(r => r.ValidationErrors.Count == 0);
        Console.WriteLine();
        Console.WriteLine($"Done. {valid}/{results.Count} schema-valid.");
        Console.WriteLine($"Comparison report: {Path.Combine(outDir, "comparison.md")}");
        return valid == results.Count ? 0 : 1;
    }

    private static string BuildUserPrompt(
        PolicyChunk p, string domain, string documentId, int chunkOrdinal) =>
        JsonSerializer.Serialize(new
        {
            domain,
            documentId,
            chunkOrdinal,
            headingPath = p.Header,
            pageNumber = (int?)null,
            chunk = p.Content,
        }, new JsonSerializerOptions { WriteIndented = true });

    private static IEnumerable<JsonNode> EnumerateObjects(JsonNode node)
    {
        if (node is JsonArray arr)
            foreach (var item in arr)
                if (item is not null) yield return item;
        else
            yield return node;
    }

    private static string? ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Strip any ``` fences the model may have added despite the prompt.
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence > 0) trimmed = trimmed[..lastFence];
            trimmed = trimmed.Trim();
        }

        // Accept either a top-level object or a top-level array.
        var first = trimmed.AsSpan().TrimStart();
        if (first.Length == 0) return null;
        return first[0] is '{' or '[' ? trimmed : null;
    }

    private static void CollectErrors(EvaluationResults result, List<string> errors)
    {
        if (result.Errors is { Count: > 0 })
        {
            foreach (var (key, msg) in result.Errors)
                errors.Add($"{result.InstanceLocation}: {key} — {msg}");
        }
        if (result.Details is not null)
        {
            foreach (var d in result.Details)
                CollectErrors(d, errors);
        }
    }

    private static async Task WriteComparisonReportAsync(
        string outPath,
        IReadOnlyList<SpikeResult> results,
        IReadOnlyDictionary<string, JsonObject> handAuthored)
    {
        using var w = new StreamWriter(outPath);
        await w.WriteLineAsync("# Spike #72 — comparison report");
        await w.WriteLineAsync();
        await w.WriteLineAsync($"- Total chunks: {results.Count}");
        await w.WriteLineAsync($"- Schema-valid: {results.Count(r => r.ValidationErrors.Count == 0)}");
        await w.WriteLineAsync($"- Hand-authored ARB rules to compare: {handAuthored.Count}");
        await w.WriteLineAsync();

        await w.WriteLineAsync("| Chunk | Status | Concepts (LLM) | Concepts (hand) | Lambda match |");
        await w.WriteLineAsync("| ----- | ------ | -------------- | --------------- | ------------ |");

        foreach (var r in results)
        {
            var status = r.ValidationErrors.Count == 0 ? "✅" : "❌";
            var llmConcepts = ExtractConcepts(r.RuleNode);
            var handMatch = handAuthored.Values
                .FirstOrDefault(h => string.Equals(
                    NormaliseHeader((string)h["metadata"]!["sourcePolicy"]!),
                    NormaliseHeader(r.SourceHeader),
                    StringComparison.OrdinalIgnoreCase));
            var handConcepts = handMatch is null ? "—" : ExtractHandConcepts(handMatch);
            var lambdaMatch = handMatch is not null
                && r.RuleNode is JsonObject ro
                && ((string?)ro["lambda"])?.Contains("MatchesAnyMeaning", StringComparison.Ordinal) == true
                ? "✅" : "—";
            await w.WriteLineAsync(
                $"| {r.RuleId} | {status} | {Truncate(string.Join(" \\| ", llmConcepts), 80)} | {Truncate(handConcepts, 80)} | {lambdaMatch} |");
        }

        await w.WriteLineAsync();
        await w.WriteLineAsync("## Validation errors (first 10 invalid chunks)");
        foreach (var r in results.Where(r => r.ValidationErrors.Count > 0).Take(10))
        {
            await w.WriteLineAsync();
            await w.WriteLineAsync($"### {r.RuleId} — {r.SourceHeader}");
            foreach (var e in r.ValidationErrors.Take(5))
                await w.WriteLineAsync($"- `{e}`");
        }
    }

    private static IReadOnlyList<string> ExtractConcepts(JsonNode? node)
    {
        if (node is JsonObject obj && obj["concepts"] is JsonArray arr)
            return arr.Select(c => (string?)c ?? "").Where(s => s.Length > 0).ToList();
        if (node is JsonArray top && top.FirstOrDefault() is JsonObject first
            && first["concepts"] is JsonArray a)
            return a.Select(c => (string?)c ?? "").Where(s => s.Length > 0).ToList();
        return Array.Empty<string>();
    }

    private static string ExtractHandConcepts(JsonObject hand)
    {
        var lambda = (string?)hand["lambda"] ?? "";
        var start = lambda.IndexOf('"');
        var end = lambda.IndexOf('"', start + 1);
        if (start < 0 || end <= start) return "—";
        return lambda.Substring(start + 1, end - start - 1).Replace("|", " | ");
    }

    private static string NormaliseHeader(string h) =>
        new(h.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string Slug(string header)
    {
        var chars = header.ToUpperInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var s = new string(chars);
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-");
        return s.Trim('-');
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LambdaRag.sln")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate LambdaRag.sln from " + AppContext.BaseDirectory);
    }

    private static Dictionary<string, string> ParseFlags(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            var val = (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                ? args[++i] : "true";
            map[key] = val;
        }
        return map;
    }

    private sealed record PolicyChunk(string Header, string Content, string Category, bool Mandatory);

    private sealed record SpikeResult(
        string RuleId,
        string SourceHeader,
        string OutputPath,
        List<string> ValidationErrors,
        JsonNode? RuleNode);
}
