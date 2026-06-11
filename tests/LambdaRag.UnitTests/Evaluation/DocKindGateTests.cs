using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Selectors;
using LambdaRag.Evaluation.Engine;
using LambdaRag.Selectors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LambdaRag.UnitTests.Evaluation;

/// <summary>
/// Pillar 1 (#116) — doc-kind gating tests. Rules whose
/// <see cref="Rule.AppliesToDocKinds"/> doesn't intersect the resolved
/// doc kind must be skipped (emitting <see cref="VerdictOutcome.Skipped"/>),
/// while rules with no declared doc-kind list must continue to evaluate
/// (backward compatibility with all pre-Pillar-1 rulesets).
/// </summary>
public class DocKindGateTests
{
    private static readonly TimeProvider Frozen = new TestFrozenTimeProvider(
        new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class TestFrozenTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private static EvaluationService Build()
    {
        var matcher = new JsonPathSelectorMatcher(NullLogger<JsonPathSelectorMatcher>.Instance);
        return new EvaluationService(matcher, NullLogger<EvaluationService>.Instance, Frozen);
    }

    private static ProjectedDocument Doc(params (string id, string category, string text)[] sections)
    {
        var arr = new JsonArray();
        foreach (var (id, cat, text) in sections)
            arr.Add(new JsonObject { ["id"] = id, ["category"] = cat, ["text"] = text, ["heading"] = id });
        var graph = new JsonObject { ["sections"] = arr };
        return new ProjectedDocument(
            ContentHash.OfString("doc-bytes"),
            "test-projector", "1.0",
            graph,
            new Dictionary<string, SourceSpan>(StringComparer.Ordinal));
    }

    private static Rule MakeRule(string id, string lambda = "true", IReadOnlyList<string>? appliesTo = null)
        => new(
            Id: id,
            Version: "1.0.0",
            NaturalLanguage: $"Rule {id}",
            Lambda: lambda,
            AppliesToSchema: new JsonObject(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Violation,
            SourceSpan: new SourceSpan("policy", 0, 0, 1, null),
            EvidenceQuote: id,
            Metadata: new Dictionary<string, string>())
        {
            Predicate = "true",
            AppliesToDocKinds = appliesTo,
        };

    private static RuleSet RuleSet(IReadOnlyList<string>? rulesetKinds, params Rule[] rules) => new(
        Id: "rs-test", Version: "1.0.0", Domain: "contract",
        PublishedAt: DateTimeOffset.UnixEpoch,
        Rules: rules,
        Metadata: new Dictionary<string, string>())
    { AppliesToDocKinds = rulesetKinds };

    [Fact]
    public async Task Rule_with_no_appliesToDocKinds_runs_normally_even_when_docKind_is_set()
    {
        var rule = MakeRule("R1", lambda: "true");
        var report = await Build().EvaluateAsync(RuleSet(null, rule), Doc(("s1", "x", "hello")), docKind: "arb-psa");

        report.Verdicts.Should().ContainSingle().Which.Outcome.Should().Be(VerdictOutcome.Pass);
        report.Skipped.Should().BeNull("no rule declared a doc-kind list, so no skips fire");
        report.WrongProfile.Should().BeNull();
    }

    [Fact]
    public async Task Rule_appliesTo_arb_psa_is_skipped_when_docKind_is_contract()
    {
        var rule = MakeRule("R1", lambda: "true", appliesTo: new[] { "arb-psa" });
        var report = await Build().EvaluateAsync(
            RuleSet(null, rule),
            Doc(("s1", "x", "hello")),
            docKind: "contract");

        report.Verdicts.Should().ContainSingle().Which.Outcome.Should().Be(VerdictOutcome.Skipped);
        report.Verdicts.Single().ErrorMessage.Should().Be("doc_kind_mismatch:contract");
        report.Skipped.Should().Be(1);
        report.WrongProfile.Should().BeTrue("every rule that ran was skipped");
    }

    [Fact]
    public async Task RulesetLevel_appliesTo_alone_is_sufficient_to_gate_rules()
    {
        // Rule itself has no list; ruleset declares arb-psa. Should still skip.
        var rule = MakeRule("R1", lambda: "true", appliesTo: null);
        var report = await Build().EvaluateAsync(
            RuleSet(new[] { "arb-psa" }, rule),
            Doc(("s1", "x", "hello")),
            docKind: "contract");

        report.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Skipped);
        report.WrongProfile.Should().BeTrue();
    }

    [Fact]
    public async Task Mixed_ruleset_emits_partial_skips_without_wrongProfile()
    {
        var rPsa = MakeRule("R-PSA", lambda: "true", appliesTo: new[] { "arb-psa" });
        var rAll = MakeRule("R-ALL", lambda: "true");

        var report = await Build().EvaluateAsync(
            RuleSet(null, rPsa, rAll),
            Doc(("s1", "x", "hello")),
            docKind: "contract");

        report.Verdicts.Should().HaveCount(2);
        report.Verdicts.Single(v => v.RuleId == "R-PSA").Outcome.Should().Be(VerdictOutcome.Skipped);
        report.Verdicts.Single(v => v.RuleId == "R-ALL").Outcome.Should().Be(VerdictOutcome.Pass);
        report.Skipped.Should().Be(1);
        report.WrongProfile.Should().BeNull("at least one rule ran, so the profile is partially right");
    }

    [Fact]
    public async Task No_docKind_means_no_gate_fires_regardless_of_rule_declarations()
    {
        var rule = MakeRule("R1", lambda: "true", appliesTo: new[] { "arb-psa" });
        var report = await Build().EvaluateAsync(RuleSet(null, rule), Doc(("s1", "x", "hello")), docKind: null);

        report.Verdicts.Single().Outcome.Should().Be(VerdictOutcome.Pass);
    }

    [Fact]
    public async Task Skipped_verdict_ids_are_byte_identical_across_two_runs()
    {
        var rule = MakeRule("R1", lambda: "true", appliesTo: new[] { "arb-psa" });
        var rs = RuleSet(null, rule);
        var doc = Doc(("s1", "x", "hello"));

        var r1 = await Build().EvaluateAsync(rs, doc, docKind: "contract");
        var r2 = await Build().EvaluateAsync(rs, doc, docKind: "contract");

        r1.Verdicts.Single().Id.Should().Be(r2.Verdicts.Single().Id);
    }

    [Fact]
    public void RuleSet_fingerprint_is_unchanged_when_appliesToDocKinds_is_null_or_empty()
    {
        var ruleA = MakeRule("R1"); // no AppliesToDocKinds
        var ruleB = MakeRule("R1") with { AppliesToDocKinds = Array.Empty<string>() };
        var rsA = RuleSet(null, ruleA);
        var rsB = RuleSet(Array.Empty<string>(), ruleB);

        // Both shapes are "applies to all" → byte-identical fingerprints.
        rsA.Fingerprint().Should().Be(rsB.Fingerprint());
    }

    [Fact]
    public void RuleSet_fingerprint_changes_when_appliesToDocKinds_becomes_non_empty()
    {
        var rA = MakeRule("R1");
        var rB = MakeRule("R1", appliesTo: new[] { "arb-psa" });
        rA.Fingerprint().Should().NotBe(rB.Fingerprint(),
            "a non-empty doc-kind list is a behaviour change and must alter the fingerprint");
    }
}

/// <summary>
/// Determinism tests for <see cref="DocKindResolver"/> — its dictionaries
/// are part of the runtime contract, so any drift must show up here.
/// </summary>
public class DocKindResolverTests
{
    [Fact]
    public void Explicit_kind_overrides_path_and_classifier()
    {
        var k = DocKindResolver.Resolve("contract", "samples/architecture/whatever.pdf", parsed: null);
        k.Should().Be("contract");
    }

