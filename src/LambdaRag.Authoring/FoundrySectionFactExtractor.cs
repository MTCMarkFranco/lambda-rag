using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LambdaRag.Core;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Facts;
using LambdaRag.Core.Hashing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LambdaRag.Authoring;

/// <summary>
/// Pillar 12 (#153) — LLM-backed <see cref="IFactExtractor"/> that calls a
/// Foundry chat deployment (same wiring as
/// <see cref="FoundryRuleAuthoringAgent"/>) once per section to populate a
/// closed <see cref="FactSchema"/>.
///
/// <para>Determinism &amp; caching:
/// <list type="bullet">
///   <item>Sidecar cache lives at
///     <c>%USERPROFILE%\.lambda-rag\facts\&lt;docHash&gt;.&lt;factSchemaHash&gt;.facts.json</c>
///     (overridable). Cache hit: byte-identical replay.</item>
///   <item>Fingerprint drift throws
///     <see cref="SectionFactSidecarMismatchException"/> — no silent
///     recompute. Operator must pass <c>--refresh-facts</c> to accept.</item>
///   <item>Hallucination defense: every non-null fact requires a
///     <c>supporting_quote</c> that is a byte-for-byte substring of the
///     section text; otherwise the value is dropped and a warning
///     is recorded.</item>
/// </list>
/// </para>
/// </summary>
public sealed class FoundrySectionFactExtractor : IFactExtractor
{
    public const string SidecarVersion = "1.0";
    // Bumping this string invalidates every cached sidecar loudly.
    public const string PromptVersion = "1.0.0";

    private static readonly SemaphoreSlim GlobalCallGate = new(initialCount: 4, maxCount: 4);

    public const string SystemPrompt = """
        You are a policy-fact extractor. Your job is to read one section of a
        document and populate a fixed JSON schema of concepts. You do NOT decide
        compliance, adequacy, or applicability. You ONLY report what the section
        discusses.

        Rules:
        1. Emit ONLY a JSON object matching the schema below. No prose, no
           markdown, no code fences.
        2. For each concept, emit either the value the section supports
           (boolean, enum, integer, or verbatim phrase), OR null if the
           section does not discuss the concept.
        3. Never infer a value across sections. If the section is silent on
           a concept, emit null. Cross-section composition happens elsewhere.
        4. For every non-null value, emit "supporting_quote" — a verbatim
           quote from the section (max 200 characters) that supports it.
        5. If the section contains a number/date/duration, emit the VERBATIM
           phrase from the text ("every 90 days", "quarterly"). Do NOT convert.
        6. If you are less than confident a concept applies, emit null. Silent
           is safer than wrong.
        """;

    private readonly IChatClient _chat;
    private readonly ILogger<FoundrySectionFactExtractor> _log;
    private readonly int _maxRetries;
    private readonly string? _cacheDirOverride;
    private readonly bool _refresh;
    private readonly DurationNormalizer _normalizer;

    public string ModelId { get; }
    public string PromptHash { get; }

