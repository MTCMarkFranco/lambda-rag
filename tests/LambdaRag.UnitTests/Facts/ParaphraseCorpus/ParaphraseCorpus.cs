// Pillar 12 / Pillar 4 (Flexibility) — the offline paraphrase corpus.
// Each ConceptGroup names one fact concept, the value the extractor is
// expected to emit, and 10+ surface phrasings of that same concept drawn
// from policy language (not from any single reviewed document). The
// corpus is intentionally checked into source: the point of Flexibility
// is that adding a new phrasing to policy vocabulary should be a
// reviewable diff, not a silent regression.
//
// Two flavours of concept:
//   * Deterministic-normalizer concepts (Duration) — exercised by
//     ParaphraseNormalizerTests without any LLM. If a phrasing here
//     fails to canonicalize, the normalizer / regex / mapping table
//     needs an entry, not the test.
//   * Extraction-contract concepts (Boolean + Enum) — exercised by
//     ParaphraseExtractionContractTests with a RecordedFactExtractor.
//     These prove the sidecar-merge + rule-evaluation plumbing is
//     invariant across phrasings *given* a correct Pass-1 emission.
//
// NB: the paraphrase corpus deliberately does NOT lift language from
// tests/Goldens/corpus/**/source.md — that would be a Flexibility
// anti-pattern. Every phrasing is authored from policy vocabulary
// ("SHALL", "MUST", "at a minimum", …) so we test coverage, not memory.

using LambdaRag.Core.Domain;

namespace LambdaRag.UnitTests.Facts.ParaphraseCorpus;

/// <summary>One fact concept and its ≥10 policy-language phrasings.</summary>
public sealed record ConceptGroup(
    string ConceptName,
    FactType Type,
    object ExpectedValue,
    IReadOnlyList<string> Paraphrases)
{
    public IReadOnlyList<string>? EnumValues { get; init; }
}

public static class ParaphraseCorpusData
{
    // ── Deterministic-normalizer concepts ────────────────────────────────

    /// <summary>90-day rotation cadence expressed 12 different ways.</summary>
    public static readonly ConceptGroup KeyRotationDays = new(
        "key_rotation_days",
        FactType.Duration,
        90L,
        new[]
        {
            "every 90 days",
            "on a 90-day cycle",
            "on a 90 day cycle",
            "every ninety (90) days",
            "quarterly",
            "every quarter",
            "Every 90 Days",
            "Quarterly.",
            "quarterly ",
            "every 90-day",
            "on a 90-day rotation",
            "on a 90-day cycle.",
        });

    public static readonly ConceptGroup LoggingRetentionDays = new(
        "logging_retention_days",
        FactType.Duration,
        90L,
        new[]
        {
            "every 90 days",
            "on a 90-day cycle",
            "quarterly",
            "every quarter",
            "Every ninety (90) days",
            "on a 90 day cycle",
            "every 90-day",
            "quarterly.",
            "on a 90-day retention window",
            "every 90-day retention window",
            "every 90 days.",
        });

    /// <summary>7-day (weekly) cadence — exercises the regex fallback.</summary>
    public static readonly ConceptGroup WeeklyCadence = new(
        "weekly_cadence_days",
        FactType.Duration,
        7L,
        new[]
        {
            "every 7 days",
            "every 7-day",
            "on a 7-day cycle",
            "on a 7 day cycle",
            "Every 7 days",
            "every 7-day rotation",
            "on a 7-day rotation",
            "every 7 days.",
            "every 7-day window",
            "every 7-day cadence",
            "every 7-day interval",
        });

    /// <summary>Regex-fallback family: arbitrary N-day phrasings the
    /// mapping table need never enumerate.</summary>
    public static readonly ConceptGroup EveryNDaysFamily = new(
        "arbitrary_days",
        FactType.Duration,
        45L,
        new[]
        {
            "every 45 days",
            "on a 45-day cycle",
            "on a 45 day cycle",
            "Every 45 Days",
            "every 45-day",
            "every 45-day rotation",
            "on a 45-day rotation",
            "every 45 days.",
            "every 45 days,",
            "every 45-day window",
            "on a 45 day cycle.",
        });