    [Theory]
    [InlineData("samples/architecture/Example.pdf", "arb-psa")]
    [InlineData("samples\\architecture\\Example.pdf", "arb-psa")]
    [InlineData("samples/contracts/foo.docx", "contract")]
    [InlineData("rulesets/contracts/x.json", "contract")]
    [InlineData("totally/unrelated/path.md", "unknown")]
    public void Filename_heuristic_resolves_known_paths(string path, string expected)
    {
        DocKindResolver.Resolve(null, path, parsed: null).Should().Be(expected);
    }

    [Fact]
    public void Heading_bigram_classifier_picks_arb_psa_for_psa_headings()
    {
        var parsed = new ParsedDocument(
            new SourceDocument(ContentHash.OfString("x"), "x.md", SourceDocumentKind.Markdown, 0, DateTimeOffset.UnixEpoch),
            CanonicalText: "",
            Blocks: new[]
            {
                new ContentBlock("b1", ContentBlockKind.Heading,
                    "Project Solution Architecture", SourceSpan.Unknown, 1, "/"),
                new ContentBlock("b2", ContentBlockKind.Heading,
                    "Architecture Risks", SourceSpan.Unknown, 2, "/"),
                new ContentBlock("b3", ContentBlockKind.Heading,
                    "Design Patterns", SourceSpan.Unknown, 2, "/"),
            },
            Metadata: new Dictionary<string, string>());

        DocKindResolver.Resolve(null, path: null, parsed: parsed).Should().Be("arb-psa");
    }

