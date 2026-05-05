namespace LambdaRag.Core.Semantic;

/// <summary>
/// Read-only lookup for precomputed embedding vectors keyed by a stable
/// content id. The key shape is intentionally opaque to callers — the
/// store decides how to derive it (typically a section id from the
/// projection, or a content hash for rule descriptions).
///
/// Implementations must be:
///   • Pure — no I/O at evaluation time after the store has been hydrated.
///   • Deterministic — the same key returns the same vector, byte-for-byte,
///     across runs. Vectors are written once at authoring / projection time.
///   • Idempotent — repeated lookups for the same key return identical results.
///
/// The runtime evaluator (RulesEngine custom function <c>ContainsMeaning</c>)
/// reads from this store. It must never trigger a remote embedding call —
/// missing vectors throw, they do not lazily backfill, so replay against a
/// snapshotted ruleset + projection is guaranteed to be cloud-free.
/// </summary>
public interface ISemanticVectorStore
{
    /// <summary>
    /// The id of the embedding model the vectors in this store were produced
    /// with (e.g. <c>azure-openai:text-embedding-3-large</c>). Stored on every
    /// rule + projection artifact so audit can detect drift.
    /// </summary>
    string ModelId { get; }

    /// <summary>The dimensionality of every vector in the store. Constant for the lifetime of the store.</summary>
    int Dimensions { get; }

    /// <summary>
    /// Lookup a section vector by section id. Returns false if the section
    /// has no precomputed vector — callers must treat this as a hard error
    /// (no silent re-embed).
    /// </summary>
    bool TryGetSection(string sectionId, out IReadOnlyList<float> vector);

    /// <summary>
    /// Lookup a concept vector by concept text. Implementations key on the
    /// SHA-256 of the normalised concept string + model id. Returns false
    /// if the concept was not pre-embedded; callers must treat this as a
    /// hard error so unknown concepts fail loud at runtime instead of
    /// silently calling out to the cloud.
    /// </summary>
    bool TryGetConcept(string concept, out IReadOnlyList<float> vector);
}

/// <summary>
/// Sentinel store that throws on every lookup. Used by tests / sample
/// configurations that don't wire a real store; ensures a clear error
/// instead of a silent zero-cosine result.
/// </summary>
public sealed class NotConfiguredSemanticVectorStore : ISemanticVectorStore
{
    public string ModelId => "not-configured";
    public int Dimensions => 0;

    public bool TryGetSection(string sectionId, out IReadOnlyList<float> vector)
        => throw new InvalidOperationException(
            $"Semantic vector store is not configured. Cannot resolve section '{sectionId}'. " +
            "Wire an ISemanticVectorStore implementation before evaluating rules that use ContainsMeaning.");

    public bool TryGetConcept(string concept, out IReadOnlyList<float> vector)
        => throw new InvalidOperationException(
            $"Semantic vector store is not configured. Cannot resolve concept '{concept}'. " +
            "Wire an ISemanticVectorStore implementation before evaluating rules that use ContainsMeaning.");
}

/// <summary>
/// Simple in-memory store backed by two dictionaries. Suitable for tests and
/// for runtime use after the rule loader / projector hydrate the maps from
/// the snapshotted JSON artifacts.
/// </summary>
public sealed class InMemorySemanticVectorStore : ISemanticVectorStore
{
    private readonly Dictionary<string, IReadOnlyList<float>> _sections;
    private readonly Dictionary<string, IReadOnlyList<float>> _concepts;

    public InMemorySemanticVectorStore(
        string modelId,
        int dimensions,
        IDictionary<string, IReadOnlyList<float>>? sections = null,
        IDictionary<string, IReadOnlyList<float>>? concepts = null)
    {
        ModelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        Dimensions = dimensions;
        _sections = sections is null
            ? new Dictionary<string, IReadOnlyList<float>>(StringComparer.Ordinal)
            : new Dictionary<string, IReadOnlyList<float>>(sections, StringComparer.Ordinal);
        _concepts = concepts is null
            ? new Dictionary<string, IReadOnlyList<float>>(StringComparer.Ordinal)
            : new Dictionary<string, IReadOnlyList<float>>(concepts, StringComparer.Ordinal);
    }

    public string ModelId { get; }
    public int Dimensions { get; }

    public bool TryGetSection(string sectionId, out IReadOnlyList<float> vector)
        => _sections.TryGetValue(sectionId, out vector!);

    public bool TryGetConcept(string concept, out IReadOnlyList<float> vector)
        => _concepts.TryGetValue(concept, out vector!);

    /// <summary>Add or overwrite a section vector. Authoring / projection time only.</summary>
    public void AddSection(string sectionId, IReadOnlyList<float> vector) => _sections[sectionId] = vector;

    /// <summary>Add or overwrite a concept vector. Authoring time only.</summary>
    public void AddConcept(string concept, IReadOnlyList<float> vector) => _concepts[concept] = vector;
}