    public static readonly ConceptGroup AnnualCadence = new(
        "audit_cadence_days",
        FactType.Duration,
        365L,
        new[]
        {
            "annually",
            "every year",
            "yearly",
            "every 365 days",
            "on a 365-day cycle",
            "Annually",
            "yearly.",
            "Every Year",
            "annually,",
            "on a 365 day cycle",
            "every 365-day",
        });

    /// <summary>30-day cadence — untagged principal disable window.</summary>
    public static readonly ConceptGroup UntaggedPrincipalDisableDays = new(
        "untagged_principal_disable_days",
        FactType.Duration,
        30L,
        new[]
        {
            "every 30 days",
            "on a 30-day cycle",
            "on a 30 day cycle",
            "monthly",
            "every month",
            "Every 30 Days",
            "monthly.",
            "on a 30-day window",
            "every 30-day",
            "every 30-day cadence",
            "on a 30-day rotation",
            "every 30 days.",
        });

    /// <summary>30-day cadence for orphan secret deletion. Shares the
    /// same numeric target as UntaggedPrincipalDisableDays but is a
    /// distinct policy concept — the paraphrase corpus keeps them
    /// separate so a normalizer regression on one concept surfaces as a
    /// concept-scoped test failure.</summary>
    public static readonly ConceptGroup OrphanSecretDeletionDays = new(
        "orphan_secret_deletion_days",
        FactType.Duration,
        30L,
        new[]
        {
            "every 30 days",
            "on a 30-day cycle",
            "monthly",
            "every month",
            "on a 30-day deletion window",
            "every 30-day cleanup cycle",
            "on a 30 day cycle",
            "Every 30 days",
            "on a 30-day retention window",
            "every 30-day cadence",
            "monthly.",
        });

    /// <summary>90-day console-action migration deadline (EA-IAC-015).</summary>
    public static readonly ConceptGroup ConsoleActionMigrationDays = new(
        "console_action_migration_days",
        FactType.Duration,
        90L,
        new[]
        {
            "every 90 days",
            "on a 90-day cycle",
            "quarterly",
            "every quarter",
            "on a 90-day migration window",
            "on a 90 day cycle",
            "Every 90 days",
            "every 90-day",
            "on a 90-day cadence",
            "every 90-day migration cycle",
            "quarterly.",
        });

    /// <summary>90-day cadence family for credential rotation. The rule
    /// EA-DATA-019 asserts <c>credential_rotation_days &gt; 0</c>, so
    /// any positive cadence satisfies it; we anchor on 90 days as the
    /// common industry baseline.</summary>
    public static readonly ConceptGroup CredentialRotationDays = new(
        "credential_rotation_days",
        FactType.Duration,
        90L,
        new[]
        {
            "every 90 days",
            "on a 90-day cycle",
            "quarterly",
            "every quarter",
            "on a 90-day rotation",
            "on a 90-day rotation window",
            "Every 90 days",
            "every 90-day",
            "on a 90 day cycle",
            "on a 90-day credential rotation",
            "quarterly.",
        });

    // ── Extraction-contract concepts (Boolean / Enum) ─────────────────────

    public static readonly ConceptGroup EncryptionDeclared = new(
        "encryption_declared",
        FactType.Boolean,
        true,
        new[]
        {
            "All data at rest is encrypted using AES-256.",
            "Encryption is applied to persisted data with envelope encryption (KEK/DEK).",
            "We use TLS in transit and AES-GCM at rest.",
            "Data shall be cryptographically protected at rest.",
            "The service uses cipher-based protection for persisted state.",
            "Sensitive fields are encrypted before persistence.",
            "Data at rest is protected via customer-managed keys and envelope encryption.",
            "All persisted PII is encrypted.",
            "Storage is encrypted end-to-end.",
            "The workload MUST encrypt all data at rest.",
            "Column-level encryption applies to all restricted columns.",
        });

