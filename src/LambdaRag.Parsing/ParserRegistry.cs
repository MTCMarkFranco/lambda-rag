using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;

namespace LambdaRag.Parsing;

/// <summary>
/// Resolves the correct <see cref="IDocumentParser"/> for a given
/// <see cref="SourceDocument"/> from the registered set, and provides a
/// convenience <see cref="ParseAsync"/> entry-point.
/// </summary>
public sealed class ParserRegistry
{
    private readonly IReadOnlyList<IDocumentParser> _parsers;

    public ParserRegistry(IEnumerable<IDocumentParser> parsers)
    {
        _parsers = parsers.ToList();
    }

    /// <summary>Returns the first parser that claims it can handle the source, or null.</summary>
    public IDocumentParser? Resolve(SourceDocument source)
        => _parsers.FirstOrDefault(p => p.CanParse(source));

    /// <summary>
    /// Convenience method: creates a <see cref="SourceDocument"/> handle from
    /// the file path, resolves the parser, and delegates.
    /// </summary>
    public Task<ParsedDocument> ParseAsync(string filePath, CancellationToken ct = default)
    {
        var source = SourceDocument.FromFile(filePath);
        var parser = Resolve(source)
            ?? throw new NotSupportedException(
                $"No parser registered for document kind '{source.Kind}' (file: {filePath}).");
        return parser.ParseAsync(filePath, ct);
    }
}
