namespace LambdaRag.Core.Domain;

/// <summary>
/// Block-level content extracted from a source document. Each block carries
/// its source span so downstream stages (projection, markup) can map back
/// exactly to the original bytes.
/// </summary>
public sealed record ContentBlock(
    string Id,
    ContentBlockKind Kind,
    string Text,
    SourceSpan Span,
    int HeadingLevel,
    string HeadingPath);

public enum ContentBlockKind
{
    Heading,
    Paragraph,
    ListItem,
    TableCell,
    CodeBlock,
    Caption,
    Other,
}

/// <summary>
/// Canonical representation of a parsed document — the deterministic
/// intermediate form that all downstream stages consume. Two parses of the
/// same bytes by the same parser version must produce equal ParsedDocument
/// trees (modulo IDs derived from offsets).
/// </summary>
public sealed record ParsedDocument(
    SourceDocument Source,
    string CanonicalText,
    IReadOnlyList<ContentBlock> Blocks,
    IReadOnlyDictionary<string, string> Metadata);
