using System.Text.Json.Nodes;
using LambdaRag.Indexing.Abstractions;

namespace LambdaRag.Indexing.InMemory;

/// <summary>
/// Per-document index that pre-extracts string values from each section
/// so signature-index lookups don't re-traverse JSON repeatedly. Build
/// once per ProjectedDocument; the runtime evaluator can ask
/// "does any section have field X = Y?" in O(1).
/// </summary>
public sealed class InMemoryDocumentSectionIndex : IDocumentSectionIndex
{
    public int SectionCount => _sectionCount;

    private int _sectionCount;
    // input1.path -> set of distinct literal values
    private readonly Dictionary<string, HashSet<string>> _values = new(StringComparer.Ordinal);
    // input1.path -> all concatenated text values, joined for substring search
    private readonly Dictionary<string, List<string>> _texts = new(StringComparer.Ordinal);

    public void Build(IReadOnlyList<JsonNode> sections)
    {
        _sectionCount = 0;
        _values.Clear();
        _texts.Clear();
        foreach (var node in sections)
        {
            _sectionCount++;
            if (node is not JsonObject obj) continue;
            CollectStrings(obj, "input1");
        }
    }

    public IReadOnlyCollection<string> ValuesForField(string fieldPath)
        => _values.TryGetValue(fieldPath, out var set) ? set : [];

    public bool ContainsEquality(string fieldPath, string literal)
        => _values.TryGetValue(fieldPath, out var set) && set.Contains(literal);

    public bool ContainsSubstring(string fieldPath, string literal)
    {
        if (!_texts.TryGetValue(fieldPath, out var texts)) return false;
        foreach (var t in texts)
        {
            if (t.Contains(literal, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private void CollectStrings(JsonObject obj, string prefix)
    {
        foreach (var (key, child) in obj)
        {
            var path = prefix + "." + key;
            switch (child)
            {
                case JsonValue jv when jv.TryGetValue<string>(out var s):
                    if (!_values.TryGetValue(path, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        _values[path] = set;
                    }
                    set.Add(s);
                    if (!_texts.TryGetValue(path, out var texts))
                    {
                        texts = new List<string>();
                        _texts[path] = texts;
                    }
                    texts.Add(s);
                    break;
                case JsonObject inner:
                    CollectStrings(inner, path);
                    break;
            }
        }
    }
}
