using System.Text.RegularExpressions;
using LambdaRag.Core.Domain;

namespace LambdaRag.Projection;

/// <summary>
/// Static analyzer that walks a ruleset's predicates and lambdas to extract
/// the topic / category literals it depends on. Diffs against a TopicMap to
/// surface gaps the operator must fill before the runtime can fire those
/// rules — i.e., topics referenced by rules but not declared in the topic
/// map.
///
/// 100% deterministic (regex over predicate strings). Intended for the
/// authoring / index-time pipeline so a CI build can fail when a new rule
/// references a topic that no map publishes.
/// </summary>
public static class RulesetTopicVocabulary
{
    private static readonly Regex CategoryEqRegex = new(
        "input1\\.(?:category|primary_topic)\\s*==\\s*\"([^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TopicsContainsRegex = new(
        "input1\\.topics(?:\\.[A-Za-z_]\\w*)?\\.(?:Contains|HasTopic)\\(\"([^\"]+)\"\\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Extract the set of topic / category literals the ruleset references
    /// in its predicates.
    /// </summary>
    public static IReadOnlySet<string> Extract(IEnumerable<Rule> rules)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rules)
        {
            foreach (var s in new[] { r.Predicate, r.Lambda })
            {
                if (string.IsNullOrEmpty(s)) continue;
                foreach (Match m in CategoryEqRegex.Matches(s))
                    seen.Add(m.Groups[1].Value);
                foreach (Match m in TopicsContainsRegex.Matches(s))
                    seen.Add(m.Groups[1].Value);
            }
        }
        return seen;
    }

    /// <summary>
    /// Diff the topic vocabulary referenced by the ruleset against a topic
    /// map. Returns separate sets for missing-from-map and unused-in-rules
    /// topics so the operator can decide which way to reconcile.
    /// </summary>
    public static VocabularyCoverage Coverage(IEnumerable<Rule> rules, TopicMap topicMap)
    {
        var referenced = Extract(rules);
        var declared = topicMap.Topics
            .Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);

        // Filter axis topics like "jurisdiction:hungary" into bare "jurisdiction"
        // so analysis works on the primary axis only — axis topics are loose tags.
        var referencedPrimary = referenced
            .Where(t => !t.Contains(':', StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var missing = referencedPrimary
            .Where(t => !declared.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        var unused = declared
            .Where(t => !referencedPrimary.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        return new VocabularyCoverage(
            Referenced: referenced.OrderBy(t => t, StringComparer.Ordinal).ToList(),
            Declared: declared.OrderBy(t => t, StringComparer.Ordinal).ToList(),
            MissingFromMap: missing,
            UnusedInRules: unused);
    }
}

public sealed record VocabularyCoverage(
    IReadOnlyList<string> Referenced,
    IReadOnlyList<string> Declared,
    IReadOnlyList<string> MissingFromMap,
    IReadOnlyList<string> UnusedInRules)
{
    public bool IsFullyCovered => MissingFromMap.Count == 0;
}
