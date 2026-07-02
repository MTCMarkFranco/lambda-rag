using LambdaRag.Core.Domain;

namespace LambdaRag.Core.Facts;

/// <summary>
/// Pillar 12 (#153) — Pass-1 fact extractor. Implementations decide how the
/// per-section fact bags are populated (LLM classifier, deterministic mock,
/// canned sidecar for tests). The evaluator does NOT know or care.
///
/// Implementations MUST be deterministic across the sidecar fingerprint
/// tuple — i.e. same doc + same schema + same model + same prompt
/// produces byte-identical sidecars — so <see cref="EvaluationService"/>
/// can replay a cached sidecar without recomputing.
/// </summary>
public interface IFactExtractor
{
    /// <summary>Model identifier folded into the sidecar fingerprint.</summary>
    string ModelId { get; }

    /// <summary>SHA-256 (or opaque hash) of the Pass-1 prompt + normalizer version.</summary>
    string PromptHash { get; }

    /// <summary>
    /// Extract per-section fact bags for <paramref name="document"/> against
    /// <paramref name="schema"/>. Returning the sidecar unconditionally
    /// implies the extractor handled any caching itself; the evaluator does
    /// not persist sidecars.
    /// </summary>
    Task<SectionFactSidecar> ExtractAsync(
        ProjectedDocument document,
        FactSchema schema,
        CancellationToken ct = default);
}
