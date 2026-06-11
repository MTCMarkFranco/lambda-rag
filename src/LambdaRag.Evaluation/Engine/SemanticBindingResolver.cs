using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Semantic;

namespace LambdaRag.Evaluation.Engine;

/// <summary>
/// Pillar 6 (#124) — resolves semantic bindings between a rule's
/// <see cref="SemanticAnchor"/>s and the tokens of a candidate section.
///
/// The resolver tokenizes the section body once (via
/// <see cref="LambdaRag.Projection.SemanticTokenizer"/>), embeds each
/// token via the injected <see cref="ITokenEmbedder"/> (with the
/// in-memory caches below ensuring identical inputs are embedded once
/// per process), and emits one <see cref="BindingRecord"/> per
/// (anchor, token) pair whose cosine ≥ anchor.threshold.
///
/// Determinism contract:
///   • Tokenizer is pinned by <see cref="LambdaRag.Projection.SemanticTokenizer.TokenizerVersion"/>.
///   • The embedder is itself deterministic (its own file-backed cache).
///   • Cache keys fold tokenizer version + embedder id so a drift in
///     either invalidates cached entries — see <see cref="TokenCacheKey"/>.
///   • All cosine math goes through <see cref="SemanticFunctions.Cosine"/>.
/// </summary>
public sealed class SemanticBindingResolver
{
    private readonly ITokenEmbedder _embedder;

    // Per-process in-memory caches. The embedder itself is expected to be
    // file-backed for cross-run determinism; these dictionaries just avoid
    // re-hitting the embedder for repeated tokens within a run.
    private readonly ConcurrentDictionary<string, float[]> _tokenVecCache
        = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, float[]> _anchorVecCache
        = new(StringComparer.Ordinal);

    public SemanticBindingResolver(ITokenEmbedder embedder)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    }

    /// <summary>
    /// Resolve every anchor on <paramref name="anchors"/> against the
    /// tokens of <paramref name="sectionText"/>. Returns a (per-anchor
    /// bindings map, flat record list) pair. Anchors with null/empty
    /// names or text are silently skipped.
    /// </summary>
    public async Task<(IReadOnlyDictionary<string, IReadOnlyList<TokenMatch>> Bindings,
                       IReadOnlyList<BindingRecord> Records)>
        ResolveAsync(
            IReadOnlyList<SemanticAnchor> anchors,
            string sectionText,
            CancellationToken ct = default)
    {
        if (anchors is null || anchors.Count == 0 || string.IsNullOrEmpty(sectionText))
        {
            return (
                new Dictionary<string, IReadOnlyList<TokenMatch>>(StringComparer.Ordinal),
                Array.Empty<BindingRecord>());
        }

        // Tokenize once for the union of all anchor n-gram orders so we
        // only walk the text a single time.
        var orderUnion = new SortedSet<int>();
        foreach (var a in anchors)
        {
            if (a.Ngram is null) { orderUnion.Add(1); orderUnion.Add(2); }
            else foreach (var n in a.Ngram) orderUnion.Add(n);
        }
        if (orderUnion.Count == 0) { orderUnion.Add(1); orderUnion.Add(2); }

        var tokens = LambdaRag.Projection.SemanticTokenizer.Tokenize(sectionText, orderUnion);
        if (tokens.Count == 0)
        {
            return (
                new Dictionary<string, IReadOnlyList<TokenMatch>>(StringComparer.Ordinal),
                Array.Empty<BindingRecord>());
        }

        // Pre-embed all unique token surface forms once.
        var distinct = tokens.Select(t => t.Text).Distinct(StringComparer.Ordinal).ToList();
        var tokenVecs = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var text in distinct)
        {
            ct.ThrowIfCancellationRequested();
            tokenVecs[text] = await GetOrEmbedAsync(_tokenVecCache, text, ct).ConfigureAwait(false);
        }

        var bindings = new Dictionary<string, IReadOnlyList<TokenMatch>>(StringComparer.Ordinal);
        var records = new List<BindingRecord>();

        foreach (var anchor in anchors.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(anchor.Name) || string.IsNullOrWhiteSpace(anchor.AnchorText))
                continue;

            var anchorVec = anchor.AnchorEmbedding is { Count: > 0 }
                ? anchor.AnchorEmbedding.ToArray()
                : await GetOrEmbedAsync(_anchorVecCache, anchor.AnchorText, ct).ConfigureAwait(false);

            var allowedOrders = anchor.Ngram is null
                ? new HashSet<int> { 1, 2 }
                : new HashSet<int>(anchor.Ngram);

            var matches = new List<TokenMatch>();
            foreach (var t in tokens)
            {
                if (!allowedOrders.Contains(t.Ngram)) continue;
                if (!tokenVecs.TryGetValue(t.Text, out var tv)) continue;
                var cos = SemanticFunctions.Cosine(anchorVec, tv);
                if (cos < anchor.Threshold) continue;
                matches.Add(new TokenMatch(t.Text, cos, t.CharStart, t.CharLength));
            }

            // Deterministic ordering of bindings: cosine desc, then char
            // position asc, then text asc. Top-8 cap keeps verdict JSON
            // bounded for evidence quoting; runtime lambda still sees the
            // full list via the in-memory scope.
            var ordered = matches
                .OrderByDescending(m => m.Cosine)
                .ThenBy(m => m.CharStart)
                .ThenBy(m => m.Text, StringComparer.Ordinal)
                .ToList();
            bindings[anchor.Name] = ordered;

            // Record top-3 per anchor for the verdict — enough evidence
            // for audit without ballooning the JSON.
            foreach (var m in ordered.Take(3))
            {
                records.Add(new BindingRecord(
                    Anchor: anchor.Name,
                    Matched: m.Text,
                    Cosine: Math.Round(m.Cosine, 6),
                    CharStart: m.CharStart,
                    CharLength: m.CharLength));
            }
        }

        // Stable record order across runs.
        records.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.Anchor, b.Anchor);
            if (c != 0) return c;
            c = b.Cosine.CompareTo(a.Cosine);
            if (c != 0) return c;
            c = a.CharStart.CompareTo(b.CharStart);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Matched, b.Matched);
        });

        return (bindings, records);
    }

    private async Task<float[]> GetOrEmbedAsync(
        ConcurrentDictionary<string, float[]> cache,
        string text,
        CancellationToken ct)
    {
        var key = TokenCacheKey(text);
        if (cache.TryGetValue(key, out var hit)) return hit;
        var v = await _embedder.EmbedAsync(text, ct).ConfigureAwait(false);
        cache[key] = v;
        return v;
    }

    /// <summary>
    /// Cache key folds the tokenizer version + embedder id so a drift in
    /// either invalidates entries (the per-process dict is rebuilt each
    /// run anyway; this future-proofs a possible persistent cache).
    /// </summary>
    private string TokenCacheKey(string text)
    {
        var raw = $"{LambdaRag.Projection.SemanticTokenizer.TokenizerVersion}|{_embedder.EmbedderId}|{text}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }
}
