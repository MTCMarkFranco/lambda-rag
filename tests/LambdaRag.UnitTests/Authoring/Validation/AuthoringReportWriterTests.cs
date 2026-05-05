using FluentAssertions;
using LambdaRag.Authoring.Validation;
using Xunit;

namespace LambdaRag.UnitTests.Authoring.Validation;

public class AuthoringReportWriterTests
{
    private static RuleSetValidationReport MakeReport() => new(
        RulesetId: "rs-1",
        RulesetVersion: "1.0.0",
        EmbedderId: "deterministic-sha256/32",
        Epsilon: 0.05,
        Results: new List<RuleValidationResult>
        {
            new(
                RuleId: "R1",
                Positives: new List<ScoredExample> { new("p1", 0.9, "concept-a") },
                Negatives: new List<ScoredExample> { new("n1", 0.3, "concept-a") },
                MinPositive: 0.9, MaxNegative: 0.3, Margin: 0.6,
                CalibratedThreshold: 0.6, Accepted: true, RejectionReason: null),
            new(
                RuleId: "R2",
                Positives: new List<ScoredExample> { new("p2", 0.5, "concept-b") },
                Negatives: new List<ScoredExample> { new("n2", 0.6, "concept-b") },
                MinPositive: 0.5, MaxNegative: 0.6, Margin: -0.1,
                CalibratedThreshold: 0.55, Accepted: false, RejectionReason: "Negative scores higher."),
        },
        AllAccepted: false);

    [Fact]
    public void Serialise_IsByteIdentical_ForSameInput()
    {
        var w = new AuthoringReportWriter();
        var a = w.Serialise(MakeReport());
        var b = w.Serialise(MakeReport());
        a.Should().Be(b);
    }

    [Fact]
    public void Serialise_IncludesRequiredTopLevelFields()
    {
        var json = new AuthoringReportWriter().Serialise(MakeReport());
        json.Should().Contain("\"rulesetId\": \"rs-1\"");
        json.Should().Contain("\"embedderId\": \"deterministic-sha256/32\"");
        json.Should().Contain("\"allAccepted\": false");
        json.Should().Contain("\"acceptedCount\": 1");
        json.Should().Contain("\"rejectedCount\": 1");
    }

    [Fact]
    public async Task WriteAsync_CreatesDirectoryAndUtf8File()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lr-authoring-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "sub", "report.json");
        try
        {
            await new AuthoringReportWriter().WriteAsync(MakeReport(), path);
            File.Exists(path).Should().BeTrue();
            var bytes = await File.ReadAllBytesAsync(path);
            // No UTF-8 BOM.
            (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
