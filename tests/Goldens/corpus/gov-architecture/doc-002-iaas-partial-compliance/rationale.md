# doc-002-iaas-partial-compliance — rationale

## What this document tests

A more mature IaaS architecture that gets four of the five guardrails
right and trips on a single, realistic gap (audit-log retention left as
"under review"). It is the **mostly-compliant case** that demonstrates
the platform produces actionable single-issue findings rather than just
red/green pass-fail.

## Expected verdict shape (high-level)

| Rule | Expected | Why |
|---|---|---|
| `GC-CG-01-MFA` | **Pass** | §2 names FIDO2 and PIV-derived credentials and uses the phrase "phishing-resistant MFA". Lambda passes. |
| `GC-CG-05-DataResidency` | **Pass** | §3 heading contains "Canadian Data Centre", body explicitly commits to Canada Central / Canada East / Toronto / Quebec and never mentions a US region. |
| `GC-CG-06-EncryptionAtRest` | **Pass** | §4 names AES-256 and "FIPS 140-2 validated". |
| `GC-CG-07-TLS` | **Pass** | §5 heading contains "TLS 1.2" → categorised; body commits to TLS 1.2 / 1.3 and explicitly disables TLS 1.0 / 1.1. |
| `GC-CG-11-AuditLogging` | **Fail** | §6 heading contains "audit log" → categorised; body names sign-in / role-assignment / resource-provisioning events ✓, but the retention period is left as "under review" — the lambda's retention check fails. |

## Pedagogical value

Demonstrates the platform's **localised remediation** value: a single
under-defined sentence in the architecture turns into one well-targeted
finding, rather than swamping the reviewer with noise from the
correctly-handled four guardrails.
