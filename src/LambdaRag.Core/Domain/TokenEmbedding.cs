namespace LambdaRag.Core.Domain;

/// <summary>
/// Pillar 6 — a single tokenized n-gram from a projected section, with its
/// character offsets relative to the section body text and (optionally)
/// the precomputed embedding vector used to score it against rule anchors.
///
/// The embedding may be null on the in-memory projection shape; tokens are
/// pure-code deterministic from section text + tokenizer version, while
/// embeddings are computed lazily via the JIT-cached embedder so the
/// runtime never makes an LLM call. Two runs against the same bytes
/// produce byte-identical token lists.
/// </summary>
public sealed record TokenEmbedding(
    string Text,
    int Ngram,
    int CharStart,
    int CharLength,
    IReadOnlyList<float>? Embedding = null);

/// <summary>
/// Pillar 6 — semantic-binding anchor declared on a rule. At evaluation
/// time, the engine cosine-compares <see cref="AnchorEmbedding"/> against
/// every token embedding of the candidate section; tokens whose cosine
/// meets or exceeds <see cref="Threshold"/> become *bindings* the rule's
/// lambda can reference by <see cref="Name"/>.
///
/// All fields are part of the rule fingerprint when the anchors list is
/// non-empty so a published rule cannot silently change its semantic
/// surface area.
/// </summary>
public sealed record SemanticAnchor(
    string Name,
    string AnchorText,
    IReadOnlyList<float>? AnchorEmbedding = null,
    double Threshold = 0.78,
    IReadOnlyList<int>? Ngram = null);

/// <summary>
/// Pillar 6 — a token that bound to an anchor at evaluation time, with its
/// character span (relative to the section body) and the cosine that
/// produced the binding. Every binding is evidenced in the verdict so an
/// auditor can re-derive it from the projection + ruleset bytes.
/// </summary>
public sealed record BindingRecord(
    string Anchor,
    string Matched,
    double Cosine,
    int CharStart,
    int CharLength);
