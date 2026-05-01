using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Cli;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Evaluation.Engine;
using LambdaRag.Projection.Projectors;
using LambdaRag.Selectors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LambdaRag.UnitTests.Regulatory;

/// <summary>
/// Genericity guard for the Quebec Law 25 ruleset.
///
/// The engine MUST evaluate the QC-LOI25-* ruleset using only the generic
/// projection paths (<c>input1.topics</c>, <c>input1.text</c>,
/// <c>input1.text_features.*</c>, <c>input1.category</c>). No engine code
/// has any knowledge of Quebec, Loi 25, P-39.1, or A-2.1. We prove that
/// here by running the ruleset against:
///
/// 1. A completely synthetic, non-Quebec vendor MSA — no rule should
///    Error; rules that don't apply to the document simply yield no
///    verdicts (predicate fails) or Fail verdicts (lambda fails).
/// 2. A Quebec-relevant synthetic privacy notice with the language Loi 25
///    requires — the relevant rules emit Pass.
///
/// If a future change accidentally hardcodes Quebec-specific behaviour
/// into the engine (or breaks the generic predicates in the JSON), one of
/// these assertions fails.
/// </summary>
public class GenericQuebecRuleEvaluationTests
{
    private static readonly TimeProvider Frozen = new TestFrozenTimeProvider(
        new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class TestFrozenTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private static string RulesetPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "samples", "contracts", "loi-25-ruleset.json"));

    private static EvaluationService Build()
    {
        var matcher = new JsonPathSelectorMatcher(NullLogger<JsonPathSelectorMatcher>.Instance);
        return new EvaluationService(matcher, NullLogger<EvaluationService>.Instance, Frozen);
    }

    private static JsonObject Section(string id, string category, string text, params string[] topics)
    {
        var topicsArr = new JsonArray();
        foreach (var t in topics) topicsArr.Add(t);
        return new JsonObject
        {
            ["id"] = id,
            ["heading"] = id,
            ["category"] = category,
            ["primary_topic"] = category,
            ["topics"] = topicsArr,
            ["is_operative_for_topic"] = true,
            ["text"] = text,
            ["text_features"] = TextFeatureExtractor.Extract(text),
        };
    }

    private static ProjectedDocument Doc(params JsonObject[] sections)
    {
        var arr = new JsonArray();
        foreach (var s in sections) arr.Add(s);
        return new ProjectedDocument(
            ContentHash.OfString("doc-bytes"),
            "test-projector",
            "1.0",
            new JsonObject { ["sections"] = arr },
            new Dictionary<string, SourceSpan>(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Loi25_ruleset_evaluates_without_errors_on_non_quebec_document()
    {
        var rs = RuleSetIO.Load(RulesetPath);

        // A synthetic vendor MSA that has nothing to do with Quebec or
        // privacy at all. Some rules will not apply (predicate false);
        // others will evaluate and Fail. Critically, none should Error
        // — that would mean the engine choked on the lambda shape,
        // which would indicate non-generic engine behaviour.
        var doc = Doc(
            Section("payment", "payment", "NET 60 payment terms apply.", "payment"),
            Section("ip", "ip", "Vendor retains all background IP.", "ip"),
            Section("warranty", "warranty", "Services provided as-is.", "warranty"));

        var report = await Build().EvaluateAsync(rs, doc);

        report.Errored.Should().Be(0,
            "the QC-LOI25 ruleset must use only generic engine paths and never trip the evaluator on non-Quebec text");
    }

    [Fact]
    public async Task Loi25_ruleset_emits_pass_when_quebec_required_language_is_present()
    {
        var rs = RuleSetIO.Load(RulesetPath);

        // A Quebec-aware privacy notice that contains the language Loi 25
        // requires. The DPO rule in particular should evaluate to Pass.
        var doc = Doc(
            Section(
                "privacy-notice",
                "privacy",
                "Our Data Protection Officer (DPO) responsible for Quebec privacy " +
                "matters can be contacted at privacy@example.com. Le responsable " +
                "de la protection des renseignements personnels (RPRP) est joignable " +
                "aux coordonnées ci-dessus.",
                "privacy"));

        var report = await Build().EvaluateAsync(rs, doc);

        report.Errored.Should().Be(0);
        report.Verdicts.Should().Contain(v =>
            v.RuleId == "QC-LOI25-DPO-001" && v.Outcome == VerdictOutcome.Pass,
            "DPO designation rule must Pass when the privacy notice names a DPO/RPRP with contact info");
    }
}
