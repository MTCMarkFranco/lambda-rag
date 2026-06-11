using FluentAssertions;
using LambdaRag.Projection;
using Xunit;

namespace LambdaRag.UnitTests.Projection;

/// <summary>
/// Pillar 2 (#117) — the ARB-PSA topic map must cover every dimension the
/// LLM baseline judged on, so the projector classifies PSA sections into
/// categories that downstream rules can predicate against. Failing this
/// test means the rules engine can never beat the LLM on a PSA artifact —
/// the predicate gate would always trip and emit N/A.
/// </summary>
public class ArbPsaTopicMapTests
{
    private static readonly string[] RequiredDimensions =
    {
        "psa_completeness",
        "architecture_constraints",
        "architecture_risks",
        "decision_records",
        "technology_standards",
        "design_patterns",
        "data_security",
        "integrations",
        "infrastructure_architecture",
        "security_architecture",
        "information_governance",
        "dr_resiliency",
    };

    [Fact]
    public void Topic_map_loads_and_has_at_least_12_primary_topics()
    {
        var map = TopicMapRegistry.Load("arb-psa.v1");

        map.Domain.Should().Be("arb-psa");
        map.Version.Should().Be("1.0.0");
        map.Topics.Where(t => t.Axis is null).Should().HaveCountGreaterThanOrEqualTo(12);
    }

    [Fact]
    public void Every_required_PSA_dimension_is_present_in_the_topic_vocabulary()
    {
        var map = TopicMapRegistry.Load("arb-psa.v1");
        var ids = map.Topics.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var dim in RequiredDimensions)
            ids.Should().Contain(dim, $"ARB-PSA review needs topic '{dim}'");
    }

    [Fact]
    public void Every_primary_topic_has_at_least_one_keyword()
    {
        var map = TopicMapRegistry.Load("arb-psa.v1");
        foreach (var t in map.Topics.Where(t => t.Axis is null))
            t.Keywords.Should().NotBeEmpty($"primary topic '{t.Id}' needs at least one keyword");
    }

    [Fact]
    public void Loading_the_topic_map_twice_returns_byte_identical_keyword_set()
    {
        var first = TopicMapRegistry.Load("arb-psa.v1");
        var second = TopicMapRegistry.Load("arb-psa.v1");

        var a = string.Join("|", first.Topics.OrderBy(t => t.Id, StringComparer.Ordinal)
            .Select(t => t.Id + ":" + string.Join(",", t.Keywords)));
        var b = string.Join("|", second.Topics.OrderBy(t => t.Id, StringComparer.Ordinal)
            .Select(t => t.Id + ":" + string.Join(",", t.Keywords)));
        a.Should().Be(b);
    }
}
