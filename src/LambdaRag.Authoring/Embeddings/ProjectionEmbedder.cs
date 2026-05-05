using System.Text.Json.Nodes;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Semantic;

namespace LambdaRag.Authoring.Embeddings;

/// <summary>
/// Walks the JSON graph of a <see cref="ProjectedDocument"/> and embeds each
/// section's body text once, populating an <see cref="InMemorySemanticVectorStore"/>
/// keyed by the section's <c>id</c> field. Designed to run after projection
/// and before evaluation — the resulting store satisfies both the evaluator's
/// applicability gate and any <c>SemanticFunctions.ContainsMeaning</c> calls
/// that take a section id as their first argument.
///
/// Determinism contract:
///   • One embedding per unique section id; duplicate ids in the same graph
///     are coalesced (last write wins, but in practice ids are unique).
///   • Empty / whitespace-only section text is skipped — the section will
///     simply not have a vector, and any rule that requires one will fail
///     loud at runtime instead of silently scoring 0.
///   • The traversal order is JSON-document order, which is stable across
///     runs because <see cref="ProjectedDocument.Graph"/> is itself
///     deterministic.
/// </summary>
public sealed class ProjectionEmbedder
{
    private readonly IRuleEmbedder _embedder;

    public ProjectionEmbedder(IRuleEmbedder embedder)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    }

    /// <summary>
    /// Add every section's vector to <paramref name="store"/>. Existing
    /// concept entries are preserved. Returns the same store for fluency.
    /// </summary>
    public async Task<InMemorySemanticVectorStore> EmbedSectionsAsync(
        ProjectedDocument document,
        InMemorySemanticVectorStore store,
        CancellationToken ct = default)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        if (store is null) throw new ArgumentNullException(nameof(store));

        foreach (var (id, text) in EnumerateSections(document.Graph))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) continue;
            var vec = await _embedder.EmbedAsync(text, ct).ConfigureAwait(false);
            store.AddSection(id, vec);
        }

        return store;
    }

    /// <summary>
    /// Yield <c>(sectionId, sectionText)</c> for every node in the graph that
    /// has both a string <c>id</c> and a string <c>text</c> field.
    /// </summary>
    public static IEnumerable<(string Id, string Text)> EnumerateSections(JsonNode? root)
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
                    if (obj["id"] is JsonValue idVal &&
                        idVal.TryGetValue<string>(out var id) &&
                        obj["text"] is JsonValue textVal &&
                        textVal.TryGetValue<string>(out var text))
                    {
                        yield return (id, text);
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
