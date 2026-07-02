using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Core.Facts;

/// <summary>
/// Pillar 12 (#153) — signed per-document artifact holding the LLM
/// classifier's per-section fact assignments. Content-addressed via
/// <see cref="ComputeFingerprint"/>; a load-time mismatch fails loudly
/// (see <see cref="SectionFactSidecarMismatchException"/>) so a stale
/// cache can never silently deliver wrong verdicts.
///
/// The typical on-disk shape (indented JSON, LF newlines) matches the
/// contract in <c>prompt-contracts/pillar-12-fact-projection.md</c>. See
/// <see cref="Sections"/> for the flat per-section fact map — nulls are
/// omitted on serialization via <see cref="CanonicalJson.Options"/>.
/// </summary>
public sealed record SectionFactSidecar(
    string SidecarVersion,
    string DocumentId,
    string FactSchemaId,
    string FactSchemaHash,
    string ModelId,
    string PromptHash,
    string GeneratedAt,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Sections)
{
    /// <summary>Optional Foundry model snapshot (e.g. <c>2026-06-15</c>).</summary>
    public string? ModelSnapshot { get; init; }

    /// <summary>
    /// Optional (docHash × schema × modelId × prompt) rollup — repeated
    /// verbatim on disk for at-a-glance audit; recomputed on load to
    /// verify the sidecar has not drifted.
    /// </summary>
    public string? Fingerprint { get; init; }

    /// <summary>
    /// Optional rule-scope map (rule id → section ids). Emitted by Pass 1
    /// as a byproduct of extraction; Pass 2 falls back to rescanning
    /// <see cref="Sections"/> against <see cref="Rule.RequiredFacts"/> when
    /// omitted.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? RuleScope { get; init; }

    /// <summary>
    /// Non-fatal warnings emitted at extraction time (e.g. hallucinated
    /// value dropped because <c>supporting_quote</c> failed the substring
    /// check). Deterministic given the same input.
    /// </summary>
    public IReadOnlyList<string>? Warnings { get; init; }

    /// <summary>
    /// Compute the sidecar's identity hash. Any drift in doc bytes, fact
    /// schema, model id, or prompt hash produces a different fingerprint;
    /// section ordering is folded in so a projector rev also invalidates.
    /// </summary>
    public static ContentHash ComputeFingerprint(
        string documentId,
        string factSchemaHash,
        string modelId,
        string promptHash,
        string sectionOrderingHash)
        => ContentHash.Compose(
            documentId,
            factSchemaHash,
            modelId,
            promptHash,
            sectionOrderingHash);
}

/// <summary>
/// Thrown at sidecar-load time when the cached fingerprint does not match
/// the current (doc, schema, model, prompt, ordering) tuple. The message
/// names the drifted component so the operator can rerun with
/// <c>--refresh-facts</c> or pin the drifted component.
/// </summary>
public sealed class SectionFactSidecarMismatchException : InvalidOperationException
{
    public SectionFactSidecarMismatchException(string message) : base(message) { }
}
