namespace LambdaRag.Core.Domain;

/// <summary>
/// Pillar 1 (#116) — deterministic resolution of an artifact's "doc kind"
/// before evaluation. Pure code, no LLM, no I/O beyond the inputs given.
///
/// Resolution precedence (first hit wins):
///   1. Explicit override (CLI / API caller).
///   2. Filename / path heuristic.
///   3. Heading-bigram classifier over the first <see cref="ClassifierWindow"/>
///      blocks — runs against a small signed dictionary baked into this
///      class. Returns the kind with the highest bigram-hit count, ties
///      broken by lowest declared order. Never returns null when at least
///      one bigram fires.
///
/// Output ids are stable strings — they participate in
/// <see cref="Rule.AppliesToDocKinds"/> and the rule-gate decision in
/// <c>EvaluationService</c>, so changing them is a breaking ruleset-format
/// change. Add new kinds at the bottom of the dictionary; never rename.
/// </summary>
public static class DocKindResolver
{
    /// <summary>The default returned when nothing fires. Treat as "unknown".</summary>
    public const string Unknown = "unknown";

    /// <summary>Number of leading blocks the heading classifier inspects.</summary>
    public const int ClassifierWindow = 60;

    // ------------------------------------------------------------------
    // Signed dictionaries. Edits must be reviewed; bigrams are matched
    // case-insensitively against headings and the first sentence of
    // adjacent body blocks. Order is the tiebreaker.
    // ------------------------------------------------------------------

    private static readonly (string Kind, string PathFragment)[] PathHeuristics =
    {
        ("arb-psa",            "samples/architecture"),
        ("arb-psa",            "samples\\architecture"),
        ("arb-psa",            "rulesets/architecture-review"),
        ("arb-psa",            "rulesets\\architecture-review"),
        ("contract",           "samples/contracts"),
        ("contract",           "samples\\contracts"),
        ("contract",           "rulesets/contracts"),
        ("contract",           "rulesets\\contracts"),
        ("fsi",                "tests/Goldens/corpus/fsi"),
        ("gov-architecture",   "tests/Goldens/corpus/gov-architecture"),
        ("oil-gas",            "tests/Goldens/corpus/oil-gas"),
        ("permitting",         "tests/Goldens/corpus/permitting"),
    };

    private static readonly (string Kind, string Bigram)[] HeadingBigrams =
    {
        // ARB-PSA — Project Solution Architecture / Architecture Review Board
        ("arb-psa", "project solution"),
        ("arb-psa", "solution architecture"),
        ("arb-psa", "architecture review"),
        ("arb-psa", "review board"),
        ("arb-psa", "psa guide"),
        ("arb-psa", "arb intake"),
        ("arb-psa", "design patterns"),
        ("arb-psa", "decision records"),
        ("arb-psa", "architecture risks"),
        ("arb-psa", "architecture constraints"),
        ("arb-psa", "data security"),
        ("arb-psa", "information governance"),
        ("arb-psa", "security architecture"),
        ("arb-psa", "infrastructure architecture"),
        ("arb-psa", "dr & resiliency"),
        ("arb-psa", "dr and resiliency"),
        ("arb-psa", "non-functional"),

        // contract
        ("contract", "master services"),
        ("contract", "services agreement"),
        ("contract", "payment terms"),
        ("contract", "governing law"),
        ("contract", "limitation of liability"),
        ("contract", "confidentiality clause"),

        // gov-architecture — Cloud Guardrails / Treasury Board flavour
        ("gov-architecture", "cloud guardrails"),
        ("gov-architecture", "treasury board"),
        ("gov-architecture", "protected b"),

        // fsi — Basel / OSFI flavour
        ("fsi", "guideline b-10"),
        ("fsi", "third-party risk"),
        ("fsi", "model risk"),

        // permitting
        ("permitting", "building code"),
        ("permitting", "impact assessment"),

        // oil-gas
        ("oil-gas", "pipeline regulations"),
        ("oil-gas", "well integrity"),
        ("oil-gas", "methane regulations"),
    };

    /// <summary>
    /// Resolve a doc kind. <paramref name="explicitKind"/> short-circuits
    /// the heuristic chain when non-null/non-blank. <paramref name="path"/>
    /// is normalised for path-fragment matching. <paramref name="parsed"/>
    /// feeds the heading-bigram classifier; pass <c>null</c> when no parse
    /// is available (the resolver then falls back to <see cref="Unknown"/>).
    /// </summary>
    public static string Resolve(string? explicitKind, string? path, ParsedDocument? parsed)
    {
        if (!string.IsNullOrWhiteSpace(explicitKind))
            return explicitKind.Trim();

        if (!string.IsNullOrWhiteSpace(path))
        {
            var norm = path.Replace('\\', '/');
            foreach (var (kind, fragment) in PathHeuristics)
            {
                var nFragment = fragment.Replace('\\', '/');
                if (norm.Contains(nFragment, StringComparison.OrdinalIgnoreCase))
                    return kind;
            }
        }

        if (parsed is not null)
        {
            var heading = ClassifyByHeadings(parsed);
            if (heading is not null) return heading;
        }

        return Unknown;
    }

    /// <summary>
    /// Score the first <see cref="ClassifierWindow"/> blocks against the
    /// signed bigram dictionary. Returns the highest-scoring kind, or
    /// <c>null</c> when nothing fires. Exposed for unit tests.
    /// </summary>
    public static string? ClassifyByHeadings(ParsedDocument parsed)
    {
        if (parsed is null || parsed.Blocks.Count == 0) return null;

        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        var firstSeenIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        var take = Math.Min(parsed.Blocks.Count, ClassifierWindow);
        for (var i = 0; i < take; i++)
        {
            var lower = parsed.Blocks[i].Text.ToLowerInvariant();
            if (string.IsNullOrEmpty(lower)) continue;
            for (var b = 0; b < HeadingBigrams.Length; b++)
            {
                var (kind, bigram) = HeadingBigrams[b];
                if (!lower.Contains(bigram, StringComparison.Ordinal)) continue;
                hits[kind] = hits.GetValueOrDefault(kind) + 1;
                if (!firstSeenIndex.ContainsKey(kind))
                    firstSeenIndex[kind] = b;
            }
        }

        if (hits.Count == 0) return null;

        // Highest hit count wins; ties broken by earlier first-seen in
        // the signed dictionary (i.e. stable in source order).
        return hits
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => firstSeenIndex[kv.Key])
            .First()
            .Key;
    }

    /// <summary>
    /// Return true if <paramref name="ruleKinds"/> + <paramref name="rulesetKinds"/>
    /// together permit <paramref name="docKind"/>. Null/empty on both sides
    /// = "applies to everything" (backward-compatible default).
    /// </summary>
    public static bool Applies(
        IReadOnlyList<string>? ruleKinds,
        IReadOnlyList<string>? rulesetKinds,
        string? docKind)
    {
        // Union semantics: union(rule, ruleset). Empty union → applies to all.
        var any = false;
        if (rulesetKinds is { Count: > 0 })
        {
            foreach (var k in rulesetKinds)
            {
                if (!string.IsNullOrWhiteSpace(k))
                {
                    any = true;
                    if (string.Equals(k.Trim(), docKind, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        if (ruleKinds is { Count: > 0 })
        {
            foreach (var k in ruleKinds)
            {
                if (!string.IsNullOrWhiteSpace(k))
                {
                    any = true;
                    if (string.Equals(k.Trim(), docKind, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        return !any;
    }
}
