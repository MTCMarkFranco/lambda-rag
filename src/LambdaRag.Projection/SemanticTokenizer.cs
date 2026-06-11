using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LambdaRag.Core.Domain;

namespace LambdaRag.Projection;

/// <summary>
/// Pillar 6 (#124) — deterministic, sentence-aware semantic tokenizer.
///
/// Tokenization is pure code: trim, lowercase (invariant culture), strip
/// punctuation, drop English stopwords from the signed list packaged as
/// <c>data/stopwords-en.v1.txt</c>, then emit unigrams + bigrams by
/// default. Trigrams are opt-in per anchor.
///
/// Determinism contract:
///   • <see cref="TokenizerVersion"/> is folded into projection cache keys
///     so changing this class invalidates downstream cached projections.
///   • <see cref="StopwordHash"/> is the SHA-256 of the signed stopword
///     bytes and is emitted into projection metadata so an auditor can
///     re-derive the token set.
///   • Output is capped at <see cref="MaxTokensPerSection"/> per section
///     ranked by TF (descending) then by char position ascending — pure
///     deterministic tie-breaks.
/// </summary>
public static class SemanticTokenizer
{
    /// <summary>Pinned tokenizer version — bumping breaks projection-cache identity.</summary>
    public const string TokenizerVersion = "semantic-tokenizer-v1";

    /// <summary>Cap on emitted tokens per section, ranked by TF then char position.</summary>
    public const int MaxTokensPerSection = 256;

    private static readonly Lazy<HashSet<string>> _stopwords = new(LoadStopwords);
    private static readonly Lazy<string> _stopwordHash = new(ComputeStopwordHash);

    /// <summary>The English stopword set bundled with this tokenizer.</summary>
    public static IReadOnlySet<string> Stopwords => _stopwords.Value;

    /// <summary>
    /// SHA-256 (lowercase hex) of the canonicalized stopword bytes (one word
    /// per line, lowercase, LF-separated, no trailing blank). Surfaced into
    /// projection metadata so audit can prove which list was used.
    /// </summary>
    public static string StopwordHash => _stopwordHash.Value;

    // Sentence terminator: . ! ? plus newlines. Kept conservative so we
    // don't over-segment numbered headings ("3.2 Standards" → one segment).
    private static readonly Regex SentenceSplitRx = new(
        @"(?<=[.!?])\s+|\r?\n+",
        RegexOptions.Compiled);

    // A "word" is a maximal run of letters / digits, optionally containing
    // internal hyphens. Numbers ("4", "120") survive — useful for binding
    // RTO/RPO/percent style anchors. Underscores split (heading-ids).
    private static readonly Regex WordRx = new(
        @"[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*",
        RegexOptions.Compiled);

    /// <summary>
    /// Tokenize <paramref name="text"/> into ordered unigrams (+ bigrams /
    /// trigrams as requested) with character spans relative to the input.
    /// Returns at most <see cref="MaxTokensPerSection"/> tokens, ranked by
    /// term frequency (desc) then earliest char position (asc) — both
    /// ties broken deterministically. Embedding fields are left null;
    /// embedding is the caller's responsibility (see EvaluationService).
    /// </summary>
    /// <param name="text">Section body text.</param>
    /// <param name="ngrams">N-gram orders to emit. Defaults to {1, 2}.</param>
    public static IReadOnlyList<TokenEmbedding> Tokenize(string text, IReadOnlyCollection<int>? ngrams = null)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<TokenEmbedding>();

        var orders = NormalizeOrders(ngrams);
        var stop = _stopwords.Value;

