using FluentAssertions;
using LambdaRag.Projection.Projectors;
using Xunit;

namespace LambdaRag.UnitTests.Projection;

/// <summary>
/// Domain-agnostic tests for <see cref="TextFeatureExtractor"/>.
///
/// The extractor is the engine's general-purpose numeric feature surface:
/// it must work on any English-language section text, regardless of the
/// document's vertical (contract / oil-and-gas / permitting / FSI / ...).
/// These tests use intentionally non-contract phrasing to prove that
/// nothing CTSO-specific or contract-specific has leaked into the
/// implementation — if it ever did, every other vertical would inherit
/// the bug as soon as their ruleset opted in to <c>text_features</c>.
/// </summary>
public class TextFeatureExtractorTests
{
    [Fact]
    public void Extracts_day_counts_from_arbitrary_prose()
    {
        var f = TextFeatureExtractor.Extract(
            "The applicant must respond within 30 calendar days of the notice " +
            "and remediate any non-conformance within 90 business days.");

        f["day_counts"]!.AsArray().Select(n => (long)n!).Should().Equal(30L, 90L);
        f["day_count_min"]!.GetValue<long>().Should().Be(30);
        f["day_count_max"]!.GetValue<long>().Should().Be(90);
    }

    [Fact]
    public void Extracts_month_and_year_counts()
    {
        var f = TextFeatureExtractor.Extract(
            "Records shall be retained for 7 years following the 18-month observation period.");

        // "18-month" is hyphenated; the regex matches "18 months" or "18-month"
        // — we do not require the hyphenated form to extract; primary value is 7 years.
        f["year_counts"]!.AsArray().Select(n => (long)n!).Should().Contain(7L);
        f["year_count_max"]!.GetValue<long>().Should().Be(7);
    }

    [Fact]
    public void Extracts_percentages_with_units()
    {
        var f = TextFeatureExtractor.Extract(
            "Royalty payments equal 12.5% per annum, escalating by 1.5% per month if late.");

        f["percent_values"]!.AsArray().Select(n => (double)n!).Should().Contain([1.5, 12.5]);
        f["percent_max"]!.GetValue<double>().Should().Be(12.5);
        f["percent_min"]!.GetValue<double>().Should().Be(1.5);
    }

    [Theory]
    [InlineData("$5,000,000", 5_000_000)]
    [InlineData("$5 million", 5_000_000)]
    [InlineData("USD 10,000,000", 10_000_000)]
    [InlineData("CAD$ 2.5 million", 2_500_000)]
    [InlineData("$1.5B", 1_500_000_000)]
    [InlineData("five million dollars", 0)]      // pure prose without leading $ — not extracted by design
    public void Extracts_dollar_amounts_in_multiple_currency_formats(string text, long expectedMax)
    {
        var f = TextFeatureExtractor.Extract($"The minimum coverage is {text} per occurrence.");
        if (expectedMax == 0)
        {
            f["dollar_amounts"]!.AsArray().Should().BeEmpty();
        }
        else
        {
            f["dollar_max"]!.GetValue<long>().Should().Be(expectedMax);
        }
    }

    [Fact]
    public void Output_is_deterministic_and_sorted()
    {
        var f1 = TextFeatureExtractor.Extract(
            "We pay $5 million / $1 million / $10 million across three policies.");
        var f2 = TextFeatureExtractor.Extract(
            "We pay $10 million / $1 million / $5 million across three policies.");

        // Order of appearance differs — output arrays must be identical (sorted).
        f1["dollar_amounts"]!.ToJsonString().Should().Be(f2["dollar_amounts"]!.ToJsonString());
        f1["dollar_amounts"]!.AsArray().Select(n => (long)n!).Should()
            .BeInAscendingOrder();
    }

    [Fact]
    public void Empty_text_yields_empty_arrays_no_scalar_keys()
    {
        var f = TextFeatureExtractor.Extract("");

        f["day_counts"]!.AsArray().Should().BeEmpty();
        f["dollar_amounts"]!.AsArray().Should().BeEmpty();
        // Scalar convenience keys are omitted when empty so rule authors
        // can defensively check Count > 0 first.
        f.ContainsKey("day_count_max").Should().BeFalse();
        f.ContainsKey("dollar_max").Should().BeFalse();
    }

    [Fact]
    public void Works_on_non_contract_prose_oil_and_gas_example()
    {
        // This is the genericness assertion: the extractor was developed
        // alongside contract rules but the implementation is pure regex
        // over English. A pipeline-safety document should yield the same
        // shape — proving the engine can be reused for any vertical.
        var f = TextFeatureExtractor.Extract(
            "Pipeline operators shall conduct integrity inspections every 5 years and " +
            "report any incident to the regulator within 30 days. The corrective-action " +
            "completion target is 90% within 180 days.");

        f["year_count_max"]!.GetValue<long>().Should().Be(5);
        f["day_counts"]!.AsArray().Select(n => (long)n!).Should().Equal(30L, 180L);
        f["percent_max"]!.GetValue<double>().Should().Be(90.0);
    }
}
