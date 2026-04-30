using System.Diagnostics;
using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using LambdaRag.Indexing.InMemory;
using Xunit;
using Xunit.Abstractions;

namespace LambdaRag.UnitTests.Indexing;

public class IndexScaleTests
{
    private readonly ITestOutputHelper _out;
    public IndexScaleTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void OneThousandRules_TenCategories_NarrowsAtLeastThreeX()
    {
        const int ruleCount = 1000;
        const int categoryCount = 10;
        var rules = new List<Rule>(ruleCount);
        for (var i = 0; i < ruleCount; i++)
        {
            var category = "cat_" + (i % categoryCount);
            rules.Add(new Rule(
                Id: $"R-{i:D5}",
                Version: "1.0.0",
                NaturalLanguage: $"Rule {i}",
                Lambda: "true",
                AppliesToSchema: new JsonObject(),
                Selector: new PathSelector("$.sections[*]"),
                Severity: RuleSeverity.Violation,
                SourceSpan: new SourceSpan("p", 0, 0, 1, null),
                EvidenceQuote: $"R-{i}",
                Metadata: new Dictionary<string, string>())
            {
                Predicate = $"input1.category == \"{category}\"",
            });
        }

        var ruleSet = new RuleSet(
            Id: "rs-bench", Version: "1.0.0", Domain: "contract",
            PublishedAt: DateTimeOffset.UnixEpoch, Rules: rules,
            Metadata: new Dictionary<string, string>());

        var idx = new InMemoryRuleSignatureIndex();
        var sw = Stopwatch.StartNew();
        idx.Build(ruleSet);
        var buildMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var hits = idx.Lookup(new JsonObject { ["category"] = "cat_3" });
        var lookupUs = sw.Elapsed.TotalMicroseconds;

        var reduction = (double)ruleCount / Math.Max(1, hits.Count);
        _out.WriteLine($"Rules: {ruleCount}, Categories: {categoryCount}");
        _out.WriteLine($"Build: {buildMs:F2}ms");
        _out.WriteLine($"Lookup: {lookupUs:F1}us, hits: {hits.Count}");
        _out.WriteLine($"Reduction factor: {reduction:F1}x");

        reduction.Should().BeGreaterThanOrEqualTo(3.0);
    }
}
