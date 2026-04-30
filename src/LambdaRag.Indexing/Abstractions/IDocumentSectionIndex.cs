using System.Text.Json.Nodes;

namespace LambdaRag.Indexing.Abstractions;

/// <summary>
/// Per-document section index — pre-extracts the field values each
/// section exposes so the signature index lookup is O(1) per section.
/// Without this the lookup would re-traverse every section's JSON for
/// every rule. Built once per ProjectedDocument, used per-rule.
/// </summary>
public interface IDocumentSectionIndex
{
    int SectionCount { get; }

    /// <summary>Build per-section signatures from a list of section JSON nodes.</summary>
    void Build(IReadOnlyList<JsonNode> sections);

    /// <summary>
    /// Field-equality view: for a given <c>input1.path</c>, return the
    /// distinct literal values present in any indexed section.
    /// </summary>
    IReadOnlyCollection<string> ValuesForField(string fieldPath);

    /// <summary>True when at least one section has the given (field, value) pair.</summary>
    bool ContainsEquality(string fieldPath, string literal);

    /// <summary>True when at least one section's field text contains the literal substring.</summary>
    bool ContainsSubstring(string fieldPath, string literal);
}
