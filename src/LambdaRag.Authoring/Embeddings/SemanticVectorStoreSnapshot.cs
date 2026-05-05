using System.Text;
using System.Text.Json;
using LambdaRag.Core.Semantic;

namespace LambdaRag.Authoring.Embeddings;

/// <summary>
/// Snapshot serialisation for an <see cref="ISemanticVectorStore"/>. The
/// JSON is small + diff-friendly (just keys + side-car file pointers); the
/// actual float32 vectors live in the file-backed cache. This is the
/// shape persisted alongside rulesets / projected documents so a replay
/// run can hydrate the in-memory store with zero cloud calls.
///
/// JSON layout:
/// <code>
/// {
///   "modelId": "azure-foundry:text-embedding-3-large/3072",
///   "dimensions": 3072,
///   "sections": { "s_00000003": "&lt;sha256-hex&gt;", ... },
///   "concepts": { "works made for hire": "&lt;sha256-hex&gt;", ... }
/// }
/// </code>
/// </summary>
public static class SemanticVectorStoreSnapshot
{
    public sealed record SnapshotPayload(
        string ModelId,
        int Dimensions,
        IDictionary<string, string> Sections,
        IDictionary<string, string> Concepts);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void WriteJson(InMemorySemanticVectorStore store, string snapshotPath)
    {
        var sectionKeys = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var conceptKeys = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (sid, _) in EnumerateSections(store))
            sectionKeys[sid] = FileBackedEmbeddingCache.ComputeKey(store.ModelId, sid);
        foreach (var (concept, _) in EnumerateConcepts(store))
            conceptKeys[concept] = FileBackedEmbeddingCache.ComputeKey(store.ModelId, concept);

        var payload = new SnapshotPayload(store.ModelId, store.Dimensions, sectionKeys, conceptKeys);
        var json = JsonSerializer.Serialize(payload, Options);
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        File.WriteAllText(snapshotPath, json, new UTF8Encoding(false));
    }

    public static InMemorySemanticVectorStore ReadJson(string snapshotPath, FileBackedEmbeddingCache cache)
    {
        var json = File.ReadAllText(snapshotPath);
        var payload = JsonSerializer.Deserialize<SnapshotPayload>(json, Options)
            ?? throw new InvalidOperationException($"Snapshot at {snapshotPath} is empty or invalid.");

        var sections = new Dictionary<string, IReadOnlyList<float>>(StringComparer.Ordinal);
        foreach (var (sid, _) in payload.Sections)
        {
            if (!cache.TryRead(sid, out var vec))
                throw new InvalidOperationException(
                    $"Snapshot {snapshotPath} references section '{sid}' but its vector is missing from the cache. " +
                    "Cache and snapshot are out of sync — re-run authoring or fail loud.");
            sections[sid] = vec;
        }

        var concepts = new Dictionary<string, IReadOnlyList<float>>(StringComparer.Ordinal);
        foreach (var (concept, _) in payload.Concepts)
        {
            if (!cache.TryRead(concept, out var vec))
                throw new InvalidOperationException(
                    $"Snapshot {snapshotPath} references concept '{concept}' but its vector is missing from the cache.");
            concepts[concept] = vec;
        }

        return new InMemorySemanticVectorStore(payload.ModelId, payload.Dimensions, sections, concepts);
    }

    private static IEnumerable<KeyValuePair<string, IReadOnlyList<float>>> EnumerateSections(
        InMemorySemanticVectorStore store) => Reflect(store, "_sections");

    private static IEnumerable<KeyValuePair<string, IReadOnlyList<float>>> EnumerateConcepts(
        InMemorySemanticVectorStore store) => Reflect(store, "_concepts");

    private static IEnumerable<KeyValuePair<string, IReadOnlyList<float>>> Reflect(object store, string field)
    {
        var f = store.GetType().GetField(field,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {field} not found on InMemorySemanticVectorStore.");
        var dict = (System.Collections.IDictionary)f.GetValue(store)!;
        foreach (System.Collections.DictionaryEntry entry in dict)
            yield return new KeyValuePair<string, IReadOnlyList<float>>(
                (string)entry.Key,
                (IReadOnlyList<float>)entry.Value!);
    }
}
