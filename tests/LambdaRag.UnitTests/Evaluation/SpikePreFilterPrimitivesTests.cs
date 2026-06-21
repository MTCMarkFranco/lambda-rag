using FluentAssertions;
using LambdaRag.Core.Semantic;
using Xunit;

namespace LambdaRag.UnitTests.Evaluation;

/// <summary>
/// Pillar 9 — pre-filter primitives ported from policy-compiler-spike
/// v0.1.1: <see cref="LambdaPrimitives.IsArbScaffolding"/>,
/// <see cref="LambdaPrimitives.IsGlossaryOrAppendixListing"/>, and the
/// helper <see cref="LambdaPrimitives.CountProseSentences"/>.
/// </summary>
public class SpikePreFilterPrimitivesTests
{
    // ─── CountProseSentences ────────────────────────────────────────────

    [Theory]
    [InlineData("", 0)]
    [InlineData("Short tag.", 0)]
    [InlineData("Architecture Risks (ARB-1)", 0)]
    [InlineData("The risk register is fully populated with 12 entries.", 1)]
    [InlineData("ARB members are appointed by the AVP for a renewable two-year term.", 1)]
    [InlineData(
        "ARB members are appointed by the AVP for a renewable two-year term. "
        + "The board reviews every proposed solution against the published architecture principles.",
        2)]
    public void CountProseSentences_returns_expected_count(string text, int expected)
    {
        LambdaPrimitives.CountProseSentences(text).Should().Be(expected);
    }

    [Fact]
    public void CountProseSentences_does_not_split_on_version_numbers()
    {
        // "6.4.1" must NOT count as three sentences — the negative
        // lookbehind on \d guards this.
        var text =
            "Per section 6.4.1 the architecture review board requires every solution "
            + "to address resilience explicitly with documented RPO and RTO targets.";
        LambdaPrimitives.CountProseSentences(text).Should().Be(1);
    }

    // ─── IsArbScaffolding ───────────────────────────────────────────────

    [Fact]
    public void IsArbScaffolding_flags_chunk_dominated_by_ARB_tags_with_no_prose()
    {
        // Three ARB tags + no prose ⇒ scaffolding.
        var text =
            "Solution Summary (ARB-1)\n"
            + "Architecture Constraints (ARB-1)\n"
            + "Architecture Risks (ARB-1)\n"
            + "Decision Records (ARB-2)";
        LambdaPrimitives.IsArbScaffolding(text).Should().BeTrue();
    }

    [Fact]
    public void IsArbScaffolding_flags_template_phrase_stubs()
    {
        // "Required for ARB-2" repeated three times + no prose ⇒ scaffolding.
        var text =
            "Data controls list: required for ARB-2.\n"
            + "Filename: required for ARB-2.\n"
            + "Timestamp: required for ARB-2.";
        LambdaPrimitives.IsArbScaffolding(text).Should().BeTrue();
    }

    [Fact]
    public void IsArbScaffolding_does_not_flag_substantive_section_carrying_an_ARB_tag()
    {
        // Two ARB tags but multiple real prose sentences ⇒ NOT scaffolding.
        // Guards against killing the legitimate "Solution Summary (ARB-1)"
        // section that is followed by paragraphs of real project context.
        var text =
            "Solution Summary (ARB-1)\n"
            + "The Shipping 360 platform replaces three legacy systems with a single Azure-native "
            + "service mesh and a unified shipment-tracking data model.\n"
            + "The new platform addresses scale and reliability gaps that the current Sendsuite "
            + "deployment cannot meet under projected peak loads.\n"
            + "Detailed integration sequencing is described in section 4 (ARB-1).";
        LambdaPrimitives.IsArbScaffolding(text).Should().BeFalse();
    }

    [Fact]
    public void IsArbScaffolding_returns_false_for_empty_or_whitespace_text()
    {
        LambdaPrimitives.IsArbScaffolding("").Should().BeFalse();
        LambdaPrimitives.IsArbScaffolding("   \n  \n").Should().BeFalse();
    }

    // ─── IsGlossaryOrAppendixListing ────────────────────────────────────

    [Fact]
    public void IsGlossaryOrAppendixListing_flags_chunk_with_glossary_heading_and_low_prose()
    {
        var text =
            "Appendix A — Glossary\n"
            + "ARB Architecture Review Board\n"
            + "PSA Project Solution Architecture\n"
            + "RPO Recovery Point Objective\n"
            + "RTO Recovery Time Objective";
        LambdaPrimitives.IsGlossaryOrAppendixListing(text).Should().BeTrue();
    }

    [Fact]
    public void IsGlossaryOrAppendixListing_flags_chunk_with_references_heading()
    {
        var text =
            "References\n"
            + "ISO/IEC 27001 — Information Security Management.\n"
            + "NIST SP 800-53 — Security Controls.";
        LambdaPrimitives.IsGlossaryOrAppendixListing(text).Should().BeTrue();
    }

    [Fact]
    public void IsGlossaryOrAppendixListing_flags_chunk_with_four_acronym_definition_rows()
    {
        // No glossary heading, but ≥4 acronym-definition rows ⇒ glossary.
        var text =
            "ARB Architecture Review Board\n"
            + "PSA Project Solution Architecture\n"
            + "DLP Data Loss Prevention\n"
            + "MFA Multi Factor Authentication";
        LambdaPrimitives.IsGlossaryOrAppendixListing(text).Should().BeTrue();
    }

    [Fact]
    public void IsGlossaryOrAppendixListing_does_not_flag_sla_table_with_numbers()
    {
        // Critical guard: lines with digits/percent (SLA data tables) must
        // NOT be classified as glossary. This is the ABCCo regression case.
        var text =
            "Azure SQL Database 99.99 percent monthly availability\n"
            + "Azure Cosmos DB 99.999 percent monthly availability\n"
            + "Azure Service Bus 99.9 percent monthly availability\n"
            + "Azure Storage 99.99 percent monthly availability";
        LambdaPrimitives.IsGlossaryOrAppendixListing(text).Should().BeFalse();
    }

    [Fact]
    public void IsGlossaryOrAppendixListing_does_not_flag_real_obligation_text_mentioning_glossary()
    {
        // Mentions "glossary" but is real prose with multiple sentences.
        // Heading hit is satisfied, but prose-sentence count > 2 disables.
        var text =
            "The architecture review board maintains a glossary of approved acronyms in the "
            + "shared workspace.\n"
            + "Every solution submitted to the board must define any non-glossary terms inline "
            + "to avoid review delays.\n"
            + "The board has rejected six submissions in the last quarter for failing this "
            + "definition requirement.";
        LambdaPrimitives.IsGlossaryOrAppendixListing(text).Should().BeFalse();
    }

    [Fact]
    public void IsGlossaryOrAppendixListing_returns_false_for_empty_or_whitespace_text()
    {
        LambdaPrimitives.IsGlossaryOrAppendixListing("").Should().BeFalse();
        LambdaPrimitives.IsGlossaryOrAppendixListing("   \n").Should().BeFalse();
    }
}
