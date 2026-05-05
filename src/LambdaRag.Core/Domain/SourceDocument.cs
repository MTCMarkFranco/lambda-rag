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

        // Text-kind documents must hash deterministically across OSes: normalise
        // CRLF/CR -> LF and strip a UTF-8 BOM before hashing. Binary kinds keep
        // raw-byte hashing so their on-disk bytes remain the source of truth.
        ContentHash hash;
        long byteLength;
        if (kind is SourceDocumentKind.Markdown or SourceDocumentKind.Text or SourceDocumentKind.Html)
        {
            var raw = File.ReadAllText(path);
            var normalised = NormaliseTextForHashing(raw);
            var bytes = System.Text.Encoding.UTF8.GetBytes(normalised);
            hash = ContentHash.OfBytes(bytes);
            byteLength = bytes.LongLength;
        }
        else
        {
            hash = ContentHash.OfFile(path);
            byteLength = fi.Length;
        }

        return new SourceDocument(hash, fi.Name, kind, byteLength, now ?? DateTimeOffset.UtcNow);
    }

    private static string NormaliseTextForHashing(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text[0] == '\uFEFF') text = text.Substring(1);
        return text.Replace("\r\n", "\n").Replace("\r", "\n");
    }
}
