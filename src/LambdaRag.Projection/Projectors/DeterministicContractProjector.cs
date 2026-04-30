using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;

namespace LambdaRag.Projection.Projectors;

/// <summary>
/// Deterministic contract projector. Walks the heading hierarchy of a
/// parsed document and produces a multi-label topic vector for each
/// section using a data-driven <see cref="TopicMap"/> (default loaded
/// from embedded <c>TopicMaps/contract.v1.json</c>; override via ctor
/// to onboard new industries without recompile).
///
/// Output graph shape (v1.2):
/// <code>
/// {
///   "doc_kind": "contract",
///   "topic_map": "contract@1.0.0",
///   "sections": [
///     {
///       "id": "s_00000123",
///       "heading": "...",
///       "heading_path": "/...",
///       "category": "liability",        // primary topic, kept as alias
///       "primary_topic": "liability",
///       "topics": ["liability", "jurisdiction:hungary"],
///       "topic_scores": { "liability": 0.62, "jurisdiction:hungary": 1.0 },
///       "inherited_from": "s_00000005",  // present iff amendment xref matched
///       "text": "..."
///     }
///   ],
///   "categories": { "liability": [ &lt;ids&gt; ], ... },
///   "unknown_sections": [ &lt;ids&gt; ]    // sections with no primary topic
/// }
/// </code>
/// </summary>
public sealed class DeterministicContractProjector : IDocumentProjector
{
    public string Id => "contract";
    public string Version => "1.2.0";
    public string Domain => "contract";
    public JsonObject Schema => SchemaInstance;

    private readonly TopicMap _topicMap;

    public DeterministicContractProjector() : this(LoadDefaultTopicMap()) { }

    public DeterministicContractProjector(TopicMap topicMap)
    {
        _topicMap = topicMap ?? throw new ArgumentNullException(nameof(topicMap));
    }

    public TopicMap TopicMap => _topicMap;

