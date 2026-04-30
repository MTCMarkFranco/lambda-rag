using LambdaRag.Core.Hashing;

namespace LambdaRag.Core.Domain;

public enum SourceDocumentKind { Pdf, Docx, Markdown, Text, Html, Unknown }

/// <summary>
/// An immutable handle to the bytes of a source document. The id is the
/// content hash of the bytes — same bytes always produce the same id, which
/// is the foundation of the idempotency story.
/// </summary>
public sealed record SourceDocument(
    ContentHash Id,
    string FileName,
    SourceDocumentKind Kind,
    long ByteLength,
    DateTimeOffset IngestedAt)
{
    public static SourceDocument FromFile(string path, DateTimeOffset? now = null)
    {
        var hash = ContentHash.OfFile(path);
        var fi = new FileInfo(path);
        var kind = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pdf" => SourceDocumentKind.Pdf,
            ".docx" => SourceDocumentKind.Docx,
            ".md" or ".markdown" => SourceDocumentKind.Markdown,
            ".txt" => SourceDocumentKind.Text,
            ".html" or ".htm" => SourceDocumentKind.Html,
            _ => SourceDocumentKind.Unknown,
        };
        return new SourceDocument(hash, fi.Name, kind, fi.Length, now ?? DateTimeOffset.UtcNow);
    }
}
