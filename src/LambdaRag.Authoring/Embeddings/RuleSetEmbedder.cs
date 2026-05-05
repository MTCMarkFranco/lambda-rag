using System.Text.RegularExpressions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Semantic;

namespace LambdaRag.Authoring.Embeddings;

/// <summary>
/// Walks a <see cref="RuleSet"/> at authoring/load time and pre-embeds every
/// piece of text the runtime semantic predicates may need to look up:
///
///   • each rule's <see cref="Rule.NaturalLanguage"/> description, keyed as
///     <c>rule:{ruleId}</c> in the resulting store. Used by the evaluator's
///     applicability gate (<c>cosine(rule.descVec, section.vec) &gt;=
///     GateThreshold</c>).
///   • every concept literal embedded in a rule's lambda expression — both
///     <c>SemanticFunctions.ContainsMeaning(input1.id, "concept", t)</c> and
///     <c>SemanticFunctions.MatchesAnyMeaning(input1.id, "a|b|c", t)</c>.
///     Concepts are keyed by their raw concept text so the runtime store
///     match is exact.
///
/// The store is populated synchronously and idempotently — re-embedding the
/// same ruleset always produces the same vectors (cache hits) and the same
/// store shape, which is what makes downstream evaluation deterministic.
/// </summary>
public sealed class RuleSetEmbedder
{
    private readonly IRuleEmbedder _embedder;

    public RuleSetEmbedder(IRuleEmbedder embedder)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    }

    /// <summary>
    /// Pre-embed every concept and rule-description into a fresh
    /// <see cref="InMemorySemanticVectorStore"/>. Section vectors are added
    /// later by the projector — the returned store has no section entries.
    /// </summary>
    public async Task<InMemorySemanticVectorStore> EmbedAsync(
        RuleSet ruleset,
        CancellationToken ct = default)
    {
        if (ruleset is null) throw new ArgumentNullException(nameof(ruleset));

        var store = new InMemorySemanticVectorStore(_embedder.EmbedderId, _embedder.Dimensions);

        foreach (var rule in ruleset.Rules)
        {
            ct.ThrowIfCancellationRequested();

            // 1. Rule description (gate vector). We always embed it; the
            //    evaluator decides whether to read it based on GateThreshold.
            if (!string.IsNullOrWhiteSpace(rule.NaturalLanguage))
            {
                var descVec = await _embedder.EmbedAsync(rule.NaturalLanguage, ct).ConfigureAwait(false);
                store.AddConcept(RuleDescriptionKey(rule.Id), descVec);
            }

            // 2. Every concept literal in the lambda — exact text used as the
            //    runtime store key, so authoring-time and runtime keys agree.
            foreach (var concept in ExtractConcepts(rule.Lambda))
            {
                if (string.IsNullOrWhiteSpace(concept)) continue;
                if (store.TryGetConcept(concept, out _)) continue; // dedupe across rules
                var vec = await _embedder.EmbedAsync(concept, ct).ConfigureAwait(false);
                store.AddConcept(concept, vec);
            }
        }

        return store;
    }

    /// <summary>
    /// Stable concept-store key used for a rule's natural-language
    /// description. Kept in one place so the evaluator and the embedder
    /// cannot drift.
    /// </summary>
    public static string RuleDescriptionKey(string ruleId) => $"rule:{ruleId}";

    private static readonly Regex ContainsMeaningRx = new(
        @"SemanticFunctions\.ContainsMeaning\s*\(\s*[^,]+,\s*""((?:[^""\\]|\\.)*)""\s*,",
        RegexOptions.Compiled);

    private static readonly Regex MatchesAnyMeaningRx = new(
        @"SemanticFunctions\.MatchesAnyMeaning\s*\(\s*[^,]+,\s*""((?:[^""\\]|\\.)*)""\s*,",
        RegexOptions.Compiled);

    /// <summary>
    /// Pull every concept literal out of a lambda string. Yields each
    /// <c>ContainsMeaning</c> concept verbatim, and every pipe-delimited
    /// piece of each <c>MatchesAnyMeaning</c> argument as a separate concept.
    /// </summary>
    public static IEnumerable<string> ExtractConcepts(string? lambda)
    {
        if (string.IsNullOrEmpty(lambda)) yield break;

        foreach (Match m in ContainsMeaningRx.Matches(lambda))
        {
            var raw = m.Groups[1].Value;
            if (raw.Length > 0) yield return Regex.Unescape(raw);
        }

        foreach (Match m in MatchesAnyMeaningRx.Matches(lambda))
        {
            var raw = Regex.Unescape(m.Groups[1].Value);
            foreach (var piece in raw.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = piece.Trim();
                if (p.Length > 0) yield return p;
            }
        }
    }
}
