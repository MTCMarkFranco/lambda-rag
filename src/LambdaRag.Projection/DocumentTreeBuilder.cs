using System.Text.Json.Nodes;
using LambdaRag.Core;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Projection;

/// <summary>
/// Builds a <see cref="DocumentTree"/> from a <see cref="ParsedDocument"/>.
///
/// This is a spike (issue TBD) exploring the PageIndex tree-index pattern
/// as an offline document primitive for lambda-rag. It is intentionally:
///   • pure — no I/O, no LLM, no external services
///   • deterministic — same input bytes → byte-identical tree JSON
///   • additive — nothing in the existing pipeline consumes this yet
///
/// Algorithm: walk the parsed blocks in order. Maintain a stack of open
/// heading nodes. On each heading, close nodes at the same or deeper level
/// (set their EndOffset), then push a new node at the heading's level.
/// Non-heading blocks attach to the top of the stack. At end-of-doc, close
/// any remaining open nodes at doc length.
/// </summary>
public sealed class DocumentTreeBuilder
{
    public const string BuilderIdConst = "heading-tree";
    public const string BuilderVersionConst = "0.1.0";

    public string BuilderId => BuilderIdConst;
    public string BuilderVersion => BuilderVersionConst;

    public DocumentTree Build(ParsedDocument parsed)
    {
        if (parsed is null) throw new ArgumentNullException(nameof(parsed));

        var docLength = parsed.CanonicalText?.Length ?? 0;
        var rootTitle = parsed.Metadata.TryGetValue("title", out var t) ? t : string.Empty;

        // Mutable frames while walking.
        var rootFrame = new NodeFrame(
            id: TreeNode.MakeId(0),
            title: rootTitle,
            headingLevel: 0,
            startOffset: 0);

        var stack = new Stack<NodeFrame>();
        stack.Push(rootFrame);

        foreach (var block in parsed.Blocks)
        {
            if (block.Kind == ContentBlockKind.Heading)
            {
                // Close every open node whose level is >= this heading's level.
                // Their EndOffset is where this heading begins.
                while (stack.Count > 1 && stack.Peek().HeadingLevel >= block.HeadingLevel)
                {
                    CloseTop(stack, block.Span.CharStart);
                }

                var frame = new NodeFrame(
                    id: TreeNode.MakeId(block.Span.CharStart),
                    title: block.Text,
                    headingLevel: block.HeadingLevel,
                    startOffset: block.Span.CharStart);

                // The heading block itself belongs to the new node.
                frame.BlockIds.Add(block.Id);
                stack.Push(frame);
            }
            else
            {
                stack.Peek().BlockIds.Add(block.Id);
            }
        }

        // Close everything remaining at end-of-document.
        while (stack.Count > 1)
        {
            CloseTop(stack, docLength);
        }

        // Close the root explicitly.
        rootFrame.EndOffset = docLength;

        var root = rootFrame.ToTreeNode();
        var fingerprint = ComputeFingerprint(parsed.Source.Id, root);

        return new DocumentTree(
            SourceId: parsed.Source.Id,
            BuilderId: BuilderId,
            BuilderVersion: BuilderVersion,
            Root: root,
            Fingerprint: fingerprint);
    }

    /// <summary>Emit canonical, indented JSON for a tree — used for fingerprinting and sidecars.</summary>
    public static string ToJson(DocumentTree tree)
    {
        var obj = new JsonObject
        {
            ["source_id"] = tree.SourceId.Value,
            ["builder_id"] = tree.BuilderId,
            ["builder_version"] = tree.BuilderVersion,
            ["fingerprint"] = tree.Fingerprint.Value,
            ["root"] = SerializeNode(tree.Root),
        };
        return obj.ToJsonString(CanonicalJson.Options);
    }

    // ── internals ─────────────────────────────────────────────────────────

    private static void CloseTop(Stack<NodeFrame> stack, int endOffset)
    {
        var closing = stack.Pop();
        closing.EndOffset = endOffset;
        var parent = stack.Peek();
        parent.Children.Add(closing);
    }

    private static ContentHash ComputeFingerprint(ContentHash sourceId, TreeNode root)
    {
        // Canonical structural fingerprint — depends on shape + offsets +
        // titles, not on the (indented) JSON whitespace itself. The JSON
        // itself is deterministic given CanonicalJson.Options, but hashing
        // the structural signature directly makes the intent explicit.
        return ContentHash.Compose(
            sourceId.Value,
            DocumentTreeBuilder.BuilderIdConst,
            DocumentTreeBuilder.BuilderVersionConst,
            NodeSignature(root));
    }

    private static string NodeSignature(TreeNode n)
    {
        // e.g. "n_00000000|L0|0..123|T=Chapter|[b1,b2]|(child1)(child2)"
        // Title is included so two structurally identical trees with
        // different heading text produce different fingerprints.
        var childSig = string.Concat(n.Children.Select(c => "(" + NodeSignature(c) + ")"));
        var blockSig = string.Join(",", n.BlockIds);
        return $"{n.NodeId}|L{n.HeadingLevel}|{n.StartOffset}..{n.EndOffset}|T={n.Title}|[{blockSig}]|{childSig}";
    }

    private static JsonObject SerializeNode(TreeNode n)
    {
        var obj = new JsonObject
        {
            ["node_id"] = n.NodeId,
            ["title"] = n.Title,
            ["heading_level"] = n.HeadingLevel,
            ["start_offset"] = n.StartOffset,
            ["end_offset"] = n.EndOffset,
        };
        var blockArr = new JsonArray();
        foreach (var b in n.BlockIds) blockArr.Add(b);
        obj["block_ids"] = blockArr;

        var childArr = new JsonArray();
        foreach (var c in n.Children) childArr.Add(SerializeNode(c));
        obj["children"] = childArr;

        return obj;
    }

    private sealed class NodeFrame
    {
        public string Id { get; }
        public string Title { get; }
        public int HeadingLevel { get; }
        public int StartOffset { get; }
        public int EndOffset { get; set; }
        public List<string> BlockIds { get; } = new();
        public List<NodeFrame> Children { get; } = new();

        public NodeFrame(string id, string title, int headingLevel, int startOffset)
        {
            Id = id;
            Title = title;
            HeadingLevel = headingLevel;
            StartOffset = startOffset;
        }

        public TreeNode ToTreeNode() =>
            new(
                NodeId: Id,
                Title: Title,
                HeadingLevel: HeadingLevel,
                StartOffset: StartOffset,
                EndOffset: EndOffset,
                BlockIds: BlockIds.ToArray(),
                Children: Children.Select(c => c.ToTreeNode()).ToArray());
    }
}
