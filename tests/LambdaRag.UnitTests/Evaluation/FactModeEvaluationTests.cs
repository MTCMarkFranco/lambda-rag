using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Facts;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Selectors;
using LambdaRag.Evaluation.Engine;
using LambdaRag.Selectors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LambdaRag.UnitTests.Evaluation;

/// <summary>
/// Pillar 12 (#153) tests for the fact-mode evaluation path. Uses a stub
/// <see cref="IFactExtractor"/> to inject canned per-section fact bags so
/// the tests are deterministic and LLM-free.
/// </summary>
public class FactModeEvaluationTests
{
    private static readonly TimeProvider Frozen = new FrozenTime(
        new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class FrozenTime(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private sealed class StubFactExtractor(SectionFactSidecar sidecar) : IFactExtractor
    {
        public string ModelId => "stub-model";
        public string PromptHash => "stub-prompt";
        public Task<SectionFactSidecar> ExtractAsync(
            ProjectedDocument document, FactSchema schema, CancellationToken ct = default)
            => Task.FromResult(sidecar);
    }

    private static FactSchema Schema() => new(
        "es-v1", "1",
        new[]
        {
            new FactConcept("encryption_declared", FactType.Boolean, ""),
            new FactConcept("key_rotation_days", FactType.Integer, ""),
            new FactConcept("tls_min_version", FactType.Enum, "") { EnumValues = new[] { "1.2", "1.3" } },
            new FactConcept("data_classification", FactType.Enum, "") { EnumValues = new[] { "Public", "Confidential", "Restricted" } },
            new FactConcept("storage_region", FactType.Text, ""),
        });

    private static SectionFactSidecar Sidecar(
        Dictionary<string, Dictionary<string, object?>> sections,
        Dictionary<string, List<string>>? ruleScope = null)
    {
        var flatSections = sections.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, object?>)kv.Value,
            StringComparer.Ordinal);
        return new SectionFactSidecar(
            SidecarVersion: "1.0",
            DocumentId: "doc-1",
            FactSchemaId: "es-v1",
            FactSchemaHash: "hash",
            ModelId: "stub-model",
            PromptHash: "stub-prompt",
            GeneratedAt: "2026-01-01T00:00:00Z",
            Sections: flatSections)
        {
            RuleScope = ruleScope?.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<string>)kv.Value,
                StringComparer.Ordinal),
        };
    }

    private static ProjectedDocument Doc()
    {
        return new ProjectedDocument(
            ContentHash.OfString("doc-1"),
            "test-projector", "1.0",
            new JsonObject { ["sections"] = new JsonArray() },
            new Dictionary<string, SourceSpan>(StringComparer.Ordinal)
            {
                ["s1"] = new SourceSpan("doc-1", 0, 10, null, "s1"),
                ["s2"] = new SourceSpan("doc-1", 10, 10, null, "s2"),
            });
    }

    private static Rule FactRule(string id, string lambda, params string[] requiredFacts) => new(
        Id: id,
        Version: "1.0.0",
        NaturalLanguage: "Fact-mode rule " + id,
        Lambda: lambda,
        AppliesToSchema: new JsonObject(),
        Selector: new PathSelector("$"),
        Severity: RuleSeverity.Violation,
        SourceSpan: new SourceSpan("doc-1", 0, 0, null, null),
        EvidenceQuote: "",
        Metadata: new Dictionary<string, string>())
    {
        EvaluationMode = "facts",
        RequiredFacts = requiredFacts,
    };

    private static RuleSet Set(params Rule[] rules) => new(
        Id: "rs", Version: "1.0", Domain: "test",
        PublishedAt: DateTimeOffset.UnixEpoch,
        Rules: rules,
        Metadata: new Dictionary<string, string>())
    {
        FactSchema = Schema(),
    };

    private static EvaluationService Build(SectionFactSidecar sidecar) => new(
        new JsonPathSelectorMatcher(NullLogger<JsonPathSelectorMatcher>.Instance),
        NullLogger<EvaluationService>.Instance,
        Frozen,
        factExtractor: new StubFactExtractor(sidecar));

    // ── Baseline compound rule ─────────────────────────────────────────────

    [Fact]
    public async Task Compound_Boolean_And_Integer_Passes_When_Union_Satisfies()
    {
        var sidecar = Sidecar(new()
        {
            ["s1"] = new() { ["encryption_declared"] = true },
            ["s2"] = new() { ["key_rotation_days"] = 90L },
        });
        var rule = FactRule("R1",
            "facts.encryption_declared == true && facts.key_rotation_days <= 90",
            "encryption_declared", "key_rotation_days");
        var svc = Build(sidecar);
        var report = await svc.EvaluateAsync(Set(rule), Doc());
        report.Verdicts.Should().ContainSingle();
        report.Verdicts[0].Outcome.Should().Be(VerdictOutcome.Pass);
    }

    [Fact]
    public async Task Compound_Fails_When_Rotation_Too_Slack()
    {
        var sidecar = Sidecar(new()
        {
            ["s1"] = new() { ["encryption_declared"] = true },
            ["s2"] = new() { ["key_rotation_days"] = 365L },
        });
        var rule = FactRule("R1",
            "facts.encryption_declared == true && facts.key_rotation_days <= 90",
            "encryption_declared", "key_rotation_days");
        var svc = Build(sidecar);
        var report = await svc.EvaluateAsync(Set(rule), Doc());
        report.Verdicts[0].Outcome.Should().Be(VerdictOutcome.Fail);
    }

    [Fact]
    public async Task Compound_Missing_Required_Fact_Yields_NotApplicable_On_Mandatory()
    {
        var sidecar = Sidecar(new()
        {
            ["s1"] = new() { ["key_rotation_days"] = 90L },
        });
        var rule = FactRule("R1",
            "facts.encryption_declared == true && facts.key_rotation_days <= 90",
            "encryption_declared", "key_rotation_days");
        var svc = Build(sidecar);
        var report = await svc.EvaluateAsync(Set(rule), Doc());
        // Contract: any RequiredFact null in the union → doc silent → NA
        // (advisory). The doc doesn't discuss the concept; softer signal
        // than Gap. Diagnostic tag preserved in ErrorMessage.
        report.Verdicts[0].Outcome.Should().Be(VerdictOutcome.NotApplicable);
        report.Verdicts[0].ErrorMessage.Should().Contain("encryption_declared");
    }

    // ── Merge semantics ────────────────────────────────────────────────────

    [Fact]
    public async Task Boolean_Union_Is_Or_Across_Sections()
    {
        var sidecar = Sidecar(new()
        {
            ["s1"] = new() { ["encryption_declared"] = false },
            ["s2"] = new() { ["encryption_declared"] = true },
        });
        var rule = FactRule("R1", "facts.encryption_declared == true", "encryption_declared");
        var svc = Build(sidecar);
        var report = await svc.EvaluateAsync(Set(rule), Doc());
        report.Verdicts[0].Outcome.Should().Be(VerdictOutcome.Pass);
    }

    [Fact]
    public async Task Integer_Union_Uses_Min_Across_Sections()
    {
        var sidecar = Sidecar(new()
        {
            ["s1"] = new() { ["key_rotation_days"] = 365L },
            ["s2"] = new() { ["key_rotation_days"] = 60L },
        });
        var rule = FactRule("R1", "facts.key_rotation_days <= 90", "key_rotation_days");
        var svc = Build(sidecar);
        var report = await svc.EvaluateAsync(Set(rule), Doc());
        report.Verdicts[0].Outcome.Should().Be(VerdictOutcome.Pass);
    }

    // ── Scope resolution ───────────────────────────────────────────────────

    [Fact]
    public async Task Explicit_RuleScope_Overrides_Required_Facts_Inference()
    {
        var sidecar = Sidecar(
            new()
            {
                ["s1"] = new() { ["encryption_declared"] = false },
                ["s2"] = new() { ["encryption_declared"] = true },
            },
            ruleScope: new() { ["R1"] = new() { "s1" } });
        var rule = FactRule("R1", "facts.encryption_declared == true", "encryption_declared");
        var svc = Build(sidecar);
        var report = await svc.EvaluateAsync(Set(rule), Doc());
        // Only s1 (false) is in scope → union = false → Fail
        report.Verdicts[0].Outcome.Should().Be(VerdictOutcome.Fail);
    }

    [Fact]
    public async Task No_Scoped_Sections_Yields_NotApplicable_For_Mandatory_Rule()
    {
        var sidecar = Sidecar(new()
        {
            ["sX"] = new() { ["tls_min_version"] = "1.3" },
        });
        var rule = FactRule("R1",
            "facts.encryption_declared == true && facts.key_rotation_days <= 90",
            "encryption_declared", "key_rotation_days");
        var svc = Build(sidecar);
        var report = await svc.EvaluateAsync(Set(rule), Doc());
        // Contract: no sidecar section mentions any RequiredFact → NA
        // (doc out-of-scope for this rule). Softer signal than Gap.
        report.Verdicts[0].Outcome.Should().Be(VerdictOutcome.NotApplicable);
        report.Verdicts[0].ErrorMessage.Should().Contain("no_scoped_sections");
    }

    // ── Enum + text ────────────────────────────────────────────────────────

    [Fact]
    public async Task Enum_Compound_Rule_Passes_When_Values_Match()
    {
        var sidecar = Sidecar(new()
        {
            ["s1"] = new() { ["data_classification"] = "Confidential" },
            ["s2"] = new() { ["storage_region"] = "Canada" },
        });
        var rule = FactRule("R1",
            "(facts.data_classification == \"Confidential\" || facts.data_classification == \"Restricted\") && facts.storage_region == \"Canada\"",
            "data_classification", "storage_region");
        var svc = Build(sidecar);
        var report = await svc.EvaluateAsync(Set(rule), Doc());
        report.Verdicts[0].Outcome.Should().Be(VerdictOutcome.Pass);
    }

    [Fact]
    public async Task Tls_Enum_Rule_Fails_When_Only_1_1_Declared()
    {
        // "1.1" is not in the schema's EnumValues, but the sidecar can still
        // report the raw string; the lambda tests string equality.
        var sidecar = Sidecar(new()
        {
            ["s1"] = new() { ["tls_min_version"] = "1.1" },
        });
        var rule = FactRule("R1",
            "facts.tls_min_version == \"1.2\" || facts.tls_min_version == \"1.3\"",
            "tls_min_version");
        var svc = Build(sidecar);
        var report = await svc.EvaluateAsync(Set(rule), Doc());
        report.Verdicts[0].Outcome.Should().Be(VerdictOutcome.Fail);
    }

    // ── Fail-loud paths ────────────────────────────────────────────────────

    [Fact]
    public async Task Fact_Mode_Rule_Without_Extractor_Yields_Error()
    {
        var svc = new EvaluationService(
            new JsonPathSelectorMatcher(NullLogger<JsonPathSelectorMatcher>.Instance),
            NullLogger<EvaluationService>.Instance,
            Frozen);
        var rule = FactRule("R1", "facts.encryption_declared == true", "encryption_declared");
        var report = await svc.EvaluateAsync(Set(rule), Doc());
        report.Verdicts[0].Outcome.Should().Be(VerdictOutcome.Error);
        report.Verdicts[0].ErrorMessage.Should().Contain("no_fact_extractor_configured");
    }

    [Fact]
    public async Task Fact_Mode_Rule_Without_Fact_Schema_Yields_Error()
    {
        var sidecar = Sidecar(new() { ["s1"] = new() { ["encryption_declared"] = true } });
        var rule = FactRule("R1", "facts.encryption_declared == true", "encryption_declared");
        var rs = new RuleSet(
            Id: "rs", Version: "1.0", Domain: "test",
            PublishedAt: DateTimeOffset.UnixEpoch,
            Rules: new[] { rule },
            Metadata: new Dictionary<string, string>());
        // FactSchema left null on the ruleset. Extractor still supplied.
        var svc = Build(sidecar);
        var report = await svc.EvaluateAsync(rs, Doc());
        report.Verdicts[0].Outcome.Should().Be(VerdictOutcome.Error);
        report.Verdicts[0].ErrorMessage.Should().Contain("ruleset_has_no_fact_schema");
    }

    // ── Byte-identity boundary ─────────────────────────────────────────────

    [Fact]
    public async Task Classic_Rules_Untouched_When_Ruleset_Has_No_Fact_Schema()
    {
        var classic = new Rule(
            Id: "C1", Version: "1.0.0",
            NaturalLanguage: "must encrypt",
            Lambda: "input1.text.Contains(\"encrypt\")",
            AppliesToSchema: new JsonObject(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Violation,
            SourceSpan: new SourceSpan("doc-1", 0, 0, null, null),
            EvidenceQuote: "must encrypt",
            Metadata: new Dictionary<string, string>());
        var rs = new RuleSet(
            Id: "rs", Version: "1.0", Domain: "test",
            PublishedAt: DateTimeOffset.UnixEpoch,
            Rules: new[] { classic },
            Metadata: new Dictionary<string, string>());
        var svc = new EvaluationService(
            new JsonPathSelectorMatcher(NullLogger<JsonPathSelectorMatcher>.Instance),
            NullLogger<EvaluationService>.Instance,
            Frozen);
        var doc = new ProjectedDocument(
            ContentHash.OfString("doc-1"),
            "test-projector", "1.0",
            new JsonObject
            {
                ["sections"] = new JsonArray(new JsonObject
                {
                    ["id"] = "s1",
                    ["text"] = "we must encrypt everything",
                })
            },
            new Dictionary<string, SourceSpan>(StringComparer.Ordinal));
        var report = await svc.EvaluateAsync(rs, doc);
        report.Verdicts.Should().ContainSingle();
        report.Verdicts[0].Outcome.Should().Be(VerdictOutcome.Pass);
    }

    // ── Conflict provenance surfaces in verdict input ──────────────────────

    [Fact]
    public async Task Conflicts_Appear_In_Evaluated_Input_When_Present()
    {
        var sidecar = Sidecar(new()
        {
            ["s1"] = new() { ["tls_min_version"] = "1.2" },
            ["s2"] = new() { ["tls_min_version"] = "1.3" },
        });
        var rule = FactRule("R1",
            "facts.tls_min_version == \"1.2\" || facts.tls_min_version == \"1.3\"",
            "tls_min_version");
        var svc = Build(sidecar);
        var report = await svc.EvaluateAsync(Set(rule), Doc());
        report.Verdicts[0].EvaluatedInput["_conflicts"].Should().NotBeNull();
    }

    [Fact]
    public async Task Deterministic_Second_Run_Same_Outcome()
    {
        var sidecar = Sidecar(new()
        {
            ["s1"] = new() { ["encryption_declared"] = true },
            ["s2"] = new() { ["key_rotation_days"] = 90L },
        });
        var rule = FactRule("R1",
            "facts.encryption_declared == true && facts.key_rotation_days <= 90",
            "encryption_declared", "key_rotation_days");
        var svc = Build(sidecar);
        var r1 = await svc.EvaluateAsync(Set(rule), Doc());
        var r2 = await svc.EvaluateAsync(Set(rule), Doc());
        r1.Verdicts[0].Id.Should().Be(r2.Verdicts[0].Id);
        r1.Verdicts[0].Outcome.Should().Be(r2.Verdicts[0].Outcome);
    }

    // ── Multiple fact rules ────────────────────────────────────────────────

    [Fact]
    public async Task Two_Fact_Rules_Each_Emit_One_Verdict()
    {
        var sidecar = Sidecar(new()
        {
            ["s1"] = new() { ["encryption_declared"] = true, ["key_rotation_days"] = 90L },
            ["s2"] = new() { ["tls_min_version"] = "1.3" },
        });
        var r1 = FactRule("R-ENC", "facts.encryption_declared == true", "encryption_declared");
        var r2 = FactRule("R-TLS", "facts.tls_min_version == \"1.3\"", "tls_min_version");
        var svc = Build(sidecar);
        var report = await svc.EvaluateAsync(Set(r1, r2), Doc());
        report.Verdicts.Should().HaveCount(2);
        report.Verdicts.Should().OnlyContain(v => v.Outcome == VerdictOutcome.Pass);
    }

    // ── Runtime error → Error verdict ──────────────────────────────────────

    [Fact]
    public async Task Bad_Lambda_Yields_Error_Verdict()
    {
        var sidecar = Sidecar(new()
        {
            ["s1"] = new() { ["encryption_declared"] = true },
        });
        var rule = FactRule("R1", "facts.nonexistent_field.SomeMethod()", "encryption_declared");
        var svc = Build(sidecar);
        var report = await svc.EvaluateAsync(Set(rule), Doc());
        report.Verdicts[0].Outcome.Should().Be(VerdictOutcome.Error);
    }
}
