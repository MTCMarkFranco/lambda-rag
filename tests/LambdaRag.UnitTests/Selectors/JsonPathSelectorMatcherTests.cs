using FluentAssertions;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Selectors;
using LambdaRag.Selectors;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;
using Xunit;

namespace LambdaRag.UnitTests.Selectors;

public class JsonPathSelectorMatcherTests
{
    // -----------------------------------------------------------------------
    // Fixture helpers
    // -----------------------------------------------------------------------

    private static ProjectedDocument BuildDocument(
        string json,
        Dictionary<string, SourceSpan>? spans = null)
    {
        var graph   = JsonNode.Parse(json)!.AsObject();
        var spanMap = (IReadOnlyDictionary<string, SourceSpan>)(spans
            ?? new Dictionary<string, SourceSpan>(StringComparer.Ordinal));
        return new ProjectedDocument(
            ContentHash.OfString("test"),
            "test-projector",
            "1.0",
            graph,
            spanMap);
    }

    private static JsonPathSelectorMatcher CreateMatcher()
        => new(NullLogger<JsonPathSelectorMatcher>.Instance);

    // -----------------------------------------------------------------------
    // 1. Parse + evaluate $.foo.bar
    // -----------------------------------------------------------------------

    [Fact]
    public void PathSelector_NestedField_ReturnsCorrectNode()
    {
        var doc     = BuildDocument("""{"foo": {"bar": "hello"}}""");
        var matcher = CreateMatcher();

        var result = matcher.Match(new PathSelector("$.foo.bar"), doc);

        result.Should().HaveCount(1);
        result[0].Path.Should().Be("$.foo.bar");
        result[0].Node.GetValue<string>().Should().Be("hello");
    }

    // -----------------------------------------------------------------------
    // 2. $.items[*].name — all array names
    // -----------------------------------------------------------------------

    [Fact]
    public void PathSelector_ArrayWildcard_ReturnsAllNames()
    {
        var doc     = BuildDocument("""{"items":[{"name":"Alpha"},{"name":"Beta"},{"name":"Gamma"}]}""");
        var matcher = CreateMatcher();

        var result = matcher.Match(new PathSelector("$.items[*].name"), doc);

        result.Should().HaveCount(3);
        result[0].Path.Should().Be("$.items[0].name");
        result[1].Path.Should().Be("$.items[1].name");
        result[2].Path.Should().Be("$.items[2].name");
        result.Select(m => m.Node.GetValue<string>())
              .Should().Equal("Alpha", "Beta", "Gamma");
    }

    // -----------------------------------------------------------------------
    // 3. $.items[?(@.severity == 'high')] — filter predicate
    // -----------------------------------------------------------------------

    [Fact]
    public void PathSelector_FilterPredicate_ReturnsOnlyMatchingItems()
    {
        var doc = BuildDocument("""
            {
              "items": [
                {"name":"A","severity":"high"},
                {"name":"B","severity":"low"},
                {"name":"C","severity":"high"}
              ]
            }
            """);
        var matcher = CreateMatcher();

        var result = matcher.Match(
            new PathSelector("$.items[?(@.severity == 'high')]"), doc);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(m =>
            ((JsonObject)m.Node)["severity"]!.GetValue<string>().Should().Be("high"));
    }

    // -----------------------------------------------------------------------
    // 4. RegexSelector with Path=null — scan all string nodes
    // -----------------------------------------------------------------------

    [Fact]
    public void RegexSelector_NoPath_MatchesAllStringNodesContainingPattern()
    {
        var doc = BuildDocument("""
            {
              "title": "payment plan",
              "notes": "review needed",
              "items": [
                {"desc":"monthly payment"},
                {"desc":"initial setup"}
              ]
            }
            """);
        var matcher = CreateMatcher();

        var result = matcher.Match(new RegexSelector("payment", Path: null), doc);

        // "payment plan" and "monthly payment" match; the others don't.
        result.Should().HaveCount(2);
        // Sorted by path ordinal: $.items[0].desc < $.title
        result[0].Path.Should().Be("$.items[0].desc");
        result[1].Path.Should().Be("$.title");
    }

    // -----------------------------------------------------------------------
    // 5a. AllOfSelector — intersection
    // -----------------------------------------------------------------------

    [Fact]
    public void AllOfSelector_Intersection_ReturnsOnlyPathsPresentInAllChildren()
    {
        var doc = BuildDocument("""
            {
              "items": [
                {"name":"A","active":true,"severity":"high"},
                {"name":"B","active":false,"severity":"high"},
                {"name":"C","active":true,"severity":"low"}
              ]
            }
            """);
        var matcher = CreateMatcher();

        var selector = new AllOfSelector([
            new PathSelector("$.items[?(@.active == true)]"),
            new PathSelector("$.items[?(@.severity == 'high')]"),
        ]);

        var result = matcher.Match(selector, doc);

        // Only item[0] is both active AND high severity.
        result.Should().HaveCount(1);
        result[0].Path.Should().Be("$.items[0]");
    }

    // -----------------------------------------------------------------------
    // 5b. AnyOfSelector — union
    // -----------------------------------------------------------------------

