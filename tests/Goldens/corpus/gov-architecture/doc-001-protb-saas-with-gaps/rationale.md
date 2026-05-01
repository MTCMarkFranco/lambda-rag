# doc-001-protb-saas-with-gaps — rationale

## What this document tests

A realistic first-draft Protected B SaaS architecture submission. Each
section is plausible but deliberately falls short of one or more GC
Cloud Guardrails. It is the **negative-case workhorse** of the
gov-architecture corpus.

## Expected verdict shape (high-level)

| Rule | Expected | Why |
|---|---|---|
| `GC-CG-01-MFA` | **Fail** | Section §2 is categorised as `phishing_resistant_mfa` (heading contains "phishing-resistant mfa"), but the body never names an actual phishing-resistant mechanism (FIDO2 / PIV / smart card). The lambda — `(FIDO2 || PIV || smart card) && phishing-resistant` — evaluates `false`. |
| `GC-CG-05-DataResidency` | **Gap** | No section heading contains any of the `data_residency_canada` keywords (`canada-resident`, `canadian data centre`, `tbs data residency`). The mandatory rule has zero candidate matches → emitted as `Gap`. |
| `GC-CG-06-EncryptionAtRest` | **Fail** | Section §3 is categorised as `encryption_at_rest`. Body mentions "encryption" but never `AES-256` and never `FIPS 140` / `FIPS-validated`. Lambda fails. |
| `GC-CG-07-TLS` | **Gap** | Section §4 is titled "Network Encryption" — none of `tls 1.2` / `tls 1.3` / `fips ciphers` appears in the *heading*, and the projector requires a heading match for primary topic assignment. Section is left as `unknown`; the mandatory rule emits a `Gap`. |
| `GC-CG-11-AuditLogging` | **Fail** | Section §5 heading contains "audit log", so it categorises. Body lacks both a retention period (`365 days`, `12 months`, `year`, etc.) and a named event type (`sign-in`, `configuration change`, `resource provisioning`). Lambda fails. |

## What this document is *not*

- Not modelled on any specific customer architecture.
- Not a recommendation. Every paragraph here is a kind of gap that we
  routinely see in real first-draft submissions and want the platform
  to surface deterministically.

## Pedagogical value

This is the document we point to when answering "what does a *failed*
review look like?" It exercises both `Fail` (rule fired, lambda false)
and `Gap` (mandatory rule, no candidate) — the two distinct
non-compliance verdicts in the platform.
