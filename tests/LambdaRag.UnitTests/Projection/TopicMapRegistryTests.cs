using FluentAssertions;
using LambdaRag.Projection;
using Xunit;

namespace LambdaRag.UnitTests.Projection;

public class TopicMapRegistryTests
{
    [Fact]
    public void ListEmbedded_returns_all_seven_industry_maps()
    {
        var ids = TopicMapRegistry.ListEmbedded();

        ids.Should().Contain(new[]
        {
            "contract.v1",
            "fsi.v1",
            "oil-gas.v1",
            "business-review.v1",
            "architecture-review.v1",
            "permitting.v1",
            "gov-architecture.v1",
        });
    }

    [Theory]
    [InlineData("contract.v1")]
    [InlineData("fsi.v1")]
    [InlineData("oil-gas.v1")]
    [InlineData("business-review.v1")]
    [InlineData("architecture-review.v1")]
    [InlineData("permitting.v1")]
    [InlineData("gov-architecture.v1")]
    public void Load_by_full_id_returns_a_topic_map(string id)
    {
        var map = TopicMapRegistry.Load(id);

        map.Should().NotBeNull();
        map.Domain.Should().NotBeNullOrWhiteSpace();
        map.Topics.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("contract")]
    [InlineData("fsi")]
    [InlineData("oil-gas")]
    public void Load_by_short_id_resolves_to_newest_version(string shortId)
    {
        var map = TopicMapRegistry.Load(shortId);
        map.Topics.Should().NotBeEmpty();
    }

    [Fact]
    public void Load_throws_for_unknown_id()
    {
        var act = () => TopicMapRegistry.Load("does-not-exist");
        act.Should().Throw<FileNotFoundException>();
    }
}
