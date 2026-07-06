using FluentAssertions;
using LambdaRag.Core.Observability;
using Xunit;

namespace LambdaRag.UnitTests.Observability;

public class RunManifestIOTests
{
    private static RunManifest Manifest(string ts, long elapsedMs) => new(
        ManifestVersion: RunManifestIO.CurrentVersion,
        RunId: "run-x",
        TimestampUtc: ts,
        Engine: new RunManifestEngine("1.2.0", "abc123", "1.2.0.0"),
        Input: new RunManifestInput("d.pdf", "lr1:doc", "ea-arb-psa", "enterprise-architecture"),
        RuleSet: new RunManifestRuleSet("rs.json", "rs", "1.0.0", "lr1:rs", 42),
        Facts: null,
        Verdicts: new RunManifestVerdicts(10, 5, 2, 25, 0, 42, 0.667),
        Elapsed: new RunManifestElapsed(elapsedMs),
        Refusal: null);

    [Fact]
    public void Identical_inputs_serialize_byte_identically()
    {
        var a = Manifest("2026-07-06T00:00:00Z", 12345);
        var b = Manifest("2026-07-06T00:00:00Z", 12345);
        RunManifestIO.Serialize(a).Should().Be(RunManifestIO.Serialize(b));
    }

    [Fact]
    public void RunId_is_stable_across_days_when_reproducibility_inputs_match()
    {
        var runId1 = RunManifestIO.ComposeRunId("1.2.0", "abc123", "lr1:doc", "lr1:rs", "lr1:s", "lr1:p");
        var runId2 = RunManifestIO.ComposeRunId("1.2.0", "abc123", "lr1:doc", "lr1:rs", "lr1:s", "lr1:p");
        runId1.Should().Be(runId2);
    }

    [Theory]
    [InlineData("1.2.1", "abc123", "lr1:doc", "lr1:rs", "lr1:s", "lr1:p")]
    [InlineData("1.2.0", "def456", "lr1:doc", "lr1:rs", "lr1:s", "lr1:p")]
    [InlineData("1.2.0", "abc123", "lr1:doc2", "lr1:rs", "lr1:s", "lr1:p")]
    [InlineData("1.2.0", "abc123", "lr1:doc", "lr1:rs2", "lr1:s", "lr1:p")]
    [InlineData("1.2.0", "abc123", "lr1:doc", "lr1:rs", "lr1:s2", "lr1:p")]
    [InlineData("1.2.0", "abc123", "lr1:doc", "lr1:rs", "lr1:s", "lr1:p2")]
    public void RunId_changes_when_any_reproducibility_input_changes(
        string engine, string sha, string doc, string rs, string s, string p)
    {
        var baseline = RunManifestIO.ComposeRunId("1.2.0", "abc123", "lr1:doc", "lr1:rs", "lr1:s", "lr1:p");
        RunManifestIO.ComposeRunId(engine, sha, doc, rs, s, p).Should().NotBe(baseline);
    }

    [Fact]
    public void Null_facts_hashes_differently_than_present_facts()
    {
        var withFacts = RunManifestIO.ComposeRunId("1.2.0", "abc", "d", "rs", "s", "p");
        var noFacts = RunManifestIO.ComposeRunId("1.2.0", "abc", "d", "rs", null, null);
        withFacts.Should().NotBe(noFacts);
    }
}
