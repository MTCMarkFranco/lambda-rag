namespace LambdaRag.Tools.LockedOracleProbe;

/// <summary>
/// The fixed input document used across all N probe runs. Chosen to be
/// unambiguous on 4 of 5 target fields (so measured drift reflects
/// hardware/kernel non-determinism, not model uncertainty) and mildly
/// ambiguous on 1 field (data_residency_region) to expose any boundary
/// instability.
///
/// ~500 tokens. Do NOT edit — changes invalidate any prior probe results
/// because the text becomes a different cache key input.
/// </summary>
internal static class ProbeDocument
{
    public const string DocumentId = "skyledger-arch-v1";

    public const string Text = """
        SkyLedger Platform — Architecture Overview (Rev A)

        SkyLedger is a multi-tenant financial reconciliation platform that
        ingests bank transaction feeds, matches them to internal ledger
        entries, and surfaces exceptions to human accountants for review.
        This document describes the security and hosting posture of the
        current production deployment.

        Network transport. All external HTTPS endpoints terminate at an
        Azure Front Door instance configured to require TLS 1.3 with a
        minimum cipher suite of TLS_AES_256_GCM_SHA384. HTTP-to-HTTPS
        redirect is enforced at the edge, and HSTS is returned with a
        max-age of 63072000 seconds on every response. Internal
        service-to-service traffic within the Kubernetes cluster runs
        over mTLS via a Linkerd mesh; certificates are rotated every
        24 hours by an internal issuer. There is no plaintext HTTP path
        anywhere in the request lifecycle. Encryption in transit is
        therefore enabled end to end.

        Data at rest. The primary transactional store is Azure SQL
        Database using Transparent Data Encryption (TDE) with a
        customer-managed key stored in Azure Key Vault Premium (HSM
        backed). Blob storage for statement PDFs uses server-side
        encryption with the same customer-managed key. Backups are
        encrypted with the same key hierarchy. Encryption at rest is
        enabled and audited quarterly.

        Identity and access. End users authenticate through the tenant's
        Entra ID via OpenID Connect. Multi-factor authentication is
        enforced by conditional access policy for every sign-in; no
        password-only sign-in is permitted. Service accounts use
        federated workload identity — no long-lived secrets are stored.
        The authoritative authentication method for interactive users
        is MFA on top of Entra ID.

        Regional footprint. The primary production stamp is deployed to
        Canada Central, with an active-passive disaster recovery stamp
        in Canada East. Customer data — including PII, transaction
        records, and audit logs — is written only to storage accounts
        located in these two Canadian regions. Cross-region replication
        never leaves Canada. Some non-customer telemetry (application
        health metrics, no PII) is sent to a shared observability
        tenant in East US 2; this is documented in the Data Handling
        Addendum. For the purposes of customer data residency, the
        platform operates in Canada.

        Change management. All infrastructure is defined in Bicep and
        deployed via GitHub Actions with required reviewers. Production
        changes require SOX-compliant approval from two release
        managers. Break-glass access is logged and reviewed weekly.
        """;
}
