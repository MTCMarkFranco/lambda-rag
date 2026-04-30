using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Persistence.Stores;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LambdaRag.UnitTests.Persistence;

public sealed class ProjectionCacheTests
{
    private static ProjectedDocument BuildProjectedDocument(string sourceText = "test-source") =>
        new(
            SourceId: ContentHash.OfString(sourceText),
            ProjectorId: "contract",
            ProjectorVersion: "1.0",
            Graph: new JsonObject
            {
                ["clauses"] = new JsonArray
                {
                    new JsonObject { ["id"] = "c1", ["text"] = "Payment is due within 30 days." },
                },
            },
            SpanMap: new Dictionary<string, SourceSpan>
            {
                ["c1"] = new SourceSpan("doc1", 0, 42, 1, "/payment"),
            });

    [Fact]
    public async Task GetAsync_UnknownKey_ReturnsCacheMiss()
    {
        await using var db = await TestDb.CreateAsync();
        var cache = new SqliteProjectionCache(db.Options, NullLogger<SqliteProjectionCache>.Instance);

        var result = await cache.GetAsync(ContentHash.OfString("unknown-key"));

        result.Should().BeNull();
    }

    [Fact]
    public async Task PutThenGet_ReturnsCacheHit_WithEqualContent()
    {
        await using var db = await TestDb.CreateAsync();
        var cache = new SqliteProjectionCache(db.Options, NullLogger<SqliteProjectionCache>.Instance);

        var original = BuildProjectedDocument();
        var cacheKey = ContentHash.OfString("cache-key-1");
        var modelId = "gpt-4o";
        var promptHash = ContentHash.OfString("my-prompt");

        await cache.PutAsync(cacheKey, original, modelId, promptHash);
        var hit = await cache.GetAsync(cacheKey);

        hit.Should().NotBeNull();
        hit!.SourceId.Should().Be(original.SourceId);
        hit.ProjectorId.Should().Be(original.ProjectorId);
        hit.ProjectorVersion.Should().Be(original.ProjectorVersion);
        hit.Graph.ToJsonString().Should().Be(original.Graph.ToJsonString());
        hit.SpanMap.Keys.Should().BeEquivalentTo(original.SpanMap.Keys);
        hit.SpanMap["c1"].Should().Be(original.SpanMap["c1"]);
    }

    [Fact]
    public async Task PutAsync_OverwritesExistingEntry()
    {
        await using var db = await TestDb.CreateAsync();
        var cache = new SqliteProjectionCache(db.Options, NullLogger<SqliteProjectionCache>.Instance);

        var cacheKey = ContentHash.OfString("overwrite-key");
        var promptHash = ContentHash.OfString("prompt");

        var first = BuildProjectedDocument("source-v1");
        await cache.PutAsync(cacheKey, first, "model-1", promptHash);

        var second = BuildProjectedDocument("source-v2");
        await cache.PutAsync(cacheKey, second, "model-1", promptHash);

        var hit = await cache.GetAsync(cacheKey);
        hit!.SourceId.Should().Be(second.SourceId);
    }
}