    [Fact]
    public void Classifier_returns_null_when_no_bigrams_fire()
    {
        var parsed = new ParsedDocument(
            new SourceDocument(ContentHash.OfString("x"), "x.md", SourceDocumentKind.Markdown, 0, DateTimeOffset.UnixEpoch),
            CanonicalText: "",
            Blocks: new[]
            {
                new ContentBlock("b1", ContentBlockKind.Heading,
                    "Lorem ipsum dolor sit amet", SourceSpan.Unknown, 1, "/"),
            },
            Metadata: new Dictionary<string, string>());

        DocKindResolver.ClassifyByHeadings(parsed).Should().BeNull();
    }

    [Fact]
    public void Two_runs_of_classifier_on_same_input_return_byte_identical_result()
    {
        var parsed = new ParsedDocument(
            new SourceDocument(ContentHash.OfString("x"), "x.md", SourceDocumentKind.Markdown, 0, DateTimeOffset.UnixEpoch),
            CanonicalText: "",
            Blocks: new[]
            {
                new ContentBlock("b1", ContentBlockKind.Heading,
                    "Architecture Review Board", SourceSpan.Unknown, 1, "/"),
            },
            Metadata: new Dictionary<string, string>());

        var first = DocKindResolver.ClassifyByHeadings(parsed);
        var second = DocKindResolver.ClassifyByHeadings(parsed);
        first.Should().Be(second);
    }

    [Theory]
    [InlineData(null, null, "arb-psa", true)]                                  // no list = applies to all
    [InlineData(new string[] { }, null, "arb-psa", true)]                      // empty list = applies to all
    [InlineData(new[] { "arb-psa" }, null, "arb-psa", true)]                   // direct match (rule-level)
    [InlineData(new[] { "arb-psa" }, null, "contract", false)]                 // mismatch
    [InlineData(null, new[] { "arb-psa" }, "arb-psa", true)]                   // ruleset-level match
    [InlineData(null, new[] { "arb-psa" }, "contract", false)]                 // ruleset-level mismatch
    [InlineData(new[] { "contract" }, new[] { "arb-psa" }, "contract", true)]  // union semantics — either side matches
    public void Applies_uses_union_semantics_of_rule_and_ruleset_lists(
        string[]? ruleKinds, string[]? rulesetKinds, string docKind, bool expected)
    {
        DocKindResolver.Applies(ruleKinds, rulesetKinds, docKind).Should().Be(expected);
    }
}