    [Fact]
    public void AnyOfSelector_Union_ReturnsCombinedUniqueResults()
    {
        var doc = BuildDocument("""
            {
              "items": [
                {"name":"A","severity":"high"},
                {"name":"B","severity":"medium"},
                {"name":"C","severity":"high"}
              ]
            }
            """);
        var matcher = CreateMatcher();

        var selector = new AnyOfSelector([
            new PathSelector("$.items[?(@.severity == 'high')]"),  // items[0], items[2]
            new PathSelector("$.items[?(@.name == 'B')]"),          // items[1]
        ]);

        var result = matcher.Match(selector, doc);

        result.Should().HaveCount(3);
        result.Select(m => m.Path).Should()
              .Equal("$.items[0]", "$.items[1]", "$.items[2]");
    }

    // -----------------------------------------------------------------------
    // 5c. NotSelector — top-level emits nothing; inside AllOf complements
    // -----------------------------------------------------------------------

    [Fact]
    public void NotSelector_TopLevel_EmitsNothingWithoutThrow()
    {
        var doc     = BuildDocument("""{"items":[{"name":"X"},{"name":"Y"}]}""");
        var matcher = CreateMatcher();

        var result = matcher.Match(
            new NotSelector(new PathSelector("$.items[0].name")), doc);

        result.Should().BeEmpty();
    }

    [Fact]
    public void NotSelector_InsideAllOf_ComplementsExcludedPaths()
    {
        // AllOf(PathSelector("$.items[*].name"),
        //       NotSelector(PathSelector("$.items[0].name")))
        //
        // child1 → { $.items[0].name, $.items[1].name }
        // child2 (Not) excluded = { $.items[0].name }
        //         leaf nodes    = { $.items[0].name, $.items[0].active,
        //                           $.items[1].name, $.items[1].active }
        //         Not result    = { $.items[0].active, $.items[1].name,
        //                           $.items[1].active }
        // Intersection → { $.items[1].name }

        var doc = BuildDocument("""
            {
              "items": [
                {"name":"A","active":"yes"},
                {"name":"B","active":"no"}
              ]
            }
            """);
        var matcher = CreateMatcher();

        var selector = new AllOfSelector([
            new PathSelector("$.items[*].name"),
            new NotSelector(new PathSelector("$.items[0].name")),
        ]);

        var result = matcher.Match(selector, doc);

        result.Should().HaveCount(1);
        result[0].Path.Should().Be("$.items[1].name");
        result[0].Node.GetValue<string>().Should().Be("B");
    }

    // -----------------------------------------------------------------------
    // 6. Idempotency — two runs produce identical results
    // -----------------------------------------------------------------------

    [Fact]
    public void Match_CalledTwice_ProducesIdenticalResults()
    {
        var doc = BuildDocument("""
            {
              "items": [
                {"name":"Alpha","severity":"high"},
                {"name":"Beta","severity":"low"},
                {"name":"Gamma","severity":"high"}
              ]
            }
            """);
        var matcher  = CreateMatcher();
        var selector = new PathSelector("$.items[?(@.severity == 'high')]");

        var result1 = matcher.Match(selector, doc);
        var result2 = matcher.Match(selector, doc);

        result1.Should().HaveSameCount(result2);
        result1.Select(m => m.Path).Should()
               .Equal(result2.Select(m => m.Path));
        result1.Select(m => m.Span).Should()
               .Equal(result2.Select(m => m.Span));
        // Node references are the same object (same graph, no cloning).
        for (int i = 0; i < result1.Count; i++)
            result1[i].Node.ToJsonString().Should().Be(result2[i].Node.ToJsonString());
    }

    // -----------------------------------------------------------------------
    // 7a. Spans propagate from SpanMap (exact hit and parent walk-up)
    // -----------------------------------------------------------------------

    [Fact]
    public void Match_ExactSpanInMap_PropagatesSpanToMatchedSection()
    {
        var span = new SourceSpan("doc1", 100, 50, 2, "/section/payment");
        var spans = new Dictionary<string, SourceSpan>(StringComparer.Ordinal)
        {
            ["$.items[0]"] = span,
        };
        var doc     = BuildDocument("""{"items":[{"name":"Widget"},{"name":"Gadget"}]}""", spans);
        var matcher = CreateMatcher();

        // $.items[0].name is not in the map, but its parent $.items[0] is.
        var result = matcher.Match(new PathSelector("$.items[0].name"), doc);

        result.Should().HaveCount(1);
        result[0].Span.Should().Be(span);
    }

    [Fact]
    public void Match_PathNotInSpanMap_UsesUnknownSpan()
    {
        var doc     = BuildDocument("""{"foo":"bar"}""");
        var matcher = CreateMatcher();

        var result = matcher.Match(new PathSelector("$.foo"), doc);

        result.Should().HaveCount(1);
        result[0].Span.Should().Be(SourceSpan.Unknown);
    }

    // -----------------------------------------------------------------------
    // Extra: multi-step path with regex filter (integration-style)
    // -----------------------------------------------------------------------

    [Fact]
    public void PathSelector_RegexFilter_MultiStep_ReturnsCorrectNodes()
    {
        var doc = BuildDocument("""
            {
              "sections": [
                {
                  "heading_path": "/payment_terms/",
                  "clauses": [{"id":"c1","text":"Net 30"},{"id":"c2","text":"Late fees"}]
                },
                {
                  "heading_path": "/liability/",
                  "clauses": [{"id":"c3","text":"Capped"}]
                }
              ]
            }
            """);
        var matcher = CreateMatcher();

        var result = matcher.Match(
            new PathSelector("$.sections[?(@.heading_path =~ '/payment_terms/.*')].clauses[*]"),
            doc);

        result.Should().HaveCount(2);
        result[0].Path.Should().Be("$.sections[0].clauses[0]");
        result[1].Path.Should().Be("$.sections[0].clauses[1]");
    }
}
