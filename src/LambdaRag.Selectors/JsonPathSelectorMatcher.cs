using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LambdaRag.Selectors;

/// <summary>
/// Pure-code, deterministic <see cref="ISelectorMatcher"/> that evaluates the
/// selector DSL against a <see cref="ProjectedDocument"/> using our JSONPath
/// subset engine. No LLM is involved at any point.
///
/// Results are always returned sorted by <c>Path</c> (ordinal ascending) so
/// two runs against the same document and selector produce byte-identical output.
/// </summary>
public sealed class JsonPathSelectorMatcher(ILogger<JsonPathSelectorMatcher> logger)
    : ISelectorMatcher
{
    private readonly ILogger<JsonPathSelectorMatcher> _logger = logger;

    // -----------------------------------------------------------------------
    // ISelectorMatcher
    // -----------------------------------------------------------------------

    public IReadOnlyList<MatchedSection> Match(Selector selector, ProjectedDocument document)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(document);
        return MatchInternal(selector, document, isTopLevel: true);
    }

    // -----------------------------------------------------------------------
    // Dispatch
    // -----------------------------------------------------------------------

    private IReadOnlyList<MatchedSection> MatchInternal(
        Selector selector, ProjectedDocument doc, bool isTopLevel)
    {
        return selector switch
        {
            PathSelector ps      => MatchPath(ps, doc),
            RegexSelector rs     => MatchRegex(rs, doc),
            HasFieldSelector hfs => MatchHasField(hfs, doc),
            ValueInSelector vis  => MatchValueIn(vis, doc),
            AllOfSelector allOf  => MatchAllOf(allOf, doc),
            AnyOfSelector anyOf  => MatchAnyOf(anyOf, doc),
            NotSelector _ when isTopLevel => MatchNotTopLevel(),
            NotSelector ns       => MatchNot(ns, doc),
            _ => throw new ArgumentException(
                     $"Unknown selector kind: {selector.GetType().Name}", nameof(selector)),
        };
    }

    // -----------------------------------------------------------------------
    // Leaf matchers
    // -----------------------------------------------------------------------

    private IReadOnlyList<MatchedSection> MatchPath(PathSelector selector, ProjectedDocument doc)
    {
        var expr = JsonPath.Parse(selector.Path);
        var result = new List<MatchedSection>();
        foreach (var (path, node) in JsonPath.Evaluate(expr, doc.Graph))
            result.Add(new MatchedSection(path, node, LookupSpan(path, doc.SpanMap)));
        return Sorted(result);
    }

    private IReadOnlyList<MatchedSection> MatchRegex(RegexSelector selector, ProjectedDocument doc)
    {
        var regex = new Regex(
            selector.Pattern,
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        IEnumerable<(string Path, JsonNode Node)> candidates = selector.Path is not null
            ? JsonPath.Evaluate(JsonPath.Parse(selector.Path), doc.Graph)
            : GetAllLeafNodes(doc.Graph, "$");

        var result = new List<MatchedSection>();
        foreach (var (path, node) in candidates)
        {
            if (node is JsonValue jv
                && jv.TryGetValue<string>(out var s)
                && s is not null
                && regex.IsMatch(s))
            {
                result.Add(new MatchedSection(path, node, LookupSpan(path, doc.SpanMap)));
            }
        }
        return Sorted(result);
    }

    private IReadOnlyList<MatchedSection> MatchHasField(HasFieldSelector selector, ProjectedDocument doc)
    {
        var expr = JsonPath.Parse(selector.Path);
        var result = new List<MatchedSection>();
        foreach (var (path, node) in JsonPath.Evaluate(expr, doc.Graph))
        {
            if (node is JsonObject obj && obj.ContainsKey(selector.Field))
                result.Add(new MatchedSection(path, node, LookupSpan(path, doc.SpanMap)));
        }
        return Sorted(result);
    }

    private IReadOnlyList<MatchedSection> MatchValueIn(ValueInSelector selector, ProjectedDocument doc)
    {
        var expr = JsonPath.Parse(selector.Path);
        var result = new List<MatchedSection>();
        foreach (var (path, node) in JsonPath.Evaluate(expr, doc.Graph))
        {
            if (selector.Values.Any(v => JsonNode.DeepEquals(node, v)))
                result.Add(new MatchedSection(path, node, LookupSpan(path, doc.SpanMap)));
        }
        return Sorted(result);
    }

    // -----------------------------------------------------------------------
    // Composite matchers
    // -----------------------------------------------------------------------

    private IReadOnlyList<MatchedSection> MatchAllOf(AllOfSelector selector, ProjectedDocument doc)
    {
        if (selector.Of.Count == 0) return [];

        var childResults = selector.Of
            .Select(child => MatchInternal(child, doc, isTopLevel: false))
            .ToList();

        // Start with all paths from the first child, then intersect with each subsequent child.
        var pathSet = new HashSet<string>(
            childResults[0].Select(m => m.Path),
            StringComparer.Ordinal);

        foreach (var child in childResults.Skip(1))
            pathSet.IntersectWith(child.Select(m => m.Path));

        // Emit using the node from the first child match for each surviving path.
        var firstByPath = childResults[0].ToDictionary(m => m.Path, StringComparer.Ordinal);

        var result = new List<MatchedSection>();
        foreach (var p in pathSet)
            if (firstByPath.TryGetValue(p, out var ms))
                result.Add(ms);
        return Sorted(result);
    }

    private IReadOnlyList<MatchedSection> MatchAnyOf(AnyOfSelector selector, ProjectedDocument doc)
    {
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MatchedSection>();

        foreach (var child in selector.Of)
        {
            foreach (var m in MatchInternal(child, doc, isTopLevel: false))
                if (seen.Add(m.Path))
                    result.Add(m);
        }
        return Sorted(result);
    }

    private IReadOnlyList<MatchedSection> MatchNotTopLevel()
    {
        _logger.LogWarning(
            "NotSelector used at top level — emitting nothing. " +
            "Wrap it inside an AllOfSelector for useful behaviour.");
        return [];
    }

    private IReadOnlyList<MatchedSection> MatchNot(NotSelector selector, ProjectedDocument doc)
    {
        var excluded = MatchInternal(selector.Of, doc, isTopLevel: false)
            .Select(m => m.Path)
            .ToHashSet(StringComparer.Ordinal);

        var result = new List<MatchedSection>();
        foreach (var (path, node) in GetAllLeafNodes(doc.Graph, "$"))
        {
            if (!excluded.Contains(path))
                result.Add(new MatchedSection(path, node, LookupSpan(path, doc.SpanMap)));
        }
        return Sorted(result);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static IEnumerable<(string Path, JsonNode Node)> GetAllLeafNodes(
        JsonNode node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (value is null) continue;
                    var childPath = $"{path}.{key}";
                    foreach (var leaf in GetAllLeafNodes(value, childPath))
                        yield return leaf;
                }
                break;

            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is null) continue;
                    var childPath = $"{path}[{i}]";
                    foreach (var leaf in GetAllLeafNodes(arr[i]!, childPath))
                        yield return leaf;
                }
                break;

            default: // JsonValue — leaf node
                yield return (path, node);
                break;
        }
    }

    private static SourceSpan LookupSpan(
        string path, IReadOnlyDictionary<string, SourceSpan> spanMap)
    {
        var current = path;

        // Walk from exact path up to root, trying each prefix.
        while (!string.IsNullOrEmpty(current) && current != "$")
        {
            if (spanMap.TryGetValue(current, out var span)) return span;

            int lastDot     = current.LastIndexOf('.');
            int lastBracket = current.LastIndexOf('[');
            int lastSep     = Math.Max(lastDot, lastBracket);

            // lastSep == 1 means the only remaining separator is the leading "$."
            // so the parent is "$".
            if (lastSep <= 1) break;
            current = current[..lastSep];
        }

        // Try root explicitly.
        if (spanMap.TryGetValue("$", out var rootSpan)) return rootSpan;

        return SourceSpan.Unknown;
    }

    private static List<MatchedSection> Sorted(List<MatchedSection> list)
    {
        list.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.Ordinal));
        return list;
    }
}
