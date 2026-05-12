using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.AI.OpenAI;
using Azure.Identity;
using Json.Schema;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LambdaRag.Authoring.ExtractFunction;

/// <summary>
/// Calls Azure OpenAI (Foundry) chat completions with the packaged
/// rule-extraction system prompt and validates the response against the
/// packaged ExtractedRule JSON schema.
///
/// Modeled after the spike-72 harness (spikes/72-ai-search-authoring/Program.cs)
/// — same prompt + same schema + same validator, repackaged as a service so
/// the AI Search WebApiSkill can drive it.
/// </summary>
public sealed class RuleExtractionService
{
    private readonly ILogger<RuleExtractionService> _log;
    private readonly IChatClient _chat;
    private readonly JsonSchema _schema;
    private readonly string _systemPrompt;

    public RuleExtractionService(ILogger<RuleExtractionService> log)
    {
        _log = log;

        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
            ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is required");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_CHAT_DEPLOYMENT")
            ?? "gpt-4o-mini";

        var promptDir = Path.Combine(AppContext.BaseDirectory, "prompts");
        var promptPath = Path.Combine(promptDir, "rule-extraction.system-prompt.md");
        var schemaPath = Path.Combine(promptDir, "rule-extraction.schema.json");

        if (!File.Exists(promptPath))
            throw new FileNotFoundException($"System prompt not packaged with build: {promptPath}");
        if (!File.Exists(schemaPath))
            throw new FileNotFoundException($"Schema not packaged with build: {schemaPath}");

        _systemPrompt = File.ReadAllText(promptPath);
        _schema = JsonSchema.FromText(File.ReadAllText(schemaPath));

        _chat = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
            .GetChatClient(deployment)
            .AsIChatClient();

        _log.LogInformation(
            "RuleExtractionService ready. endpoint={Endpoint} deployment={Deployment} promptBytes={Bytes}",
            endpoint, deployment, _systemPrompt.Length);
    }

    public async Task<ExtractionOutcome> ExtractAsync(
        WebApiSkillContract.InputData input,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Chunk))
        {
            return ExtractionOutcome.Skipped("empty chunk");
        }

        // Derive a section identity from the first markdown heading in the
        // chunk content. Two sibling chunks from the same source section
        // share a heading line (Document Intelligence's layout output
        // injects headings at section boundaries), so this gives us a
        // stable group key for reassembly without needing the skillset to
        // expose ordinal information. parentDocumentId provides the
        // outer scope so two policies that happen to use the same heading
        // never collide.
        var (sectionHeading, sectionId) = DeriveSectionIdentity(
            input.HeadingPath, input.Chunk, input.ParentDocumentId ?? input.DocumentId);

        var userPayload = JsonSerializer.Serialize(new
        {
            domain = "architecture-review",
            documentId = input.DocumentId ?? "unknown",
            parentDocumentId = input.ParentDocumentId ?? input.DocumentId ?? "unknown",
            sectionId,
            sectionHeading,
            chunkOrdinal = input.ChunkOrdinal ?? 0,
            headingPath = input.HeadingPath ?? sectionHeading,
            chunk = input.Chunk,
        }, new JsonSerializerOptions { WriteIndented = true });

        try
        {
            var resp = await _chat.GetResponseAsync(
                new[]
                {
                    new ChatMessage(ChatRole.System, _systemPrompt),
                    new ChatMessage(ChatRole.User,   userPayload),
                },
                new ChatOptions
                {
                    ResponseFormat = ChatResponseFormat.Json,
                    Temperature = 0,
                },
                cancellationToken: ct);

            var raw = resp.Text ?? string.Empty;
            var json = ExtractJson(raw);
            if (string.IsNullOrEmpty(json))
            {
                return ExtractionOutcome.Failed("model response did not contain parseable JSON");
            }

            var node = JsonNode.Parse(json);
            if (node is null)
            {
                return ExtractionOutcome.Failed("response JSON did not parse to a node");
            }

            // Validate every rule object in the response (object or array).
            var errors = new List<string>();
            foreach (var obj in EnumerateObjects(node))
            {
                var element = JsonSerializer.SerializeToElement(obj);
                var result = _schema.Evaluate(element, new EvaluationOptions
                {
                    OutputFormat = OutputFormat.List,
                });
                if (!result.IsValid)
                {
                    CollectErrors(result, errors);
                }
            }

            if (errors.Count > 0)
            {
                return ExtractionOutcome.Failed(string.Join("; ", errors.Take(3)));
            }

            // The skillset projection is wired to consume a single object —
            // if the model returned an array, we project the first element.
            var firstObj = node is JsonArray arr
                ? arr.OfType<JsonObject>().FirstOrDefault()
                : node as JsonObject;

            if (firstObj is null)
            {
                return ExtractionOutcome.Failed("response did not contain a rule object");
            }

            // Stamp the section identity onto the extracted rule so the
            // index ingests parentDocumentId + sectionId. Downstream
            // consumers (AzureSearchRuleSemanticIndex.ReassembleAsync)
            // group by these to merge sibling chunks of the same source
            // clause into a single canonical Rule.
            firstObj["parentDocumentId"] =
                input.ParentDocumentId ?? input.DocumentId ?? "unknown";
            firstObj["sectionId"] = sectionId;

            return ExtractionOutcome.Ok(firstObj);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Foundry call failed");
            return ExtractionOutcome.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static (string Heading, string SectionId) DeriveSectionIdentity(
        string? explicitHeadingPath,
        string chunkText,
        string? parentScope)
    {
        // Prefer an explicit heading path when the skillset supplied one.
        // Otherwise, scan the chunk for its first markdown heading
        // (lines starting with one or more '#'). Two sibling chunks of
        // the same Document-Intelligence section share the same heading
        // because layoutMarkdown injects the heading at the top of each
        // emitted page.
        var heading = explicitHeadingPath;
        if (string.IsNullOrWhiteSpace(heading))
        {
            foreach (var rawLine in chunkText.Split('\n'))
            {
                var line = rawLine.TrimStart('\uFEFF', ' ', '\t');
                if (line.StartsWith('#'))
                {
                    heading = line.TrimStart('#').Trim();
                    if (heading.Length > 0) break;
                }
            }
        }
        heading ??= string.Empty;

        // SectionId = SHA-256 of (parent || heading). Short hex prefix is
        // human-tolerable for logs while still globally unique. Empty
        // heading yields the empty-scope sentinel so we never claim two
        // unrelated chunks belong to the same section.
        if (heading.Length == 0)
        {
            return (string.Empty, "section:no-heading");
        }
        var scoped = (parentScope ?? "") + "\u001f" + heading;
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(scoped));
        var hex = Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
        return (heading, $"section:{hex}");
    }

    private static IEnumerable<JsonNode> EnumerateObjects(JsonNode node)
    {
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
                if (item is not null) yield return item;
        }
        else
        {
            yield return node;
        }
    }

    private static string? ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
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
}

public sealed record ExtractionOutcome(
    ExtractionStatus Status,
    JsonObject? Rule,
    string? Reason)
{
    public static ExtractionOutcome Ok(JsonObject rule) =>
        new(ExtractionStatus.Ok, rule, null);

    public static ExtractionOutcome Skipped(string reason) =>
        new(ExtractionStatus.Skipped, null, reason);

    public static ExtractionOutcome Failed(string reason) =>
        new(ExtractionStatus.Failed, null, reason);
}

public enum ExtractionStatus
{
    Ok,
    Skipped,
    Failed,
}
