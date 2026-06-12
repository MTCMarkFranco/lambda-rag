using FluentAssertions;
using LambdaRag.Core.Semantic;
using Xunit;

namespace LambdaRag.UnitTests.Evaluation;

/// <summary>
/// Pillar 3 (#118) + Pillar 5 (#120) — determinism + correctness tests for
/// <see cref="LambdaPrimitives"/>. These primitives are part of the rule
/// artifact contract; failures here mean a published ruleset would
/// produce drifted verdicts.
/// </summary>
public class LambdaPrimitivesTests
{
    // ---- RegexMatch ---------------------------------------------------

    [Theory]
    [InlineData("we maintain RPO of 4 hours", "(?i)\\b(?:rpo|recovery\\s+point\\s+objective)\\b", true)]
    [InlineData("recovery point objective is 1h", "(?i)\\b(?:rpo|recovery\\s+point\\s+objective)\\b", true)]
    [InlineData("the recovery is daily",         "(?i)\\b(?:rpo|recovery\\s+point\\s+objective)\\b", false)]
    [InlineData("", "anything", false)]
    [InlineData("text", "", false)]
    public void RegexMatch_pinned_pattern_returns_expected(string text, string pattern, bool expected)
    {
        LambdaPrimitives.RegexMatch(text, pattern).Should().Be(expected);
    }

    [Fact]
    public void RegexMatch_throws_on_malformed_pattern_with_error_marker()
    {
        var act = () => LambdaPrimitives.RegexMatch("text", "[invalid");
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(LambdaPrimitives.ErrorMarker);
    }

    [Fact]
    public void RegexMatch_two_runs_produce_byte_identical_bool()
    {
        var a = LambdaPrimitives.RegexMatch("Confidentiality survives for 5 years.", "(?i)\\bsurviv(?:e|es|al)\\b");
        var b = LambdaPrimitives.RegexMatch("Confidentiality survives for 5 years.", "(?i)\\bsurviv(?:e|es|al)\\b");
        a.Should().Be(b).And.BeTrue();
    }

    // ---- PhraseMatch --------------------------------------------------

    [Fact]
    public void PhraseMatch_uses_active_phrasebook_store()
    {
        var phrasebooks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["dr_rpo"] = new[] { "recovery point objective", "rpo", "4 hour recovery" },
        };
        using var _ = PhrasebookAccessor.Push(new DictionaryPhrasebookStore(phrasebooks));

