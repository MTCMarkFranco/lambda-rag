using System.Text.Json.Nodes;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Projection.Projectors;

/// <summary>
/// Deterministic contract projector. Walks the heading hierarchy of a
/// parsed document and bins paragraphs into well-known contract sections
/// (parties, term, payment_terms, governing_law, termination, ...). All
/// classification is rule-based — no LLM is involved.
///
/// Output graph shape (simplified):
/// <code>
/// {
///   "doc_kind": "contract",
///   "sections": [
///     { "id": "s_00000123", "heading": "...", "heading_path": "/...",
///       "category": "payment_terms", "text": "..." }
///   ],
///   "categories": { "payment_terms": [ &lt;section ids&gt; ], ... }
/// }
/// </code>
///
/// Span map: each section has a span entry under "$.sections[i]" pointing
/// at the source span of its first block.
/// </summary>
public sealed class DeterministicContractProjector : IDocumentProjector
{
    public string Id => "contract";
    public string Version => "1.0.0";
    public string Domain => "contract";
    public JsonObject Schema => SchemaInstance;

    private static readonly JsonObject SchemaInstance = new()
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["title"] = "ProjectedContract",
        ["type"] = "object",
        ["required"] = new JsonArray("doc_kind", "sections", "categories"),
        ["properties"] = new JsonObject
        {
            ["doc_kind"] = new JsonObject { ["const"] = "contract" },
            ["sections"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("id", "heading", "heading_path", "category", "text"),
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string" },
                        ["heading"] = new JsonObject { ["type"] = "string" },
                        ["heading_path"] = new JsonObject { ["type"] = "string" },
                        ["category"] = new JsonObject { ["type"] = "string" },
                        ["text"] = new JsonObject { ["type"] = "string" },
                    },
                },
            },
            ["categories"] = new JsonObject { ["type"] = "object" },
        },
    };

    private static readonly (string[] Keywords, string Category)[] CategoryRules =
    [
        (new[] { "payment", "fees", "compensation", "invoice" }, "payment_terms"),
        (new[] { "termination", "cancel" }, "termination"),
        (new[] { "governing law", "jurisdiction", "venue" }, "governing_law"),
        (new[] { "warranty", "warranties", "disclaimer" }, "warranty"),
        (new[] { "confidential", "non-disclosure", "nda" }, "confidentiality"),
        (new[] { "indemn" }, "indemnification"),
        (new[] { "liabil" }, "liability"),
        (new[] { "term", "duration", "effective date" }, "term"),
        (new[] { "parties", "party", "between" }, "parties"),
        (new[] { "data protection", "privacy", "gdpr", "personal data" }, "privacy"),
        (new[] { "security", "infosec" }, "security"),
        (new[] { "service level", "sla" }, "service_levels"),
    ];

    public Task<ProjectedDocument> ProjectAsync(ParsedDocument parsed, CancellationToken ct = default)
    {
        var sections = new JsonArray();
        var spanMap = new Dictionary<string, SourceSpan>(StringComparer.Ordinal);
        var categories = new Dictionary<string, JsonArray>(StringComparer.Ordinal);

        var groups = GroupByHeading(parsed.Blocks);

        var index = 0;
        foreach (var group in groups)
        {
            var heading = group.Heading?.Text ?? string.Empty;
            var headingPath = group.HeadingPath;
            var bodyText = string.Join("\n", group.Body.Select(b => b.Text));
            var firstSpan = (group.Heading ?? group.Body.FirstOrDefault())?.Span ?? SourceSpan.Unknown;
            var category = ClassifyHeading(heading);

            var sectionId = $"s_{index:D8}";
            var sectionNode = new JsonObject
            {
                ["id"] = sectionId,
                ["heading"] = heading,
                ["heading_path"] = headingPath,
                ["category"] = category,
                ["text"] = bodyText,
            };
            sections.Add(sectionNode);

            spanMap[$"$.sections[{index}]"] = firstSpan;
            spanMap[$"$.sections[?(@.id == '{sectionId}')]"] = firstSpan;

            if (!categories.TryGetValue(category, out var ids))
                categories[category] = ids = new JsonArray();
            ids.Add(sectionId);

            index++;
        }

        var categoriesObj = new JsonObject();
        foreach (var (cat, ids) in categories.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            categoriesObj[cat] = ids;

        var graph = new JsonObject
        {
            ["doc_kind"] = "contract",
            ["sections"] = sections,
            ["categories"] = categoriesObj,
        };

        var projected = new ProjectedDocument(
            SourceId: parsed.Source.Id,
            ProjectorId: Id,
            ProjectorVersion: Version,
            Graph: graph,
            SpanMap: spanMap);

        return Task.FromResult(projected);
    }

    private static string ClassifyHeading(string heading)
    {
        var lowered = heading.ToLowerInvariant();
        foreach (var (keywords, category) in CategoryRules)
        {
            foreach (var kw in keywords)
                if (lowered.Contains(kw, StringComparison.Ordinal))
                    return category;
        }
        return "other";
    }

    private static IEnumerable<HeadingGroup> GroupByHeading(IReadOnlyList<ContentBlock> blocks)
    {
        ContentBlock? heading = null;
        var body = new List<ContentBlock>();
        var headingPath = "/";

        foreach (var block in blocks)
        {
            if (block.Kind == ContentBlockKind.Heading)
            {
                if (heading is not null || body.Count > 0)
                {
                    yield return new HeadingGroup(heading, body.ToList(), headingPath);
                    body.Clear();
                }
                heading = block;
                headingPath = string.IsNullOrEmpty(block.HeadingPath) ? "/" : block.HeadingPath;
            }
            else
            {
                body.Add(block);
            }
        }

        if (heading is not null || body.Count > 0)
            yield return new HeadingGroup(heading, body.ToList(), headingPath);
    }

    private sealed record HeadingGroup(ContentBlock? Heading, IReadOnlyList<ContentBlock> Body, string HeadingPath);

    public ContentHash CacheKeyFor(ParsedDocument parsed) => ProjectedDocument.CacheKey(
        parsed.Source.Id,
        Id,
        Version,
        modelId: "deterministic",
        promptHash: ContentHash.OfString("deterministic-contract-projector"));
}
