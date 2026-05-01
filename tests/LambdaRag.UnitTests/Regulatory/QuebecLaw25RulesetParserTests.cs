using System.Text.RegularExpressions;
using FluentAssertions;
using LambdaRag.Cli;
using Xunit;

namespace LambdaRag.UnitTests.Regulatory;

/// <summary>
/// Structural guard for <c>samples/contracts/loi-25-ruleset.json</c>.
///
/// The Quebec Law 25 mapping is bilingual by contract: every rule must
/// carry a French translation, a citation back to the underlying statute,
/// and a quoted evidence span from the source. These assertions catch
/// regressions where someone hand-edits the JSON and drops one of those
/// fields.
/// </summary>
public class QuebecLaw25RulesetParserTests
{
    private static string RulesetPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "samples", "contracts", "loi-25-ruleset.json"));

    [Fact]
    public void Ruleset_loads_with_canonical_options()
    {
        var rs = RuleSetIO.Load(RulesetPath);

        rs.Id.Should().Be("rs_quebec_loi_25");
        rs.Version.Should().Be("1.0.0");
        rs.Domain.Should().Be("contract");
        rs.Rules.Should().HaveCountGreaterThanOrEqualTo(20,
            "Loi 25 mapping ships at least the 25 QC-LOI25-* rules");
    }

    [Fact]
    public void Every_rule_has_french_translation_and_law_reference()
    {
        var rs = RuleSetIO.Load(RulesetPath);
        var lawRefPattern = new Regex(
            @"(P-39\.1|A-2\.1|LCCJTI|c\.\s*C-1\.1)",
            RegexOptions.IgnoreCase);

        foreach (var rule in rs.Rules)
        {
            rule.Id.Should().StartWith("QC-LOI25-",
                "every rule in the Loi 25 ruleset uses the QC-LOI25-* prefix");
            rule.NaturalLanguage.Should().NotBeNullOrWhiteSpace();
            rule.EvidenceQuote.Should().NotBeNullOrWhiteSpace(
                $"rule {rule.Id} must carry an evidence quote from the statute");

            rule.Metadata.Should().ContainKey("naturalLanguageFr",
                $"rule {rule.Id} must carry a French translation");
            rule.Metadata["naturalLanguageFr"].Should().NotBeNullOrWhiteSpace(
                $"rule {rule.Id} naturalLanguageFr must be non-empty");

            rule.Metadata.Should().ContainKey("lawReference",
                $"rule {rule.Id} must cite the underlying statute");
            var lawRef = rule.Metadata["lawReference"];
            lawRefPattern.IsMatch(lawRef).Should().BeTrue(
                $"rule {rule.Id} lawReference '{lawRef}' must reference P-39.1, A-2.1, LCCJTI, or c.C-1.1");

            rule.Metadata.Should().ContainKey("reviewer",
                $"rule {rule.Id} must declare a reviewer label for SME triage");
            rule.Metadata["reviewer"].Should().Match(r =>
                r == "qc-privacy" || r == "qc-public-sector",
                $"rule {rule.Id} reviewer must be qc-privacy or qc-public-sector");
        }
    }

    [Fact]
    public void Severities_use_only_documented_tiers()
    {
        var rs = RuleSetIO.Load(RulesetPath);
        foreach (var rule in rs.Rules)
        {
            var sev = rule.Severity.ToString();
            (sev is "Critical" or "Violation" or "Deviation" or "Suggestion")
                .Should().BeTrue(
                    $"rule {rule.Id} severity '{sev}' must be one of the documented tiers");
        }
    }
}
