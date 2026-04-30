using LambdaRag.Projection;

namespace LambdaRag.Authoring;

/// <summary>
/// Authoring-time service that proposes topic-map additions for "unknown"
/// sections surfaced by the projector. NEVER called at runtime — runtime
/// stays 100% deterministic. Any suggestion must be reviewed and committed
/// to the topic-map JSON (or a draft) before it can affect projections.
///
/// Two implementations ship today:
///   * <see cref="KeywordHeuristicTopicSuggester"/> — deterministic, no LLM.
///     Falls back to nearest existing topic by keyword overlap.
///   * <see cref="LlmTopicSuggester"/> — uses an injected IChatClient to
///     propose new topic IDs + seed keywords, then runs deterministic
///     post-validation (snake_case id, min 3 keywords, no PII leakage).
/// </summary>
public interface ITopicSuggester
{
    /// <summary>
    /// Suggest one or more topics for a section the projector classified as
    /// "unknown". Returns suggestions in deterministic, ranked order.
    /// </summary>
    Task<IReadOnlyList<TopicSuggestion>> SuggestAsync(
        TopicSuggestionRequest request,
        CancellationToken ct = default);
}

public sealed record TopicSuggestionRequest(
    string Heading,
    string Body,
    TopicMap CurrentMap,
    string? DocKind = null);

public sealed record TopicSuggestion(
    string TopicId,
    IReadOnlyList<string> SeedKeywords,
    bool IsExisting,
    double Confidence,
    string Rationale);
