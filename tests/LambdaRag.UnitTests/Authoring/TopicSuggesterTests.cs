using FluentAssertions;
using LambdaRag.Authoring;
using LambdaRag.Projection;
using Xunit;

namespace LambdaRag.UnitTests.Authoring;

public class TopicSuggesterTests
{
    [Fact]
    public async Task Keyword_heuristic_suggests_existing_topic_when_keywords_match()
    {
        var map = TopicMapRegistry.Load("contract.v1");
        var sut = new KeywordHeuristicTopicSuggester();

        var req = new TopicSuggestionRequest(
            Heading: "Payment Terms",
            Body: "Customer shall pay invoices within thirty (30) days of receipt of invoice.",
            CurrentMap: map);

        var suggestions = await sut.SuggestAsync(req);

        suggestions.Should().NotBeEmpty();
        suggestions.Should().Contain(s => s.IsExisting);
        suggestions.First().IsExisting.Should().BeTrue();
    }

    [Fact]
    public async Task Keyword_heuristic_proposes_new_topic_when_no_match()
    {
        var map = TopicMapRegistry.Load("contract.v1");
        var sut = new KeywordHeuristicTopicSuggester();

        var req = new TopicSuggestionRequest(
            Heading: "Quantum Resilience Provisions",
            Body: "The hyperdrive flux capacitor must remain operative during chronosynclastic infundibulum events.",
            CurrentMap: map);

        var suggestions = await sut.SuggestAsync(req);

        suggestions.Should().Contain(s => !s.IsExisting);
        var newOne = suggestions.First(s => !s.IsExisting);
        newOne.TopicId.Should().MatchRegex("^[a-z0-9_]+$");
        newOne.SeedKeywords.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Keyword_heuristic_is_deterministic()
    {
        var map = TopicMapRegistry.Load("contract.v1");
        var sut = new KeywordHeuristicTopicSuggester();

        var req = new TopicSuggestionRequest(
            Heading: "Confidentiality Clause",
            Body: "Each party shall keep confidential information strictly secret for five years.",
            CurrentMap: map);

        var s1 = await sut.SuggestAsync(req);
        var s2 = await sut.SuggestAsync(req);

        s1.Select(x => (x.TopicId, x.IsExisting)).Should()
            .Equal(s2.Select(x => (x.TopicId, x.IsExisting)));
    }

    [Fact]
    public void LlmTopicSuggester_validates_topic_id_format()
    {
        LlmTopicSuggester.IsValidTopicId("payment_terms").Should().BeTrue();
        LlmTopicSuggester.IsValidTopicId("Payment_Terms").Should().BeFalse();   // uppercase
        LlmTopicSuggester.IsValidTopicId("a").Should().BeFalse();                // too short
        LlmTopicSuggester.IsValidTopicId("kebab-case").Should().BeFalse();        // hyphen
        LlmTopicSuggester.IsValidTopicId(new string('x', 41)).Should().BeFalse(); // too long
    }
}
