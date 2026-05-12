using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.AI.OpenAI;
using Azure.Identity;
using Json.Schema;
using LambdaRag.Authoring;
using LambdaRag.Authoring.Embeddings;
using LambdaRag.Authoring.Validation;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace LambdaRag.Tools.PromptEval;

/// <summary>
/// Eval harness for issue #102. Drives the rule-extraction prompt against
/// the ARB markdown corpus, validates each emitted rule against the
/// production JSON schema, then runs the Phase B <see cref="RuleSelfValidator"/>
/// against the live <c>text-embedding-3-large</c> embedder. Reports
/// survival rate per chunk + summary + reason buckets.
///
/// Usage:
///   prompt-eval [--prompt &lt;path&gt;] [--max N] [--out &lt;dir&gt;]
///
/// Configuration is sourced from <c>dotnet user-secrets</c> (shared with
/// <c>src/LambdaRag.Cli</c>) and environment variables.
///   • LambdaRag:Foundry:Endpoint
///   • LambdaRag:Foundry:Edit:Deployment (chat model — defaults gpt-5.1)
///   • LambdaRag:Foundry:Deployment (embedding model)
///   • LambdaRag:Foundry:Model
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var f = ParseFlags(args);
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets("lambda-rag-cli-3f1e7b8c-9c2a-4f6e-bf2a-2c5b9c6d4e10")
            .AddEnvironmentVariables()
            .Build();

        var repoRoot = FindRepoRoot();
        var promptPath = f.GetValueOrDefault("prompt")
            ?? Path.Combine(repoRoot, "samples", "authoring", "rule-extraction.system-prompt.md");
        var schemaPath = Path.Combine(repoRoot, "samples", "authoring", "rule-extraction.schema.json");
        var policiesPath = Path.Combine(repoRoot, "policies", "arb", "policies.json");
        var max = int.TryParse(f.GetValueOrDefault("max"), out var m) ? m : int.MaxValue;
        var outRoot = f.GetValueOrDefault("out")
            ?? Path.Combine(repoRoot, "out", "prompt-eval");
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var outDir = Path.Combine(outRoot, stamp);
        Directory.CreateDirectory(outDir);

        var chatEndpoint = configuration["LambdaRag:Foundry:Endpoint"]
            ?? throw new InvalidOperationException("LambdaRag:Foundry:Endpoint user-secret not set.");
        var chatDeployment = configuration["LambdaRag:Foundry:Edit:Deployment"] ?? "gpt-5.1";

        Console.WriteLine($"prompt:     {promptPath}");
        Console.WriteLine($"schema:     {schemaPath}");
        Console.WriteLine($"corpus:     {policiesPath}");
        Console.WriteLine($"endpoint:   {chatEndpoint}");
        Console.WriteLine($"chat dep:   {chatDeployment}");
        Console.WriteLine($"out dir:    {outDir}");
        Console.WriteLine();

        var systemPrompt = await File.ReadAllTextAsync(promptPath);
        var schema = JsonSchema.FromText(await File.ReadAllTextAsync(schemaPath));
        var policies = JsonSerializer.Deserialize<List<PolicyChunk>>(
            await File.ReadAllTextAsync(policiesPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("policies.json failed to parse");

        IChatClient chat = new AzureOpenAIClient(new Uri(chatEndpoint), new DefaultAzureCredential())
            .GetChatClient(chatDeployment)
            .AsIChatClient();

        var embedder = FoundryEmbedderFactory.TryCreate(configuration)
            ?? throw new InvalidOperationException("Foundry embedder settings missing in user-secrets.");
        var phaseB = new RuleSelfValidator(embedder);

        var results = new List<EvalResult>();
        var i = 0;
        var sw = Stopwatch.StartNew();
        foreach (var p in policies.Take(max))
        {
            i++;
            var ordinal = i.ToString("D2");
            var stem = $"ARB-{ordinal}-{Slug(p.Header)}";
            Console.Write($"[{i:D2}/{policies.Count}] {Truncate(stem, 50),-50} ");

            var r = await EvaluateChunkAsync(
                chat, systemPrompt, schema, embedder, phaseB,
                p, i - 1, ordinal, outDir);
            results.Add(r);
            Console.WriteLine(r.Verdict);
            foreach (var note in r.Notes.Take(2))
                Console.WriteLine($"        · {note}");
        }
        sw.Stop();

        var emitted = results.Count(r => r.SchemaValid);
        var accepted = results.Count(r => r.PhaseBAccepted == true);
        var survival = emitted == 0 ? 0.0 : (double)accepted / emitted;

        var summary = new
        {
            timestamp = stamp,
            prompt = Path.GetRelativePath(repoRoot, promptPath),
            chatDeployment,
            embedder = embedder.EmbedderId,
            chunks = results.Count,
            emitted,
            accepted,
            schemaInvalid = results.Count(r => !r.SchemaValid),
            phaseBRejected = results.Count(r => r.PhaseBAccepted == false),
            survivalRate = Math.Round(survival, 4),
            durationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
            results = results.Select(r => new
            {
                r.RuleId,
                r.Header,
                r.SchemaValid,
                r.PhaseBAccepted,
                r.Verdict,
                notes = r.Notes,
            }).ToList(),
        };
        await File.WriteAllTextAsync(
            Path.Combine(outDir, "summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine();
        Console.WriteLine($"chunks         : {results.Count}");
        Console.WriteLine($"schema-valid   : {emitted}");
        Console.WriteLine($"schema-invalid : {results.Count(r => !r.SchemaValid)}");
        Console.WriteLine($"phaseB-accepted: {accepted}");
        Console.WriteLine($"phaseB-rejected: {results.Count(r => r.PhaseBAccepted == false)}");
        Console.WriteLine($"survival rate  : {survival:P2}  ({accepted}/{emitted})");
        Console.WriteLine($"elapsed        : {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"summary        : {Path.Combine(outDir, "summary.json")}");

        return survival >= 0.80 ? 0 : 2;
    }

    private static async Task<EvalResult> EvaluateChunkAsync(
        IChatClient chat,
        string systemPrompt,
        JsonSchema schema,
        IRuleEmbedder embedder,
        RuleSelfValidator phaseB,
        PolicyChunk p,
        int chunkOrdinal,
        string ordinalStr,
        string outDir)
    {
        var notes = new List<string>();
        string ruleId = $"ARB-{ordinalStr}-{Slug(p.Header)}";

        var userPayload = JsonSerializer.Serialize(new
        {
            domain = "architecture-review",
            documentId = "arb-cloud-security-directive",
            parentDocumentId = "arb-cloud-security-directive",
            sectionId = $"section:{ordinalStr}",
            sectionHeading = p.Header,
            chunkOrdinal,
            headingPath = p.Header,
            chunk = p.Content,
        }, new JsonSerializerOptions { WriteIndented = true });

        ChatResponse resp;
        try
        {
            resp = await chat.GetResponseAsync(
                new[]
                {
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userPayload),
                },
                new ChatOptions { ResponseFormat = ChatResponseFormat.Json, Temperature = 0 });
        }
        catch (Exception ex)
        {
            return new EvalResult(ruleId, p.Header, false, null, "ERROR-CHAT",
                new List<string> { $"{ex.GetType().Name}: {ex.Message}" });
        }

        var raw = resp.Text ?? string.Empty;
        await File.WriteAllTextAsync(Path.Combine(outDir, $"{ruleId}.raw.txt"), raw);

        var json = ExtractJson(raw);
        if (json is null)
        {
            return new EvalResult(ruleId, p.Header, false, null, "INVALID-JSON",
                new List<string> { "response did not contain parseable JSON" });
        }

        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (Exception ex)
        {
            return new EvalResult(ruleId, p.Header, false, null, "INVALID-JSON",
                new List<string> { ex.Message });
        }
        if (node is null)
        {
            return new EvalResult(ruleId, p.Header, false, null, "INVALID-JSON",
                new List<string> { "JsonNode.Parse returned null" });
        }

        // Stamp Function-only fields so the schema's `required` set passes.
        // The eval harness does not exercise the AFD-fronted Function; we
        // simulate the same stamping ExtractFunction performs server-side.
        foreach (var obj in EnumerateObjects(node).OfType<JsonObject>())
        {
            obj["status"] ??= "approved";
            obj["rulesetName"] ??= "architecture-review";
            obj["rulesetVersion"] ??= "2026.05-eval";
            obj["approvedAtUtc"] ??= DateTime.UtcNow.ToString("o");
            obj["approvedBy"] ??= "system";
            obj["contentHash"] ??= new string('0', 64);
        }

        var schemaErrors = new List<string>();
        foreach (var obj in EnumerateObjects(node))
        {
            var element = JsonSerializer.SerializeToElement(obj);
            var result = schema.Evaluate(element, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!result.IsValid) CollectErrors(result, schemaErrors);
        }

        await File.WriteAllTextAsync(
            Path.Combine(outDir, $"{ruleId}.json"),
            JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true }));

        if (schemaErrors.Count > 0)
        {
            return new EvalResult(ruleId, p.Header, false, null, "INVALID-SCHEMA",
                schemaErrors.Take(3).ToList());
        }

        // Project the first object to a Rule + run Phase B (embedding-based)
        var firstObj = (node is JsonArray a ? a.OfType<JsonObject>().FirstOrDefault() : node as JsonObject)
            ?? throw new InvalidOperationException("schema passed but no object");

        var rule = BuildRule(firstObj);
        try
        {
            var v = await phaseB.ValidateAsync(rule);
            var verdict = v.Accepted ? "ACCEPTED" : "REJECTED-PHASEB";
            notes.Add(
                $"minPos={v.MinPositive:F3} maxNeg={v.MaxNegative:F3} margin={v.Margin:F3} thr={v.CalibratedThreshold:F3}");
            if (!v.Accepted && v.RejectionReason is not null)
                notes.Add(v.RejectionReason);
            return new EvalResult(ruleId, p.Header, true, v.Accepted, verdict, notes);
        }
        catch (Exception ex)
        {
            return new EvalResult(ruleId, p.Header, true, false, "PHASEB-ERROR",
                new List<string> { $"{ex.GetType().Name}: {ex.Message}" });
        }
    }

    private static Rule BuildRule(JsonObject obj)
    {
        var ruleId = (string?)obj["ruleId"] ?? "UNKNOWN";
        var nl = (string?)obj["naturalLanguage"] ?? "";
        var lambda = (string?)obj["lambda"] ?? "";
        var predicate = (string?)obj["predicate"] ?? "true";
        var examples = obj["examples"] as JsonObject;
        var positive = (examples?["positive"] as JsonArray)?
            .Select(n => (string?)n ?? "").Where(s => s.Length > 0).ToList() ?? new List<string>();
        var negative = (examples?["negative"] as JsonArray)?
            .Select(n => (string?)n ?? "").Where(s => s.Length > 0).ToList() ?? new List<string>();

        return new Rule(
            Id: ruleId,
            Version: "eval",
            NaturalLanguage: nl,
            Lambda: lambda,
            AppliesToSchema: new JsonObject(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Violation,
            SourceSpan: new LambdaRag.Core.Domain.SourceSpan("arb-cloud-security-directive", 0, 0, null, null),
            EvidenceQuote: (string?)obj["evidenceQuote"] ?? "",
            Metadata: new Dictionary<string, string>())
        {
            Predicate = predicate,
            Examples = new RuleExamples(positive, negative),
        };
    }

    private static IEnumerable<JsonNode> EnumerateObjects(JsonNode node)
    {
        if (node is JsonArray arr)
            foreach (var item in arr) if (item is not null) yield return item;
        else
            yield return node;
    }

    private static string? ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = t.IndexOf('\n');
            if (nl > 0) t = t[(nl + 1)..];
            var lf = t.LastIndexOf("```", StringComparison.Ordinal);
            if (lf > 0) t = t[..lf];
            t = t.Trim();
        }
        return t.Length > 0 && (t[0] == '{' || t[0] == '[') ? t : null;
    }

    private static void CollectErrors(EvaluationResults result, List<string> errors)
    {
        if (result.Errors is { Count: > 0 })
            foreach (var (k, v) in result.Errors)
                errors.Add($"{result.InstanceLocation}: {k} — {v}");
        if (result.Details is not null)
            foreach (var d in result.Details) CollectErrors(d, errors);
    }

    private static string Slug(string s)
    {
        var chars = s.ToUpperInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var r = new string(chars);
        while (r.Contains("--", StringComparison.Ordinal)) r = r.Replace("--", "-");
        return r.Trim('-');
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

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

    public sealed record PolicyChunk(string Header, string Content, string Category, bool Mandatory);

    public sealed record EvalResult(
        string RuleId,
        string Header,
        bool SchemaValid,
        bool? PhaseBAccepted,
        string Verdict,
        List<string> Notes);
}
