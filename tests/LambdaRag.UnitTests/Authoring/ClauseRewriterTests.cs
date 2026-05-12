using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Authoring.Editing;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Markup;
using Xunit;

namespace LambdaRag.UnitTests.Authoring;

/// <summary>
/// Unit tests for the new clause-rewrite pipeline:
///  * <see cref="DeterministicMockClauseRewriter"/> — returns remediation text verbatim.
///  * <see cref="ComplianceEditor.Normalize"/> — strips quotes, collapses whitespace, honors NO_REWRITE, clamps length.
///  * <see cref="AnnotationFactory.FromReportWithRewritesAsync"/> — emits a
///    <see cref="AnnotationKind.Replace"/> annotation when the rewriter returns text,
///    falls back to a plain <see cref="AnnotationKind.Comment"/> otherwise.
///  * <see cref="ComplianceEditor.ComputeCacheKey"/> — deterministic, sensitive to
///    rule id/version, verdict id, remediation text, and clause text.
/// </summary>
public sealed class ClauseRewriterTests
{
    [Fact]
    public async Task Mock_rewriter_returns_remediation_text_trimmed()
    {
        var rewriter = new DeterministicMockClauseRewriter();
        var verdict = MakeVerdict("v1", "r1", remediation: "  rewritten clause.  ");

        var result = await rewriter.RewriteAsync(verdict, "original", rule: null);

        result.Should().Be("rewritten clause.");
    }

    [Fact]
    public async Task Mock_rewriter_returns_null_when_no_remediation()
    {
        var rewriter = new DeterministicMockClauseRewriter();
        var verdict = MakeVerdict("v1", "r1", remediation: null);

        var result = await rewriter.RewriteAsync(verdict, "original", rule: null);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("NO_REWRITE", null)]
    [InlineData("\"quoted clause\"", "quoted clause")]
    [InlineData("\u201Csmart quotes\u201D", "smart quotes")]
    [InlineData("one   two\tthree", "one two three")]
    public void Normalize_strips_sentinel_quotes_and_whitespace(string? input, string? expected)
    {
        ComplianceEditor.Normalize(input, maxLength: 100).Should().Be(expected);
    }

    [Fact]
    public void Normalize_clamps_to_max_length_with_ellipsis()
    {
        var input = new string('x', 50);
        var result = ComplianceEditor.Normalize(input, maxLength: 10);
        result.Should().HaveLength(11).And.EndWith("\u2026");
    }

    [Fact]
    public void ComputeCacheKey_is_deterministic_and_distinguishes_inputs()
    {
        var rule = MakeRule("r1", "1");
        var verdict = MakeVerdict("v1", "r1", remediation: "fix it");

        var k1 = ComplianceEditor.ComputeCacheKey(rule, verdict, "clause text");
        var k2 = ComplianceEditor.ComputeCacheKey(rule, verdict, "clause text");
        var k3 = ComplianceEditor.ComputeCacheKey(rule, verdict, "different clause");

        k1.Should().Be(k2);
        k1.Should().NotBe(k3);
        k1.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task FromReportWithRewritesAsync_emits_Replace_when_rewriter_returns_text()
    {
        var rule = MakeRule("r1", "1");
        var verdict = MakeVerdict("v1", "r1", outcome: VerdictOutcome.Fail,
            remediation: "carrier shall maintain $5,000,000 in cyber liability coverage.",
            evidenceQuote: "carrier shall maintain $1,000,000 in cyber liability coverage.");
        var report = MakeReport(verdict);
        var rewriter = new DeterministicMockClauseRewriter();
        var rules = new Dictionary<string, Rule>(StringComparer.Ordinal) { [rule.Id] = rule };

        var annotations = new List<Annotation>();
        await foreach (var a in AnnotationFactory.FromReportWithRewritesAsync(report, rules, rewriter))
            annotations.Add(a);

        annotations.Should().Contain(a => a.Kind == AnnotationKind.Replace);
        var replace = annotations.First(a => a.Kind == AnnotationKind.Replace);
        replace.Replacement.Should().Be("carrier shall maintain $5,000,000 in cyber liability coverage.");
    }

    [Fact]
    public async Task FromReportWithRewritesAsync_falls_back_to_Comment_when_rewriter_returns_null()
    {
        var rule = MakeRule("r1", "1");
        var verdict = MakeVerdict("v1", "r1", outcome: VerdictOutcome.Fail,
            remediation: null, // mock returns null → comment-only path
            evidenceQuote: "some clause text");
        var report = MakeReport(verdict);
        var rewriter = new DeterministicMockClauseRewriter();
        var rules = new Dictionary<string, Rule>(StringComparer.Ordinal) { [rule.Id] = rule };

        var annotations = new List<Annotation>();
        await foreach (var a in AnnotationFactory.FromReportWithRewritesAsync(report, rules, rewriter))
            annotations.Add(a);

        annotations.Should().OnlyContain(a => a.Kind == AnnotationKind.Comment);
    }

    private static Verdict MakeVerdict(
        string id, string ruleId,
        VerdictOutcome outcome = VerdictOutcome.Fail,
        string? remediation = null,
        string evidenceQuote = "evidence quote")
    {
        return new Verdict(
            Id: id,
            RuleId: ruleId,
            RuleSetVersion: "1.0",
            Outcome: outcome,
            LambdaText: "true",
            EvaluatedInput: new JsonObject(),
            SourceSpan: new SourceSpan("doc", 0, evidenceQuote.Length, null, null),
            ErrorMessage: null,
            EvidenceQuotes: new[] { evidenceQuote },
            EvaluatedAt: DateTimeOffset.UnixEpoch)
        {
            RemediationText = remediation,
        };
    }

    private static Rule MakeRule(string id, string version)
    {
        var ruleType = typeof(Rule);
        // Use the existing Rule public ctor reflectively to stay decoupled from
        // its evolving positional shape (the rule record carries many fields).
        var ctor = ruleType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            args[i] = p.Name switch
            {
                "Id" => id,
                "Version" => version,
                _ => DefaultForParameter(p.ParameterType),
            };
        }
        return (Rule)ctor.Invoke(args);
    }

    private static object? DefaultForParameter(Type t)
    {
        if (t == typeof(string)) return string.Empty;
        if (t.IsValueType) return Activator.CreateInstance(t);
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return Activator.CreateInstance(typeof(List<>).MakeGenericType(t.GetGenericArguments()));
        if (t == typeof(IReadOnlyDictionary<string, string>))
            return new Dictionary<string, string>();
        return null;
    }

    private static ComplianceReport MakeReport(params Verdict[] verdicts)
    {
        return new ComplianceReport(
            DocumentId: ContentHash.OfString("doc"),
            RuleSetId: "rs",
            RuleSetVersion: "1.0",
            RuleSetFingerprint: ContentHash.OfString("rs"),
            ProjectorId: "proj",
            ProjectorVersion: "1",
            Score: 0.0,
            TotalRules: verdicts.Length,
            Passed: 0,
            Failed: verdicts.Count(v => v.Outcome == VerdictOutcome.Fail),
            NotApplicable: 0,
            Errored: 0,
            Verdicts: verdicts,
            GeneratedAt: DateTimeOffset.UnixEpoch);
    }
}
