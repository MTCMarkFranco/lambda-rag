using FluentAssertions;
using LambdaRag.Parsing;
using Xunit;

namespace LambdaRag.UnitTests.Parsing;

/// <summary>
/// Tests for <see cref="ParsingHelpers.LooksLikeNumberedHeading"/> —
/// tightened numeric-prefix classifier that rejects numbered list items.
/// Pins the fix for the v1.2.0 latent bug where <c>PdfParser</c> tagged
/// numbered body content as headings.
/// </summary>
public class ParsingHelpersHeadingTests
{
    // ── Positive cases: real headings ─────────────────────────────────────

    [Theory]
    [InlineData("1. Introduction")]
    [InlineData("1.1 Scope")]
    [InlineData("6.2 Security Design")]
    [InlineData("6.1.3 Security Design Assumption")]
    [InlineData("6.4 Security Architecture - Appendices & Links")]
    [InlineData("10.15.2 Very Deep Section")]
    public void Recognises_real_numbered_headings(string text)
    {
        // Reflection into the internal helper for direct coverage.
        InvokeLooksLikeNumberedHeading(text).Should().BeTrue(
            $"'{text}' is a canonical numbered heading pattern");
    }

    // ── Negative cases: numbered LIST ITEMS should not be headings ────────

    [Theory]
    [InlineData("2. Function publishes messages to Azure Service Bus Queue. Azure Function Apps handle ingestion and lightweight transformation.")]
    [InlineData("3. Data lands in Azure Data Lake Storage Gen2 where raw data is retained for 90 days. Azure Databricks consumes data from the landing zone.")]
    [InlineData("1. Do the first thing. Then do the second thing.")]
    [InlineData("4. This is a longer sentence that describes something in detail. Another sentence follows immediately.")]
    public void Rejects_numbered_list_items_with_multiple_sentences(string text)
    {
        InvokeLooksLikeNumberedHeading(text).Should().BeFalse(
            $"'{text.Substring(0, Math.Min(60, text.Length))}...' contains multiple sentences and is body text, not a heading");
    }

    [Fact]
    public void Rejects_overlong_numbered_line_even_without_sentence_breaks()
    {
        // > 120 chars without periods still fails on length alone.
        var longLine = "1. This is a very long single line that never terminates a sentence " +
                       "but runs on and on and on and on and on and on and on and on and on and on";
        (longLine.Length > 120).Should().BeTrue("test-input must exceed the length threshold");
        InvokeLooksLikeNumberedHeading(longLine).Should().BeFalse();
    }

    [Fact]
    public void Rejects_numbered_line_starting_with_lowercase()
    {
        InvokeLooksLikeNumberedHeading("2. first item goes here").Should().BeFalse();
    }

    [Fact]
    public void Rejects_line_with_no_numeric_prefix()
    {
        InvokeLooksLikeNumberedHeading("Introduction").Should().BeFalse();
    }

    [Fact]
    public void Rejects_prefix_followed_by_digit_run_on()
    {
        // "1. Do X. 2. Do Y" pattern: sentence terminator followed by a digit.
        InvokeLooksLikeNumberedHeading("1. Do X. 2. Do Y").Should().BeFalse();
    }

    // ── reflection glue ───────────────────────────────────────────────────

    private static bool InvokeLooksLikeNumberedHeading(string text)
    {
        var t = typeof(ParserRegistry).Assembly
            .GetType("LambdaRag.Parsing.ParsingHelpers", throwOnError: true)!;
        var m = t.GetMethod(
            "LooksLikeNumberedHeading",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        return (bool)m.Invoke(null, new object[] { text })!;
    }
}
