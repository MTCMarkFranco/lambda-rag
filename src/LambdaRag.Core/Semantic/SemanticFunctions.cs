namespace LambdaRag.Core.Semantic;

/// <summary>
/// Static functions registered with RulesEngine via <c>ReSettings.CustomTypes</c>
/// so rule lambda expressions can call them directly. The names and signatures
/// here are part of the rule artifact contract — do not rename or change
/// argument order without bumping the ruleset major version.
///
/// The runtime is intentionally trivial: each call is a cosine-similarity
/// compare over precomputed vectors plus a deterministic <c>&gt;=</c>
/// threshold tie-break. Determinism, repeatability and idempotency are
/// preserved — same inputs always produce the same boolean.
///
/// Vectors are resolved through the ambient <see cref="VectorStoreAccessor.Current"/>
/// which is set per-evaluation by the engine. This avoids threading the store
/// through every RulesEngine input shape (RulesEngine custom-type methods
/// must be static and have no DI awareness).
/// </summary>
public static class SemanticFunctions
{
    /// <summary>
    /// Marker prefix on every <see cref="InvalidOperationException"/> raised
    /// from inside this class. The evaluator recognises this prefix and
    /// surfaces such failures as <c>VerdictOutcome.Error</c> instead of
    /// <c>Fail</c> — missing vectors must never masquerade as a "rule said
    /// false" outcome.
    /// </summary>
    public const string ErrorMarker = "lambda-rag.semantic:";

    /// <summary>
    /// Registered as <c>ContainsMeaning(sectionId, concept, threshold)</c>.
    /// Returns true iff <c>cosine(sectionVec, conceptVec) &gt;= threshold</c>.
    /// Throws when either vector is missing — replay must be loud.
    /// </summary>
    public static bool ContainsMeaning(string sectionId, string concept, double threshold)
    {
        var store = VectorStoreAccessor.RequireCurrent();
        if (!store.TryGetSection(sectionId, out var sectionVec))
            throw new InvalidOperationException($"{ErrorMarker} no precomputed vector for section '{sectionId}'.");
        if (!store.TryGetConcept(concept, out var conceptVec))
            throw new InvalidOperationException($"{ErrorMarker} no precomputed vector for concept '{concept}'.");
        return Cosine(sectionVec, conceptVec) >= threshold;
    }

    /// <summary>
    /// Registered as <c>MatchesAnyMeaning(sectionId, "concept1|concept2|...", threshold)</c>.
    /// True iff at least one concept's cosine ≥ threshold. Concepts are
    /// pipe-delimited and must each have a precomputed vector.
    /// </summary>
    public static bool MatchesAnyMeaning(string sectionId, string pipeDelimitedConcepts, double threshold)
    {
        var store = VectorStoreAccessor.RequireCurrent();
        if (!store.TryGetSection(sectionId, out var sectionVec))
            throw new InvalidOperationException($"{ErrorMarker} no precomputed vector for section '{sectionId}'.");

        foreach (var concept in pipeDelimitedConcepts.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!store.TryGetConcept(concept, out var conceptVec))
                throw new InvalidOperationException($"{ErrorMarker} no precomputed vector for concept '{concept}'.");
            if (Cosine(sectionVec, conceptVec) >= threshold)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Cosine of two same-length float vectors. Computed in <c>double</c> to
    /// limit accumulation error; the inputs are typically L2-normalised so
    /// the denominator collapses to 1, but we compute the full form for
    /// correctness against arbitrary vectors.
    /// </summary>
    public static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a is null || b is null || a.Count == 0 || a.Count != b.Count) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            double ai = a[i], bi = b[i];
            dot += ai * bi;
            na  += ai * ai;
            nb  += bi * bi;
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

/// <summary>
/// Ambient holder for the <see cref="ISemanticVectorStore"/> visible to
/// <see cref="SemanticFunctions"/> during a single evaluation pass. The
/// engine pushes the store before invoking RulesEngine and clears it after,
/// so concurrent evaluations on different stores are isolated per
/// <see cref="AsyncLocal{T}"/>.
/// </summary>
public static class VectorStoreAccessor
{
    private static readonly AsyncLocal<ISemanticVectorStore?> _current = new();

    /// <summary>The store visible to the current async flow, or null if none has been pushed.</summary>
    public static ISemanticVectorStore? Current => _current.Value;

    internal static ISemanticVectorStore RequireCurrent() =>
        _current.Value ?? throw new InvalidOperationException(
            "SemanticFunctions invoked outside an evaluation scope. " +
            "Use VectorStoreAccessor.Push(...) to make the store visible.");

    /// <summary>
    /// Make <paramref name="store"/> visible to <see cref="SemanticFunctions"/>
    /// inside the returned scope. Disposing the scope restores the previous
    /// store. Safe for nesting; safe across <c>await</c>.
    /// </summary>
    public static IDisposable Push(ISemanticVectorStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var previous = _current.Value;
        _current.Value = store;
        return new PopOnDispose(previous);
    }

    private sealed class PopOnDispose(ISemanticVectorStore? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = previous;
        }
    }
}
