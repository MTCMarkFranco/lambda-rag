using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using LambdaRag.Evaluation.Engine;
using Xunit;

namespace LambdaRag.UnitTests.Evaluation;

public class RemediationRenderingTests
{
    private static Rule MakeRule(string? template) => new(
        Id: "PAY-001",
        Version: "1.0.0",
        NaturalLanguage: "Payment within 30 days.",
        Lambda: "true",
        AppliesToSchema: new JsonObject(),
        Selector: new PathSelector("$.sections[*]"),
        Severity: RuleSeverity.Violation,
        SourceSpan: new SourceSpan("doc", 0, 0, 1, null),
        EvidenceQuote: "30 days",
        Metadata: new Dictionary<string, string>
        {
            ["maxDays"] = "30",
            ["requiredJurisdiction"] = "Delaware",
        })
    {
        Remediation = template,
    };

    private static MatchedSection MakeSection(string text, string heading = "Payment Terms", string category = "payment_terms")
    {
        var node = new JsonObject
        {
            ["id"] = "sec-3",
            ["heading"] = heading,
            ["heading_path"] = "/Contract/" + heading,
            ["category"] = category,
            ["text"] = text,
        };
        return new MatchedSection("$.sections[0]", node, new SourceSpan("doc", 10, text.Length, 1, heading));
    }

    [Fact]
    public void Renders_RuleSectionAndMetaPlaceholders()
    {
        var rule = MakeRule("Replace the {section.heading} clause: pay within {meta.maxDays} days. (rule {rule.id})");
        var section = MakeSection("Customer shall pay within 45 days.");

        var result = RemediationRenderer.Render(rule.Remediation, rule, section);

        result.Should().Be("Replace the Payment Terms clause: pay within 30 days. (rule PAY-001)");
    }

    [Fact]
    public void Renders_SectionFirstSentence_FromText()
    {
        var rule = MakeRule("Issue with: \"{section.firstSentence}\"");
        var section = MakeSection("Customer shall pay within 45 days. Late fees apply.");

        var result = RemediationRenderer.Render(rule.Remediation, rule, section);

        result.Should().Be("Issue with: \"Customer shall pay within 45 days.\"");
    }

    [Fact]
    public void Renders_InputDottedPath()
    {
        var rule = MakeRule("category={input.category}");
        var section = MakeSection("anything", category: "payment_terms");

        var result = RemediationRenderer.Render(rule.Remediation, rule, section);

        result.Should().Be("category=payment_terms");
    }

    [Fact]
    public void UnresolvedPlaceholder_IsKeptVerbatim()
    {
        var rule = MakeRule("known={rule.id} unknown={rule.unknown} stillKnown={meta.maxDays}");
        var section = MakeSection("any");

        var result = RemediationRenderer.Render(rule.Remediation, rule, section);

        result.Should().Be("known=PAY-001 unknown={rule.unknown} stillKnown=30");
    }

    [Fact]
    public void NullOrEmptyTemplate_RendersNull()
    {
        var rule = MakeRule(null);
        RemediationRenderer.Render(null, rule, null).Should().BeNull();
        RemediationRenderer.Render(string.Empty, rule, null).Should().BeNull();
    }

    [Fact]
    public void Render_IsDeterministic_AcrossCalls()
    {
        var rule = MakeRule("{rule.id}/{section.heading}/{meta.maxDays}");
        var section = MakeSection("text");
        var first = RemediationRenderer.Render(rule.Remediation, rule, section);
        var second = RemediationRenderer.Render(rule.Remediation, rule, section);
        first.Should().Be(second);
    }

    [Fact]
    public void FormatNumber_UsesInvariantCulture()
    {
        RemediationRenderer.FormatNumber(1234.5).Should().Be("1234.5");
        RemediationRenderer.FormatNumber(0.1).Should().Be("0.1");
    }
}
