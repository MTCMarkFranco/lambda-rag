using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Selectors;
using LambdaRag.Markup;
using Xunit;

namespace LambdaRag.UnitTests.Markup;

/// <summary>
/// Coverage for the deterministic comment-formatting layer that turns a
/// Rule + Verdict into the visible author label and body banner shown in
/// Word's review pane. Focus is on:
///
///   • category → human-domain mapping (matching Air Canada's UX)
///   • severity → emoji banner
///   • <c>FromReport</c> body composition (fail / error / synopsis / pol-ref)
///   • idempotency: identical inputs → identical author / text bytes
/// </summary>
public class CommentFormattingTests
{
    private static Rule MakeRule(
        string id,
        string predicate = "true",
        RuleSeverity severity = RuleSeverity.Violation,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? naturalLanguage = null) => new(
        Id: id,
        Version: "1.0.0",
        NaturalLanguage: naturalLanguage ?? $"{id} statement.",
        Lambda: "true",
        AppliesToSchema: new JsonObject(),
        Selector: new PathSelector("$.sections[*]"),
        Severity: severity,
        SourceSpan: new SourceSpan("doc", 0, 0, 1, null),
        EvidenceQuote: id,
        Metadata: metadata ?? new Dictionary<string, string>())
    {
        Predicate = predicate,
    };

    private static Verdict MakeVerdict(
        string ruleId,
        VerdictOutcome outcome = VerdictOutcome.Fail,
        string? remediation = null,
        string? error = null) => new Verdict(
        Id: ContentHash.Compose("verdict", ruleId, outcome.ToString()).Value,
        RuleId: ruleId,
        RuleSetVersion: "1.0.0",
        Outcome: outcome,
        LambdaText: "true",
        EvaluatedInput: new JsonObject(),
        SourceSpan: new SourceSpan("doc", 0, 50, 1, null),
        ErrorMessage: error,
        EvidenceQuotes: Array.Empty<string>(),
        EvaluatedAt: DateTimeOffset.UnixEpoch)
    { RemediationText = remediation };

    [Theory]
    [InlineData("payment_terms", "Finance")]
    [InlineData("governing_law", "Legal")]
    [InlineData("privacy", "Privacy")]
    [InlineData("security", "Security")]
    [InlineData("audit", "Compliance")]
    [InlineData("insurance", "Insurance")]
    [InlineData("hse", "Health & Safety")]
    public void DomainForCategory_maps_well_known_categories(string category, string expected)
    {
        CommentFormatting.DomainForCategory(category).Should().Be(expected);
    }

    [Fact]
    public void DomainForCategory_falls_back_to_title_case_for_unknown_categories()
    {
        // New topic-maps still produce a sensible label without a code change.
        CommentFormatting.DomainForCategory("foo_bar").Should().Be("Foo bar");
    }

    [Fact]
    public void ResolveCategoryLabel_uses_metadata_categoryLabel_when_present()
    {
        var rule = MakeRule(
            "X",
            predicate: "input1.category == \"payment_terms\"",
            metadata: new Dictionary<string, string>
            {
                ["categoryLabel"] = "Treasury",
            });
        // Explicit categoryLabel wins over predicate-derived category.
        CommentFormatting.ResolveCategoryLabel(rule).Should().Be("Treasury");
    }

    [Fact]
    public void ResolveCategoryLabel_uses_metadata_category_when_label_absent()
    {
        var rule = MakeRule(
            "X",
            predicate: "true",
            metadata: new Dictionary<string, string> { ["category"] = "privacy" });
        CommentFormatting.ResolveCategoryLabel(rule).Should().Be("Privacy");
    }

    [Fact]
    public void ResolveCategoryLabel_extracts_from_predicate_input1_category_literal()
    {
        var rule = MakeRule("X", predicate: "input1.category == \"governing_law\"");
        CommentFormatting.ResolveCategoryLabel(rule).Should().Be("Legal");
    }

    [Fact]
    public void ResolveCategoryLabel_falls_back_to_generic_when_nothing_matches()
    {
        var rule = MakeRule("X", predicate: "true");
        CommentFormatting.ResolveCategoryLabel(rule).Should().Be(CommentFormatting.GenericLabel);
    }

    [Fact]
    public void BuildAuthor_prefixes_emoji_and_appends_guidance()
    {
        var rule = MakeRule("X", predicate: "input1.category == \"privacy\"");
        CommentFormatting.BuildAuthor(rule).Should().Be("\U0001F575 - Privacy guidance");
    }

