using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Persistence.Stores;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LambdaRag.UnitTests.Persistence;

public sealed class SourceDocumentStoreTests
{
    private static SourceDocument BuildDocument(string content = "hello world") =>
        new(
            Id: ContentHash.OfString(content),
            FileName: "test.txt",
            Kind: SourceDocumentKind.Text,
            ByteLength: content.Length,
            IngestedAt: new DateTimeOffset(2024, 2, 20, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task UpsertThenGet_ReturnsEquivalentDocument()
    {
        await using var db = await TestDb.CreateAsync();
        var store = new SqliteSourceDocumentStore(db.Options, NullLogger<SqliteSourceDocumentStore>.Instance);

        var original = BuildDocument();
        await store.UpsertAsync(original);

        var retrieved = await store.GetAsync(original.Id);

        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(original.Id);
        retrieved.FileName.Should().Be(original.FileName);
        retrieved.Kind.Should().Be(original.Kind);
        retrieved.ByteLength.Should().Be(original.ByteLength);
        retrieved.IngestedAt.Should().Be(original.IngestedAt);
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        await using var db = await TestDb.CreateAsync();
        var store = new SqliteSourceDocumentStore(db.Options, NullLogger<SqliteSourceDocumentStore>.Instance);

        var result = await store.GetAsync(ContentHash.OfString("unknown"));

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_UpdatesExistingDocument()
    {
        await using var db = await TestDb.CreateAsync();
        var store = new SqliteSourceDocumentStore(db.Options, NullLogger<SqliteSourceDocumentStore>.Instance);

        var doc = BuildDocument();
        await store.UpsertAsync(doc);

        var updated = doc with { FileName = "renamed.txt" };
        await store.UpsertAsync(updated);

        var retrieved = await store.GetAsync(doc.Id);
        retrieved!.FileName.Should().Be("renamed.txt");
    }

    [Fact]
    public async Task UpsertAsync_AllDocumentKinds_RoundTrip()
    {
        await using var db = await TestDb.CreateAsync();
        var store = new SqliteSourceDocumentStore(db.Options, NullLogger<SqliteSourceDocumentStore>.Instance);

        foreach (var kind in Enum.GetValues<SourceDocumentKind>())
        {
            var doc = BuildDocument(kind.ToString()) with { Kind = kind };
            await store.UpsertAsync(doc);

            var retrieved = await store.GetAsync(doc.Id);
            retrieved!.Kind.Should().Be(kind);
        }
    }
}
