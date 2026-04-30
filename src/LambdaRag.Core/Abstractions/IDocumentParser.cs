using System.Text.Json.Nodes;
using LambdaRag.Core.Domain;

namespace LambdaRag.Core.Abstractions;

/// <summary>Parses a SourceDocument into our canonical ParsedDocument form.</summary>
public interface IDocumentParser
{
    bool CanParse(SourceDocument source);
    Task<ParsedDocument> ParseAsync(string filePath, CancellationToken ct = default);
}

/// <summary>
/// Projects a ParsedDocument to a typed JSON graph that selectors and
/// lambdas operate on. Implementations must be deterministic: same input +
/// same projector version = byte-identical graph.
/// </summary>
public interface IDocumentProjector
{
    string Id { get; }
    string Version { get; }
    string Domain { get; }
    JsonObject Schema { get; }
    Task<ProjectedDocument> ProjectAsync(ParsedDocument parsed, CancellationToken ct = default);
}

/// <summary>Matches a Selector against a ProjectedDocument, returning matched sub-graphs with spans.</summary>
public interface ISelectorMatcher
{
    IReadOnlyList<MatchedSection> Match(Selectors.Selector selector, ProjectedDocument document);
}

public sealed record MatchedSection(
    string Path,
    JsonNode Node,
    SourceSpan Span);
