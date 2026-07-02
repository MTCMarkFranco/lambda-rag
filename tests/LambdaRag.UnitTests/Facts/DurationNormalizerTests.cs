using FluentAssertions;
using LambdaRag.Core.Facts;
using Xunit;

namespace LambdaRag.UnitTests.Facts;

public class DurationNormalizerTests
{
    private readonly DurationNormalizer _n = DurationNormalizer.Default;

    [Theory]
    [InlineData("every 90 days", "P90D")]
    [InlineData("Every 90 Days", "P90D")]
    [InlineData("on a 90-day cycle", "P90D")]
    [InlineData("on a 90 day cycle", "P90D")]
    [InlineData("quarterly", "P90D")]
    [InlineData("Quarterly.", "P90D")]
    [InlineData("annually", "P365D")]
    [InlineData("every year", "P365D")]
    [InlineData("monthly", "P30D")]
    [InlineData("semi-annually", "P180D")]
    [InlineData("every six months", "P180D")]
    public void Normalize_Maps_Canonical_And_Fuzzy_Phrases(string input, string expected)
    {
        _n.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("every 45 days", "P45D")]
    [InlineData("every 7 days", "P7D")]
    [InlineData("on a 14-day cycle", "P14D")]
    public void Normalize_Regex_Fallback_Extracts_Days(string input, string expected)
    {
        _n.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("as needed")]
    [InlineData("occasionally")]
    [InlineData("I refuse to answer")]
    public void Normalize_Refuses_Unknown(string? input)
    {
        _n.Normalize(input).Should().BeNull();
    }

    [Theory]
    [InlineData("P90D", 90)]
    [InlineData("P1W", 7)]
    [InlineData("every 90 days", 90)]
    [InlineData("quarterly", 90)]
    [InlineData("every 45 days", 45)]
    public void NormalizeToDays_Handles_Iso_And_Phrases(string input, int expected)
    {
        _n.NormalizeToDays(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeToDays_Returns_Null_On_Unknown()
    {
        _n.NormalizeToDays("as needed").Should().BeNull();
    }

    [Fact]
    public void Default_Has_Stable_TableHash()
    {
        var a = DurationNormalizer.Default.TableHash.Value;
        var b = DurationNormalizer.Default.TableHash.Value;
        b.Should().Be(a);
        DurationNormalizer.Default.Version.Should().Be("2");
    }
}
