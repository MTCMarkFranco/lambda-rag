using LambdaRag.Core.Domain;

namespace LambdaRag.Core.Facts;

/// <summary>
/// Pillar 12 (#153) — the merged fact view a rule's lambda sees at Pass 2.
/// A <see cref="FactBag"/> is the union of every scoped section's per-
/// section fact bag, resolved per the documented merge semantics:
/// <list type="bullet">
///   <item><b>Boolean</b>: OR — once <c>true</c> anywhere, the union is
///     <c>true</c>. A <c>false</c> only survives when no section said
///     <c>true</c>. Missing everywhere → <c>null</c>.</item>
///   <item><b>Duration / Integer</b>: MIN — the tightest requirement wins.</item>
///   <item><b>Enum / Text</b>: first-non-null in section-id order —
///     stable, and conflicts are recorded in <see cref="Conflicts"/>.</item>
/// </list>
/// Every conflict is captured for audit and rendered into the verdict's
/// <c>EvaluatedInput</c> so reviewers can see how a value was resolved.
/// </summary>
public sealed class FactBag
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly List<FactConflict> _conflicts = new();

    public IReadOnlyDictionary<string, object?> Values => _values;
    public IReadOnlyList<FactConflict> Conflicts => _conflicts;

    /// <summary>Look up a fact; returns null when the concept is undecided.</summary>
    public object? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;

    /// <summary>
    /// Fold one section's per-concept values into the bag, respecting merge
    /// semantics keyed on the concept type in <paramref name="schema"/>.
    /// Values outside the schema are silently dropped.
    /// </summary>
    public void Merge(
        string sectionId,
        IReadOnlyDictionary<string, object?> sectionFacts,
        FactSchema schema)
    {
        ArgumentNullException.ThrowIfNull(sectionId);
        ArgumentNullException.ThrowIfNull(sectionFacts);
        ArgumentNullException.ThrowIfNull(schema);

        foreach (var concept in schema.Concepts)
        {
            if (!sectionFacts.TryGetValue(concept.Name, out var incoming) || incoming is null)
                continue;
            var hasExisting = _values.TryGetValue(concept.Name, out var current);
            var resolved = ResolveMerge(concept, current, hasExisting, incoming, sectionId, out var conflict);
            _values[concept.Name] = resolved;
            if (conflict is not null) _conflicts.Add(conflict);
        }
    }

    private static object? ResolveMerge(
        FactConcept concept,
        object? current,
        bool hasExisting,
        object incoming,
        string sectionId,
        out FactConflict? conflict)
    {
        conflict = null;
        if (!hasExisting || current is null)
            return incoming;

        switch (concept.Type)
        {
            case FactType.Boolean:
            {
                var a = ToBool(current);
                var b = ToBool(incoming);
                if (a is null) return incoming;
                if (b is null) return current;
                var merged = a.Value || b.Value;
                if (a.Value != b.Value)
                    conflict = new FactConflict(concept.Name, current, incoming, sectionId, "boolean_or", merged);
                return merged;
            }
            case FactType.Integer:
            case FactType.Duration:
            {
                var a = ToLong(current);
                var b = ToLong(incoming);
                if (a is null) return incoming;
                if (b is null) return current;
                var merged = Math.Min(a.Value, b.Value);
                if (a.Value != b.Value)
                    conflict = new FactConflict(concept.Name, current, incoming, sectionId, "min", merged);
                return merged;
            }
            case FactType.Enum:
            case FactType.Text:
            default:
            {
                var aStr = current?.ToString();
                var bStr = incoming.ToString();
                if (!string.Equals(aStr, bStr, StringComparison.Ordinal))
                    conflict = new FactConflict(concept.Name, current, incoming, sectionId, "first_non_null", current);
                return current;
            }
        }
    }

    private static bool? ToBool(object? o) => o switch
    {
        null => null,
        bool b => b,
        string s when bool.TryParse(s, out var b) => b,
        _ => null,
    };

    private static long? ToLong(object? o) => o switch
    {
        null => null,
        long l => l,
        int i => i,
        double d when d == Math.Floor(d) => (long)d,
        string s when long.TryParse(s, out var l) => l,
        _ => null,
    };
}

/// <summary>
/// Records a per-concept merge conflict for audit trail purposes.
/// </summary>
public sealed record FactConflict(
    string ConceptName,
    object? Existing,
    object Incoming,
    string IncomingSectionId,
    string Resolver,
    object? Resolved);
