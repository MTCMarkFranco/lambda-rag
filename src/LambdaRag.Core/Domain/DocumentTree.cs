using System.Text.Json.Nodes;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Core.Domain;

/// <summary>
/// Hierarchical section tree over a <see cref="ParsedDocument"/>. Inspired
/// by VectifyAI/PageIndex — but built deterministically from the parser's
/// existing heading structure, with no LLM in the loop.
///
/// The tree is an OFFLINE, FINGERPRINTED artifact:
///   • same source bytes + same parser + same builder version
///       → byte-identical <see cref="Fingerprint"/>
///   • node_ids are stable location tokens (<c>n_</c> + zero-padded hex of
///     the section's start offset in canonical text) so rules can reference
///     a section without depending on heading text.
///
/// Nothing in this record is intended to be evaluated by an LLM at runtime.
/// It exists as a candidate anchor primitive for selectors and as a scoping
/// input for fact extraction. Runtime evaluation stays deterministic C#.
/// </summary>
public sealed record DocumentTree(
    ContentHash SourceId,
    string BuilderId,
    string BuilderVersion,
    TreeNode Root,
    ContentHash Fingerprint);

/// <summary>
/// A single node in the <see cref="DocumentTree"/>. Every node covers a
/// half-open <c>[StartOffset, EndOffset)</c> range of the parent
/// <see cref="ParsedDocument.CanonicalText"/> — the range extends to just
/// before the next sibling or ancestor heading.
///
/// <see cref="BlockIds"/> lists the <see cref="ContentBlock.Id"/> values
/// that live directly under this heading (i.e. before any child heading).
/// Blocks nested under deeper headings appear in child nodes' block lists,
/// not here.
///
/// Root node has <see cref="HeadingLevel"/> = 0 and <see cref="Title"/>
/// equal to the document's title metadata (or empty string).
/// </summary>
public sealed record TreeNode(
    string NodeId,
    string Title,
    int HeadingLevel,
    int StartOffset,
    int EndOffset,
    IReadOnlyList<string> BlockIds,
    IReadOnlyList<TreeNode> Children)
{
    /// <summary>Stable node-id format: <c>n_</c> + 8-hex-zero-padded start offset.</summary>
    public static string MakeId(int startOffset)
        => $"n_{startOffset:x8}";
}
