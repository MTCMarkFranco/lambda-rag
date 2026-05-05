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
    public string Version => "1.4.0";
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
                        ["topic_density"] = new JsonObject { ["type"] = "number" },
                        ["is_operative_for_topic"] = new JsonObject { ["type"] = "boolean" },
                        ["text_features"] = new JsonObject { ["type"] = "object" },
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

        // Defined-party alias resolution: capture every `Canonical ("Alias")`
        // form across the whole document so that downstream lambdas which
        // expect a literal like `Contains("Contoso")` still match when the
        // operative clause uses the alias `"Company"`. Pure pre-processor —
        // we substitute alias → canonical inside each section's body text
        // before classification / feature extraction. The original parsed
        // text is left untouched so spans remain valid.
        var aliasMap = ExtractPartyAliases(parsed.Blocks);

        // Pre-pass: collect (sectionId, primaryTopic, headingLower) so amendment
        // resolver can look up by heading text mentioned in cross-references.
        var processed = new List<(string Id, string Heading, string PrimaryTopic)>();

        // For #44 — vocabulary-density tie-break across same-topic sections.
        // Build per-section per-section JsonObject refs as we go so we can
        // post-mark the operative section per primary topic without rewriting
        // the whole graph.
        var sectionNodesById = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var densityByTopicByScn = new Dictionary<string, double>(StringComparer.Ordinal);

        var groups = GroupByHeading(parsed.Blocks).ToList();
        var index = 0;
        foreach (var group in groups)
        {
            var heading = group.Heading?.Text ?? string.Empty;
            var headingPath = group.HeadingPath;
            var bodyText = string.Join("\n", group.Body.Select(b => b.Text));
            var resolvedBodyText = ApplyAliases(bodyText, aliasMap);
            var firstSpan = (group.Heading ?? group.Body.FirstOrDefault())?.Span ?? SourceSpan.Unknown;
            // Char offset of body text inside the canonical document — lets
            // downstream markup compute substring-precise spans without
            // re-walking the parser output. Falls back to firstSpan for
            // body-less sections so the offset is always well-defined.
            var bodyCharStart = group.Body.FirstOrDefault()?.Span.CharStart
                                ?? firstSpan.CharStart;
            var sectionId = $"s_{index:D8}";

            var classification = Classify(heading, resolvedBodyText, processed);

            // Country-supplement detection (#65 cluster 2): a section is a
            // country supplement if its heading matches a heading-pattern of
            // any "jurisdiction"-style axis in the topic map. The flag lets
            // rule authors guard predicates with `&& !is_country_supplement`
            // so master-clause rules don't misfire on per-country annexes
            // that mention warranty / liability / indemnification verbatim.
            // Keeps the body-derived multi-label topic vector intact for any
            // future rule that does want to inspect supplements.
            var isCountrySupplement = IsCountrySupplement(heading);

            var topicsArr = new JsonArray();
            var scoresObj = new JsonObject();
            foreach (var (topic, score) in classification.Topics
                         .OrderByDescending(t => t.Score)
                         .ThenBy(t => t.Topic, StringComparer.Ordinal))
            {
                topicsArr.Add(topic);
                scoresObj[topic] = score;
            }

            var primary = classification.PrimaryTopic ?? "unknown";
            var density = ComputeDensity(primary, resolvedBodyText);

            var sectionNode = new JsonObject
            {
                ["id"] = sectionId,
                ["heading"] = heading,
                ["heading_path"] = headingPath,
                ["category"] = classification.PrimaryTopic ?? "unknown",
                ["primary_topic"] = classification.PrimaryTopic ?? "unknown",
                ["topics"] = topicsArr,
                ["topic_scores"] = scoresObj,
                ["topic_density"] = density,
                ["is_operative_for_topic"] = false,
                ["is_country_supplement"] = isCountrySupplement,
                ["text_features"] = TextFeatureExtractor.Extract(resolvedBodyText),
                ["text"] = resolvedBodyText,
                // Original (non-alias-resolved) body text — kept so callers
                // that need verbatim source can still get it without
                // re-parsing the document.
                ["text_raw"] = bodyText,
                ["text_char_start"] = bodyCharStart,
            };
            if (classification.InheritedFrom is not null)
                sectionNode["inherited_from"] = classification.InheritedFrom;

            sections.Add(sectionNode);
            sectionNodesById[sectionId] = sectionNode;
            densityByTopicByScn[sectionId] = density;

            spanMap[$"$.sections[{index}]"] = firstSpan;
            spanMap[$"$.sections[?(@.id == '{sectionId}')]"] = firstSpan;

            if (!categories.TryGetValue(primary, out var ids))
                categories[primary] = ids = new JsonArray();
            ids.Add(sectionId);

            if (classification.PrimaryTopic is null)
                unknown.Add(sectionId);

            processed.Add((sectionId, heading, primary));
            index++;
        }

        // Pre-pass before operative selection: split "wrapper" sections (e.g.
        // §11 Miscellaneous) into synthetic child sections per inline
        // subheading. Lets predicates targeting category=="payment_terms" /
        // "privacy" / "governing_law" find sub-clauses that would otherwise
        // be buried under the wrapper's primary topic. Children participate
        // in operative-selection alongside top-level sections.
        ExpandWrapperSections(sections, sectionNodesById, categories, densityByTopicByScn, unknown);

        // Post-pass: for each non-"unknown" primary topic that has more than
        // one matched section, flag the section with the highest body
        // vocabulary density as operative. This is the projector-side fix for
        // #44 — when a contract mentions a topic at the top (heading-only)
        // and again later with the operative obligation, downstream rule
        // authors can target the operative span via
        //   predicate: input1.primary_topic == "X" && input1.is_operative_for_topic
        // instead of binding to whichever section happens to match first.
        // Tie-breaker: lowest section id (earliest occurrence) wins, so the
        // selection is fully deterministic.
        // Country supplements are excluded from operative selection (#65) so
        // master clauses always win even when a per-country annex has higher
        // local keyword density (e.g. Albania's liability supplement).
        foreach (var (topic, idArr) in categories)
        {
            if (topic == "unknown") continue;
            string? bestId = null;
            var bestDensity = -1.0;
            foreach (var node in idArr)
            {
                var id = node!.GetValue<string>();
                if (sectionNodesById.TryGetValue(id, out var n) &&
                    n["is_country_supplement"] is JsonValue jv &&
                    jv.TryGetValue<bool>(out var isSupp) && isSupp)
                {
                    continue; // master clauses only
                }
                var d = densityByTopicByScn.GetValueOrDefault(id, 0.0);
                if (d > bestDensity || (d == bestDensity && bestId is not null && string.CompareOrdinal(id, bestId) < 0))
                {
                    bestDensity = d;
                    bestId = id;
                }
            }
            // Fallback: if every section for this topic was a country
            // supplement (no master clause), don't flag anything as operative.
            if (bestId is not null && sectionNodesById.TryGetValue(bestId, out var operativeNode))
                operativeNode["is_operative_for_topic"] = true;
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

    /// <summary>
    /// Vocabulary-density score for a primary topic in a section's body —
    /// (count of distinct topic-keyword occurrences) divided by (max(1, body
    /// word count) / 100). Larger = more operative content. Returns 0 for
    /// the synthetic "unknown" topic or when the topic is not in the map.
    /// Result is rounded to 4 decimals so projection output is stable across
    /// platforms (no doubles drift in golden hashes).
    /// </summary>
    private double ComputeDensity(string topicId, string body)
    {
        if (string.IsNullOrEmpty(topicId) || topicId == "unknown" || string.IsNullOrEmpty(body))
            return 0.0;
        var topic = _topicMap.Topics.FirstOrDefault(t => t.Id == topicId && t.Axis is null);
        if (topic is null) return 0.0;
        var bodyLower = body.ToLowerInvariant();
        var hits = 0;
        foreach (var kw in topic.Keywords)
        {
            var idx = 0;
            while ((idx = bodyLower.IndexOf(kw, idx, StringComparison.Ordinal)) >= 0)
            {
                hits++;
                idx += kw.Length;
            }
        }
        var words = Math.Max(1, body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        return Math.Round(hits / (words / 100.0), 4);
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

    /// <summary>
    /// True iff the heading text matches any heading-pattern declared on a
    /// "jurisdiction"-style axis in the topic map. Used to flag per-country
    /// annex sections so master-clause rules can guard with
    /// <c>!is_country_supplement</c>. Pure case-insensitive substring match
    /// — same routine used inside <see cref="Classify"/> for axis tagging,
    /// kept consistent so the flag and the topic vector never disagree.
    /// </summary>
    private bool IsCountrySupplement(string heading)
    {
        if (string.IsNullOrEmpty(heading)) return false;
        var lower = heading.ToLowerInvariant();
        foreach (var (axisName, axisDef) in _topicMap.Axes)
        {
            // Only treat axes named like "jurisdiction" / "country" /
            // "region" as supplement markers. New non-geographic axes (e.g.
            // a future "product_line") should NOT flip the supplement flag.
            if (!IsJurisdictionLikeAxis(axisName)) continue;
            foreach (var pat in axisDef.HeadingPatterns)
            {
                if (lower.Contains(pat, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    private static bool IsJurisdictionLikeAxis(string axisName) =>
        axisName.Equals("jurisdiction", StringComparison.OrdinalIgnoreCase) ||
        axisName.Equals("country", StringComparison.OrdinalIgnoreCase) ||
        axisName.Equals("region", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Inline-subheading detector for wrapper sections. Matches a short
    /// label-like phrase at line-start, ending with a period, followed by
    /// whitespace and a capitalized continuation (the body of the
    /// sub-clause). Internal periods are accepted to handle labels like
    /// "U.S. Export." which would otherwise be cut on the first dot.
    /// Non-greedy quantifier keeps the captured label as short as possible.
    /// </summary>
    private static readonly Regex InlineSubheadingRx = new(
        @"(?m)^[ \t]*([A-Z][A-Za-z][A-Za-z0-9\s,.&'\-/]{1,58}?)\.\s+(?=[A-Z])",
        RegexOptions.Compiled);

    /// <summary>
    /// Topics whose sections are treated as "wrapper" containers — their
    /// body is scanned for inline subheading markers and split into
    /// synthetic child sections. Configurable here so domains can opt
    /// additional wrappers in (e.g. an "annexes" topic) without code change.
    /// </summary>
    private static readonly HashSet<string> WrapperTopics = new(StringComparer.Ordinal)
    {
        "miscellaneous",
    };

    /// <summary>
    /// Splits each wrapper section (primary_topic in <see cref="WrapperTopics"/>)
    /// into synthetic child sections, one per inline subheading detected in
    /// its body. Children are appended to the sections array and registered
    /// in the categories map, sectionNodesById, and density table so that
    /// operative-selection sees them. Parent wrapper section is retained
    /// unchanged so existing rules / golden hashes that target the wrapper
    /// directly still resolve.
    /// </summary>
    private void ExpandWrapperSections(
        JsonArray sections,
        Dictionary<string, JsonObject> sectionNodesById,
        Dictionary<string, JsonArray> categories,
        Dictionary<string, double> densityByTopicByScn,
        JsonArray unknown)
    {
        // Snapshot wrapper parents first; we mutate `sections` while iterating.
        var parents = new List<JsonObject>();
        foreach (var node in sections)
        {
            if (node is not JsonObject obj) continue;
            var primary = obj["primary_topic"]?.GetValue<string>();
            if (primary is null) continue;
            if (WrapperTopics.Contains(primary)) parents.Add(obj);
        }

        foreach (var parent in parents)
        {
            var parentId = parent["id"]!.GetValue<string>();
            var parentText = parent["text"]?.GetValue<string>() ?? string.Empty;
            if (parentText.Length < 200) continue;

            var matches = InlineSubheadingRx.Matches(parentText);
            // Need at least 2 inline subheadings to consider a split worthwhile;
            // a single match is more likely a false positive (mid-paragraph
            // sentence start) than a real section structure.
            if (matches.Count < 2) continue;

            // Filter implausible labels: too many words → likely a sentence
            // start, not a label. Real subheadings in MS-style contracts are
            // 1-7 words.
            var validMatches = new List<Match>();
            foreach (Match m in matches)
            {
                var label = m.Groups[1].Value.Trim();
                var wordCount = label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                if (wordCount < 1 || wordCount > 7) continue;
                // Reject obvious non-labels that begin with common sentence
                // openers — these phrases are sentence-shaped, not labels.
                if (label.StartsWith("This ", StringComparison.Ordinal) ||
                    label.StartsWith("If ", StringComparison.Ordinal) ||
                    label.StartsWith("The ", StringComparison.Ordinal) ||
                    label.StartsWith("By ", StringComparison.Ordinal) ||
                    label.StartsWith("In ", StringComparison.Ordinal) ||
                    label.StartsWith("For ", StringComparison.Ordinal))
                    continue;
                validMatches.Add(m);
            }
            if (validMatches.Count < 2) continue;

            var parentIsSupplement = parent["is_country_supplement"] is JsonValue jv &&
                                     jv.TryGetValue<bool>(out var b) && b;
            var parentHeadingPath = parent["heading_path"]?.GetValue<string>() ?? "/";
            var parentTextCharStart = parent["text_char_start"]?.GetValue<int>() ?? 0;

            var letterIdx = 0;
            for (int i = 0; i < validMatches.Count; i++)
            {
                var m = validMatches[i];
                var label = m.Groups[1].Value.Trim();
                var bodyStart = m.Index + m.Length;
                var bodyEnd = (i + 1 < validMatches.Count) ? validMatches[i + 1].Index : parentText.Length;
                var body = parentText.Substring(bodyStart, bodyEnd - bodyStart).Trim();
                if (body.Length == 0) continue;

                var childId = $"{parentId}_{LetterFor(letterIdx++)}";

                // Reclassify using the captured label as heading. Pass an
                // empty `processed` list — amendment cross-references rarely
                // appear inside wrapper sub-clauses and would only confuse
                // the resolver here.
                var classification = Classify(label, body, Array.Empty<(string, string, string)>());

                var topicsArr = new JsonArray();
                var scoresObj = new JsonObject();
                foreach (var (topic, score) in classification.Topics
                             .OrderByDescending(t => t.Score)
                             .ThenBy(t => t.Topic, StringComparer.Ordinal))
                {
                    topicsArr.Add(topic);
                    scoresObj[topic] = score;
                }

                var primary = classification.PrimaryTopic ?? "unknown";
                var density = ComputeDensity(primary, body);

                var childNode = new JsonObject
                {
                    ["id"] = childId,
                    ["heading"] = label,
                    ["heading_path"] = parentHeadingPath + "/" + label,
                    ["category"] = primary,
                    ["primary_topic"] = primary,
                    ["topics"] = topicsArr,
                    ["topic_scores"] = scoresObj,
                    ["topic_density"] = density,
                    ["is_operative_for_topic"] = false,
                    ["is_country_supplement"] = parentIsSupplement,
                    ["text_features"] = TextFeatureExtractor.Extract(body),
                    ["text"] = body,
                    ["text_raw"] = body,
                    ["text_char_start"] = parentTextCharStart + bodyStart,
                    ["parent_section"] = parentId,
                };

                sections.Add(childNode);
                sectionNodesById[childId] = childNode;
                densityByTopicByScn[childId] = density;

                if (!categories.TryGetValue(primary, out var ids))
                    categories[primary] = ids = new JsonArray();
                ids.Add(childId);

                if (classification.PrimaryTopic is null)
                    unknown.Add(childId);
            }
        }
    }

    /// <summary>
    /// Generate a stable child suffix ("a", "b", ..., "z", "aa", "ab", ...).
    /// Letters are easier to read than numeric indexes when child sections
    /// appear in reports and reviewer columns.
    /// </summary>
    private static string LetterFor(int index)
    {
        if (index < 0) index = 0;
        var sb = new System.Text.StringBuilder();
        do
        {
            sb.Insert(0, (char)('a' + index % 26));
            index = (index / 26) - 1;
        } while (index >= 0);
        return sb.ToString();
    }

    private record HeadingGroup(ContentBlock? Heading, List<ContentBlock> Body, string HeadingPath);

    /// <summary>
    /// Defined-term party-alias regex. Captures patterns like:
    ///   <c>Contoso ("Company")</c> · <c>Vendor Corp ("Vendor")</c> ·
    ///   <c>Acme Inc. ("Supplier")</c>. Group 1 = canonical name (left of
    ///   parens), group 2 = alias inside the quotes. We accept ASCII straight
    ///   quotes and curly quotes so the regex works on Word-processed text.
    /// </summary>
    private static readonly Regex AliasRx = new(
        @"([A-Z][\w&.\-]*(?:\s+[A-Z][\w&.\-]*){0,4})\s*\([\u0022\u201C\u2018']([A-Z][\w]{2,30})[\u0022\u201D\u2019']\)",
        RegexOptions.Compiled);

    /// <summary>
    /// Build alias → canonical map by scanning every parsed block. Aliases
    /// shorter than 3 characters or identical to the canonical name are
    /// discarded. The map is ordered by alias length (longest first) so
    /// substitution doesn't get partially shadowed by shorter aliases.
    /// </summary>
    private static IReadOnlyList<(string Alias, string Canonical)> ExtractPartyAliases(
        IReadOnlyList<ContentBlock> blocks)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            foreach (Match m in AliasRx.Matches(block.Text))
            {
                var canonical = m.Groups[1].Value.Trim();
                var alias = m.Groups[2].Value.Trim();
                if (alias.Length < 3) continue;
                if (string.Equals(alias, canonical, StringComparison.Ordinal)) continue;
                if (canonical.Contains(alias, StringComparison.Ordinal)) continue;
                // First definition wins — contracts only define each alias once.
                map.TryAdd(alias, canonical);
            }
        }
        return map
            .OrderByDescending(kvp => kvp.Key.Length)
            .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();
    }

    /// <summary>
    /// Substitute aliases with their canonical names using whole-word
    /// boundaries so we don't rewrite substrings of unrelated words. The
    /// match is case-sensitive (defined terms in contracts are capitalized
    /// consistently), which avoids accidentally rewriting common nouns.
    /// </summary>
    private static string ApplyAliases(string text, IReadOnlyList<(string Alias, string Canonical)> map)
    {
        if (map.Count == 0 || string.IsNullOrEmpty(text)) return text;
        var result = text;
        foreach (var (alias, canonical) in map)
        {
            // Word-boundary substitution. We don't add a possessive carve-out
            // ("Company's") on purpose — `\b` already breaks on the apostrophe,
            // so "Company's" becomes "Contoso's" naturally.
            var rx = new Regex($@"\b{Regex.Escape(alias)}\b", RegexOptions.None,
                TimeSpan.FromMilliseconds(200));
            result = rx.Replace(result, canonical);
        }
        return result;
    }
}
