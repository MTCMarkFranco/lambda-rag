using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LambdaRag.Authoring;

/// <summary>
/// LLM-backed <see cref="IRuleAuthoringAgent"/> that calls an Azure Foundry
/// (Azure OpenAI) chat deployment through <see cref="IChatClient"/>. Emits
/// one rule per SHALL / MUST / SHALL NOT / MUST NOT clause found in the
/// supplied chunk, with a strictly constrained predicate DSL so the runtime
/// evaluation path stays 100% deterministic (no LLM at runtime).
///
/// <para>Design highlights:</para>
/// <list type="bullet">
///   <item>Constrained predicate DSL: only <c>input1.text.Contains("...")</c>,
///     <c>input1.text.ToLower().Contains("...")</c>, boolean <c>||</c>/<c>&amp;&amp;</c>
///     and parentheses. Anything else is rejected before the rule is emitted.</item>
///   <item>Skips meta-governance clauses ("This policy SHALL be reviewed
///     annually", "Exceptions SHALL be time-boxed to 90 days" etc.) — the
///     prompt explicitly instructs the model, and the caller can additionally
///     inspect the emitted <c>metadata.meta-governance</c> flag.</item>
///   <item>Optimized for the three lambda-rag pillars:
///     <list type="number">
///       <item><b>Determinism</b> (runtime) — the constrained predicate DSL
///         guarantees compile-time-safe lambdas; the runtime never sees an LLM.</item>
///       <item><b>Idempotency</b> (build-time reproducibility) — bounded
///         <c>MaxOutputTokens</c>, JSON-schema-enforced response shape, and a
///         stable-ordering post-pass keep re-runs as close to byte-identical
///         as the deployment allows. <c>Temperature</c> and <c>Seed</c> are
///         intentionally NOT set — the Foundry <c>gpt-5.x</c> deployments
///         reject non-default temperature and do not honour a seed parameter
///         (per the supported request schema).</item>
///       <item><b>Defensibility</b> (legal-grade evidence) — every emitted
///         rule's <c>sourceQuote</c> must be a byte-for-byte substring of
///         the source chunk; candidates that fail this check are dropped so
///         evidence is always regulator-replayable.</item>
///     </list></item>
///   <item>Structured JSON output validated against a JSON schema server-side
///     AND revalidated locally. Bad entries are dropped, not thrown — one bad
///     rule can't kill an entire policy pass.</item>
///   <item>Concurrency: pipeline-wide <see cref="SemaphoreSlim"/>(4) so a
///     serial or parallel caller both stay under Foundry rate limits.</item>
///   <item>Retry: transient failures (HTTP 408 / 429 / 5xx, network errors,
///     timeouts) retried up to 3× with exponential backoff + jitter.</item>
/// </list>
///
/// <para>Rule ID scheme is intentionally partial. The agent emits a topic
/// slug (e.g. <c>IAM</c>, <c>NET</c>) in <c>Rule.Metadata["topicSlug"]</c>
/// and a placeholder <c>Rule.Id</c>. The extraction pipeline is responsible
/// for stamping the final <c>{prefix}{TOPIC}-{NNN}</c> ID because per-topic
/// counters need cross-chunk state the single-chunk agent cannot see.</para>
/// </summary>
public sealed class FoundryRuleAuthoringAgent : IRuleAuthoringAgent
{
    /// <summary>Key exposed on <see cref="Rule.Metadata"/> holding the topic slug.</summary>
    public const string TopicSlugMetadataKey = "topicSlug";

    /// <summary>Key exposed on <see cref="Rule.Metadata"/> flagging the
    /// authoring provenance (useful for downstream audit).</summary>
    public const string AuthoringAgentMetadataKey = "authoringAgent";

    // Pipeline-wide semaphore. Static so multiple agent instances constructed
    // per DI scope still share a single 4-slot budget against Foundry.
    private static readonly SemaphoreSlim GlobalCallGate = new(initialCount: 4, maxCount: 4);

    // The predicate validator: strip every legal token and confirm nothing is left.
    // Order matters — longer/more-specific patterns first.
    private static readonly Regex[] AllowedTokens =
    {
        // input1.text.ToLower().Contains("...")
        new(@"input1\.text\.ToLower\(\)\.Contains\(""(?:[^""\\]|\\.)*""\)", RegexOptions.Compiled),
        // input1.text.Contains("...")
        new(@"input1\.text\.Contains\(""(?:[^""\\]|\\.)*""\)", RegexOptions.Compiled),
        // Boolean operators and parentheses
        new(@"\|\||&&|\(|\)", RegexOptions.Compiled),
        // Whitespace
        new(@"\s+", RegexOptions.Compiled),
    };

