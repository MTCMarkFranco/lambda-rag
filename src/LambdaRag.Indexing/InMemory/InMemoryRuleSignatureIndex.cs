using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Indexing.Abstractions;
using LambdaRag.Indexing.Signatures;

namespace LambdaRag.Indexing.InMemory;

/// <summary>
/// In-memory rule signature index. Builds two inverted maps:
/// <list type="bullet">
///   <item><c>(fieldPath, literal)</c> → ruleIds — for equality predicates</item>
///   <item><c>fieldPath</c> → list of (ruleId, literal) — for Contains predicates</item>
/// </list>
/// plus a universal bucket for rules that could not be parsed (always
/// candidates). Lookup returns the union of (a) all rules whose equality
/// constraints are satisfied by some section field, (b) all rules whose
/// Contains literals appear as substrings, and (c) the universal bucket.
///
/// Determinism: build is idempotent for a given RuleSet; Lookup result
/// order is fixed (ordinal by rule id).
/// </summary>
public sealed class InMemoryRuleSignatureIndex : IRuleSignatureIndex, ICandidateRuleFilter
{
    public string IndexId => "in-memory:signatures";
    public string FilterId => IndexId;
    public bool IsReady => _signatures.Count > 0;
    public int RuleCount => _signatures.Count;
    public int UniversalCount => _universal.Count;

    public IReadOnlyCollection<string> LookupCandidates(JsonNode section) => Lookup(section);

    private readonly ConcurrentDictionary<string, RuleSignature> _signatures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _universal = new(StringComparer.Ordinal);
    // (fieldPath, literal) -> ordered set of ruleIds
    private readonly Dictionary<(string Path, string Literal), SortedSet<string>> _equality = new();
    // fieldPath -> list of (ruleId, literal)
    private readonly Dictionary<string, List<(string RuleId, string Literal)>> _contains = new(StringComparer.Ordinal);

    public void Build(RuleSet ruleSet)
    {
        _signatures.Clear();
        _universal.Clear();
        _equality.Clear();
        _contains.Clear();

        foreach (var rule in ruleSet.Rules)
        {
            var sig = PredicateSignatureExtractor.Extract(rule);
            _signatures[rule.Id] = sig;

            if (sig.Universal)
            {
                _universal.Add(rule.Id);
                continue;
            }

            foreach (var eq in sig.Equalities)
            {
                if (!_equality.TryGetValue((eq.FieldPath, eq.Literal), out var set))
                {
                    set = new SortedSet<string>(StringComparer.Ordinal);
                    _equality[(eq.FieldPath, eq.Literal)] = set;
                }
                set.Add(rule.Id);
            }
            foreach (var c in sig.Containments)
            {
                if (!_contains.TryGetValue(c.FieldPath, out var list))
                {
                    list = new List<(string, string)>();
                    _contains[c.FieldPath] = list;
                }
                list.Add((rule.Id, c.Literal));
            }
        }
    }

    public IReadOnlyList<string> Lookup(JsonNode section)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);

        // Universal bucket: rules with predicate=true or unparseable predicates.
        foreach (var id in _universal) result.Add(id);

        if (section is not JsonObject obj)
        {
            // Anonymous value — only universal rules can match without field access.
            return result.ToList();
        }

        // Walk every (fieldPath, literal) equality bucket and check if the
        // section actually carries that pair. We iterate the index keys
        // because the typical field set is small (category, severity, ...).
        foreach (var ((path, literal), ids) in _equality)
        {
            var actual = ResolveString(obj, path);
            if (actual is not null && string.Equals(actual, literal, StringComparison.Ordinal))
            {
                foreach (var id in ids) result.Add(id);
            }
        }

        // Contains: probe each indexed field once, check substring per rule.
        foreach (var (path, list) in _contains)
        {
            var actual = ResolveString(obj, path);
            if (actual is null) continue;
            foreach (var (ruleId, literal) in list)
            {
                if (actual.Contains(literal, StringComparison.Ordinal))
                {
                    result.Add(ruleId);
                }
            }
        }

        return result.ToList();
    }

    public RuleSignature? GetSignature(string ruleId) =>
        _signatures.TryGetValue(ruleId, out var sig) ? sig : null;

    private static string? ResolveString(JsonObject root, string path)
    {
        // path begins with "input1." — strip and walk.
        const string prefix = "input1.";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        JsonNode? cursor = root;
        foreach (var part in path[prefix.Length..].Split('.'))
        {
            if (cursor is not JsonObject inner) return null;
            cursor = inner[part];
            if (cursor is null) return null;
        }
        return cursor switch
        {
            JsonValue jv => jv.ToString(),
            null => null,
            _ => cursor.ToJsonString(),
        };
    }
}
