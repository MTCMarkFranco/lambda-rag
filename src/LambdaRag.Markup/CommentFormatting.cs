using System.Text.RegularExpressions;
using LambdaRag.Core.Domain;

namespace LambdaRag.Markup;

/// <summary>
/// Deterministic helpers that turn a Rule + Verdict into the visible
/// surface of a tracked-change comment: author label and body banner.
///
/// Visual parity with the Contoso agent-based reviewer (🕵 prefix,
/// severity emoji, <c>[Policy Reference: …]</c> footer) is intentional
/// product UX. Everything here is pure-code and deterministic — no LLM
/// in the runtime path.
/// </summary>
public static class CommentFormatting
{
    /// <summary>
    /// Detective emoji + " - " — matches Contoso's tracked-changes
    /// author prefix so reviewers familiar with the agent-based output
    /// see the same visual signal in lambda-rag's redlines.
    /// </summary>
    public const string AuthorEmojiPrefix = "\U0001F575 - ";

    /// <summary>
    /// Generic fallback used when a rule's category cannot be resolved.
    /// Kept stable so author strings remain reproducible.
    /// </summary>
    public const string GenericLabel = "Compliance";

    private static readonly Regex CategoryEqualsLiteral = new(
        @"input1\.category\s*==\s*""(?<cat>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Resolve the human-readable guidance label for a rule.
    ///
    /// Order of resolution (first match wins):
    ///   1. <c>rule.Metadata["categoryLabel"]</c> — set explicitly at
    ///      authoring time.
    ///   2. <c>rule.Metadata["category"]</c> mapped via
    ///      <see cref="DomainForCategory"/>.
    ///   3. The first <c>input1.category == "X"</c> literal in the rule's
    ///      Predicate, mapped via <see cref="DomainForCategory"/>.
    ///   4. <see cref="GenericLabel"/>.
    ///
    /// All steps are pure code so the resolved label is byte-stable
    /// across runs.
    /// </summary>
    public static string ResolveCategoryLabel(Rule rule)
    {
        if (rule.Metadata.TryGetValue("categoryLabel", out var explicitLabel)
            && !string.IsNullOrWhiteSpace(explicitLabel))
        {
            return explicitLabel;
        }

        if (rule.Metadata.TryGetValue("category", out var metaCat)
            && !string.IsNullOrWhiteSpace(metaCat))
        {
            return DomainForCategory(metaCat);
        }

        var match = CategoryEqualsLiteral.Match(rule.Predicate ?? string.Empty);
        if (match.Success)
        {
            return DomainForCategory(match.Groups["cat"].Value);
        }

        return GenericLabel;
    }

    /// <summary>
    /// Build the OOXML <c>w:author</c> attribute. Format mirrors the
    /// Contoso reviewer: <c>"🕵 - {Label} guidance"</c>.
    /// </summary>
    public static string BuildAuthor(Rule rule)
        => AuthorEmojiPrefix + ResolveCategoryLabel(rule) + " guidance";

    /// <summary>
    /// Two-letter author initials shown in Word's review pane. Derived
    /// from the resolved label (e.g. <c>"Legal" → "LE"</c>) so the side
    /// panel aligns with the comment author and stays stable per rule.
    /// </summary>
    public static string BuildInitials(Rule rule)
    {
        var label = ResolveCategoryLabel(rule);
        if (string.IsNullOrWhiteSpace(label)) return "LR";
        var letters = label
            .Where(char.IsLetter)
            .Select(char.ToUpperInvariant)
            .ToArray();
        if (letters.Length == 0) return "LR";
        if (letters.Length == 1) return new string(letters[0], 2);
        return new string(new[] { letters[0], letters[1] });
    }

    /// <summary>
    /// Severity → banner line shown at the top of every comment body.
    /// Matches Contoso's _severity_banner emoji set so reviewers see
    /// the same visual escalation cue.
    /// </summary>
    public static string SeverityBanner(RuleSeverity severity) => severity switch
    {
        RuleSeverity.Critical   => "\U0001F6A8 CRITICAL — Must Fix Before Signing",
        RuleSeverity.Violation  => "\u26A0\uFE0F MAJOR — Policy Violation",
        RuleSeverity.Deviation  => "\u270F\uFE0F MODERATE — Needs Revision",
        RuleSeverity.Suggestion => "\U0001F4A1 SUGGESTION — Best Practice",
        _                       => "\u2753 UNKNOWN SEVERITY",
    };

    /// <summary>
    /// Banner used for the rare <c>VerdictOutcome.Error</c> case where
    /// the predicate or lambda threw at evaluation time. Distinct from
    /// the rule severities so reviewers immediately see this is a tool
    /// problem rather than a compliance call.
    /// </summary>
    public const string ErrorBanner = "\U0001F6D1 ERROR — Rule Could Not Be Evaluated";

    /// <summary>
    /// Build the visible comment body for a Fail / Error verdict.
    ///
    /// Layout (newline-separated):
    /// <code>
    /// 🚨 CRITICAL — Must Fix Before Signing
    /// {synopsis}                          (when rule.Metadata["synopsis"] is set)
    ///
    /// {naturalLanguage}
    ///
    /// Suggested remediation: {remediationText}    (Fail only, when present)
    ///
    /// Detail: {errorMessage}                       (Error only)
    ///
    /// [Policy Reference: {ruleId} v{version}]
    /// </code>
    /// </summary>
    public static string BuildBody(Rule rule, Verdict verdict)
    {
        var banner = verdict.Outcome == VerdictOutcome.Error
            ? ErrorBanner
            : SeverityBanner(rule.Severity);

        var sb = new System.Text.StringBuilder();
        sb.Append(banner);

        if (rule.Metadata.TryGetValue("synopsis", out var synopsis)
            && !string.IsNullOrWhiteSpace(synopsis))
        {
            sb.Append('\n').Append(synopsis);
        }

        if (!string.IsNullOrWhiteSpace(rule.NaturalLanguage))
        {
            sb.Append("\n\n").Append(rule.NaturalLanguage);
        }

        if (verdict.Outcome == VerdictOutcome.Fail
            && !string.IsNullOrWhiteSpace(verdict.RemediationText))
        {
            sb.Append("\n\nSuggested remediation: ").Append(verdict.RemediationText);
        }

        if (verdict.Outcome == VerdictOutcome.Error
            && !string.IsNullOrWhiteSpace(verdict.ErrorMessage))
        {
            sb.Append("\n\nDetail: ").Append(verdict.ErrorMessage);
        }

        sb.Append("\n\n[Policy Reference: ").Append(rule.Id);
        if (!string.IsNullOrWhiteSpace(rule.Version))
        {
            sb.Append(' ').Append('v').Append(rule.Version);
        }
        sb.Append(']');

        return sb.ToString();
    }

    /// <summary>
    /// Build the comment body for an opt-in Pass verdict (markup mode's
    /// <c>--annotate-pass</c>). Same structure as Fail/Error, but with a
    /// ✓ Passed banner and no remediation.
    /// </summary>
    public static string BuildPassBody(Rule? rule, Verdict verdict, string fallbackStatement)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("\u2713 Passed");

        if (rule is not null)
        {
            if (rule.Metadata.TryGetValue("synopsis", out var synopsis)
                && !string.IsNullOrWhiteSpace(synopsis))
            {
                sb.Append('\n').Append(synopsis);
            }
            if (!string.IsNullOrWhiteSpace(rule.NaturalLanguage))
            {
                sb.Append("\n\n").Append(rule.NaturalLanguage);
            }
            sb.Append("\n\n[Policy Reference: ").Append(rule.Id);
            if (!string.IsNullOrWhiteSpace(rule.Version))
            {
                sb.Append(' ').Append('v').Append(rule.Version);
            }
            sb.Append(']');
        }
        else
        {
            sb.Append(": ").Append(fallbackStatement);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Map a topic-map / predicate category id (e.g. <c>"payment_terms"</c>,
    /// <c>"governing_law"</c>, <c>"privacy"</c>) to a human-readable
    /// guidance domain (<c>"Finance"</c>, <c>"Legal"</c>, <c>"Privacy"</c>).
    ///
    /// Coverage targets the bundled <c>contract.v1</c>, <c>fsi.v1</c>,
    /// <c>oil-gas.v1</c>, <c>architecture-review.v1</c>,
    /// <c>gov-architecture.v1</c>, <c>permitting.v1</c>, and
    /// <c>business-review.v1</c> topic maps.
    ///
    /// Categories outside the table fall back to a Title-cased version of
    /// the raw category id (e.g. <c>"foo_bar" → "Foo bar"</c>) so new
    /// topic maps still produce a sensible label without a code change.
    /// </summary>
    public static string DomainForCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return GenericLabel;

        var key = category.Trim().ToLowerInvariant();
        if (CategoryToDomain.TryGetValue(key, out var domain)) return domain;
        return TitleCaseFallback(key);
    }

    private static string TitleCaseFallback(string category)
    {
        var parts = category.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return GenericLabel;
        var first = parts[0];
        var rest = parts.Length > 1 ? " " + string.Join(' ', parts.Skip(1)) : string.Empty;
        return char.ToUpperInvariant(first[0]) + first[1..] + rest;
    }

    private static readonly IReadOnlyDictionary<string, string> CategoryToDomain =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // contract.v1
            ["payment_terms"]    = "Finance",
            ["term"]             = "Legal",
            ["termination"]      = "Legal",
            ["governing_law"]    = "Legal",
            ["warranty"]         = "Legal",
            ["confidentiality"]  = "Privacy",
            ["indemnification"]  = "Legal",
            ["liability"]        = "Legal",
            ["privacy"]          = "Privacy",
            ["security"]         = "Security",
            ["service_levels"]   = "Operations",
            ["ip_ownership"]     = "Legal",
            ["audit"]            = "Compliance",
            ["insurance"]        = "Insurance",
            ["support"]          = "Operations",
            ["force_majeure"]    = "Legal",
            ["assignment"]       = "Legal",
            ["notices"]          = "Legal",
            ["definitions"]      = "Legal",
            ["miscellaneous"]    = "General",
            ["parties"]          = "Legal",

            // fsi.v1 (financial services)
            ["aml"]              = "Compliance",
            ["kyc"]              = "Compliance",
            ["basel"]            = "Risk",
            ["capital_adequacy"] = "Risk",
            ["model_risk"]       = "Risk",
            ["sanctions"]        = "Compliance",
            ["consumer_protection"] = "Compliance",

            // oil-gas.v1
            ["hse"]              = "Health & Safety",
            ["well_integrity"]   = "Operations",
            ["asset_integrity"]  = "Operations",
            ["environmental"]    = "Environmental",
            ["methane"]          = "Environmental",

            // architecture-review.v1 / gov-architecture.v1
            ["network"]          = "Architecture",
            ["compute"]          = "Architecture",
            ["storage"]          = "Architecture",
            ["identity"]         = "Identity",
            ["observability"]    = "Architecture",
            ["compliance"]       = "Compliance",
            ["performance"]      = "Architecture",
            ["resiliency"]       = "Architecture",
            ["cost"]             = "Finance",

            // permitting.v1
            ["zoning"]           = "Land Use",
            ["accessibility"]    = "Accessibility",
            ["impact_assessment"] = "Environmental",
            ["indigenous_consultation"] = "Indigenous Consultation",
            ["building_code"]    = "Building Code",

            // business-review.v1
            ["scope"]            = "Business",
            ["pricing"]          = "Finance",
            ["governance"]       = "Governance",
        };
}
