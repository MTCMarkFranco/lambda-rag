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

    // ---- Pillar 8 POC (#133) — ResolveAnchorSpan ---------------------

    private static IDisposable PushBindings(params (string Anchor, TokenMatch[] Matches)[] bindings)
    {
        var map = new Dictionary<string, IReadOnlyList<TokenMatch>>(StringComparer.Ordinal);
        foreach (var (a, m) in bindings) map[a] = m;
        return SemanticBindingAccessor.Push(new DictionarySemanticBindingScope(map));
    }

    [Fact]
    public void ResolveAnchorSpan_returns_null_when_scope_absent()
    {
        // No scope pushed.
        LambdaPrimitives.ResolveAnchorSpan("rpo").Should().BeNull();
    }

    [Fact]
    public void ResolveAnchorSpan_returns_null_for_null_or_empty_anchor()
    {
        using var _ = PushBindings(("rpo", new[] { new TokenMatch("rpo", 0.9, 0, 3) }));
        LambdaPrimitives.ResolveAnchorSpan(null!).Should().BeNull();
        LambdaPrimitives.ResolveAnchorSpan("").Should().BeNull();
    }

    [Fact]
    public void ResolveAnchorSpan_returns_null_when_anchor_has_no_bindings()
    {
        using var _ = PushBindings(("rto", new[] { new TokenMatch("rto", 0.9, 0, 3) }));
        LambdaPrimitives.ResolveAnchorSpan("rpo").Should().BeNull();
    }

    [Fact]
    public void ResolveAnchorSpan_picks_highest_cosine_when_multiple_bindings()
    {
        using var _ = PushBindings(("rpo", new[]
        {
            new TokenMatch("recovery", 0.81, 50, 8),
            new TokenMatch("rpo",      0.99, 10, 3),
            new TokenMatch("point",    0.78, 20, 5),
        }));
        var best = LambdaPrimitives.ResolveAnchorSpan("rpo");
        best.Should().NotBeNull();
        best!.Text.Should().Be("rpo");
        best.Cosine.Should().Be(0.99);
    }

    [Fact]
    public void ResolveAnchorSpan_ties_broken_by_lowest_charstart_then_ordinal_text()
    {
        using var _ = PushBindings(("rpo", new[]
        {
            new TokenMatch("zebra", 0.90, 5, 5),
            new TokenMatch("alpha", 0.90, 5, 5),   // same cosine, same offset, ordinal-lower text → wins
            new TokenMatch("beta",  0.90, 10, 4),  // higher offset → loses
        }));
        var best = LambdaPrimitives.ResolveAnchorSpan("rpo");
        best!.Text.Should().Be("alpha");
        best.CharStart.Should().Be(5);
    }

    [Fact]
    public void ResolveAnchorSpan_is_idempotent_across_calls()
    {
        using var _ = PushBindings(("rpo", new[]
        {
            new TokenMatch("rpo",       0.99, 10, 3),
            new TokenMatch("recovery",  0.81, 50, 8),
        }));
        var a = LambdaPrimitives.ResolveAnchorSpan("rpo");
        var b = LambdaPrimitives.ResolveAnchorSpan("rpo");
        a.Should().Be(b);
    }

    [Fact]
    public void ResolveAnchorSpan_with_text_falls_back_to_literal_when_no_bindings()
    {
        using var _ = PushBindings();
        var text = "Business RTO/RPO: RTO: 72 hours\nRPO: 24 hours\n";
        var span = LambdaPrimitives.ResolveAnchorSpan("rpo", text);
        span.Should().NotBeNull();
        span!.Cosine.Should().Be(1.0);
        span.CharStart.Should().Be(13);
        span.CharLength.Should().Be(3);
    }

    [Fact]
    public void ResolveAnchorSpan_with_text_returns_null_when_literal_not_present()
    {
        using var _ = PushBindings();
        var text = "Backup window is the second Sunday of every month.";
        LambdaPrimitives.ResolveAnchorSpan("rpo", text).Should().BeNull();
    }

    [Fact]
    public void ResolveAnchorSpan_with_text_prefers_cosine_when_present()
    {
        using var _ = PushBindings(("rpo", new[]
        {
            new TokenMatch("recovery point objective", 0.92, 42, 24),
        }));
        var text = "Earlier mentions of rpo are out of policy scope. " +
                   "The recovery point objective is the policy commitment.";
        var span = LambdaPrimitives.ResolveAnchorSpan("rpo", text);
        span.Should().NotBeNull();
        span!.CharStart.Should().Be(42);
        span.Cosine.Should().Be(0.92);
    }

    // ---- Pillar 8 POC (#133) — ExtractDurationNear -------------------

    private const string PassChunk =
        "RPO: 4 hours. RTO: 2 hours. Failover via warm standby.";

    private const string FailChunk =
        "We will document RPO and RTO commitments in a future release of this PSA.";

    // Bindings hand-authored to mirror what the Pillar 6 evaluator would
    // populate after a high-cosine token-level match against the anchor
    // embedding. CharStart values are real offsets into the chunks above.
    private static (string Anchor, TokenMatch[] Matches)[] PassChunkBindings() => new[]
    {
        ("rpo", new[] { new TokenMatch("rpo", 1.0, 0,  3) }),
        ("rto", new[] { new TokenMatch("rto", 1.0, 14, 3) }),
    };

    private static (string Anchor, TokenMatch[] Matches)[] FailChunkBindings() => new[]
    {
        ("rpo", new[] { new TokenMatch("rpo", 1.0, 17, 3) }),
        ("rto", new[] { new TokenMatch("rto", 1.0, 25, 3) }),
    };

    [Fact]
    public void ExtractDurationNear_returns_null_when_text_is_empty()
    {
        using var _ = PushBindings(PassChunkBindings());
        LambdaPrimitives.ExtractDurationNear("", "rpo").Should().BeNull();
        LambdaPrimitives.ExtractDurationNear(null!, "rpo").Should().BeNull();
    }

    [Fact]
    public void ExtractDurationNear_returns_null_when_anchor_is_empty()
    {
        using var _ = PushBindings(PassChunkBindings());
        LambdaPrimitives.ExtractDurationNear(PassChunk, "").Should().BeNull();
        LambdaPrimitives.ExtractDurationNear(PassChunk, null!).Should().BeNull();
    }

    [Fact]
    public void ExtractDurationNear_returns_null_when_no_anchor_signal_in_text()
    {
        // No bindings pushed AND text contains no literal "rpo" — neither
        // cosine nor literal-fallback resolve the anchor.
        var text = "Backup completes nightly. Restore tested quarterly.";
        LambdaPrimitives.ExtractDurationNear(text, "rpo").Should().BeNull();
    }

    [Fact]
    public void ExtractDurationNear_uses_literal_fallback_when_no_bindings()
    {
        // No cosine bindings, but the literal anchor name appears in text —
        // the fallback locates it and extraction proceeds. This mirrors
        // the actual ARB-PSA shape where RTO/RPO acronyms are present but
        // don't clear the rule-level cosine threshold against the
        // multi-word anchor text.
        var text = "RPO: 4 hours.";
        LambdaPrimitives.ExtractDurationNear(text, "rpo").Should().Be(TimeSpan.FromHours(4));
    }

    [Fact]
    public void ExtractDurationNear_returns_null_when_anchor_has_no_binding_and_not_in_text()
    {
        // Different anchor is bound. The requested anchor name isn't in
        // bindings AND isn't a literal whole-word in text.
        using var _ = PushBindings(("rto", new[] { new TokenMatch("rto", 1.0, 14, 3) }));
        var text = "RTO: 2 hours.";
        LambdaPrimitives.ExtractDurationNear(text, "rpo").Should().BeNull();
    }

    [Fact]
    public void ExtractDurationNear_finds_hours_after_anchor()
    {
        using var _ = PushBindings(PassChunkBindings());
        var d = LambdaPrimitives.ExtractDurationNear(PassChunk, "rpo");
        d.Should().Be(TimeSpan.FromHours(4));
    }

    [Fact]
    public void ExtractDurationNear_finds_hours_for_rto_anchor()
    {
        using var _ = PushBindings(PassChunkBindings());
        var d = LambdaPrimitives.ExtractDurationNear(PassChunk, "rto");
        d.Should().Be(TimeSpan.FromHours(2));
    }

    [Theory]
    [InlineData("RPO target is 30 minutes.",          "rpo", 0, 3, 30 * 60.0)]   // seconds
    [InlineData("RPO target is 30 mins.",             "rpo", 0, 3, 30 * 60.0)]
    [InlineData("RPO target is 5 seconds.",           "rpo", 0, 3, 5.0)]
    [InlineData("RPO target is 5 secs.",              "rpo", 0, 3, 5.0)]
    [InlineData("RPO target is 7 days.",              "rpo", 0, 3, 7 * 86400.0)]
    [InlineData("RPO target is 1 day.",               "rpo", 0, 3, 86400.0)]
    [InlineData("RPO target is 2 weeks.",             "rpo", 0, 3, 14 * 86400.0)]
    [InlineData("RPO target is 3 wk.",                "rpo", 0, 3, 21 * 86400.0)]
    [InlineData("RPO target is 4hrs.",                "rpo", 0, 3, 4 * 3600.0)]
    [InlineData("RPO target is 4HOURS.",              "rpo", 0, 3, 4 * 3600.0)]
    [InlineData("RPO target is 4.5 hours.",           "rpo", 0, 3, 4.5 * 3600.0)]
    [InlineData("RPO target is 4,5 hours.",           "rpo", 0, 3, 4.5 * 3600.0)] // EU decimal
    public void ExtractDurationNear_parses_unit_aliases(
        string text, string anchor, int charStart, int charLen, double expectedSeconds)
    {
        using var _ = PushBindings((anchor, new[] { new TokenMatch(anchor, 1.0, charStart, charLen) }));
        var d = LambdaPrimitives.ExtractDurationNear(text, anchor);
        d.Should().NotBeNull();
        d!.Value.TotalSeconds.Should().BeApproximately(expectedSeconds, 0.0001);
    }

    [Fact]
    public void ExtractDurationNear_returns_null_when_terms_only_no_values()
    {
        using var _ = PushBindings(FailChunkBindings());
        LambdaPrimitives.ExtractDurationNear(FailChunk, "rpo").Should().BeNull();
        LambdaPrimitives.ExtractDurationNear(FailChunk, "rto").Should().BeNull();
    }

    [Fact]
    public void ExtractDurationNear_returns_null_when_duration_outside_window()
    {
        // Long single sentence — anchor at offset 0, duration far downstream
        // inside the SAME sentence. Default window=120 chars stops short of
        // the duration; larger window reaches it.
        var text = "RPO is committed by the platform team, the operations director, "
                 + "the architecture council, the chief technology officer, and we set it to 4 hours.";
        using var _ = PushBindings(("rpo", new[] { new TokenMatch("rpo", 1.0, 0, 3) }));
        LambdaPrimitives.ExtractDurationNear(text, "rpo", windowChars: 120).Should().BeNull();
        LambdaPrimitives.ExtractDurationNear(text, "rpo", windowChars: 500).Should().Be(TimeSpan.FromHours(4));
    }

    [Fact]
    public void ExtractDurationNear_scopes_to_anchor_sentence_not_neighbors()
    {
        // Different sentence holds the duration — must NOT be extracted even
        // though it lies inside the window radius. Sentence scoping prevents
        // bleed-over between anchor commitments and ambient text.
        var text = "RPO will be documented in a future release. The architecture team has 4 hours of weekly office hours.";
        using var _ = PushBindings(("rpo", new[] { new TokenMatch("rpo", 1.0, 0, 3) }));
        LambdaPrimitives.ExtractDurationNear(text, "rpo", windowChars: 500).Should().BeNull();
    }

    [Fact]
    public void ExtractDurationNear_picks_nearest_to_anchor_when_multiple_matches_in_window()
    {
        // Both 3h (offset 5) and 8h (offset 34) lie in the window. With anchor
        // at offset 0 (RPO), 3h is the closer span (gap=2 vs gap=27). Picks 3h.
        var text = "RPO: 3 hours target, escalated to 8 hours under degraded mode.";
        using var _ = PushBindings(("rpo", new[] { new TokenMatch("rpo", 1.0, 0, 3) }));
        LambdaPrimitives.ExtractDurationNear(text, "rpo").Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public void ExtractDurationNear_picks_correct_value_per_anchor_in_shared_chunk()
    {
        // Same chunk, two anchors — nearest-to-anchor semantics yield the
        // right value for each anchor independently (the leftmost-in-window
        // alternative would incorrectly return 4h for the RTO anchor too).
        using var _ = PushBindings(PassChunkBindings());
        LambdaPrimitives.ExtractDurationNear(PassChunk, "rpo").Should().Be(TimeSpan.FromHours(4));
        LambdaPrimitives.ExtractDurationNear(PassChunk, "rto").Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public void ExtractDurationNear_does_not_match_hyphenated_form()
    {
        var text = "RPO is a 4-hour budget.";
        using var _ = PushBindings(("rpo", new[] { new TokenMatch("rpo", 1.0, 0, 3) }));
        // "4-hour" is hyphenated; the regex requires whitespace OR no separator
        // before the unit. The hyphen breaks word-boundary unit alternation.
        LambdaPrimitives.ExtractDurationNear(text, "rpo").Should().BeNull();
    }

    [Fact]
    public void ExtractDurationNear_does_not_match_bare_m_or_d_or_s_or_w()
    {
        // Ambiguous bare units intentionally excluded.
        using var _ = PushBindings(("rpo", new[] { new TokenMatch("rpo", 1.0, 0, 3) }));
        LambdaPrimitives.ExtractDurationNear("RPO is 4m", "rpo").Should().BeNull();
        LambdaPrimitives.ExtractDurationNear("RPO is 4d", "rpo").Should().BeNull();
        LambdaPrimitives.ExtractDurationNear("RPO is 4s", "rpo").Should().BeNull();
        LambdaPrimitives.ExtractDurationNear("RPO is 4w", "rpo").Should().BeNull();
    }

    [Fact]
    public void ExtractDurationNear_window_zero_only_matches_inside_anchor_span()
    {
        // windowChars=0 → slice is exactly the anchor span ("rpo"), which has no digits.
        using var _ = PushBindings(("rpo", new[] { new TokenMatch("rpo", 1.0, 0, 3) }));
        LambdaPrimitives.ExtractDurationNear(PassChunk, "rpo", windowChars: 0).Should().BeNull();
    }

    [Fact]
    public void ExtractDurationNear_negative_window_treated_as_zero()
    {
        using var _ = PushBindings(("rpo", new[] { new TokenMatch("rpo", 1.0, 0, 3) }));
        LambdaPrimitives.ExtractDurationNear(PassChunk, "rpo", windowChars: -50).Should().BeNull();
    }

    [Fact]
    public void ExtractDurationNear_is_byte_identical_across_two_calls()
    {
        using var _ = PushBindings(PassChunkBindings());
        var a = LambdaPrimitives.ExtractDurationNear(PassChunk, "rpo");
        var b = LambdaPrimitives.ExtractDurationNear(PassChunk, "rpo");
        a.Should().Be(b);
    }

    // ---- Pillar 8 POC (#133) — HasExtractedDurationNear --------------

    [Fact]
    public void HasExtractedDurationNear_delegates_to_extract()
    {
        using var _ = PushBindings(PassChunkBindings());
        LambdaPrimitives.HasExtractedDurationNear(PassChunk, "rpo").Should().BeTrue();
        LambdaPrimitives.HasExtractedDurationNear(PassChunk, "missing").Should().BeFalse();
    }

    [Fact]
    public void HasExtractedDurationNear_returns_false_when_no_anchor_signal_in_text()
    {
        var text = "Backup completes nightly. Restore tested quarterly.";
        LambdaPrimitives.HasExtractedDurationNear(text, "rpo").Should().BeFalse();
    }

    // ---- Pillar 8 POC (#133) — ARCHITECTURAL PROOF -------------------
    // Composes the new ARB-PSA-DR-001 lambda shape against two crafted
    // chunks and asserts the same lambda discriminates presence-of-value
    // from presence-of-term. This is the gate the prompt contract
    // (§POC integration test) calls out as the architectural validation.

    [Fact]
    public void DR001_extraction_lambda_passes_when_durations_present()
    {
        using var _ = PushBindings(PassChunkBindings());

        // Composition equivalent to:
        //   !IsTemplateBoilerplate(input1.text)
        //     && HasExtractedDurationNear(input1.text, "rpo")
        //     && HasExtractedDurationNear(input1.text, "rto")
        var result =
            !LambdaPrimitives.IsTemplateBoilerplate(PassChunk)
            && LambdaPrimitives.HasExtractedDurationNear(PassChunk, "rpo")
            && LambdaPrimitives.HasExtractedDurationNear(PassChunk, "rto");

        result.Should().BeTrue(
            "PASS chunk specifies real RPO (4h) and RTO (2h) values — policy intent satisfied.");
    }

    [Fact]
    public void DR001_extraction_lambda_fails_when_terms_only_no_values()
    {
        using var _ = PushBindings(FailChunkBindings());

        var result =
            !LambdaPrimitives.IsTemplateBoilerplate(FailChunk)
            && LambdaPrimitives.HasExtractedDurationNear(FailChunk, "rpo")
            && LambdaPrimitives.HasExtractedDurationNear(FailChunk, "rto");

        result.Should().BeFalse(
            "FAIL chunk mentions RPO and RTO but commits no values — pure lexical match would " +
            "incorrectly PASS; extraction-based rule correctly FAILs.");
    }

    [Fact]
    public void DR001_extraction_lambda_is_byte_identical_across_two_evaluations()
    {
        bool Evaluate(string chunk, (string, TokenMatch[])[] bindings)
        {
            using var _ = PushBindings(bindings);
            return !LambdaPrimitives.IsTemplateBoilerplate(chunk)
                && LambdaPrimitives.HasExtractedDurationNear(chunk, "rpo")
                && LambdaPrimitives.HasExtractedDurationNear(chunk, "rto");
        }

        var p1 = Evaluate(PassChunk, PassChunkBindings());
        var p2 = Evaluate(PassChunk, PassChunkBindings());
        var f1 = Evaluate(FailChunk, FailChunkBindings());
        var f2 = Evaluate(FailChunk, FailChunkBindings());

        p1.Should().Be(p2).And.BeTrue();
        f1.Should().Be(f2).And.BeFalse();
    }
}
