using System.Text.Json.Nodes;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;

namespace LambdaRag.Authoring;

/// <summary>
/// Deterministic, pattern-based authoring agent used for tests and as a
/// reference implementation. Does NOT call an LLM. Instead it scans the
/// chunk for recognisable contract clauses (payment, governing law, data
/// protection) and emits a hand-crafted Rule with a compiled predicate,
/// lambda, and remediation template.
///
/// Production deployments should swap this with an LLM-backed implementation
/// behind the same <see cref="IRuleAuthoringAgent"/> interface — keeping
/// the runtime evaluation path identical.
/// </summary>
public sealed class DeterministicMockAuthoringAgent : IRuleAuthoringAgent
{
    private readonly IRuleEmbedder _embedder;

    public DeterministicMockAuthoringAgent(IRuleEmbedder? embedder = null)
    {
        _embedder = embedder ?? new DeterministicHashEmbedder();
    }

    public async Task<IReadOnlyList<RuleAuthoringSuggestion>> AuthorAsync(
        RuleAuthoringRequest request,
        CancellationToken ct = default)
    {
        var lowered = (request.SourceContent ?? string.Empty).ToLowerInvariant();
        var suggestions = new List<RuleAuthoringSuggestion>();

        if (ContainsAny(lowered, "payment", "invoice", "net 30", "net 45", "net 60"))
        {
            suggestions.Add(await BuildPaymentRule(request).ConfigureAwait(false));
        }
        if (ContainsAny(lowered, "governing law", "jurisdiction", "venue"))
        {
            suggestions.Add(await BuildGoverningLawRule(request).ConfigureAwait(false));
        }
        if (ContainsAny(lowered, "data protection", "privacy", "personal data", "gdpr"))
        {
            suggestions.Add(await BuildDataProtectionRule(request).ConfigureAwait(false));
        }
        if (ContainsAny(lowered, "confidential", "non-disclosure", "nda"))
        {
            suggestions.Add(await BuildConfidentialityRule(request).ConfigureAwait(false));
        }
        if (ContainsAny(lowered, "limitation of liability", "liability cap", "limit of liability"))
        {
            suggestions.Add(await BuildLiabilityCapRule(request).ConfigureAwait(false));
        }
        if (ContainsAny(lowered, "warrant", "warranty"))
        {
            suggestions.Add(await BuildWarrantyRule(request).ConfigureAwait(false));
        }
        if (ContainsAny(lowered, "termination", "terminate"))
        {
            suggestions.Add(await BuildTerminationRule(request).ConfigureAwait(false));
        }
        if (ContainsAny(lowered, "intellectual property", "work product", "ownership of"))
        {
            suggestions.Add(await BuildIpOwnershipRule(request).ConfigureAwait(false));
        }

        // Stable order — sort by Id so consumers can rely on deterministic output.
        return suggestions
            .OrderBy(s => s.Rule.Id, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<RuleAuthoringSuggestion> BuildPaymentRule(RuleAuthoringRequest req)
    {
        var rule = new Rule(
            Id: $"{req.RuleIdPrefix}PAY-001",
            Version: "1.0.0",
            NaturalLanguage: "Payment terms must be 30 days or fewer.",
            Lambda: "input1.text.Contains(\"30 days\") || input1.text.Contains(\"15 days\") || input1.text.Contains(\"net 30\") || input1.text.Contains(\"Net 30\")",
            AppliesToSchema: SectionTextSchema(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Violation,
            SourceSpan: req.SourceSpan,
            EvidenceQuote: "Payment terms",
            Metadata: new Dictionary<string, string>
            {
                ["maxDays"] = "30",
            })
        {
            Predicate = "input1.category == \"payment_terms\"",
            Remediation = "Replace the {section.heading} clause with: \"Customer shall pay all undisputed invoices within {meta.maxDays} days of the invoice date.\"",
            SourceContent = req.SourceContent,
            SourceEmbedding = await _embedder.EmbedAsync(req.SourceContent).ConfigureAwait(false),
        };
        return new RuleAuthoringSuggestion(
            rule,
            Confidence: 0.92,
            Rationale: "Chunk mentions payment / invoicing terms; emitting Net-30 rule with remediation template.");
    }

    private async Task<RuleAuthoringSuggestion> BuildGoverningLawRule(RuleAuthoringRequest req)
    {
        var rule = new Rule(
            Id: $"{req.RuleIdPrefix}GOV-001",
            Version: "1.0.0",
            NaturalLanguage: "Governing law must be a U.S. jurisdiction.",
            Lambda: "input1.text.Contains(\"United States\") || input1.text.Contains(\"Delaware\") || input1.text.Contains(\"New York\") || input1.text.Contains(\"California\")",
            AppliesToSchema: SectionTextSchema(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Deviation,
            SourceSpan: req.SourceSpan,
            EvidenceQuote: "governing law",
            Metadata: new Dictionary<string, string>
            {
                ["requiredJurisdiction"] = "Delaware, USA",
            })
        {
            Predicate = "input1.category == \"governing_law\"",
            Remediation = "Replace the {section.heading} clause with: \"This Agreement is governed by the laws of the State of {meta.requiredJurisdiction}.\"",
            SourceContent = req.SourceContent,
            SourceEmbedding = await _embedder.EmbedAsync(req.SourceContent).ConfigureAwait(false),
        };
        return new RuleAuthoringSuggestion(
            rule,
            Confidence: 0.88,
            Rationale: "Chunk mentions governing law / jurisdiction; emitting US-jurisdiction rule.");
    }

    private async Task<RuleAuthoringSuggestion> BuildDataProtectionRule(RuleAuthoringRequest req)
    {
        var rule = new Rule(
            Id: $"{req.RuleIdPrefix}DPA-001",
            Version: "1.0.0",
            NaturalLanguage: "Data protection clause must reference an industry security standard.",
            Lambda: "input1.text.Contains(\"ISO 27001\") || input1.text.Contains(\"SOC 2\") || input1.text.Contains(\"NIST\")",
            AppliesToSchema: SectionTextSchema(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Violation,
            SourceSpan: req.SourceSpan,
            EvidenceQuote: "data protection",
            Metadata: new Dictionary<string, string>
            {
                ["requiredStandard"] = "ISO 27001 or SOC 2",
            })
        {
            Predicate = "input1.category == \"privacy\"",
            Remediation = "Add to the {section.heading} clause: \"Provider shall maintain security controls aligned with {meta.requiredStandard}.\"",
            SourceContent = req.SourceContent,
            SourceEmbedding = await _embedder.EmbedAsync(req.SourceContent).ConfigureAwait(false),
        };
        return new RuleAuthoringSuggestion(
            rule,
            Confidence: 0.85,
            Rationale: "Chunk mentions data protection / privacy; emitting industry-standard rule.");
    }

    private async Task<RuleAuthoringSuggestion> BuildConfidentialityRule(RuleAuthoringRequest req)
    {
        var rule = new Rule(
            Id: $"{req.RuleIdPrefix}CONF-001",
            Version: "1.0.0",
            NaturalLanguage: "Confidentiality clause must define a survival period or be perpetual.",
            Lambda: "input1.text.Contains(\"year\") || input1.text.Contains(\"years\") || input1.text.Contains(\"perpetual\") || input1.text.Contains(\"survive\")",
            AppliesToSchema: SectionTextSchema(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Violation,
            SourceSpan: req.SourceSpan,
            EvidenceQuote: "Confidentiality survival",
            Metadata: new Dictionary<string, string> { ["minYears"] = "5" })
        {
            Predicate = "input1.category == \"confidentiality\"",
            Remediation = "Add an explicit survival period to the {section.heading} clause: \"obligations survive for {meta.minYears} years from termination.\"",
            SourceContent = req.SourceContent,
            SourceEmbedding = await _embedder.EmbedAsync(req.SourceContent).ConfigureAwait(false),
        };
        return new RuleAuthoringSuggestion(rule, 0.86, "Chunk references confidentiality / NDA; emitting survival-period rule.");
    }

    private async Task<RuleAuthoringSuggestion> BuildLiabilityCapRule(RuleAuthoringRequest req)
    {
        var rule = new Rule(
            Id: $"{req.RuleIdPrefix}LIAB-001",
            Version: "1.0.0",
            NaturalLanguage: "Limitation of liability must reference an explicit cap (dollar amount or fee multiplier).",
            Lambda: "input1.text.Contains(\"$\") || input1.text.Contains(\"fees paid\") || input1.text.Contains(\"amount paid\") || input1.text.Contains(\"twelve months\") || input1.text.Contains(\"12 months\")",
            AppliesToSchema: SectionTextSchema(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Critical,
            SourceSpan: req.SourceSpan,
            EvidenceQuote: "Limitation of liability",
            Metadata: new Dictionary<string, string> { ["capWindow"] = "12 months" })
        {
            Predicate = "input1.category == \"liability\"",
            Remediation = "Add an explicit cap to the {section.heading} clause: \"total liability shall not exceed the fees paid in the {meta.capWindow} preceding the claim.\"",
            SourceContent = req.SourceContent,
            SourceEmbedding = await _embedder.EmbedAsync(req.SourceContent).ConfigureAwait(false),
        };
        return new RuleAuthoringSuggestion(rule, 0.90, "Chunk references limitation of liability; emitting cap rule.");
    }

    private async Task<RuleAuthoringSuggestion> BuildWarrantyRule(RuleAuthoringRequest req)
    {
        var rule = new Rule(
            Id: $"{req.RuleIdPrefix}WAR-001",
            Version: "1.0.0",
            NaturalLanguage: "Warranties must include a defined remedy or cure period.",
            Lambda: "input1.text.Contains(\"days\") || input1.text.Contains(\"remedy\") || input1.text.Contains(\"replace\") || input1.text.Contains(\"refund\")",
            AppliesToSchema: SectionTextSchema(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Deviation,
            SourceSpan: req.SourceSpan,
            EvidenceQuote: "Warranty remedy",
            Metadata: new Dictionary<string, string> { ["cureDays"] = "30" })
        {
            Predicate = "input1.category == \"warranty\"",
            Remediation = "Specify a cure window in the {section.heading} clause: \"Provider shall correct non-conforming Services within {meta.cureDays} days.\"",
            SourceContent = req.SourceContent,
            SourceEmbedding = await _embedder.EmbedAsync(req.SourceContent).ConfigureAwait(false),
        };
        return new RuleAuthoringSuggestion(rule, 0.84, "Chunk references warranty / warranties; emitting remedy rule.");
    }

    private async Task<RuleAuthoringSuggestion> BuildTerminationRule(RuleAuthoringRequest req)
    {
        var rule = new Rule(
            Id: $"{req.RuleIdPrefix}TRM-001",
            Version: "1.0.0",
            NaturalLanguage: "Termination clause must specify a written-notice period.",
            Lambda: "input1.text.Contains(\"30 days\") || input1.text.Contains(\"60 days\") || input1.text.Contains(\"90 days\") || input1.text.Contains(\"30 calendar days\") || input1.text.Contains(\"60 calendar days\") || input1.text.Contains(\"90 calendar days\")",
            AppliesToSchema: SectionTextSchema(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Violation,
            SourceSpan: req.SourceSpan,
            EvidenceQuote: "Termination notice",
            Metadata: new Dictionary<string, string> { ["minNoticeDays"] = "60" })
        {
            Predicate = "input1.category == \"termination\"",
            Remediation = "Update the {section.heading} clause to require at least {meta.minNoticeDays} calendar days prior written notice to terminate for convenience.",
            SourceContent = req.SourceContent,
            SourceEmbedding = await _embedder.EmbedAsync(req.SourceContent).ConfigureAwait(false),
        };
        return new RuleAuthoringSuggestion(rule, 0.87, "Chunk references termination; emitting notice-period rule.");
    }

    private async Task<RuleAuthoringSuggestion> BuildIpOwnershipRule(RuleAuthoringRequest req)
    {
        var rule = new Rule(
            Id: $"{req.RuleIdPrefix}IP-001",
            Version: "1.0.0",
            NaturalLanguage: "IP ownership clause must clearly assign work product or grant a perpetual license.",
            Lambda: "input1.text.Contains(\"work for hire\") || input1.text.Contains(\"assign\") || input1.text.Contains(\"perpetual\") || input1.text.Contains(\"irrevocable\") || input1.text.Contains(\"license\")",
            AppliesToSchema: SectionTextSchema(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Violation,
            SourceSpan: req.SourceSpan,
            EvidenceQuote: "IP ownership",
            Metadata: new Dictionary<string, string> { ["preferredModel"] = "work-for-hire" })
        {
            Predicate = "input1.category == \"ip_ownership\"",
            Remediation = "Clarify the {section.heading} clause: prefer a {meta.preferredModel} assignment of all deliverables, or a perpetual, irrevocable, royalty-free license.",
            SourceContent = req.SourceContent,
            SourceEmbedding = await _embedder.EmbedAsync(req.SourceContent).ConfigureAwait(false),
        };
        return new RuleAuthoringSuggestion(rule, 0.83, "Chunk references intellectual property / ownership; emitting assignment rule.");
    }

    private static JsonObject SectionTextSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["text"] = new JsonObject { ["type"] = "string" },
            ["category"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("text", "category"),
    };

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var n in needles)
        {
            if (haystack.Contains(n, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
