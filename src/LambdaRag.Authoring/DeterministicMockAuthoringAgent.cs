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

        // ---- Cloud architecture review patterns (FSI / regulated industries) ----
        if (ContainsAny(lowered, "encryption at rest", "encrypted at rest", "data at rest"))
        {
            suggestions.Add(await BuildArchRule(
                request, "ENCRYPT-REST-001", "encryption_at_rest",
                "Data at rest must be encrypted using customer-managed keys (CMK) or platform-managed keys with documented rotation.",
                "input1.text.Contains(\"AES-256\") || input1.text.ToLower().Contains(\"customer-managed\") || input1.text.ToLower().Contains(\"platform-managed\") || input1.text.Contains(\"TDE\") || input1.text.ToLower().Contains(\"cmk\")",
                "Specify the encryption algorithm and key management model (e.g., \"AES-256 with customer-managed keys in Azure Key Vault\")."
            ).ConfigureAwait(false));
        }
        if (ContainsAny(lowered, "encryption in transit", "tls", "https", "in-transit", "transport layer"))
        {
            suggestions.Add(await BuildArchRule(
                request, "ENCRYPT-TRANSIT-001", "encryption_in_transit",
                "Data in transit must be protected by TLS 1.2 or higher.",
                "input1.text.Contains(\"TLS 1.2\") || input1.text.Contains(\"TLS 1.3\") || input1.text.Contains(\"TLS1.2\") || input1.text.Contains(\"TLS1.3\")",
                "State the minimum TLS version explicitly (TLS 1.2 or TLS 1.3)."
            ).ConfigureAwait(false));
        }
        if (ContainsAny(lowered, "multi-factor", "multifactor", " mfa ", "two-factor", "2fa"))
        {
            suggestions.Add(await BuildArchRule(
                request, "IDENT-MFA-001", "security_iam",
                "Privileged and remote access must require phishing-resistant MFA.",
                "input1.text.ToLower().Contains(\"mfa\") || input1.text.ToLower().Contains(\"multi-factor\") || input1.text.ToLower().Contains(\"fido2\") || input1.text.ToLower().Contains(\"passkey\")",
                "Add a clause requiring phishing-resistant MFA (FIDO2 / passkeys) for privileged and remote access."
            ).ConfigureAwait(false));
        }
        if (ContainsAny(lowered, "private endpoint", "privatelink", "private link", "private network"))
        {
            suggestions.Add(await BuildArchRule(
                request, "NET-PRIVATE-001", "network_segmentation",
                "Public PaaS data planes must be reached via Private Endpoint / PrivateLink (no public network access).",
                "input1.text.ToLower().Contains(\"private endpoint\") || input1.text.ToLower().Contains(\"privatelink\") || input1.text.ToLower().Contains(\"private link\")",
                "Replace public-internet access with a Private Endpoint and disable the public network access flag on the resource."
            ).ConfigureAwait(false));
        }
        if (ContainsAny(lowered, "data residency", "residency", "in-country", "in-region"))
        {
            suggestions.Add(await BuildArchRule(
                request, "DATA-RES-001", "data_residency",
                "Customer data must remain in the contracted residency region.",
                "input1.text.ToLower().Contains(\"canada\") || input1.text.ToLower().Contains(\"residency\") || input1.text.Contains(\"region\")",
                "Pin the workload region(s) explicitly (e.g., Canada Central + Canada East) and disable cross-region replication for protected data."
            ).ConfigureAwait(false));
        }
        if (ContainsAny(lowered, "audit log", "logging", "sentinel", "log analytics", "siem"))
        {
            suggestions.Add(await BuildArchRule(
                request, "AUDIT-LOG-001", "audit_logging",
                "Control-plane and data-plane audit logs must be forwarded to a tamper-resistant SIEM with the documented retention.",
                "input1.text.ToLower().Contains(\"sentinel\") || input1.text.ToLower().Contains(\"log analytics\") || input1.text.ToLower().Contains(\"siem\") || input1.text.ToLower().Contains(\"audit log\")",
                "Forward control-plane and data-plane audit logs to Microsoft Sentinel (or equivalent SIEM) with a defined retention policy."
            ).ConfigureAwait(false));
        }
        if (ContainsAny(lowered, "backup", "restore", "recovery", "rpo", "rto"))
        {
            suggestions.Add(await BuildArchRule(
                request, "DR-BACKUP-001", "backup_restore",
                "Backup, restore and DR must have stated RPO and RTO.",
                "input1.text.ToLower().Contains(\"rpo\") || input1.text.ToLower().Contains(\"rto\") || input1.text.ToLower().Contains(\"recovery point\") || input1.text.ToLower().Contains(\"recovery time\")",
                "State explicit RPO and RTO targets and the test cadence for restore drills."
            ).ConfigureAwait(false));
        }
        if (ContainsAny(lowered, "infrastructure as code", "infra as code", "iac", "terraform", "bicep", "arm template"))
        {
            suggestions.Add(await BuildArchRule(
                request, "IAC-001", "infra_as_code",
                "Infrastructure must be deployed via Infrastructure-as-Code with policy-as-code guardrails.",
                "input1.text.ToLower().Contains(\"terraform\") || input1.text.ToLower().Contains(\"bicep\") || input1.text.ToLower().Contains(\"arm template\") || input1.text.ToLower().Contains(\"infrastructure as code\")",
                "Adopt Terraform or Bicep with PR-gated CI and Azure Policy / OPA as policy-as-code."
            ).ConfigureAwait(false));
        }
        if (ContainsAny(lowered, "shared responsibility", "shared-responsibility"))
        {
            suggestions.Add(await BuildArchRule(
                request, "SHARED-RESP-001", "compliance_posture",
                "Solution design must explicitly map controls to the Shared Responsibility Model.",
                "input1.text.ToLower().Contains(\"shared responsibility\") || input1.text.ToLower().Contains(\"customer responsibility\") || input1.text.ToLower().Contains(\"csp responsibility\")",
                "Add a Shared Responsibility matrix mapping each control to Customer / CSP / Joint."
            ).ConfigureAwait(false));
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
            Applicability = InferApplicability(req.SourceContent),
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
            Applicability = InferApplicability(req.SourceContent),
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
            Applicability = InferApplicability(req.SourceContent),
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
            Applicability = InferApplicability(req.SourceContent),
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
            Applicability = InferApplicability(req.SourceContent),
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
            Applicability = InferApplicability(req.SourceContent),
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
            Applicability = InferApplicability(req.SourceContent),
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
            Applicability = InferApplicability(req.SourceContent),
            Remediation = "Clarify the {section.heading} clause: prefer a {meta.preferredModel} assignment of all deliverables, or a perpetual, irrevocable, royalty-free license.",
            SourceContent = req.SourceContent,
            SourceEmbedding = await _embedder.EmbedAsync(req.SourceContent).ConfigureAwait(false),
        };
        return new RuleAuthoringSuggestion(rule, 0.83, "Chunk references intellectual property / ownership; emitting assignment rule.");
    }

    private async Task<RuleAuthoringSuggestion> BuildArchRule(
        RuleAuthoringRequest req,
        string idSuffix,
        string category,
        string naturalLanguage,
        string lambda,
        string remediation)
    {
        var rule = new Rule(
            Id: $"{req.RuleIdPrefix}{idSuffix}",
            Version: "1.0.0",
            NaturalLanguage: naturalLanguage,
            Lambda: lambda,
            AppliesToSchema: SectionTextSchema(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: RuleSeverity.Violation,
            SourceSpan: req.SourceSpan,
            EvidenceQuote: category,
            Metadata: new Dictionary<string, string> { ["topic"] = category })
        {
            Predicate = $"input1.category == \"{category}\"",
            Applicability = InferApplicability(req.SourceContent),
            Remediation = remediation,
            SourceContent = req.SourceContent,
            SourceEmbedding = await _embedder.EmbedAsync(req.SourceContent).ConfigureAwait(false),
        };
        return new RuleAuthoringSuggestion(rule, 0.85, $"Chunk matches architecture pattern '{category}'.");
    }

    /// <summary>
    /// Deterministically infer rule applicability from the policy text.
    /// "must / shall / required / mandatory / will" → Mandatory.
    /// "should / recommended / preferred / encouraged" → Optional.
    /// "if / when / where / unless / conditional / applicable" → Conditional.
    /// Default is Mandatory (compliance-safe: when in doubt, treat absence
    /// as a gap rather than silently passing).
    /// </summary>
    internal static RuleApplicability InferApplicability(string sourceContent)
    {
        if (string.IsNullOrWhiteSpace(sourceContent))
            return RuleApplicability.Mandatory;

        var lowered = sourceContent.ToLowerInvariant();
        var hasMust = ContainsAny(lowered,
            "must ", "shall ", " required", "mandatory", "will ");
        var hasShould = ContainsAny(lowered,
            "should ", "recommended", "preferred", "encouraged", "may ");
        var hasConditional = ContainsAny(lowered,
            " if ", " when ", " where ", "unless ", "conditional", "applicable to", "where applicable");

        if (hasMust) return RuleApplicability.Mandatory;
        if (hasConditional) return RuleApplicability.Conditional;
        if (hasShould) return RuleApplicability.Optional;
        return RuleApplicability.Mandatory;
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