    [Theory]
    [InlineData(RuleSeverity.Critical, "\U0001F6A8 CRITICAL")]
    [InlineData(RuleSeverity.Violation, "\u26A0\uFE0F MAJOR")]
    [InlineData(RuleSeverity.Deviation, "\u270F\uFE0F MODERATE")]
    [InlineData(RuleSeverity.Suggestion, "\U0001F4A1 SUGGESTION")]
    public void SeverityBanner_uses_AC_aligned_emoji(RuleSeverity severity, string prefix)
    {
        CommentFormatting.SeverityBanner(severity).Should().StartWith(prefix);
    }

    [Fact]
    public void BuildBody_includes_banner_natural_language_remediation_and_policy_ref()
    {
        var rule = MakeRule(
            "PAY-001",
            predicate: "input1.category == \"payment_terms\"",
            severity: RuleSeverity.Critical,
            naturalLanguage: "Payment terms must be 30 days or fewer.");
        var verdict = MakeVerdict("PAY-001", VerdictOutcome.Fail,
            remediation: "Replace clause with 30-day terms.");

        var body = CommentFormatting.BuildBody(rule, verdict);

        body.Should().Contain("CRITICAL");
        body.Should().Contain("Payment terms must be 30 days or fewer.");
        body.Should().Contain("Suggested remediation: Replace clause with 30-day terms.");
        body.Should().Contain("[Policy Reference: PAY-001 v1.0.0]");
    }

    [Fact]
    public void BuildBody_includes_synopsis_when_metadata_carries_one()
    {
        var rule = MakeRule(
            "PAY-001",
            predicate: "input1.category == \"payment_terms\"",
            metadata: new Dictionary<string, string>
            {
                ["synopsis"] = "Verifies the payment-terms clause uses 30-day or shorter net terms.",
            });
        var body = CommentFormatting.BuildBody(rule, MakeVerdict("PAY-001"));

        body.Should().Contain("Verifies the payment-terms clause uses 30-day or shorter net terms.");
    }

    [Fact]
    public void BuildBody_uses_error_banner_for_error_outcomes()
    {
        var rule = MakeRule("X", severity: RuleSeverity.Violation);
        var verdict = MakeVerdict("X", VerdictOutcome.Error, error: "predicate threw");

        var body = CommentFormatting.BuildBody(rule, verdict);

        body.Should().StartWith(CommentFormatting.ErrorBanner);
        body.Should().Contain("Detail: predicate threw");
    }

    [Fact]
    public void BuildBody_omits_remediation_section_for_error_outcomes()
    {
        var rule = MakeRule("X", severity: RuleSeverity.Violation);
        var verdict = MakeVerdict(
            "X", VerdictOutcome.Error, remediation: "should not appear", error: "boom");

        var body = CommentFormatting.BuildBody(rule, verdict);

        body.Should().NotContain("should not appear");
    }

    [Fact]
    public void BuildAuthor_and_BuildBody_are_byte_stable_across_calls()
    {
        var rule = MakeRule(
            "PAY-001",
            predicate: "input1.category == \"payment_terms\"",
            severity: RuleSeverity.Critical,
            metadata: new Dictionary<string, string>
            {
                ["synopsis"] = "Verifies the clause uses ≤30-day net payment terms.",
            });
        var verdict = MakeVerdict("PAY-001", VerdictOutcome.Fail,
            remediation: "Replace with 30-day terms.");

        CommentFormatting.BuildAuthor(rule)
            .Should().Be(CommentFormatting.BuildAuthor(rule));
        CommentFormatting.BuildBody(rule, verdict)
            .Should().Be(CommentFormatting.BuildBody(rule, verdict));
    }
}

/// <summary>
/// Body-shape coverage for the live <see cref="AnnotationFactory.FromReport"/>
/// path — the place in the pipeline that decides what reviewers see.
/// </summary>
public class AnnotationFactoryFromReportBodyTests
{
    private static Rule MakeRule(string id, string predicate, RuleSeverity sev) => new(
        Id: id,
        Version: "1.0.0",
        NaturalLanguage: $"{id} requirement statement.",
        Lambda: "true",
        AppliesToSchema: new JsonObject(),
        Selector: new PathSelector("$.sections[*]"),
        Severity: sev,
        SourceSpan: new SourceSpan("doc", 0, 0, 1, null),
        EvidenceQuote: id,
        Metadata: new Dictionary<string, string>())
    { Predicate = predicate };

    private static Verdict MakeVerdict(string ruleId, VerdictOutcome outcome, string? remediation = null) => new Verdict(
        Id: ContentHash.Compose("verdict", ruleId, outcome.ToString()).Value,
        RuleId: ruleId,
        RuleSetVersion: "1.0.0",
        Outcome: outcome,
        LambdaText: "true",
        EvaluatedInput: new JsonObject(),
        SourceSpan: new SourceSpan("doc", 100, 50, 1, null),
        ErrorMessage: null,
        EvidenceQuotes: Array.Empty<string>(),
        EvaluatedAt: DateTimeOffset.UnixEpoch)
    { RemediationText = remediation };