    public static readonly ConceptGroup MfaRequired = new(
        "mfa_required",
        FactType.Boolean,
        true,
        new[]
        {
            "MFA is required for all privileged accounts.",
            "Two-factor authentication SHALL be enforced on administrative access.",
            "2FA is mandatory for production access.",
            "Multi-factor authentication is required for every human identity.",
            "Step-up authentication is required for privileged operations.",
            "Administrators MUST authenticate with a phishing-resistant second factor.",
            "FIDO2 tokens are required for privileged sign-in.",
            "Second-factor authentication is enforced by conditional access.",
            "Human access requires two-factor login.",
            "All engineers MUST use MFA to reach production.",
            "Access to production requires a hardware second factor.",
        });

    public static readonly ConceptGroup LoggingEnabled = new(
        "logging_enabled",
        FactType.Boolean,
        true,
        new[]
        {
            "Audit logging is enabled on all data-plane operations.",
            "All access to protected data SHALL be logged.",
            "Diagnostic logs are captured for every request.",
            "The service emits audit events for writes and reads.",
            "Activity SHALL be logged to a tamper-evident log store.",
            "All administrative actions are audited.",
            "We log every mutation to the audit pipeline.",
            "Access events are recorded in the SIEM.",
            "Security-relevant events are logged and forwarded.",
            "The system MUST retain access logs.",
            "Auditing is enabled on the storage account.",
        });

    public static readonly ConceptGroup BackupDeclared = new(
        "backup_declared",
        FactType.Boolean,
        true,
        new[]
        {
            "Backups are configured with geo-redundant storage.",
            "The database SHALL be backed up on a nightly cadence.",
            "Regular snapshots of persistent data are taken.",
            "Daily backups are taken and stored off-region.",
            "Point-in-time restore is enabled on all production databases.",
            "The workload maintains geo-redundant backups.",
            "Backup and recovery procedures are documented and tested.",
            "Snapshots are retained for 90 days.",
            "The system is protected by scheduled backups.",
            "Data is regularly copied to a secondary region for disaster recovery.",
            "Backup jobs run nightly.",
        });

    public static readonly ConceptGroup ResidencyBoundaryDeclared = new(
        "residency_boundary_declared",
        FactType.Boolean,
        true,
        new[]
        {
            "All data SHALL reside within Canadian datacentres.",
            "Data residency is pinned to the Canada Central region.",
            "Storage regions are restricted to EU member states.",
            "The workload enforces a strict data-residency boundary.",
            "Personal information MUST NOT leave the country of origin.",
            "Geo-fencing is applied to all persisted data.",
            "The service is deployed only in in-region tenants.",
            "Cross-border data flows are prohibited.",
            "All primary and secondary regions are within Canada.",
            "Data locality is enforced at the storage tier.",
            "Regional pinning is enforced for all persisted state.",
        });

    public static readonly ConceptGroup TlsMinVersion12 = new(
        "tls_min_version",
        FactType.Enum,
        "1.2",
        new[]
        {
            "TLS 1.2 minimum",
            "at least TLS 1.2",
            "TLS v1.2 or higher",
            "no less than TLS 1.2",
            "TLS 1.2 is the floor",
            "Minimum TLS version: 1.2",
            "connections MUST use TLS 1.2 or above",
            "we require TLS 1.2 as the baseline",
            "TLS >= 1.2",
            "TLS 1.2 (or higher) is required",
            "reject any TLS handshake below 1.2",
        })
    { EnumValues = new[] { "1.0", "1.1", "1.2", "1.3" } };

