namespace LambdaRag.Tools.LockedOracleProbe;

/// <summary>
/// Stress documents for Phase 0 probing. All three share the same
/// <see cref="StructuredFacts"/> 5-field schema so results are directly
/// comparable. The documents differ along three axes chosen from the
/// FID-Lottery paper's variance sources:
///
///   default    - short (~500 tok), unambiguous facts. Baseline.
///   long       - ~2000 tok, same 5 facts embedded in dense noise.
///                Tests whether longer autoregressive traces expose
///                more floating-point summation drift.
///   ambiguous  - ~500 tok, but every answer is deliberately borderline.
///                Tests whether close top-2 logits amplify drift into
///                verdict flips.
/// </summary>
internal static class Documents
{
    public static (string Id, string Text) Get(string name) => name switch
    {
        "default"   => (DefaultId,   DefaultText),
        "long"      => (LongId,      LongText),
        "ambiguous" => (AmbiguousId, AmbiguousText),
        _ => throw new ArgumentException(
            $"Unknown document '{name}'. Use: default | long | ambiguous",
            nameof(name)),
    };

    // ------- default (existing, kept identical for continuity) -------
    public const string DefaultId = ProbeDocument.DocumentId;
    public const string DefaultText = ProbeDocument.Text;

    // ------- long: same facts, more noise -------
    public const string LongId = "skyledger-arch-long-v1";
    public const string LongText = """
        SkyLedger Platform — Extended Architecture Overview (Rev A, Long-form)

        1. Business context
        SkyLedger is a multi-tenant financial reconciliation platform serving
        mid-market lenders and asset servicers across North America. Its
        primary responsibility is to ingest daily bank transaction feeds
        (SWIFT MT940, ISO 20022 camt.053, and BAI2), match them against
        internal ledger entries produced by a customer's core-banking or
        loan-servicing system, and surface exceptions to accountants for
        manual investigation. The platform currently processes on the order
        of 40 million transactions per business day at peak load, with a
        24-month retention policy for reconciled entries and a 7-year
        retention for the associated audit ledger.

        2. Deployment topology
        Production is hosted entirely on Microsoft Azure. The primary stamp
        runs in Canada Central across three availability zones. An
        active-passive disaster-recovery stamp exists in Canada East and is
        kept warm by continuous asynchronous replication with an RPO target
        of five minutes and an RTO target of one hour. A dedicated shared
        observability tenant (metrics, traces, non-customer application
        logs) is located in East US 2 for operational-cost reasons; this
        tenant handles no customer data.

        3. Network transport (encryption in transit)
        All external ingress terminates at an Azure Front Door instance
        configured to require TLS 1.3 with a minimum cipher suite of
        TLS_AES_256_GCM_SHA384. Front Door enforces HTTP-to-HTTPS redirect
        at the edge and returns HSTS with a max-age of 63072000 seconds
        (two years) on every response. Public API traffic uses mutual TLS
        for select partners under contractual obligation. Internal
        service-to-service traffic within the AKS cluster is transported
        over mTLS by a Linkerd service mesh; workload certificates are
        rotated every 24 hours by an internal issuer sourced from Azure
        Key Vault Premium. There is no plaintext HTTP path anywhere in
        the request lifecycle, including for readiness probes and for
        internal admin endpoints, which are gated behind Entra ID SSO
        and mTLS both.

        4. Data at rest (encryption at rest)
        The primary transactional store is Azure SQL Database Hyperscale
        with Transparent Data Encryption (TDE) using a customer-managed
        key held in Azure Key Vault Premium (HSM-backed, FIPS 140-3
        Level 3). The blob storage account for statement PDFs and
        supporting documents uses server-side encryption with the same
        customer-managed key. All database backups and long-term archives
        inherit the same key hierarchy; a quarterly attestation report is
        produced for SOC 2 auditors demonstrating that no unencrypted
        copy of customer data has been produced anywhere in the estate.

        5. Identity and access management
        End users authenticate through their organization's Entra ID tenant
        via OpenID Connect. Multi-factor authentication is enforced by
        tenant conditional-access policy for every sign-in; no password-
        only sign-in path is permitted. Service accounts use federated
        workload identity — no long-lived secrets are stored in the
        cluster or in application configuration. Break-glass local
        accounts exist for two named site-reliability engineers only, are
        stored in a dedicated Key Vault under HSM protection, and are
        logged and reviewed weekly. The authoritative interactive-user
        authentication mechanism is therefore MFA on top of Entra ID.

        6. Regional footprint and data residency
        Customer data — including PII, transaction records, reconciled
        ledger state, and the immutable audit ledger — is written only
        to storage accounts located in the two Canadian regions listed in
        Section 2 above. Cross-region replication traffic between Canada
        Central and Canada East is transported over the Microsoft
        backbone and never leaves Canadian geography. Application-level
        telemetry (health, latency, error counts — no PII by policy) is
        shipped to the East US 2 observability tenant; this arrangement
        is documented in the Data Handling Addendum executed by every
        customer and by the DPA where applicable. For all purposes of
        customer data residency the platform operates in Canada.

        7. Change management (out of scope for extraction)
        Infrastructure is defined in Bicep and deployed via GitHub
        Actions with required reviewers. Production changes require
        SOX-compliant approval from two release managers. Break-glass
        access is logged and reviewed weekly.
        """;

    // ------- ambiguous: every answer is a coin flip -------
    public const string AmbiguousId = "skyledger-arch-ambiguous-v1";
    public const string AmbiguousText = """
        SkyLedger Platform — Draft Architecture Sketch (Rev 0.3, Working Draft)

        (This is an early architectural sketch. Some items are aspirational,
        some are under discussion, and some have partial coverage. Extract
        what is described.)

        Product name. The team currently uses "SkyLedger" internally and on
        the design wiki, but the marketing team is pushing to rename to
        "SkyLedger Pro" for the GA launch. Legal has requested "SkyLedger
        Reconciliation Suite" for contractual documents. No decision yet.

        External endpoints. Public HTTPS is terminated at Azure Front Door
        with TLS 1.2 as the enforced minimum. TLS 1.3 is offered but not
        required. There is one legacy webhook path (/v1/legacy-callback)
        that accepts plaintext HTTP from a small number of pre-2024
        integrations, gated by IP allow-list; it is scheduled for removal
        in Q4 but is currently live. Internal service-to-service traffic
        within the cluster uses mTLS through a service mesh for the newer
        microservices; the older monolith components still communicate
        over unencrypted HTTP within the VPC boundary.

        Database. The primary Azure SQL instance has TDE enabled by
        default with a Microsoft-managed key. A migration to a customer-
        managed key stored in Key Vault Premium is planned but not yet
        executed; the design is signed off, the CMK is provisioned, but
        the rotation has been deferred pending an availability window.
        Blob storage uses account-level server-side encryption with the
        Microsoft-managed key.

        Authentication. Entra ID sign-in is available and is the default
        path. Multi-factor authentication is enforced for administrative
        users through Privileged Identity Management, but for regular
        end-users MFA is currently in a "reminder" (not-enforced) state
        while change-management works through the rollout plan. A
        password-only fallback exists for three legacy service accounts
        that pre-date federation.

        Regions. The application runs in Canada Central. The database is
        currently in East US due to a historical migration; a copy-back
        to Canada Central is in flight and expected to complete this
        quarter. Blob storage is Canada Central. Backups replicate to
        Canada East. So depending on which subsystem you ask about,
        customer data touches either Canada Central, Canada East, or
        East US — the intent is Canada-only once the DB move completes.
        """;
}
