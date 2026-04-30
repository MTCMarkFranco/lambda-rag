namespace LambdaRag.Core.Domain;

/// <summary>
/// A precise location inside a source document. Drives traceability and markup.
/// All offsets are character offsets (UTF-16 .NET semantics) into the parsed
/// canonical text. PDF page numbers are 1-based.
/// </summary>
public sealed record SourceSpan(
    string DocumentId,
    int CharStart,
    int CharLength,
    int? PageNumber,
    string? HeadingPath)
{
    public int CharEnd => CharStart + CharLength;

    public static readonly SourceSpan Unknown =
        new("(unknown)", 0, 0, null, null);
}