    public static readonly ConceptGroup DataClassificationConfidential = new(
        "data_classification",
        FactType.Enum,
        "Confidential",
        new[]
        {
            "The data is classified as Confidential.",
            "This is Confidential data under our classification scheme.",
            "Confidential (per data-classification policy)",
            "classification level: Confidential",
            "The information is treated as Confidential.",
            "Confidential business information",
            "This system processes Confidential data.",
            "The workload handles Confidential-tier data.",
            "Data classification: Confidential.",
            "Confidential — per policy Section 3.",
            "labelled as Confidential under the data-classification scheme",
        })
    { EnumValues = new[] { "Public", "Internal", "Confidential", "Restricted" } };

    public static readonly ConceptGroup EncryptionAlgorithmAes256 = new(
        "encryption_algorithm",
        FactType.Enum,
        "AES-256",
        new[]
        {
            "AES-256",
            "encryption uses AES-256",
            "AES-256 is applied to data at rest",
            "we use AES-256 for storage encryption",
            "AES-256 with envelope keys",
            "AES-256 (customer-managed keys)",
            "cipher: AES-256",
            "AES-256 SHALL be used",
            "data is encrypted with AES-256",
            "AES-256 is the mandated algorithm",
            "AES-256 for all persisted state",
        })
    { EnumValues = new[] { "AES-256", "AES-128", "ChaCha20-Poly1305", "AES-GCM" } };

    /// <summary>Encryption-at-rest boolean — used by EA-AKS-013 and
    /// EA-SECR-003. Distinct from <see cref="EncryptionDeclared"/>
    /// which is the umbrella "some form of encryption is declared"
    /// signal; <c>encryption_at_rest</c> is the tighter at-rest-only
    /// commitment.</summary>
    public static readonly ConceptGroup EncryptionAtRest = new(
        "encryption_at_rest",
        FactType.Boolean,
        true,
        new[]
        {
            "All data at rest is encrypted.",
            "Persisted data is encrypted using AES-256.",
            "Storage-tier encryption is enabled for all volumes.",
            "Data at rest SHALL be cryptographically protected.",
            "Encryption at rest is enforced by the storage service.",
            "Disks are encrypted using customer-managed keys.",
            "Every persisted byte is encrypted before it lands on disk.",
            "Envelope encryption is applied to all stored data.",
            "Storage accounts have infrastructure encryption enabled.",
            "The database uses transparent data encryption (TDE).",
            "At-rest encryption is on by default for all workloads.",
        });

    /// <summary>Encryption-in-transit boolean — used by EA-DATA-002.
    /// Complements <see cref="EncryptionAtRest"/>; policies often
    /// mandate both together.</summary>
    public static readonly ConceptGroup EncryptionInTransit = new(
        "encryption_in_transit",
        FactType.Boolean,
        true,
        new[]
        {
            "All traffic is encrypted in transit.",
            "TLS is required for all network communication.",
            "Traffic between services is encrypted using mTLS.",
            "In-transit encryption is enforced for every hop.",
            "All API calls SHALL be made over HTTPS.",
            "Cross-service traffic uses TLS 1.2 or higher.",
            "Data in motion is protected via TLS.",
            "Every connection to the workload is TLS-terminated.",
            "Internal traffic is encrypted end-to-end.",
            "The workload rejects unencrypted transport.",
            "HTTPS is enforced by conditional access at the ingress.",
        });

    /// <summary>Secrets-vaulted boolean — used by EA-DATA-018. Asserts
    /// that secrets live in a managed vault rather than in code /
    /// config / env vars.</summary>
    public static readonly ConceptGroup SecretsVaulted = new(
        "secrets_vaulted",
        FactType.Boolean,
        true,
        new[]
        {
            "All secrets are stored in Azure Key Vault.",
            "Secrets are managed by a centralized vault.",
            "Application secrets are held in a managed secret store.",
            "Secrets SHALL NOT be committed to source control.",
            "Every credential is retrieved from Key Vault at runtime.",
            "The workload uses a managed identity + vault pattern.",
            "Secrets are vaulted and rotated by the platform.",
            "No secrets appear in configuration files or environment variables.",
            "Secrets are stored exclusively in a hardware-backed vault.",
            "All service credentials live in a secret manager.",
            "Secret material is externalized to a policy-controlled vault.",
        });