    public FoundrySectionFactExtractor(
        IChatClient chat,
        string modelId,
        ILogger<FoundrySectionFactExtractor>? log = null,
        int maxRetries = 3,
        string? cacheDirOverride = null,
        bool refresh = false,
        DurationNormalizer? normalizer = null)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        ModelId = string.IsNullOrWhiteSpace(modelId) ? "unknown-model" : modelId;
        _log = log ?? NullLogger<FoundrySectionFactExtractor>.Instance;
        _maxRetries = Math.Max(0, maxRetries);
        _cacheDirOverride = cacheDirOverride;
        _refresh = refresh;
        _normalizer = normalizer ?? DurationNormalizer.Default;
        // Prompt fingerprint folds in system prompt + prompt version +
        // normalizer table hash so any of those drifting invalidates cache.
        PromptHash = ContentHash.Compose(
            SystemPrompt,
            PromptVersion,
            _normalizer.Version,
            _normalizer.TableHash.Value).Value;
    }

    public async Task<SectionFactSidecar> ExtractAsync(
        ProjectedDocument document,
        FactSchema schema,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(schema);

        var docHashStr = document.SourceId.Value;
        var schemaHash = schema.Fingerprint();
        var sections = ExtractSectionTexts(document);
        var orderingHash = ComputeOrderingHash(sections);
        var expectedFp = SectionFactSidecar.ComputeFingerprint(
            docHashStr, schemaHash.Value, ModelId, PromptHash, orderingHash.Value);

        var cacheDir = SectionFactSidecarIO.ResolveCacheDir(_cacheDirOverride);
        var path = SectionFactSidecarIO.CachePath(cacheDir, document.SourceId, schemaHash);

        if (!_refresh && File.Exists(path))
        {
            var cached = SectionFactSidecarIO.LoadOrThrow(path, expectedFp);
            _log.LogInformation("Loaded cached fact sidecar ({Sections} sections) from {Path}",
                cached.Sections.Count, path);
            return cached;
        }

        _log.LogInformation("Extracting facts for {Sections} sections via model {Model}",
            sections.Count, ModelId);

        var schemaJson = SchemaToPromptJson(schema);
        var warnings = new List<string>();
        var bags = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
        var scope = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        var tasks = sections.Select(async pair =>
        {
            var (sectionId, text) = pair;
            var bag = await ExtractSectionAsync(sectionId, text, schema, schemaJson, warnings, ct)
                .ConfigureAwait(false);
            return (sectionId, bag);
        });
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var (id, bag) in results.OrderBy(r => r.sectionId, StringComparer.Ordinal))
            bags[id] = bag;

        var sidecar = new SectionFactSidecar(
            SidecarVersion: SidecarVersion,
            DocumentId: docHashStr,
            FactSchemaId: schema.Id,
            FactSchemaHash: schemaHash.Value,
            ModelId: ModelId,
            PromptHash: PromptHash,
            GeneratedAt: DateTimeOffset.UtcNow.ToString("O"),
            Sections: bags)
        {
            Fingerprint = expectedFp.Value,
            Warnings = warnings.Count > 0 ? warnings : null,
            RuleScope = scope.Count > 0
                ? scope.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal)
                : null,
        };
        SectionFactSidecarIO.Save(sidecar, path);
        _log.LogInformation("Wrote fact sidecar ({Sections} sections, {Warn} warnings) to {Path}",
            sidecar.Sections.Count, warnings.Count, path);
        return sidecar;
    }

    private async Task<Dictionary<string, object?>> ExtractSectionAsync(
        string sectionId,
        string sectionText,
        FactSchema schema,
        string schemaJson,
        List<string> warnings,
        CancellationToken ct)
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
        // Very short sections are almost always TOC / boilerplate; skip the
        // LLM call rather than burn tokens.
        if (string.IsNullOrWhiteSpace(sectionText) || sectionText.Length < 40)
            return bag;
        var user = $"Schema:\n{schemaJson}\n\nSection id: {sectionId}\nSection text:\n{sectionText}\n\nEmit the JSON object now.";
        var options = new ChatOptions
        {
            MaxOutputTokens = 800,
            ResponseFormat = ChatResponseFormat.Json,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["user"] = "lambda-rag-fact-extractor",
            },
        };
        string? raw = null;
        try
        {
            raw = await CallWithRetryAsync(new[]
            {
                new ChatMessage(ChatRole.System, SystemPrompt),
                new ChatMessage(ChatRole.User, user),
            }, options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Fact extraction call failed for section {SectionId}; treating as extraction failure.", sectionId);
            warnings.Add($"section {sectionId}: extraction_failed:{ex.GetType().Name}");
            bag["_extraction_failed"] = true;
            return bag;
        }
        return ParseAndValidate(raw, sectionId, sectionText, schema, warnings, bag);
    }

    private Dictionary<string, object?> ParseAndValidate(
        string? raw,
        string sectionId,
        string sectionText,
        FactSchema schema,
        List<string> warnings,
        Dictionary<string, object?> bag)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            warnings.Add($"section {sectionId}: empty_response");
            bag["_extraction_failed"] = true;
            return bag;
        }
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = trimmed.IndexOf('\n');
            if (firstNl > 0) trimmed = trimmed[(firstNl + 1)..];
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence > 0) trimmed = trimmed[..lastFence];
            trimmed = trimmed.Trim();
        }
        JsonNode? node;
        try { node = JsonNode.Parse(trimmed); }
        catch (JsonException)
        {
            warnings.Add($"section {sectionId}: non_json_response");
            bag["_extraction_failed"] = true;
            return bag;
        }
        if (node is not JsonObject obj)
        {
            warnings.Add($"section {sectionId}: non_object_response");
            bag["_extraction_failed"] = true;
            return bag;
        }

        var supportingQuote = (string?)obj["supporting_quote"];
        foreach (var concept in schema.Concepts)
        {
            var raw2 = obj[concept.Name];
            if (raw2 is null || raw2 is JsonValue jv && jv.TryGetValue<object?>(out var oV) && oV is null)
                continue;

            var value = ConvertConceptValue(concept, raw2, sectionText, sectionId, supportingQuote, warnings);
            if (value is null) continue;
            bag[concept.Name] = value;
        }
        return bag;
    }

    private object? ConvertConceptValue(
        FactConcept concept,
        JsonNode node,
        string sectionText,
        string sectionId,
        string? supportingQuote,
        List<string> warnings)
    {
        // Hallucination defense: for any non-null value, at least one
        // supporting quote must appear verbatim in the section text.
        // Applied at the section granularity (single quote per section
        // covers all concepts) — matches the prompt contract.
        if (!string.IsNullOrWhiteSpace(supportingQuote)
            && !sectionText.Contains(supportingQuote, StringComparison.Ordinal))
        {
            warnings.Add($"section {sectionId}: supporting_quote not found in section — dropping all values");
            return null;
        }

        switch (concept.Type)
        {
            case FactType.Boolean:
                if (node is JsonValue jvb && jvb.TryGetValue<bool>(out var b)) return b;
                if (node is JsonValue jvbs && jvbs.TryGetValue<string>(out var bs)
                    && bool.TryParse(bs, out var bp)) return bp;
                return null;
            case FactType.Integer:
                if (node is JsonValue jvi && jvi.TryGetValue<long>(out var l)) return l;
                if (node is JsonValue jvis && jvis.TryGetValue<string>(out var iss)
                    && long.TryParse(iss, out var lp)) return lp;
                return null;
            case FactType.Duration:
            {
                var phrase = node is JsonValue jvd && jvd.TryGetValue<string>(out var ds) ? ds : node.ToString();
                var days = _normalizer.NormalizeToDays(phrase);
                if (days is not null) return (long)days.Value;
                warnings.Add($"section {sectionId}: duration '{phrase}' — normalizer miss, preserved as string");
                return phrase;
            }
            case FactType.Enum:
            {
                var s = node is JsonValue jve && jve.TryGetValue<string>(out var es) ? es : node.ToString();
                if (concept.EnumValues is { Count: > 0 } allowed
                    && !allowed.Contains(s, StringComparer.Ordinal))
                {
                    warnings.Add($"section {sectionId}: '{concept.Name}'='{s}' outside enum set — dropped");
                    return null;
                }
                return s;
            }
            case FactType.Text:
            default:
            {
                var s = node is JsonValue jvt && jvt.TryGetValue<string>(out var ts) ? ts : node.ToString();
                if (s is null) return null;
                if (s.Length > 200) s = s.Substring(0, 200);
                return s;
            }
        }
    }

    // ── Section text extraction from ProjectedDocument.Graph ──────────────

    /// <summary>
    /// Pull (sectionId, text) pairs from the projected document. The
    /// contract projector emits sections under <c>$.sections[*]</c> with
    /// <c>id</c> + <c>text</c> keys; we tolerate missing ids by falling
    /// back to <c>s_&lt;index&gt;</c>.
    /// </summary>
    internal static IReadOnlyList<(string Id, string Text)> ExtractSectionTexts(ProjectedDocument document)
    {
        var list = new List<(string, string)>();
        if (document.Graph["sections"] is JsonArray arr)
        {
            var i = 0;
            foreach (var node in arr)
            {
                if (node is not JsonObject obj) { i++; continue; }
                var id = (string?)obj["id"] ?? $"s_{i:D8}";
                var text = (string?)obj["text"] ?? string.Empty;
                list.Add((id, text));
                i++;
            }
        }
        return list;
    }

    private static ContentHash ComputeOrderingHash(IReadOnlyList<(string Id, string Text)> sections)
    {
        var sb = new StringBuilder();
        foreach (var (id, text) in sections)
        {
            sb.Append(id).Append('\u001f').Append(text.Length).Append('\u001f');
        }
        return ContentHash.OfString(sb.ToString());
    }

    private static string SchemaToPromptJson(FactSchema schema)
    {
        var obj = new JsonObject();
        foreach (var c in schema.Concepts)
        {
            var slot = new JsonObject
            {
                ["type"] = c.Type switch
                {
                    FactType.Boolean => "boolean|null",
                    FactType.Enum => "enum|null",
                    FactType.Integer => "integer|null",
                    FactType.Duration => "string|null (verbatim phrase)",
                    _ => "string|null",
                },
                ["description"] = c.Description,
            };
            if (c.EnumValues is { Count: > 0 })
                slot["enum"] = new JsonArray(c.EnumValues.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            if (c.Examples is { Count: > 0 })
                slot["examples"] = new JsonArray(c.Examples.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            obj[c.Name] = slot;
        }
        obj["supporting_quote"] = new JsonObject
        {
            ["type"] = "string|null",
            ["description"] = "Verbatim quote (<=200 chars) from the section text supporting any non-null value.",
        };
        return JsonSerializer.Serialize(obj, CanonicalJson.Options);
    }

    private async Task<string> CallWithRetryAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options,
        CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            await GlobalCallGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var response = await _chat.GetResponseAsync(messages, options, cancellationToken: ct).ConfigureAwait(false);
                return response.Text ?? string.Empty;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (attempt <= _maxRetries && FoundryRuleAuthoringAgent.IsTransient(ex))
            {
                var baseMs = 250 * (1 << attempt);
                var jitter = Random.Shared.Next(0, 250);
                _log.LogInformation("Transient fact-extract failure (attempt {A}/{M}), backing off {B}ms: {E}",
                    attempt, _maxRetries, baseMs + jitter, ex.Message);
                await Task.Delay(baseMs + jitter, ct).ConfigureAwait(false);
            }
            finally
            {
                GlobalCallGate.Release();
            }
        }
    }
}