        // Walk sentences so n-grams never cross sentence boundaries —
        // "...failover. Recovery..." must not emit "failover recovery".
        var raw = new List<(string Text, int Ngram, int CharStart, int CharLength)>();
        var cursor = 0;
        foreach (var sentence in SentenceSplitRx.Split(text))
        {
            if (sentence.Length == 0)
            {
                cursor = NextCursor(text, cursor, sentence);
                continue;
            }
            var sentenceStart = text.IndexOf(sentence, cursor, StringComparison.Ordinal);
            if (sentenceStart < 0) sentenceStart = cursor;
            cursor = sentenceStart + sentence.Length;

            // Per-sentence word collection with absolute spans.
            var words = new List<(string Lower, int Start, int Length)>();
            foreach (Match m in WordRx.Matches(sentence))
            {
                var lower = m.Value.ToLowerInvariant();
                if (lower.Length == 1 && !char.IsLetterOrDigit(lower[0])) continue;
                if (stop.Contains(lower)) continue;
                if (IsNumericNoise(lower)) continue;
                words.Add((lower, sentenceStart + m.Index, m.Length));
            }

            foreach (var n in orders)
            {
                if (words.Count < n) continue;
                for (var i = 0; i + n <= words.Count; i++)
                {
                    var first = words[i];
                    var last = words[i + n - 1];
                    var ngramText = n == 1
                        ? first.Lower
                        : string.Join(' ', words.GetRange(i, n).Select(w => w.Lower));
                    var charStart = first.Start;
                    var charLength = last.Start + last.Length - first.Start;
                    raw.Add((ngramText, n, charStart, charLength));
                }
            }
        }

        if (raw.Count == 0) return Array.Empty<TokenEmbedding>();

        // TF-rank cap. Group by (text, ngram) — same surface form at
        // different positions counts toward TF. We keep the *earliest*
        // span as the canonical surface so spans are stable & deterministic.
        var grouped = raw
            .GroupBy(t => (t.Text, t.Ngram))
            .Select(g =>
            {
                var head = g.OrderBy(x => x.CharStart).First();
                return new
                {
                    head.Text,
                    head.Ngram,
                    head.CharStart,
                    head.CharLength,
                    Tf = g.Count(),
                };
            })
            // Deterministic ordering: TF desc, then char position asc,
            // then text asc (final tie-break — irrelevant in practice but
            // guarantees byte-identical lists across runs).
            .OrderByDescending(t => t.Tf)
            .ThenBy(t => t.CharStart)
            .ThenBy(t => t.Text, StringComparer.Ordinal)
            .Take(MaxTokensPerSection)
            .ToList();

        var result = new TokenEmbedding[grouped.Count];
        for (var i = 0; i < grouped.Count; i++)
        {
            var t = grouped[i];
            result[i] = new TokenEmbedding(t.Text, t.Ngram, t.CharStart, t.CharLength);
        }
        return result;
    }

    private static int NextCursor(string text, int cursor, string fragment)
    {
        if (fragment.Length == 0) return cursor;
        var idx = text.IndexOf(fragment, cursor, StringComparison.Ordinal);
        return idx < 0 ? cursor : idx + fragment.Length;
    }

    private static bool IsNumericNoise(string token)
    {
        // Pure-numeric tokens of 1-2 digits are noise (page numbers, list
        // markers); 3+ digit numbers and any alphanumeric pass through so
        // anchors like "rpo 4 hours" still bind.
        if (token.Length > 2) return false;
        foreach (var c in token) if (!char.IsDigit(c)) return false;
        return true;
    }

    private static IReadOnlyList<int> NormalizeOrders(IReadOnlyCollection<int>? ngrams)
    {
        if (ngrams is null || ngrams.Count == 0) return new[] { 1, 2 };
        var set = new SortedSet<int>();
        foreach (var n in ngrams) if (n is >= 1 and <= 3) set.Add(n);
        return set.Count == 0 ? new[] { 1, 2 } : set.ToArray();
    }

    private static HashSet<string> LoadStopwords()
    {
        var asm = typeof(SemanticTokenizer).Assembly;
        // Embedded resource name is "<DefaultNamespace>.data.stopwords-en.v1.txt"
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("stopwords-en.v1.txt", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("stopwords-en.v1.txt embedded resource is missing.");
        using var s = asm.GetManifestResourceStream(name)!;
        using var rdr = new StreamReader(s, Encoding.UTF8);
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (rdr.ReadLine() is { } line)
        {
            var w = line.Trim().ToLowerInvariant();
            if (w.Length > 0) set.Add(w);
        }
        return set;
    }

    private static string ComputeStopwordHash()
    {
        // Canonicalise: sorted unique lines, LF-joined, no trailing newline.
        var sorted = _stopwords.Value.OrderBy(w => w, StringComparer.Ordinal);
        var canonical = string.Join("\n", sorted);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
