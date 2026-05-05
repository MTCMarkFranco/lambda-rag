using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Semantic;

namespace LambdaRag.Authoring.Embeddings;

/// <summary>
/// Decorator over an inner <see cref="ISemanticVectorStore"/> that lazily
/// embeds section vectors on first miss using an injected
/// <see cref="IRuleEmbedder"/>. Closes the gap that issue #69 identified:
/// when a rule's applicability widens beyond what
/// <see cref="ProjectionEmbedder"/> pre-vectorised at projection time, the
/// raw <see cref="InMemorySemanticVectorStore"/> throws
/// <c>"no precomputed vector for section ..."</c> and the verdict bubbles up
/// as <c>Error</c> instead of a real <c>Pass</c>/<c>Fail</c>.
///
/// Determinism contract:
///   • The underlying <see cref="IRuleEmbedder"/> is itself deterministic
///     (e.g. <see cref="AzureFoundryEmbeddingProvider"/> L2-normalises every
///     vector and writes it through a hash-keyed <see cref="FileBackedEmbeddingCache"/>).
///   • Two runs hitting the same JIT path against the same model + text
///     therefore produce byte-identical vectors.
///   • Concept lookups are passed straight through to the inner store —
///     concepts are authored offline by <see cref="RuleSetEmbedder"/> and
///     must already be present at evaluation time.
///   • Replay-only environments — i.e. those without a configured
///     embedder — should not wrap their store in this decorator. A missing
///     section vector in replay mode is a real audit failure (snapshot
///     and ruleset are out of sync) and must throw loudly.
///
/// Concurrency:
///   • Backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/> for the
///     section-text map so the store is safe to share across the
///     <see cref="AsyncLocal{T}"/>-based <see cref="VectorStoreAccessor"/>.
///   • Mutating <c>_inner</c> via <see cref="InMemorySemanticVectorStore.AddSection"/>
///     is not concurrency-safe by itself, but the engine evaluates one
///     section at a time per rule and the JIT path lands inside a single
///     RulesEngine call — repeated lookups for the same id resolve through
///     the inner dictionary on subsequent hits.
/// </summary>
public sealed class JitEmbeddingSemanticVectorStore : ISemanticVectorStore
{
    private readonly InMemorySemanticVectorStore _inner;
    private readonly IRuleEmbedder _embedder;
    private readonly ConcurrentDictionary<string, string> _sectionText;
    private readonly object _writeGate = new();

    /// <summary>Number of sections that were actually embedded on demand.</summary>
    public int JitEmbedCount { get; private set; }

    public JitEmbeddingSemanticVectorStore(
        InMemorySemanticVectorStore inner,
        IRuleEmbedder embedder,
        IDictionary<string, string>? sectionTexts = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _sectionText = sectionTexts is null
            ? new ConcurrentDictionary<string, string>(StringComparer.Ordinal)
            : new ConcurrentDictionary<string, string>(sectionTexts, StringComparer.Ordinal);
    }

    public string ModelId => _inner.ModelId;
    public int Dimensions => _inner.Dimensions;

    /// <summary>
    /// Register the section-id → section-text map for an entire projected
    /// document. Idempotent — repeated registrations of the same id with
    /// the same text are a no-op; conflicting text overwrites (last write
    /// wins) which mirrors <see cref="ProjectionEmbedder"/> semantics.
    ///
    /// Heading-only sections (those whose body <c>text</c> is empty but
    /// which carry a non-empty <c>heading</c> or <c>heading_path</c>) fall
    /// back to the heading text. This is what unblocks the bulk of the #69
    /// regression on real architecture documents — a section titled
    /// "Implementation View" with no body still carries semantic signal
    /// through its heading and must not collapse to an <c>Error</c> verdict.
    /// </summary>
    public void RegisterSectionTexts(ProjectedDocument document)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        foreach (var (id, text, heading) in EnumerateSectionsWithHeading(document.Graph))
        {
            var effective = !string.IsNullOrWhiteSpace(text) ? text
                : !string.IsNullOrWhiteSpace(heading) ? heading
                : null;
            if (effective is null) continue;
            _sectionText[id] = effective;
        }
    }

    /// <summary>
    /// Register a single section's text. Useful for tests and for callers
    /// that build their text map outside <see cref="ProjectedDocument"/>.
    /// </summary>
    public void RegisterSectionText(string sectionId, string text)
    {
        if (string.IsNullOrWhiteSpace(sectionId)) throw new ArgumentException("sectionId required", nameof(sectionId));
        if (string.IsNullOrWhiteSpace(text)) return;
        _sectionText[sectionId] = text;
    }

    public bool TryGetSection(string sectionId, out IReadOnlyList<float> vector)
    {
        if (_inner.TryGetSection(sectionId, out vector!)) return true;

        if (!_sectionText.TryGetValue(sectionId, out var text) || string.IsNullOrWhiteSpace(text))
        {
            vector = null!;
            return false;
        }

        // RulesEngine custom functions are synchronous, so we have to bridge
        // the async embedder here. Two safety nets keep this robust:
        //   1. The embedder's own cache short-circuits identical text — a
        //      repeat JIT for the same content is a pure dictionary read.
        //   2. We re-check the inner store under the write gate so concurrent
        //      JIT calls for the same section collapse to a single embed.
        lock (_writeGate)
        {
            if (_inner.TryGetSection(sectionId, out vector!)) return true;

            var vec = _embedder.EmbedAsync(text).ConfigureAwait(false).GetAwaiter().GetResult();
            _inner.AddSection(sectionId, vec);
            JitEmbedCount++;
            vector = vec;
            return true;
        }
    }

    public bool TryGetConcept(string concept, out IReadOnlyList<float> vector)
        => _inner.TryGetConcept(concept, out vector!);

    /// <summary>
    /// Walks the projection graph the same way <see cref="ProjectionEmbedder.EnumerateSections"/>
    /// does, but also yields the optional <c>heading</c> field. Used to
    /// build a richer text map that still has something to embed when a
    /// section is heading-only.
    /// </summary>
    private static IEnumerable<(string Id, string? Text, string? Heading)> EnumerateSectionsWithHeading(JsonNode? root)
    {
        if (root is null) yield break;
        var stack = new Stack<JsonNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case JsonObject obj:
                    if (obj["id"] is JsonValue idVal && idVal.TryGetValue<string>(out var id))
                    {
                        string? text = obj["text"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : null;
                        string? heading = obj["heading"] is JsonValue hv && hv.TryGetValue<string>(out var h) ? h
                            : obj["heading_path"] is JsonValue hpv && hpv.TryGetValue<string>(out var hp) ? hp
                            : null;
                        if (text is not null || heading is not null)
                            yield return (id, text, heading);
                    }
                    foreach (var prop in obj)
                    {
                        if (prop.Value is JsonObject or JsonArray) stack.Push(prop.Value);
                    }
                    break;
                case JsonArray arr:
                    foreach (var item in arr)
                    {
                        if (item is JsonObject or JsonArray) stack.Push(item!);
                    }
                    break;
            }
        }
    }
}