    public const string SystemPrompt = """
        You are a policy-to-rule authoring assistant. Extract every normative clause
        (SHALL, MUST, SHALL NOT, MUST NOT) from the provided policy text and emit
        one machine-checkable rule per clause.

        Output a JSON object with a single key "rules" whose value is an array. Each
        element is:
        {
          "topicSlug": "<3-6 letter uppercase slug derived from the section heading, e.g. IAM, NET, SECR, CNTR, AKS, LOG, MON, TRACE, EXC, RETRY, CICD, IAC, SVC, COST, PRIV, SRE, SFI, DATA, COMP>",
          "naturalLanguage": "<the clause, verbatim or lightly normalized>",
          "predicate": "<a boolean expression in the CONSTRAINED DSL below>",
          "remediation": "<one sentence telling the author what to change>",
          "sourceQuote": "<byte-for-byte verbatim substring of the input section text — no paraphrasing, no ellipsis, no rewording. Must appear character-identical inside the input. If you cannot find a verbatim excerpt <= 400 chars that grounds the clause, SKIP the rule.>"
        }

        CONSTRAINED PREDICATE DSL — the ONLY allowed constructs:
          input1.text.Contains("literal")
          input1.text.ToLower().Contains("literal")   // for case-insensitive
          Boolean operators: || (or), && (and)
          Parentheses: ( )
        Nothing else. No .Length, no regex, no other method calls, no variables
        other than input1.text.

        The predicate must evaluate to TRUE when the target document COMPLIES with
        the clause and FALSE when it violates. Example:
          Clause: "All public ingress SHALL traverse a WAF in prevention mode."
          Predicate: input1.text.ToLower().Contains("waf") &&
                     (input1.text.ToLower().Contains("prevention") ||
                      input1.text.ToLower().Contains("blocking"))

        SKIP meta-governance clauses that govern the policy process itself, for
        example "Exceptions SHALL be time-boxed to 90 days" or "This policy SHALL
        be reviewed annually". Only emit rules testable against a document under
        review (an architecture doc, a service design, a contract).

        Emit ONE rule per distinct normative clause — do not collapse multiple
        clauses into one rule.

        If the input contains no normative clauses, return {"rules": []}.
        """;

    private readonly IChatClient _chat;
    private readonly IRuleEmbedder _embedder;
    private readonly ILogger<FoundryRuleAuthoringAgent> _log;
    private readonly int _maxRetries;

