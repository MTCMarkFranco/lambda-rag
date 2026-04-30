using System.Text.Json.Nodes;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Core.Domain;

/// <summary>
/// A typed graph projected from a ParsedDocument by an IDocumentProjector.
/// The graph is JSON-serializable so that selectors written in our path DSL
/// can match against it, and so the same graph drives both evaluation and
/// audit-trail rendering.
///
/// Every node optionally carries a SourceSpan so the markup engine can
/// trace verdicts back to exact byte ranges.
/// </summary>
public sealed record ProjectedDocument(
    ContentHash SourceId,
    string ProjectorId,
    string ProjectorVersion,
    JsonObject Graph,
    IReadOnlyDictionary<string, SourceSpan> SpanMap)
{
    /// <summary>
    /// Cache key for projection — same source bytes + same projector +
    /// same model + same prompt = byte-identical projection.
    /// </summary>
    public static ContentHash CacheKey(
        ContentHash sourceId,
        string projectorId,
        string projectorVersion,
        string modelId,
        ContentHash promptHash)
        => ContentHash.Compose(
            sourceId.Value,
            projectorId,
            projectorVersion,
            modelId,
            promptHash.Value);
}