    private static ComplianceReport MakeReport(IReadOnlyList<Verdict> verdicts) => new(
        DocumentId: ContentHash.OfString("doc"),
        RuleSetId: "rs",
        RuleSetVersion: "1.0.0",
        RuleSetFingerprint: ContentHash.OfString("fp"),
        ProjectorId: "contract",
        ProjectorVersion: "1.0.0",
        Score: 1.0,
        TotalRules: verdicts.Count,
        Passed: 0,
        Failed: verdicts.Count(v => v.Outcome == VerdictOutcome.Fail),
        NotApplicable: 0,
        Errored: verdicts.Count(v => v.Outcome == VerdictOutcome.Error),
        Verdicts: verdicts,
        GeneratedAt: DateTimeOffset.UnixEpoch);

    [Fact]
    public void Author_is_category_derived_for_each_known_category()
    {
        var rules = new Dictionary<string, Rule>
        {
            ["PAY-1"] = MakeRule("PAY-1", "input1.category == \"payment_terms\"", RuleSeverity.Critical),
            ["GOV-1"] = MakeRule("GOV-1", "input1.category == \"governing_law\"", RuleSeverity.Deviation),
            ["DPA-1"] = MakeRule("DPA-1", "input1.category == \"privacy\"", RuleSeverity.Violation),
            ["SEC-1"] = MakeRule("SEC-1", "input1.category == \"security\"", RuleSeverity.Violation),
        };
        var report = MakeReport(new[]
        {
            MakeVerdict("PAY-1", VerdictOutcome.Fail),
            MakeVerdict("GOV-1", VerdictOutcome.Fail),
            MakeVerdict("DPA-1", VerdictOutcome.Fail),
            MakeVerdict("SEC-1", VerdictOutcome.Fail),
        });

        var annots = AnnotationFactory.FromReport(report, rules).ToList();

        annots.Should().HaveCount(4);
        annots[0].Author.Should().Be("\U0001F575 - Finance guidance");
        annots[1].Author.Should().Be("\U0001F575 - Legal guidance");
        annots[2].Author.Should().Be("\U0001F575 - Privacy guidance");
        annots[3].Author.Should().Be("\U0001F575 - Security guidance");
    }

    [Fact]
    public void Body_carries_severity_banner_natural_language_and_policy_ref()
    {
        var rules = new Dictionary<string, Rule>
        {
            ["PAY-1"] = MakeRule("PAY-1", "input1.category == \"payment_terms\"", RuleSeverity.Critical),
        };
        var report = MakeReport(new[]
        {
            MakeVerdict("PAY-1", VerdictOutcome.Fail, remediation: "Use 30-day terms."),
        });

        var annot = AnnotationFactory.FromReport(report, rules).Single();

        annot.Text.Should().StartWith("\U0001F6A8 CRITICAL");
        annot.Text.Should().Contain("PAY-1 requirement statement.");
        annot.Text.Should().Contain("Suggested remediation: Use 30-day terms.");
        annot.Text.Should().EndWith("[Policy Reference: PAY-1 v1.0.0]");
    }

    [Fact]
    public void Orphan_verdict_uses_generic_fallback_author_and_error_banner()
    {
        var report = MakeReport(new[] { MakeVerdict("ORPHAN", VerdictOutcome.Fail) });

        var annot = AnnotationFactory.FromReport(report, new Dictionary<string, Rule>()).Single();

        annot.Author.Should().Be(AnnotationFactory.Author);
        annot.Text.Should().StartWith(CommentFormatting.ErrorBanner);
        annot.Text.Should().Contain("ORPHAN");
    }
}

public class OpenXmlMarkupServiceInitialsTests
{
    [Theory]
    [InlineData("\U0001F575 - Legal guidance", "LG")]
    [InlineData("\U0001F575 - Privacy guidance", "PG")]
    [InlineData("\U0001F575 - Health & Safety guidance", "HS")]
    [InlineData("\U0001F575 - Compliance guidance", "CG")]
    [InlineData("\U0001F575 - Compliance", "CC")]
    [InlineData("", "LR")]
    [InlineData("123", "LR")]
    public void ResolveInitials_takes_first_letter_of_first_two_tokens(string author, string expected)
    {
        OpenXmlMarkupService.ResolveInitials(author).Should().Be(expected);
    }
}
