using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LambdaRag.Projection.Projectors;

/// <summary>
/// Domain-agnostic numeric / regex feature extraction for projected
/// section text. Produces a stable, JSON-serializable shape that any
/// ruleset (in any domain) can refer to from a lambda or predicate
/// expression — e.g. <c>input1.text_features.day_counts.Contains(45L)</c>
/// or <c>input1.text_features.dollar_amounts_max &gt;= 5000000</c>.
///
/// All extractors are pure regex over the section's body text. Output is
/// fully deterministic: arrays are de-duplicated and sorted ascending.
/// Nothing here is contract-specific or rule-specific; the same shape is
/// emitted for every section regardless of topic.
///
/// Determinism notes:
///   • Numbers are emitted as <c>long</c> when integral, <c>double</c>
///     otherwise — matching <see cref="Workflow.JsonToExpando"/>'s rules.
///   • All regexes are compiled and case-insensitive.
///   • Locale-independent number parsing (InvariantCulture) so a "1,200"
///     in narrative text doesn't drift between runs on machines with
///     different culture settings.
/// </summary>
public static class TextFeatureExtractor
{
    private static readonly Regex DayCountRx = new(
        @"\b(\d{1,4})[\s-]*(?:calendar\s+|business\s+|working\s+)?days?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MonthCountRx = new(
        @"\b(\d{1,3})\s*(?:calendar\s+)?months?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex YearCountRx = new(
        @"\b(\d{1,3})\s*(?:calendar\s+|fiscal\s+)?years?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PercentRx = new(
        @"(\d{1,3}(?:\.\d{1,4})?)\s*%(?:\s*per\s+(month|annum|year))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // $1,000 / $1.5M / $5 million / USD 1,000,000 / CAD$ 5,000,000
    private static readonly Regex DollarRx = new(
        @"(?:\$|USD\s*\$?|CAD\s*\$?|US\$|CAD\$)\s*(\d{1,3}(?:[,\s]\d{3})*(?:\.\d+)?)\s*(million|billion|[mbk])?(?![A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Spelled-out dollar amounts: "five million dollars" / "$5 million"
    private static readonly Regex DollarSpelledRx = new(
        @"(\d{1,3}(?:[,\s]\d{3})*(?:\.\d+)?)\s*(million|billion)\s*(?:dollars|USD|CAD)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Builds the <c>text_features</c> sub-object for one section.
    /// Returns an object even when no features were extracted (empty
    /// arrays) so rule authors can write Contains / .Length / .Max
    /// expressions without null-checking.
    /// </summary>
    public static JsonObject Extract(string text)
    {
        var dayCounts = SortedLongs(DayCountRx, text);
        var monthCounts = SortedLongs(MonthCountRx, text);
        var yearCounts = SortedLongs(YearCountRx, text);
        var percentValues = SortedDoubles(PercentRx, text, parseGroup: 1);
        var dollarAmounts = ExtractDollars(text);

        var features = new JsonObject
        {
            ["day_counts"] = ToArray(dayCounts),
            ["month_counts"] = ToArray(monthCounts),
            ["year_counts"] = ToArray(yearCounts),
            ["percent_values"] = ToArray(percentValues),
            ["dollar_amounts"] = ToArray(dollarAmounts),
        };

        // Convenience scalars for the most common comparisons. We always emit
        // them (with a deterministic 0 / 0.0 default when the underlying
        // array is empty) so that rule lambdas like
        //   `text_features.year_counts.Count > 0 && text_features.year_count_max <= 7`
        // compile cleanly. Previously these scalars were omitted when the
        // array was empty, which caused the C# expression compiler to bind
        // them as `System.Object`, producing
        //   "binary operator LessThanOrEqual is not defined for ..."
        // — a runtime parse exception silently surfaced as Fail.
        features["day_count_min"] = dayCounts.Count > 0 ? dayCounts[0] : 0L;
        features["day_count_max"] = dayCounts.Count > 0 ? dayCounts[^1] : 0L;
        features["month_count_min"] = monthCounts.Count > 0 ? monthCounts[0] : 0L;
        features["month_count_max"] = monthCounts.Count > 0 ? monthCounts[^1] : 0L;
        features["year_count_min"] = yearCounts.Count > 0 ? yearCounts[0] : 0L;
        features["year_count_max"] = yearCounts.Count > 0 ? yearCounts[^1] : 0L;
        features["percent_min"] = percentValues.Count > 0 ? percentValues[0] : 0.0;
        features["percent_max"] = percentValues.Count > 0 ? percentValues[^1] : 0.0;
        features["dollar_min"] = dollarAmounts.Count > 0 ? dollarAmounts[0] : 0L;
        features["dollar_max"] = dollarAmounts.Count > 0 ? dollarAmounts[^1] : 0L;

        // Per-keyword features: scan each line for a topical keyword AND a
        // feature, recording the max numeric value seen on a line that
        // matches both. Keys the rule author can rely on without having to
        // reason about other clauses in the same section. Always emitted
        // with a deterministic zero default for the same compile-time
        // safety reason as the global scalars above.
        var perKeywordDollar = ExtractDollarsByKeyword(text, KeywordsForDollar);
        foreach (var (keyword, _) in KeywordsForDollar)
            features[$"dollar_for_{keyword}"] = perKeywordDollar.GetValueOrDefault(keyword, 0L);

        var perKeywordDay = ExtractDayCountsByKeyword(text, KeywordsForDayCount);
        foreach (var (keyword, _) in KeywordsForDayCount)
            features[$"day_count_for_{keyword}"] = perKeywordDay.GetValueOrDefault(keyword, 0L);

        return features;
    }

    private static List<long> SortedLongs(Regex rx, string text)
    {
        var set = new SortedSet<long>();
        foreach (Match m in rx.Matches(text))
        {
            if (long.TryParse(m.Groups[1].Value.Replace(",", "").Replace(" ", ""),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                set.Add(n);
        }
        return [.. set];
    }

    private static List<double> SortedDoubles(Regex rx, string text, int parseGroup)
    {
        var set = new SortedSet<double>();
        foreach (Match m in rx.Matches(text))
        {
            if (double.TryParse(m.Groups[parseGroup].Value.Replace(",", "").Replace(" ", ""),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                set.Add(Math.Round(d, 4));
        }
        return [.. set];
    }

    private static List<long> ExtractDollars(string text)
    {
        var set = new SortedSet<long>();
        foreach (Match m in DollarRx.Matches(text))
        {
            if (TryParseDollar(m.Groups[1].Value, m.Groups[2].Value, out var amt))
                set.Add(amt);
        }
        foreach (Match m in DollarSpelledRx.Matches(text))
        {
            if (TryParseDollar(m.Groups[1].Value, m.Groups[2].Value, out var amt))
                set.Add(amt);
        }
        return [.. set];
    }

    private static bool TryParseDollar(string mantissa, string suffix, out long amount)
    {
        amount = 0;
        var clean = mantissa.Replace(",", "").Replace(" ", "");
        if (!double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return false;
        var multiplier = suffix?.ToLowerInvariant() switch
        {
            "k" => 1_000.0,
            "m" or "million" => 1_000_000.0,
            "b" or "billion" => 1_000_000_000.0,
            _ => 1.0,
        };
        amount = (long)Math.Round(d * multiplier);
        return true;
    }

    private static JsonArray ToArray(IEnumerable<long> values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        return arr;
    }

    private static JsonArray ToArray(IEnumerable<double> values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        return arr;
    }

    /// <summary>
    /// Per-keyword dollar-amount feature keys. Each entry is
    /// <c>(featureKey, anchorRegex)</c>: the rule lambda accesses
    /// <c>text_features.dollar_for_&lt;featureKey&gt;</c>, and the regex is
    /// matched against any line / sentence containing a $-amount to decide
    /// which keyword bucket the amount belongs to. Hardens insurance and
    /// limit rules against asymmetric figures (e.g., cyber=$2M while
    /// GCL=$1M would otherwise both pass an aggregate <c>dollar_max</c>
    /// test if either crossed the threshold).
    /// </summary>
    private static readonly IReadOnlyList<(string Key, Regex Anchor)> KeywordsForDollar = new[]
    {
        ("cyber", new Regex(@"\bcyber|network\s+security|privacy\s+liability\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("gcl", new Regex(@"\b(general\s+commercial\s+liability|general\s+liability|GCL|commercial\s+liability)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("professional", new Regex(@"\bprofessional\s+(?:liability|indemnity|errors)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    /// <summary>Per-keyword day-count features (Net N, payment terms, notice periods).</summary>
    private static readonly IReadOnlyList<(string Key, Regex Anchor)> KeywordsForDayCount = new[]
    {
        ("payment", new Regex(@"\b(pay|paid|payment|invoice|net)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("notice", new Regex(@"\b(notice|terminat|written)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    private static Dictionary<string, long> ExtractDollarsByKeyword(
        string text,
        IReadOnlyList<(string Key, Regex Anchor)> keywords)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return result;

        foreach (var line in SplitClauses(text))
        {
            // Combine $ regex + spelled regex per line.
            var dollarHits = new List<long>();
            foreach (Match m in DollarRx.Matches(line))
                if (TryParseDollar(m.Groups[1].Value, m.Groups[2].Value, out var amt)) dollarHits.Add(amt);
            foreach (Match m in DollarSpelledRx.Matches(line))
                if (TryParseDollar(m.Groups[1].Value, m.Groups[2].Value, out var amt)) dollarHits.Add(amt);
            if (dollarHits.Count == 0) continue;

            foreach (var (key, anchor) in keywords)
            {
                if (!anchor.IsMatch(line)) continue;
                var maxOnLine = dollarHits.Max();
                if (!result.TryGetValue(key, out var existing) || maxOnLine > existing)
                    result[key] = maxOnLine;
            }
        }
        return result;
    }

    private static Dictionary<string, long> ExtractDayCountsByKeyword(
        string text,
        IReadOnlyList<(string Key, Regex Anchor)> keywords)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return result;

        foreach (var line in SplitClauses(text))
        {
            var dayHits = new List<long>();
            foreach (Match m in DayCountRx.Matches(line))
                if (long.TryParse(m.Groups[1].Value.Replace(",", "").Replace(" ", ""),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    dayHits.Add(n);
            if (dayHits.Count == 0) continue;

            foreach (var (key, anchor) in keywords)
            {
                if (!anchor.IsMatch(line)) continue;
                var maxOnLine = dayHits.Max();
                if (!result.TryGetValue(key, out var existing) || maxOnLine > existing)
                    result[key] = maxOnLine;
            }
        }
        return result;
    }

    /// <summary>
    /// Split section body text into "clauses" — newline-separated lines are
    /// the natural unit (numbered paragraphs in our sample contracts), and
    /// for paragraph-shaped text we further split on sentence terminators.
    /// </summary>
    private static IEnumerable<string> SplitClauses(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // If the line is a single short paragraph, yield it as-is. For
            // longer lines, split on sentence boundaries so adjacent
            // unrelated obligations don't get paired with the wrong amount.
            if (line.Length < 200)
            {
                yield return line;
                continue;
            }
            foreach (var sent in Regex.Split(line, @"(?<=[.!?])\s+"))
                if (sent.Length > 0) yield return sent;
        }
    }
}
