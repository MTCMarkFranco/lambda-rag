using FluentAssertions;
using LambdaRag.Core.Observability;
using Xunit;

namespace LambdaRag.UnitTests.Observability;

public class TokenCostEstimatorTests
{
    [Fact]
    public void Known_model_returns_usd_estimate()
    {
        var usd = TokenCostEstimator.EstimateUsd("gpt-5.4-mini", 1_000_000, 1_000_000);
        usd.Should().NotBeNull();
        usd!.Value.Should().BeApproximately(5.25, 1e-9);
    }

    [Fact]
    public void Known_model_scales_linearly()
    {
        var usd = TokenCostEstimator.EstimateUsd("gpt-5.4-mini", 100_000, 50_000);
        usd!.Value.Should().BeApproximately(0.30, 1e-9);
    }

    [Fact]
    public void Match_is_case_insensitive()
        => TokenCostEstimator.EstimateUsd("GPT-5.4-MINI", 100, 100).Should().NotBeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("gpt-5.4")]
    [InlineData("grok-4-20-non-reasoning")]
    public void Unknown_or_blank_model_returns_null(string? model)
        => TokenCostEstimator.EstimateUsd(model, 1000, 1000).Should().BeNull();
}
