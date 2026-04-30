using System.Text.Json;
using LambdaRag.Projection;
using Microsoft.Extensions.AI;

namespace LambdaRag.Authoring;

/// <summary>
/// LLM-backed topic suggester. The LLM proposes topic IDs and seed keywords;
/// this class then runs a strict deterministic post-validation pass before
/// emitting <see cref="TopicSuggestion"/> records:
///   * topic id must be snake_case [a-z0-9_]+, length 3..40
///   * at least 3 seed keywords, each ASCII lowercase, length 3..40
///   * no duplicate topic ids per response
///   * if the LLM hallucinates an existing topic id, IsExisting=true
///
/// Falls back to <see cref="KeywordHeuristicTopicSuggester"/> if the LLM
/// returns no valid suggestions, so the system degrades gracefully.
///
/// IMPORTANT: This is an authoring-time service. Suggestions are NEVER
/// auto-committed to a topic map at runtime — they are written to a
/// review queue for human approval.
/// </summary>
public sealed class LlmTopicSuggester : ITopicSuggester
{
    private readonly IChatClient _chat;
    private readonly KeywordHeuristicTopicSuggester _fallback;
    private readonly int _maxSuggestions;

    public LlmTopicSuggester(
        IChatClient chat,
        KeywordHeuristicTopicSuggester? fallback = null,
        int maxSuggestions = 3)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _fallback = fallback ?? new KeywordHeuristicTopicSuggester();
        _maxSuggestions = Math.Clamp(maxSuggestions, 1, 10);
    }

    public async Task<IReadOnlyList<TopicSuggestion>> SuggestAsync(
        TopicSuggestionRequest request,
        CancellationToken ct = default)
    {
        var existingIds = request.CurrentMap.Topics
            .Where(t => t.Axis is null)
            .Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);

        var systemPrompt = $$"""
            You are a topic-map curator. Given a heading and body from a
            "{{request.DocKind ?? request.CurrentMap.Domain}}" document, suggest up to
            {{_maxSuggestions}} topics that classify this section.
            Prefer reusing one of these existing topic ids: {{string.Join(", ", existingIds)}}
            Only invent a NEW snake_case id if no existing topic is a clean fit.
            Return JSON only, with this exact shape:
            {"suggestions":[
              {"topic_id":"<id>","seed_keywords":["kw1","kw2","kw3"],"rationale":"<why>"}
            ]}
            """;

        var userPrompt = $"HEADING: {request.Heading}\n\nBODY:\n{Trim(request.Body, 4000)}";

        string? raw = null;
        try
        {
            var resp = await _chat.GetResponseAsync(
                new[] {
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userPrompt),
                },
                cancellationToken: ct).ConfigureAwait(false);
            raw = resp.Text;
        }
        catch
        {
            // LLM call failed — fall back deterministically.
            return await _fallback.SuggestAsync(request, ct).ConfigureAwait(false);
        }

        var parsed = TryParseSuggestions(raw);
        if (parsed is null || parsed.Count == 0)
            return await _fallback.SuggestAsync(request, ct).ConfigureAwait(false);

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var validated = new List<TopicSuggestion>();
        foreach (var s in parsed)
        {
            if (!IsValidTopicId(s.TopicId)) continue;
            if (s.SeedKeywords is null || s.SeedKeywords.Count < 3) continue;
            if (!s.SeedKeywords.All(IsValidKeyword)) continue;
            if (!seenIds.Add(s.TopicId)) continue;

            validated.Add(new TopicSuggestion(
                TopicId: s.TopicId,
                SeedKeywords: s.SeedKeywords.Select(k => k.ToLowerInvariant()).Distinct().ToList(),
                IsExisting: existingIds.Contains(s.TopicId),
                Confidence: 0.7,
                Rationale: string.IsNullOrWhiteSpace(s.Rationale) ? "LLM-proposed." : s.Rationale));
            if (validated.Count >= _maxSuggestions) break;
        }

        return validated.Count == 0
            ? await _fallback.SuggestAsync(request, ct).ConfigureAwait(false)
            : validated;
    }

    public static bool IsValidTopicId(string id) =>
        !string.IsNullOrEmpty(id)
        && id.Length is >= 3 and <= 40
        && id.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_');

    public static bool IsValidKeyword(string kw) =>
        !string.IsNullOrEmpty(kw)
        && kw.Length is >= 3 and <= 40
        && kw.All(c => c == ' ' || c == '-' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'));

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max);

    private static List<RawSuggestion>? TryParseSuggestions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var json = raw.Substring(start, end - start + 1);
        try
        {
            var dto = JsonSerializer.Deserialize<RawEnvelope>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return dto?.Suggestions;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class RawEnvelope
    {
        public List<RawSuggestion>? Suggestions { get; set; }
    }

    private sealed class RawSuggestion
    {
        public string TopicId { get; set; } = string.Empty;
        public List<string>? SeedKeywords { get; set; }
        public string? Rationale { get; set; }
    }
}