    private static TopicMap LoadDefaultTopicMap()
    {
        return TopicMapRegistry.Load("contract.v1");
    }

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
                        ["primary_topic"] = new JsonObject { ["type"] = "string" },
                        ["topics"] = new JsonObject { ["type"] = "array" },
                        ["topic_scores"] = new JsonObject { ["type"] = "object" },
                        ["inherited_from"] = new JsonObject { ["type"] = "string" },
                        ["text"] = new JsonObject { ["type"] = "string" },
                    },
                },
            },
            ["categories"] = new JsonObject { ["type"] = "object" },
            ["unknown_sections"] = new JsonObject { ["type"] = "array" },
        },
    };

    public Task<ProjectedDocument> ProjectAsync(ParsedDocument parsed, CancellationToken ct = default)
    {
        var sections = new JsonArray();
        var spanMap = new Dictionary<string, SourceSpan>(StringComparer.Ordinal);
        var categories = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        var unknown = new JsonArray();

        // Pre-pass: collect (sectionId, primaryTopic, headingLower) so amendment
        // resolver can look up by heading text mentioned in cross-references.
        var processed = new List<(string Id, string Heading, string PrimaryTopic)>();

        var groups = GroupByHeading(parsed.Blocks).ToList();
        var index = 0;
        foreach (var group in groups)
        {
            var heading = group.Heading?.Text ?? string.Empty;
            var headingPath = group.HeadingPath;
            var bodyText = string.Join("\n", group.Body.Select(b => b.Text));
            var firstSpan = (group.Heading ?? group.Body.FirstOrDefault())?.Span ?? SourceSpan.Unknown;
            var sectionId = $"s_{index:D8}";

            var classification = Classify(heading, bodyText, processed);

            var topicsArr = new JsonArray();
            var scoresObj = new JsonObject();
            foreach (var (topic, score) in classification.Topics
                         .OrderByDescending(t => t.Score)
                         .ThenBy(t => t.Topic, StringComparer.Ordinal))
            {
                topicsArr.Add(topic);
                scoresObj[topic] = score;
            }

            var sectionNode = new JsonObject
            {
                ["id"] = sectionId,
                ["heading"] = heading,
                ["heading_path"] = headingPath,
                ["category"] = classification.PrimaryTopic ?? "unknown",
                ["primary_topic"] = classification.PrimaryTopic ?? "unknown",
                ["topics"] = topicsArr,
                ["topic_scores"] = scoresObj,
                ["text"] = bodyText,
            };
            if (classification.InheritedFrom is not null)
                sectionNode["inherited_from"] = classification.InheritedFrom;

            sections.Add(sectionNode);

            spanMap[$"$.sections[{index}]"] = firstSpan;
            spanMap[$"$.sections[?(@.id == '{sectionId}')]"] = firstSpan;

            var primary = classification.PrimaryTopic ?? "unknown";
            if (!categories.TryGetValue(primary, out var ids))
                categories[primary] = ids = new JsonArray();
            ids.Add(sectionId);

            if (classification.PrimaryTopic is null)
                unknown.Add(sectionId);

            processed.Add((sectionId, heading, primary));
            index++;
        }

        var categoriesObj = new JsonObject();
        foreach (var (cat, ids) in categories.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            categoriesObj[cat] = ids;

        var graph = new JsonObject
        {
            ["doc_kind"] = "contract",
            ["topic_map"] = $"{_topicMap.Domain}@{_topicMap.Version}",
            ["sections"] = sections,
            ["categories"] = categoriesObj,
            ["unknown_sections"] = unknown,
        };

        var projected = new ProjectedDocument(
            SourceId: parsed.Source.Id,
            ProjectorId: Id,
            ProjectorVersion: Version,
            Graph: graph,
            SpanMap: spanMap);

        return Task.FromResult(projected);
    }

    private record TopicScore(string Topic, double Score);
    private record Classification(string? PrimaryTopic, IReadOnlyList<TopicScore> Topics, string? InheritedFrom);

    private Classification Classify(
        string heading,
        string body,
        IReadOnlyList<(string Id, string Heading, string PrimaryTopic)> processed)
    {
        var topics = new Dictionary<string, double>(StringComparer.Ordinal);
        string? primary = null;
        string? inheritedFrom = null;

        var headingLower = heading.ToLowerInvariant();
        var bodyLower = body.ToLowerInvariant();
        var combinedLower = headingLower + "\n" + bodyLower;

        // 1. Primary topic from heading (first match wins, declared order).
        foreach (var t in _topicMap.Topics.Where(t => t.Axis is null))
        {
            foreach (var kw in t.Keywords)
            {
                if (headingLower.Contains(kw, StringComparison.Ordinal))
                {
                    primary ??= t.Id;
                    topics[t.Id] = Math.Max(topics.GetValueOrDefault(t.Id), 0.9);
                    break;
                }
            }
        }

        // 2. Body-level signal for primary topics: lower confidence than heading.
        foreach (var t in _topicMap.Topics.Where(t => t.Axis is null))
        {
            foreach (var kw in t.Keywords)
            {
                if (bodyLower.Contains(kw, StringComparison.Ordinal))
                {
                    if (!topics.ContainsKey(t.Id))
                        topics[t.Id] = 0.4;
                    break;
                }
            }
        }

        // 3. Axis topics (e.g. jurisdiction:<country>) — pure tags, never primary.
        foreach (var (axisName, axisDef) in _topicMap.Axes)
        {
            foreach (var pat in axisDef.HeadingPatterns)
            {
                if (headingLower.Contains(pat, StringComparison.Ordinal))
                {
                    var slug = pat.Replace(' ', '_');
                    topics[$"{axisName}:{slug}"] = 1.0;
                }
            }
        }

        // 4. Amendment cross-reference resolver — if the body cites another
        //    section's title, inherit that section's primary topic.
        if (primary is null)
        {
            foreach (var rx in _topicMap.CompiledAmendmentPatterns)
            {
                var m = rx.Match(body);
                if (m.Success && m.Groups.Count > 1)
                {
                    var referencedTitle = m.Groups[1].Value.Trim().ToLowerInvariant();
                    var parent = processed.FirstOrDefault(p =>
                        p.Heading.Equals(m.Groups[1].Value.Trim(), StringComparison.OrdinalIgnoreCase)
                        || p.Heading.ToLowerInvariant().Contains(referencedTitle, StringComparison.Ordinal));
                    if (parent.PrimaryTopic is not null && parent.PrimaryTopic != "unknown")
                    {
                        primary = parent.PrimaryTopic;
                        inheritedFrom = parent.Id;
                        topics[primary] = Math.Max(topics.GetValueOrDefault(primary), 0.95);
                        break;
                    }
                    // Even without parent in `processed`, classify the referenced
                    // title against the topic map directly.
                    foreach (var t in _topicMap.Topics.Where(t => t.Axis is null))
                    {
                        if (t.Keywords.Any(kw => referencedTitle.Contains(kw, StringComparison.Ordinal)))
                        {
                            primary = t.Id;
                            inheritedFrom = "<by-title>";
                            topics[primary] = Math.Max(topics.GetValueOrDefault(primary), 0.85);
                            break;
                        }
                    }
                    if (primary is not null) break;
                }
            }
        }

        var scoreList = topics
            .Select(kvp => new TopicScore(kvp.Key, Math.Round(kvp.Value, 3)))
            .ToList();

        return new Classification(primary, scoreList, inheritedFrom);
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

    private record HeadingGroup(ContentBlock? Heading, List<ContentBlock> Body, string HeadingPath);
}
