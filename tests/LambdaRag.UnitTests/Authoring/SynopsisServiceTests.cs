using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Authoring.Synopsis;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using Xunit;

namespace LambdaRag.UnitTests.Authoring;

/// <summary>
/// Unit-level coverage for the deterministic surface of
/// <see cref="SynopsisService"/> — the parts that don't need an LLM:
///   • <c>Normalize</c> shaping (sentence trimming, whitespace, length cap)
///   • <c>ComputeCacheKey</c> stability and sensitivity
///
/// The LLM call path is exercised via integration tests / live runs.
/// </summary>
public class SynopsisServiceTests
{
    private static Rule MakeRule(
        string id = "PAY-001",
        string version = "1.0.0",
        string statement = "Payment terms must be 30 days or fewer.",
        string predicate = "input1.category == \"payment_terms\"",
        string lambda = "input1.netDays <= 30") => new(
        Id: id,
        Version: version,
        NaturalLanguage: statement,
        Lambda: lambda,
        AppliesToSchema: new JsonObject(),
        Selector: new PathSelector("$.sections[*]"),
        Severity: RuleSeverity.Violation,
        SourceSpan: new SourceSpan("doc", 0, 0, 1, null),
        EvidenceQuote: id,
        Metadata: new Dictionary<string, string>())
    { Predicate = predicate };

    [Fact]
    public void Normalize_keeps_a_single_well_formed_sentence_intact()
    {
        var s = SynopsisService.Normalize("Verifies the payment-terms clause uses 30-day or shorter net terms.");
        s.Should().Be("Verifies the payment-terms clause uses 30-day or shorter net terms.");
    }

    [Fact]
    public void Normalize_keeps_only_the_first_sentence_when_model_overruns()
    {
        var raw = "Verifies that net payment terms are <=30 days. Also flags ambiguous wording. Plus other things.";
        var s = SynopsisService.Normalize(raw);
        s.Should().Be("Verifies that net payment terms are <=30 days.");
    }

    [Fact]
    public void Normalize_collapses_internal_whitespace_and_strips_quotes()
    {
        var raw = "  \"Verifies\tthat   payment terms\nare 30 days.\"  ";
        var s = SynopsisService.Normalize(raw);
        s.Should().Be("Verifies that payment terms are 30 days.");
    }

    [Fact]
    public void Normalize_appends_period_when_missing()
    {
        var s = SynopsisService.Normalize("Verifies that payment terms are 30 days");
        s.Should().EndWith(".");
    }

    [Fact]
    public void Normalize_truncates_with_ellipsis_when_too_long()
    {
        var raw = new string('a', 250);
        var s = SynopsisService.Normalize(raw, maxLength: 100);
        s.Length.Should().BeLessOrEqualTo(101);
        s.Should().EndWith("\u2026");
    }

    [Fact]
    public void Normalize_throws_on_empty_input()
    {
        Action act = () => SynopsisService.Normalize("   ");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ComputeCacheKey_is_byte_stable_for_identical_rules()
    {
        var a = MakeRule();
        var b = MakeRule();
        SynopsisService.ComputeCacheKey(a).Should().Be(SynopsisService.ComputeCacheKey(b));
    }

    [Fact]
    public void ComputeCacheKey_changes_when_lambda_changes()
    {
        var a = MakeRule(lambda: "input1.netDays <= 30");
        var b = MakeRule(lambda: "input1.netDays <= 45");
        SynopsisService.ComputeCacheKey(a).Should().NotBe(SynopsisService.ComputeCacheKey(b));
    }

    [Fact]
    public void ComputeCacheKey_changes_when_natural_language_changes()
    {
        var a = MakeRule(statement: "Payment terms must be 30 days or fewer.");
        var b = MakeRule(statement: "Payment terms must not exceed 30 days.");
        SynopsisService.ComputeCacheKey(a).Should().NotBe(SynopsisService.ComputeCacheKey(b));
    }

    [Fact]
    public void ComputeCacheKey_changes_when_version_changes()
    {
        var a = MakeRule(version: "1.0.0");
        var b = MakeRule(version: "1.0.1");
        SynopsisService.ComputeCacheKey(a).Should().NotBe(SynopsisService.ComputeCacheKey(b));
    }

    [Fact]
    public void ComputeCacheKey_is_64_lowercase_hex_chars()
    {
        var key = SynopsisService.ComputeCacheKey(MakeRule());
        key.Should().HaveLength(64);
        key.Should().MatchRegex("^[0-9a-f]{64}$");
    }
}
