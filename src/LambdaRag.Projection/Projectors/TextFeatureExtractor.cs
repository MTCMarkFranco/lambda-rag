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

        // Convenience scalars for the most common comparisons. These are
        // omitted (not written) when the underlying array is empty so a
        // lambda that references them can be written defensively as
        // `input1.text_features.day_counts.Count > 0 && ...max < 45`.
        if (dayCounts.Count > 0)
        {
            features["day_count_min"] = dayCounts[0];
            features["day_count_max"] = dayCounts[^1];
        }
        if (monthCounts.Count > 0)
        {
            features["month_count_min"] = monthCounts[0];
            features["month_count_max"] = monthCounts[^1];
        }
        if (yearCounts.Count > 0)
        {
            features["year_count_min"] = yearCounts[0];
            features["year_count_max"] = yearCounts[^1];
        }
        if (percentValues.Count > 0)
        {
            features["percent_min"] = percentValues[0];
            features["percent_max"] = percentValues[^1];
        }
        if (dollarAmounts.Count > 0)
        {
            features["dollar_min"] = dollarAmounts[0];
            features["dollar_max"] = dollarAmounts[^1];
        }

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
}
