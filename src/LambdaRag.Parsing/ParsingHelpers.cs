using System.Text.RegularExpressions;
using LambdaRag.Core.Domain;

namespace LambdaRag.Parsing;

/// <summary>
/// Shared utilities for whitespace normalisation and heading-path tracking
/// that every parser needs identically.
/// </summary>
internal static partial class ParsingHelpers
{
    [GeneratedRegex(@"[ \t]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HorizontalWhitespaceRun();

    [GeneratedRegex(@"\n{2,}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MultipleNewlines();

    [GeneratedRegex(@"^\d+(\.\d+)*\.?\s", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    internal static partial Regex NumericPrefixPattern();

    [GeneratedRegex(@"\.\s+[A-Z0-9]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SentenceBoundaryFollowedByCapitalOrDigit();

    /// <summary>
    /// Collapses horizontal whitespace runs to a single space and trims.
    /// Newlines within the string are replaced with a space first (caller
    /// should have already joined multi-line paragraph text).
    /// </summary>
    internal static string NormalizeInlineParagraph(string text)
    {
        var noNewlines = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        return HorizontalWhitespaceRun().Replace(noNewlines, " ").Trim();
    }

    /// <summary>
    /// Splits a multi-paragraph text blob (e.g. from a PDF page) into
    /// individual paragraph segments by one or more blank lines.
    /// </summary>
    internal static IEnumerable<string> SplitIntoParagraphs(string text)
    {
        // Normalize CRLF, then split on 2+ consecutive newlines
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return MultipleNewlines()
            .Split(normalized)
            .Select(s => s.Trim('\n', ' '))
            .Where(s => s.Length > 0);
    }

    /// <summary>
    /// Decides whether a short, all-caps, letter-bearing string looks like a
    /// heading purely from typography.
    /// </summary>
    internal static bool IsAllCapsHeading(string text)
        => text.Length <= 120
           && text.Any(char.IsLetter)
           && text.Where(char.IsLetter).All(char.IsUpper);

    /// <summary>
    /// Returns the numeric depth implied by a "1.", "1.1.", "2.3.4." prefix,
    /// or 0 when the text has no such prefix.
    /// </summary>
    internal static int NumericPrefixDepth(string text)
    {
        var m = NumericPrefixPattern().Match(text);
        if (!m.Success) return 0;
        return m.Value.TrimEnd().Count(c => c == '.') + (m.Value.TrimEnd().EndsWith('.') ? 0 : 1);
    }

    /// <summary>
    /// Strict "does this look like a numbered heading?" test. A raw
    /// <see cref="NumericPrefixPattern"/> match is not enough on its own —
    /// numbered list items ("2. Function publishes...") also start with a
    /// numeric prefix but are body text, not headings.
    ///
    /// We require ALL of:
    ///   • matches <see cref="NumericPrefixPattern"/>
    ///   • total length ≤ 120 (aligned with <see cref="IsAllCapsHeading"/>)
    ///   • character after the numeric prefix is a capital letter
    ///   • body has no "sentence boundary" — a period followed by whitespace
    ///     and then a capital or digit — which indicates concatenated list
    ///     items or multiple sentences
    /// </summary>
    internal static bool LooksLikeNumberedHeading(string text)
    {
        var m = NumericPrefixPattern().Match(text);
        if (!m.Success) return false;
        if (text.Length > 120) return false;

        var rest = text.Substring(m.Length);
        if (rest.Length == 0 || !char.IsUpper(rest[0])) return false;

        if (SentenceBoundaryFollowedByCapitalOrDigit().IsMatch(rest)) return false;

        return true;
    }

    // ── Heading-path tracking ──────────────────────────────────────────────

    internal static void PushHeading(
        List<(int Level, string Text)> stack, int level, string text)
    {
        while (stack.Count > 0 && stack[^1].Level >= level)
            stack.RemoveAt(stack.Count - 1);
        stack.Add((level, text.Replace("/", "-")));
    }

    internal static string BuildHeadingPath(List<(int Level, string Text)> stack)
        => stack.Count == 0 ? "/" : "/" + string.Join("/", stack.Select(h => h.Text));

    // ── Block-ID helper ───────────────────────────────────────────────────

    internal static string BlockId(int charStart) => $"b{charStart:D8}";

    // ── Deterministic IngestedAt from file metadata ───────────────────────

    internal static DateTimeOffset FileIngestedAt(string filePath)
        => new DateTimeOffset(
            new FileInfo(filePath).LastWriteTimeUtc,
            TimeSpan.Zero);
}