        LambdaPrimitives.PhraseMatch("we target a 4 hour recovery", "dr_rpo").Should().BeTrue();
        LambdaPrimitives.PhraseMatch("yearly basis", "dr_rpo").Should().BeFalse();
    }

    [Fact]
    public void PhraseMatch_throws_when_phrasebook_id_is_unknown()
    {
        using var _ = PhrasebookAccessor.Push(new DictionaryPhrasebookStore(new Dictionary<string, IReadOnlyList<string>>()));
        var act = () => LambdaPrimitives.PhraseMatch("text", "missing");
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(LambdaPrimitives.ErrorMarker);
    }

    [Fact]
    public void PhraseMatch_throws_when_no_phrasebook_scope_is_active()
    {
        var act = () => LambdaPrimitives.PhraseMatch("text", "any");
        act.Should().Throw<InvalidOperationException>();
    }

    // ---- IsTemplateBoilerplate (Pillar 5) -----------------------------

    [Theory]
    [InlineData("This section will be completed in ARB-2 review.", true)]
    [InlineData("TBD — owner not yet assigned.", true)]
    [InlineData("[insert risk severity here]", true)]
    [InlineData("Lorem ipsum dolor sit amet consectetur adipiscing elit.", true)]
    [InlineData("The risk register is fully populated with 12 entries; each row has severity, mitigation owner, and status.", false)]
    [InlineData("Network topology uses three subnets in active-active across two regions.", false)]
    [InlineData("", false)]
    public void IsTemplateBoilerplate_flags_known_placeholder_phrases(string text, bool expected)
    {
        LambdaPrimitives.IsTemplateBoilerplate(text).Should().Be(expected);
    }

    [Fact]
    public void IsTemplateBoilerplate_phrase_list_is_non_empty_and_stable_across_calls()
    {
        var first = string.Join("|", LambdaPrimitives.BoilerplatePhrases);
        var second = string.Join("|", LambdaPrimitives.BoilerplatePhrases);
        first.Should().Be(second);
        LambdaPrimitives.BoilerplatePhrases.Should().NotBeEmpty();
    }

    // ---- HasTopic (Pillar 7 / #129) ----------------------------------

    private static System.Dynamic.ExpandoObject ExpandoWith(params (string Key, object? Value)[] pairs)
    {
        var e = new System.Dynamic.ExpandoObject();
        var dict = (IDictionary<string, object?>)e;
        foreach (var (k, v) in pairs) dict[k] = v;
        return e;
    }

    [Fact]
    public void HasTopic_returns_true_when_topics_array_contains_value()
    {
        var input = ExpandoWith(("topics", new List<object?> { "design_patterns", "dr_resiliency" }));
        LambdaPrimitives.HasTopic(input, "dr_resiliency").Should().BeTrue();
    }

    [Fact]
    public void HasTopic_returns_false_when_topics_array_does_not_contain_value()
    {
        var input = ExpandoWith(("topics", new List<object?> { "design_patterns" }));
        LambdaPrimitives.HasTopic(input, "decision_records").Should().BeFalse();
    }

    [Fact]
    public void HasTopic_returns_false_for_null_input()
    {
        LambdaPrimitives.HasTopic(null, "design_patterns").Should().BeFalse();
    }

    [Fact]
    public void HasTopic_returns_false_for_null_or_empty_topic()
    {
        var input = ExpandoWith(("topics", new List<object?> { "design_patterns" }));
        LambdaPrimitives.HasTopic(input, null!).Should().BeFalse();
        LambdaPrimitives.HasTopic(input, "").Should().BeFalse();
    }

    [Fact]
    public void HasTopic_returns_false_when_topics_key_missing()
    {
        var input = ExpandoWith(("category", (object?)"design_patterns"));
        LambdaPrimitives.HasTopic(input, "design_patterns").Should().BeFalse();
    }

    [Fact]
    public void HasTopic_returns_false_when_topics_is_null_or_empty_or_non_enumerable()
    {
        LambdaPrimitives.HasTopic(ExpandoWith(("topics", (object?)null)), "x").Should().BeFalse();
        LambdaPrimitives.HasTopic(ExpandoWith(("topics", new List<object?>())), "x").Should().BeFalse();
        LambdaPrimitives.HasTopic(ExpandoWith(("topics", (object?)"design_patterns")), "design_patterns")
            .Should().BeFalse();
    }

    [Fact]
    public void HasTopic_skips_non_string_elements_and_keeps_matching_siblings()
    {
        var input = ExpandoWith(("topics",
            (object?)new List<object?> { 42L, true, null, "design_patterns" }));
        LambdaPrimitives.HasTopic(input, "design_patterns").Should().BeTrue();
        LambdaPrimitives.HasTopic(input, "42").Should().BeFalse();
    }

    [Fact]
    public void HasTopic_axis_qualified_topic_requires_exact_match_no_prefix()
    {
        var input = ExpandoWith(("topics",
            (object?)new List<object?> { "platform:azure" }));
        LambdaPrimitives.HasTopic(input, "platform:azure").Should().BeTrue();
        LambdaPrimitives.HasTopic(input, "platform").Should().BeFalse();
        LambdaPrimitives.HasTopic(input, "azure").Should().BeFalse();
    }

    [Fact]
    public void HasTopic_primary_topic_alone_does_not_satisfy_membership()
    {
        var input = ExpandoWith(
            ("category", (object?)"decision_records"),
            ("topics", (object?)new List<object?>()));
        LambdaPrimitives.HasTopic(input, "decision_records").Should().BeFalse();
    }

    [Fact]
    public void HasTopic_works_on_jsonobject_input()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(
            "{\"topics\":[\"design_patterns\",\"dr_resiliency\"]}")
            as System.Text.Json.Nodes.JsonObject;
        LambdaPrimitives.HasTopic(json, "dr_resiliency").Should().BeTrue();
        LambdaPrimitives.HasTopic(json, "psa_completeness").Should().BeFalse();
    }

    [Fact]
    public void HasTopic_is_deterministic_across_two_calls()
    {
        var input = ExpandoWith(("topics", new List<object?> { "design_patterns" }));
        var a = LambdaPrimitives.HasTopic(input, "design_patterns");
        var b = LambdaPrimitives.HasTopic(input, "design_patterns");
        a.Should().Be(b);
    }

    // Proxy for Microsoft RulesEngine's DynamicClassFactory output: a real
    // POCO with a typed `topics` property. At evaluation time `input1` is
    // an instance of a generated class shaped like this, NOT an
    // ExpandoObject — so HasTopic must read `topics` via reflection.
    private sealed class SectionPocoStrings
    {
        public string category { get; set; } = "";
        public List<string> topics { get; set; } = new();
    }

    private sealed class SectionPocoObjects
    {
        public string category { get; set; } = "";
        public List<object?> topics { get; set; } = new();
    }

    private sealed class SectionPocoArray
    {
        public object?[] topics { get; set; } = Array.Empty<object?>();
    }

    private sealed class SectionPocoNoTopics
    {
        public string category { get; set; } = "";
    }

    [Fact]
    public void HasTopic_reads_topics_via_reflection_on_poco_with_string_list()
    {
        var p = new SectionPocoStrings { topics = new() { "design_patterns", "dr_resiliency" } };
        LambdaPrimitives.HasTopic(p, "dr_resiliency").Should().BeTrue();
        LambdaPrimitives.HasTopic(p, "decision_records").Should().BeFalse();
    }

    [Fact]
    public void HasTopic_reads_topics_via_reflection_on_poco_with_object_list()
    {
        var p = new SectionPocoObjects { topics = new() { "decision_records", 42L } };
        LambdaPrimitives.HasTopic(p, "decision_records").Should().BeTrue();
        LambdaPrimitives.HasTopic(p, "missing").Should().BeFalse();
    }

    [Fact]
    public void HasTopic_reads_topics_via_reflection_on_poco_with_array()
    {
        var p = new SectionPocoArray { topics = new object?[] { "security_architecture" } };
        LambdaPrimitives.HasTopic(p, "security_architecture").Should().BeTrue();
    }

    [Fact]
    public void HasTopic_returns_false_when_poco_has_no_topics_member()
    {
        var p = new SectionPocoNoTopics { category = "design_patterns" };
        LambdaPrimitives.HasTopic(p, "design_patterns").Should().BeFalse();
    }

    // ---- HasTopic with score gate (Pillar 7 / #129) -------------------

    [Fact]
    public void HasTopic_with_score_gate_passes_when_score_meets_threshold()
    {
        var scores = ExpandoWith(("decision_records", (object?)0.9));
        var input = ExpandoWith(
            ("topics", new List<object?> { "decision_records" }),
            ("topic_scores", (object?)scores));
        LambdaPrimitives.HasTopic(input, "decision_records", 0.5).Should().BeTrue();
        LambdaPrimitives.HasTopic(input, "decision_records", 0.9).Should().BeTrue();
    }

    [Fact]
    public void HasTopic_with_score_gate_fails_when_score_below_threshold()
    {
        // 0.4 body-only keyword match — exactly the case Pillar 7 wants
        // to filter out so false positives stay contained.
        var scores = ExpandoWith(("decision_records", (object?)0.4));
        var input = ExpandoWith(
            ("topics", new List<object?> { "decision_records" }),
            ("topic_scores", (object?)scores));
        LambdaPrimitives.HasTopic(input, "decision_records", 0.5).Should().BeFalse();
        LambdaPrimitives.HasTopic(input, "decision_records", 0.4).Should().BeTrue();
    }

    [Fact]
    public void HasTopic_with_score_gate_fails_when_topic_scores_missing()
    {
        var input = ExpandoWith(("topics", new List<object?> { "decision_records" }));
        LambdaPrimitives.HasTopic(input, "decision_records", 0.5).Should().BeFalse();
    }

    [Fact]
    public void HasTopic_with_zero_threshold_is_equivalent_to_membership_only()
    {
        var input = ExpandoWith(("topics", new List<object?> { "decision_records" }));
        // Threshold 0.0 → don't even consult topic_scores; pure membership.
        LambdaPrimitives.HasTopic(input, "decision_records", 0.0).Should().BeTrue();
    }

    private sealed class SectionPocoWithScores
    {
        public List<string> topics { get; set; } = new();
        public Dictionary<string, double> topic_scores { get; set; } = new();
    }

    [Fact]
    public void HasTopic_with_score_gate_reads_topic_scores_via_reflection()
    {
        var p = new SectionPocoWithScores
        {
            topics = new() { "decision_records" },
            topic_scores = new() { ["decision_records"] = 0.9 },
        };
        LambdaPrimitives.HasTopic(p, "decision_records", 0.5).Should().BeTrue();
        p.topic_scores["decision_records"] = 0.3;
        LambdaPrimitives.HasTopic(p, "decision_records", 0.5).Should().BeFalse();
    }

    // ---- HasSyntheticAnchor (Pillar 7.B #130) -------------------------

    [Fact]
    public void HasSyntheticAnchor_returns_false_for_null_input()
    {
        LambdaPrimitives.HasSyntheticAnchor(null).Should().BeFalse();
        LambdaPrimitives.HasSyntheticAnchor(null, "x").Should().BeFalse();
    }

    [Fact]
    public void HasSyntheticAnchor_returns_false_when_flag_missing()
    {
        var input = ExpandoWith(("topics", new List<object?> { "x" }));
        LambdaPrimitives.HasSyntheticAnchor(input).Should().BeFalse();
    }

    [Fact]
    public void HasSyntheticAnchor_returns_true_for_synthetic_section_expando()
    {
        var input = ExpandoWith(
            ("is_synthetic_anchor", (object?)true),
            ("synthetic_anchor", (object?)"severity"));
        LambdaPrimitives.HasSyntheticAnchor(input).Should().BeTrue();
        LambdaPrimitives.HasSyntheticAnchor(input, "severity").Should().BeTrue();
        LambdaPrimitives.HasSyntheticAnchor(input, "rationale").Should().BeFalse();
    }

    [Fact]
    public void HasSyntheticAnchor_returns_false_when_flag_explicitly_false()
    {
        var input = ExpandoWith(("is_synthetic_anchor", (object?)false));
        LambdaPrimitives.HasSyntheticAnchor(input).Should().BeFalse();
    }

    [Fact]
    public void HasSyntheticAnchor_reads_jsonobject_input()
    {
        var input = new System.Text.Json.Nodes.JsonObject
        {
            ["is_synthetic_anchor"] = true,
            ["synthetic_anchor"] = "rationale",
        };
        LambdaPrimitives.HasSyntheticAnchor(input).Should().BeTrue();
        LambdaPrimitives.HasSyntheticAnchor(input, "rationale").Should().BeTrue();
        LambdaPrimitives.HasSyntheticAnchor(input, "severity").Should().BeFalse();
    }

    private sealed class SyntheticPoco
    {
        public bool is_synthetic_anchor { get; set; }
        public string? synthetic_anchor { get; set; }
    }

    [Fact]
    public void HasSyntheticAnchor_reads_poco_via_reflection()
    {
        var p = new SyntheticPoco
        {
            is_synthetic_anchor = true,
            synthetic_anchor = "owner",
        };
        LambdaPrimitives.HasSyntheticAnchor(p).Should().BeTrue();
        LambdaPrimitives.HasSyntheticAnchor(p, "owner").Should().BeTrue();
        LambdaPrimitives.HasSyntheticAnchor(p, "severity").Should().BeFalse();

        p.is_synthetic_anchor = false;
        LambdaPrimitives.HasSyntheticAnchor(p).Should().BeFalse();
        LambdaPrimitives.HasSyntheticAnchor(p, "owner").Should().BeFalse();
    }

    [Fact]
    public void HasSyntheticAnchor_empty_anchor_name_returns_false()
    {
        var input = ExpandoWith(
            ("is_synthetic_anchor", (object?)true),
            ("synthetic_anchor", (object?)"severity"));
        LambdaPrimitives.HasSyntheticAnchor(input, "").Should().BeFalse();
        LambdaPrimitives.HasSyntheticAnchor(input, null!).Should().BeFalse();
    }
}
