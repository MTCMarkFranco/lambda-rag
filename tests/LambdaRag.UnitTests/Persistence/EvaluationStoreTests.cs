using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Persistence.Stores;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LambdaRag.UnitTests.Persistence;

public sealed class EvaluationStoreTests
{
    private static ComplianceReport BuildReport(ContentHash? docId = null) =>
        new(
            DocumentId: docId ?? ContentHash.OfString("test-document"),
            RuleSetId: "rs-1",
            RuleSetVersion: "1.0",
            RuleSetFingerprint: ContentHash.OfString("fingerprint"),
            ProjectorId: "contract",
            ProjectorVersion: "1.0",
            Score: 0.75,
            TotalRules: 4,
            Passed: 3,
            Failed: 1,
            NotApplicable: 0,
            Errored: 0,
            Verdicts:
            [
                new Verdict(
                    Id: "v1",
                    RuleId: "r1",
                    RuleSetVersion: "1.0",
                    Outcome: VerdictOutcome.Pass,
                    LambdaText: "input.Amount > 0",
                    EvaluatedInput: new JsonObject { ["Amount"] = JsonValue.Create(500) },
                    SourceSpan: SourceSpan.Unknown,
                    ErrorMessage: null,
                    EvidenceQuotes: ["payment clause"],
                    EvaluatedAt: new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero)),
            ],
            GeneratedAt: new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero));

    private static ContentHash ReportId(ComplianceReport report) =>
        ContentHash.OfString(JsonSerializer.Serialize(report, CanonicalJson.Compact));

    [Fact]
    public async Task SaveThenGet_ReturnsEquivalentReport()
    {
        await using var db = await TestDb.CreateAsync();
        var store = new SqliteEvaluationStore(db.Options, NullLogger<SqliteEvaluationStore>.Instance);

        var original = BuildReport();
        await store.SaveAsync(original);

        var id = ReportId(original);
        var retrieved = await store.GetAsync(id);

        retrieved.Should().NotBeNull();
        Canonical(retrieved!).Should().Be(Canonical(original));
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        await using var db = await TestDb.CreateAsync();
        var store = new SqliteEvaluationStore(db.Options, NullLogger<SqliteEvaluationStore>.Instance);

        var result = await store.GetAsync(ContentHash.OfString("no-such-report"));

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByDocumentAsync_ReturnsAllReportsForDocument()
    {
        await using var db = await TestDb.CreateAsync();
        var store = new SqliteEvaluationStore(db.Options, NullLogger<SqliteEvaluationStore>.Instance);

        var docId = ContentHash.OfString("shared-document");

        // Two reports for the same document (different rule sets)
        var r1 = BuildReport(docId) with { RuleSetId = "rs-1" };
        var r2 = BuildReport(docId) with { RuleSetId = "rs-2" };
        // One report for a different document
        var r3 = BuildReport(ContentHash.OfString("other-document"));

        await store.SaveAsync(r1);
        await store.SaveAsync(r2);
        await store.SaveAsync(r3);

        var results = await store.GetByDocumentAsync(docId);

        results.Should().HaveCount(2);
        results.Select(r => r.RuleSetId).Should().BeEquivalentTo(["rs-1", "rs-2"]);
    }

    [Fact]
    public async Task SaveAsync_IdempotentOnIdenticalReport()
    {
        await using var db = await TestDb.CreateAsync();
        var store = new SqliteEvaluationStore(db.Options, NullLogger<SqliteEvaluationStore>.Instance);

        var report = BuildReport();

        await store.SaveAsync(report);
        await store.SaveAsync(report); // second save — must not throw

        var results = await store.GetByDocumentAsync(report.DocumentId);
        results.Should().HaveCount(1);
    }

    private static string Canonical(ComplianceReport r) =>
        JsonSerializer.Serialize(r, CanonicalJson.Compact);
}