    public FoundryRuleAuthoringAgent(
        IChatClient chat,
        IRuleEmbedder embedder,
        ILogger<FoundryRuleAuthoringAgent>? log = null,
        int maxRetries = 3)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _log = log ?? NullLogger<FoundryRuleAuthoringAgent>.Instance;
        _maxRetries = Math.Max(0, maxRetries);
    }

    public async Task<IReadOnlyList<RuleAuthoringSuggestion>> AuthorAsync(
        RuleAuthoringRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SourceContent))
            return Array.Empty<RuleAuthoringSuggestion>();

        var heading = request.SourceSpan.HeadingPath ?? "(no heading)";
        var chunk = request.SourceContent.Length > 4000
            ? request.SourceContent[..4000]
            : request.SourceContent;
        var userPrompt = $"Section heading: {heading}\nSection text:\n{chunk}";

        // ChatOptions aligned to the Foundry gpt-5.x supported request
        // schema. The deployment accepts: model, messages, system,
        // max_output_tokens, stream, response_format, tools, tool_choice,
        // input, user, context. It rejects Temperature (any value) and
        // does not honour Seed — so both are intentionally omitted.
        // Idempotency comes from bounded output, JSON-schema shape
        // enforcement, and the DSL + verbatim-quote post-validators.
        var options = new ChatOptions
        {
            MaxOutputTokens = 8000,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                schema: RuleEnvelopeJsonSchema,
                schemaName: "LambdaRagAuthoringRuleEnvelope",
                schemaDescription: "Envelope of extracted policy rules."),
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                // Foundry-side telemetry / rate-limit isolation.
                ["user"] = "lambda-rag-authoring",
            },
        };

        string? responseText = null;
        try
        {
            responseText = await CallWithRetryAsync(
                new[]
                {
                    new ChatMessage(ChatRole.System, SystemPrompt),
                    new ChatMessage(ChatRole.User, userPrompt),
                },
                options,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Foundry authoring call failed permanently for chunk {DocId}@{Start}+{Len}; skipping.",
                request.SourceSpan.DocumentId, request.SourceSpan.CharStart, request.SourceSpan.CharLength);
            return Array.Empty<RuleAuthoringSuggestion>();
        }

        var parsed = TryParseRulesEnvelope(responseText);
        if (parsed is null)
        {
            _log.LogWarning(
                "Foundry authoring response was not valid JSON envelope for chunk {DocId}@{Start}; response length={Len}.",
                request.SourceSpan.DocumentId, request.SourceSpan.CharStart, responseText?.Length ?? 0);
            return Array.Empty<RuleAuthoringSuggestion>();
        }

        var suggestions = new List<RuleAuthoringSuggestion>(parsed.Count);
        foreach (var candidate in parsed)
        {
            if (!TryValidate(candidate, chunk, out var reason))
            {
                _log.LogInformation(
                    "Dropping authoring candidate (topic={Topic}) in {DocId}@{Start}: {Reason}",
                    candidate?.TopicSlug, request.SourceSpan.DocumentId, request.SourceSpan.CharStart, reason);
                continue;
            }

            var rule = await BuildRuleAsync(candidate!, request, ct).ConfigureAwait(false);
            suggestions.Add(new RuleAuthoringSuggestion(
                Rule: rule,
                Confidence: 0.85,
                Rationale: "Foundry-backed extraction, schema+DSL-validated."));
        }

        // Stable order so callers get deterministic output for a given chunk.
        return suggestions
            .OrderBy(s => s.Rule.Metadata.TryGetValue(TopicSlugMetadataKey, out var t) ? t : s.Rule.Id, StringComparer.Ordinal)
            .ThenBy(s => s.Rule.NaturalLanguage, StringComparer.Ordinal)
            .ToList();
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt <= _maxRetries && IsTransient(ex))
            {
                var backoffMs = ComputeBackoffMs(attempt);
                _log.LogInformation(
                    "Transient authoring failure (attempt {Attempt}/{Max}); backing off {Delay}ms. Error: {Error}",
                    attempt, _maxRetries, backoffMs, ex.Message);
                await Task.Delay(backoffMs, ct).ConfigureAwait(false);
            }
            finally
            {
                GlobalCallGate.Release();
            }
        }
    }

    private static int ComputeBackoffMs(int attempt)
    {
        // Exponential: 500ms, 1000ms, 2000ms; plus 0-250ms of jitter.
        var baseMs = 250 * (1 << attempt);
        var jitter = Random.Shared.Next(0, 250);
        return baseMs + jitter;
    }

    internal static bool IsTransient(Exception ex)
    {
        // Broaden the net: any exception exposing an int Status property
        // in the transient band (408/429/5xx) is retryable. This covers
        // Azure.RequestFailedException, System.ClientModel.ClientResultException,
        // and test doubles alike without hard-referencing either SDK type.
        if (ex is HttpRequestException || ex is TimeoutException || ex is TaskCanceledException)
            return true;

        var statusProp = ex.GetType().GetProperty("Status");
        if (statusProp?.PropertyType == typeof(int) && statusProp.GetValue(ex) is int status)
            return status == 408 || status == 429 || (status >= 500 && status < 600);

        return false;
    }

    /// <summary>
    /// JSON schema used with <see cref="ChatResponseFormat.ForJsonSchema"/>
    /// so Foundry constrains the response shape server-side (idempotency).
    /// Kept intentionally permissive on string content — the tight
    /// validation is done locally by <see cref="TryValidate"/>.
    /// </summary>
    private static readonly System.Text.Json.JsonElement RuleEnvelopeJsonSchema =
        System.Text.Json.JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["rules"],
          "properties": {
            "rules": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["topicSlug", "naturalLanguage", "predicate", "remediation", "sourceQuote"],
                "properties": {
                  "topicSlug":       { "type": "string", "minLength": 3, "maxLength": 6 },
                  "naturalLanguage": { "type": "string", "minLength": 1 },
                  "predicate":       { "type": "string", "minLength": 1 },
                  "remediation":     { "type": "string" },
                  "sourceQuote":     { "type": "string", "minLength": 1, "maxLength": 400 }
                }
              }
            }
          }
        }
        """).RootElement.Clone();

    internal static List<ExtractedRuleDto>? TryParseRulesEnvelope(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

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
        catch (JsonException) { return null; }
        if (node is null) return null;

        JsonArray? arr = null;
        if (node is JsonObject obj && obj["rules"] is JsonArray a) arr = a;
        else if (node is JsonArray topArr) arr = topArr;
        if (arr is null) return null;

        var list = new List<ExtractedRuleDto>(arr.Count);
        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            list.Add(new ExtractedRuleDto
            {
                TopicSlug = (string?)o["topicSlug"] ?? string.Empty,
                NaturalLanguage = (string?)o["naturalLanguage"] ?? string.Empty,
                Predicate = (string?)o["predicate"] ?? string.Empty,
                Remediation = (string?)o["remediation"] ?? string.Empty,
                SourceQuote = (string?)o["sourceQuote"] ?? string.Empty,
            });
        }
        return list;
    }

    /// <summary>
    /// Validates an LLM-emitted rule candidate against the schema + DSL +
    /// defensibility guarantees. <paramref name="sourceChunk"/> is the exact
    /// prompt text handed to the model; the sourceQuote must be a
    /// byte-for-byte substring of it (Pillar 3 — Defensibility).
    /// </summary>
    internal static bool TryValidate(ExtractedRuleDto? dto, string sourceChunk, out string reason)
    {
        reason = string.Empty;
        if (dto is null) { reason = "null candidate"; return false; }
        if (string.IsNullOrWhiteSpace(dto.TopicSlug)) { reason = "missing topicSlug"; return false; }
        if (!IsValidTopicSlug(dto.TopicSlug)) { reason = $"invalid topicSlug '{dto.TopicSlug}'"; return false; }
        if (string.IsNullOrWhiteSpace(dto.NaturalLanguage)) { reason = "missing naturalLanguage"; return false; }
        if (string.IsNullOrWhiteSpace(dto.Predicate)) { reason = "missing predicate"; return false; }
        if (!IsValidPredicate(dto.Predicate)) { reason = $"predicate rejected by DSL validator: '{dto.Predicate}'"; return false; }
        if (string.IsNullOrWhiteSpace(dto.SourceQuote)) { reason = "missing sourceQuote"; return false; }
        if (!string.IsNullOrEmpty(sourceChunk) && !sourceChunk.Contains(dto.SourceQuote, StringComparison.Ordinal))
        {
            reason = "sourceQuote is not a verbatim substring of the input chunk (defensibility violation)";
            return false;
        }
        return true;
    }

    internal static bool IsValidTopicSlug(string s)
    {
        if (s.Length is < 3 or > 6) return false;
        foreach (var c in s)
            if (c is < 'A' or > 'Z') return false;
        return true;
    }

    /// <summary>
    /// Returns true iff <paramref name="predicate"/> is exhausted by the
    /// allowed-token grammar. Uses a simple "strip and see what's left"
    /// tokenizer rather than a full parser — the DSL is small enough that
    /// this is both safe and easy to audit.
    /// </summary>
    public static bool IsValidPredicate(string predicate)
    {
        if (string.IsNullOrWhiteSpace(predicate)) return false;

        var remaining = predicate;
        // Loop stripping the longest leading match on each iteration.
        while (remaining.Length > 0)
        {
            var matched = false;
            foreach (var rx in AllowedTokens)
            {
                var m = rx.Match(remaining);
                if (m.Success && m.Index == 0 && m.Length > 0)
                {
                    remaining = remaining[m.Length..];
                    matched = true;
                    break;
                }
            }
            if (!matched) return false;
        }
        return true;
    }

    private async Task<Rule> BuildRuleAsync(
        ExtractedRuleDto dto,
        RuleAuthoringRequest request,
        CancellationToken ct)
    {
        var topic = dto.TopicSlug;
        // Placeholder ID — the extraction pipeline stamps the final counter.
        var provisionalId = $"{request.RuleIdPrefix}{topic}";

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TopicSlugMetadataKey] = topic,
            [AuthoringAgentMetadataKey] = nameof(FoundryRuleAuthoringAgent),
        };

        float[]? embedding = null;
        try
        {
            embedding = await _embedder.EmbedAsync(request.SourceContent, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Embedding failed for chunk; rule will be emitted without SourceEmbedding.");
        }

        return new Rule(
            Id: provisionalId,
            Version: "1.0.0",
            NaturalLanguage: dto.NaturalLanguage,
            Lambda: dto.Predicate,
            AppliesToSchema: SectionTextSchema(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Violation,
            SourceSpan: request.SourceSpan,
            EvidenceQuote: dto.SourceQuote,
            Metadata: metadata)
        {
            Predicate = "true",
            Applicability = DeterministicMockAuthoringAgent.InferApplicability(dto.NaturalLanguage),
            Remediation = string.IsNullOrWhiteSpace(dto.Remediation) ? null : dto.Remediation,
            SourceContent = request.SourceContent,
            SourceEmbedding = embedding,
        };
    }

    private static JsonObject SectionTextSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["text"] = new JsonObject { ["type"] = "string" },
            ["category"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("text"),
    };

    internal sealed class ExtractedRuleDto
    {
        public string TopicSlug { get; set; } = string.Empty;
        public string NaturalLanguage { get; set; } = string.Empty;
        public string Predicate { get; set; } = string.Empty;
        public string Remediation { get; set; } = string.Empty;
        public string SourceQuote { get; set; } = string.Empty;
    }
}
