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
}
