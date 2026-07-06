#pragma warning disable OPENAI001
using FluentAssertions;
using LambdaRag.Authoring;
using Xunit;

namespace LambdaRag.UnitTests.Authoring;

// Issue #181 — TryExtractRegion is best-effort but must be deterministic and
// safe on every input shape we can plausibly hand it. This gets fingerprinted
// so drift matters.
public class FoundrySectionFactExtractorFactoryTests
{
    [Theory]
    [InlineData("https://foundry-cc-canada.services.ai.azure.com/", "canada")]
    [InlineData("https://my-account-eastus.openai.azure.com/", "eastus")]
    [InlineData("https://acct-westus2.openai.azure.com/", "westus2")]
    [InlineData("https://acct-eastus2-euap.openai.azure.com/", "euap")]
    public void Extracts_trailing_hyphen_segment_as_region(string url, string expected)
        => FoundrySectionFactExtractorFactory.TryExtractRegion(url).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Null_or_blank_input_returns_null(string? input)
        => FoundrySectionFactExtractorFactory.TryExtractRegion(input).Should().BeNull();

    [Fact]
    public void Malformed_url_does_not_throw()
    {
        var act = () => FoundrySectionFactExtractorFactory.TryExtractRegion("not-a-url");
        act.Should().NotThrow();
    }
}
