using LambdaRag.Core.Hashing;

namespace LambdaRag.Core.Domain;

/// <summary>
/// Pillar 12 (#153) — the closed set of fact types the LLM classifier is
/// permitted to emit for a section. Each type maps to a specific
/// deterministic-evaluation shape on our side:
/// <list type="bullet">
///   <item><see cref="Boolean"/>: <c>true</c>/<c>false</c>/<c>null</c>.</item>
///   <item><see cref="Enum"/>: one of the declared <c>EnumValues</c> or null.</item>
///   <item><see cref="Integer"/>: whole number or null.</item>
///   <item><see cref="Duration"/>: verbatim phrase (LLM); a deterministic
///     normalizer maps to ISO-8601 (e.g. <c>P90D</c>) or leaves as-is.</item>
///   <item><see cref="Text"/>: free string ≤ 200 chars; no downstream logic.</item>
/// </list>
/// </summary>
public enum FactType { Boolean, Enum, Integer, Duration, Text }

/// <summary>
/// A single fact concept declared by a <see cref="FactSchema"/>. The LLM
/// classifier emits at most one value per concept per section and never
/// invents new concepts — everything outside this schema is dropped.
/// </summary>
public sealed record FactConcept(
    string Name,
    FactType Type,
    string Description)
{
    /// <summary>Few-shot exemplars folded into the Pass-1 prompt.</summary>
    public IReadOnlyList<string>? Examples { get; init; }

    /// <summary>Closed set of allowed values when <see cref="Type"/> is <see cref="FactType.Enum"/>.</summary>
    public IReadOnlyList<string>? EnumValues { get; init; }

    /// <summary>
    /// Named deterministic normalizer applied post-LLM. Recognized values:
    /// <c>"duration-iso8601"</c>, <c>"integer-days"</c>. Null → no normalization
    /// (the verbatim phrase passes through to the fact bag).
    /// </summary>
    public string? Normalizer { get; init; }

    internal string FingerprintPart()
    {
        var examples = Examples is { Count: > 0 }
            ? string.Join("\u001e", Examples)
            : string.Empty;
        var enums = EnumValues is { Count: > 0 }
            ? string.Join("\u001e", EnumValues.OrderBy(v => v, StringComparer.Ordinal))
            : string.Empty;
        return string.Join(
            "\u001f",
            Name,
            Type.ToString(),
            Description,
            examples,
            enums,
            Normalizer ?? string.Empty);
    }
}

/// <summary>
/// Pillar 12 (#153) — the fixed set of concepts a ruleset asks the LLM
/// classifier to populate per section. Immutable; identity is expressed by
/// <see cref="Fingerprint"/>, which is folded into
/// <see cref="RuleSet.Fingerprint"/> so schema drift invalidates every
/// pre-Pillar-12 sidecar cache loudly.
/// </summary>
public sealed record FactSchema(
    string Id,
    string Version,
    IReadOnlyList<FactConcept> Concepts)
{
    public ContentHash Fingerprint()
    {
        var parts = new List<string> { Id, Version };
        foreach (var c in Concepts.OrderBy(c => c.Name, StringComparer.Ordinal))
            parts.Add("concept:" + c.FingerprintPart());
        return ContentHash.Compose(parts.ToArray());
    }
}