    /// <summary>Secrets-IaC-managed boolean — used by EA-SECR-007.
    /// Asserts that vault contents / access are declared through
    /// infrastructure-as-code, not click-ops.</summary>
    public static readonly ConceptGroup SecretsIacManaged = new(
        "secrets_iac_managed",
        FactType.Boolean,
        true,
        new[]
        {
            "Secret configuration is managed via infrastructure-as-code.",
            "All Key Vault entries are declared in Bicep.",
            "Secret access policies are defined in Terraform.",
            "Vault contents SHALL be provisioned by IaC.",
            "Secrets and their access grants are code-reviewed via pull request.",
            "No manual portal changes to the vault are permitted.",
            "Vault RBAC is expressed as ARM templates under source control.",
            "The pipeline is the only path that mutates vault state.",
            "Secret rotation and access are declaratively managed.",
            "All vault mutations go through GitOps.",
            "Portal-based secret writes are disabled by policy.",
        });

    /// <summary>Break-glass-audited boolean — used by EA-CICD-011. The
    /// concept is "when the break-glass account is used, that use is
    /// captured to an audit trail," not "break-glass exists."</summary>
    public static readonly ConceptGroup BreakGlassAudited = new(
        "break_glass_audited",
        FactType.Boolean,
        true,
        new[]
        {
            "Break-glass account usage is fully audited.",
            "Any use of the emergency access account is logged to the SIEM.",
            "Break-glass sign-ins trigger an audit alert.",
            "Emergency-access invocations are captured in the audit pipeline.",
            "The break-glass workflow is instrumented for post-incident review.",
            "Every use of the break-glass credential SHALL be reviewed within 24 hours.",
            "Break-glass activity is retained for forensic analysis.",
            "The break-glass account is monitored by an always-on alerting rule.",
            "Emergency credential use is escalated to security automatically.",
            "All break-glass sessions are recorded to the tamper-evident log.",
            "Post-hoc audit of break-glass access is mandatory.",
        });

    // ── Master listing consumed by the test theories ─────────────────────

    /// <summary>All concept groups whose values are derivable from the
    /// deterministic normalizer/parser (Duration). These get exercised
    /// without any LLM call.</summary>
    public static IReadOnlyList<ConceptGroup> NormalizerGroups => new[]
    {
        KeyRotationDays,
        LoggingRetentionDays,
        WeeklyCadence,
        EveryNDaysFamily,
        AnnualCadence,
        UntaggedPrincipalDisableDays,
        OrphanSecretDeletionDays,
        ConsoleActionMigrationDays,
        CredentialRotationDays,
    };

    /// <summary>All concept groups exercised through the
    /// RecordedFactExtractor / merge / evaluate plumbing. These prove
    /// that once the LLM correctly emits the fact, the downstream is
    /// invariant to the surface phrasing.</summary>
    public static IReadOnlyList<ConceptGroup> ExtractionContractGroups => new[]
    {
        EncryptionDeclared,
        MfaRequired,
        LoggingEnabled,
        BackupDeclared,
        ResidencyBoundaryDeclared,
        TlsMinVersion12,
        DataClassificationConfidential,
        EncryptionAlgorithmAes256,
        EncryptionAtRest,
        EncryptionInTransit,
        SecretsVaulted,
        SecretsIacManaged,
        BreakGlassAudited,
    };

    /// <summary>Union — the full corpus, 22 concepts × ≥10 paraphrases.</summary>
    public static IReadOnlyList<ConceptGroup> All =>
        NormalizerGroups.Concat(ExtractionContractGroups).ToList();
}
