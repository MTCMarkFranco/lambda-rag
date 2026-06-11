namespace LambdaRag.Core.Semantic;

/// <summary>
/// Pillar 6 (#124) — minimal embedder abstraction used by the
/// evaluator to lazily embed individual tokens for semantic binding.
/// Lives in <c>LambdaRag.Core</c> so Evaluation can depend on it
/// without referencing Authoring; the production
/// <see cref="LambdaRag.Authoring"/> embedders adapt to this interface.
///
/// Determinism contract:
///   • Pure code, no remote call at runtime (file/in-memory cache only).
///   • Same text + same <see cref="EmbedderId"/> = byte-identical vector.
///   • <see cref="EmbedAsync"/> may persist its result to a cache but
///     must not vary by environment, time, or process.
/// </summary>
public interface ITokenEmbedder
{
    /// <summary>Stable id for the embedder; folded into cache keys.</summary>
    string EmbedderId { get; }

    /// <summary>Vector dimensionality. Constant for the lifetime of the embedder.</summary>
    int Dimensions { get; }

    /// <summary>Embed a single text fragment, deterministically.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}
