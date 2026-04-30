using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using LambdaRag.Persistence.Stores;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LambdaRag.UnitTests.Persistence;

public sealed class RuleSetStoreTests
{
    private static RuleSet BuildRuleSet(string id = "rs-test", string version = "1.0") =>
        new(
            Id: id,
            Version: version,
            Domain: "contract",
            PublishedAt: new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero),
            Rules:
            [
                new Rule(
                    Id: "r1",
                    Version: "1.0",
                    NaturalLanguage: "Payment amount must be positive.",
                    Lambda: "input.Amount > 0",
                    AppliesToSchema: new JsonObject { ["type"] = "object" },
                    Selector: new PathSelector("$.payment"),
                    Severity: RuleSeverity.Violation,
                    SourceSpan: SourceSpan.Unknown,
                    EvidenceQuote: "payment amount",
                    Metadata: new Dictionary<string, string> { ["source"] = "section-3" }),
            ],
            Metadata: new Dictionary<string, string> { ["author"] = "test" });

    [Fact]
    public async Task RoundTrip_GetByIdAndVersion_ReturnsEquivalentRuleSet()
    {
        await using var db = await TestDb.CreateAsync();
        var store = new SqliteRuleSetStore(db.Options, NullLogger<SqliteRuleSetStore>.Instance);

        var original = BuildRuleSet();
        await store.PublishAsync(original);

        var retrieved = await store.GetAsync(original.Id, original.Version);

        retrieved.Should().NotBeNull();
        // Compare via canonical JSON — structural equality across JsonObject members
        Canonical(retrieved!).Should().Be(Canonical(original));
    }

    [Fact]
    public async Task GetLatestAsync_ReturnsHighestPublishedAt()
    {
        await using var db = await TestDb.CreateAsync();
        var store = new SqliteRuleSetStore(db.Options, NullLogger<SqliteRuleSetStore>.Instance);

        var v1 = BuildRuleSet(version: "1.0") with
        {
            PublishedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var v2 = BuildRuleSet(version: "2.0") with
        {
            PublishedAt = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero),
        };

        await store.PublishAsync(v1);
        await store.PublishAsync(v2);

        var latest = await store.GetLatestAsync("rs-test");

        latest.Should().NotBeNull();
        latest!.Version.Should().Be("2.0");
    }

    [Fact]
    public async Task ListAsync_ReturnsAllPublishedRuleSets()
    {
        await using var db = await TestDb.CreateAsync();
        var store = new SqliteRuleSetStore(db.Options, NullLogger<SqliteRuleSetStore>.Instance);

        await store.PublishAsync(BuildRuleSet("rs-a", "1.0"));
        await store.PublishAsync(BuildRuleSet("rs-b", "1.0"));

        var list = await store.ListAsync();

        list.Should().HaveCount(2);
        list.Select(x => x.Id).Should().Contain(["rs-a", "rs-b"]);
    }

    [Fact]
    public async Task IdempotentPublish_DoesNotThrowOrDuplicate()
    {
        await using var db = await TestDb.CreateAsync();
        var store = new SqliteRuleSetStore(db.Options, NullLogger<SqliteRuleSetStore>.Instance);

        var ruleSet = BuildRuleSet();

        await store.PublishAsync(ruleSet);
        await store.PublishAsync(ruleSet); // second publish — must not throw

        var list = await store.ListAsync();
        list.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        await using var db = await TestDb.CreateAsync();
        var store = new SqliteRuleSetStore(db.Options, NullLogger<SqliteRuleSetStore>.Instance);

        var result = await store.GetAsync("no-such-id", "1.0");

        result.Should().BeNull();
    }

    private static string Canonical(RuleSet rs) =>
        JsonSerializer.Serialize(rs, CanonicalJson.Compact);
}
