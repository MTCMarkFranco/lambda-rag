# gov-architecture corpus — Government of Canada Cloud Guardrails v2.0

## Source attribution

The rules in `ruleset.json` are derived directly from the **Government of
Canada Cloud Guardrails v2.0** — a baseline of mandatory cyber-security
controls that GC departments must implement, validate, and report on within
the first 30 business days of obtaining access to a cloud account.

| Field | Value |
|---|---|
| Source repo | https://github.com/canada-ca/cloud-guardrails |
| Canonical doc | https://www.tbs-sct.canada.ca/pol/doc-eng.aspx?id=32787 |
| Custodian | Treasury Board of Canada Secretariat (TBS) / Shared Services Canada (SSC) |
| Licence | Open Government Licence – Canada (OGL-Canada) |
| Sanitisation | None required — public, openly licensed source |
| Topic map | `gov-architecture.v1` |

The natural-language statements, evidence quotes, and `sourceContent`
fields in `ruleset.json` are **verbatim or near-verbatim quotes from the
public guardrail text** — no customer or restricted material is included.

## Rules in this set

Five **Mandatory** rules drawn from the highest-impact guardrails:

| ID | Guardrail | Topic | What it checks |
|---|---|---|---|
| `GC-CG-01-MFA` | 01 — Protect user accounts and identities | `phishing_resistant_mfa` | Architecture explicitly names a phishing-resistant MFA mechanism (FIDO2, PIV, or smart card) |
| `GC-CG-05-DataResidency` | 05 — Data location | `data_residency_canada` | Protected B workloads are committed to a Canadian region (Canada Central / Canada East / Toronto / Montreal / Quebec) and *not* a US region |
| `GC-CG-06-EncryptionAtRest` | 06 — Protection of data at rest | `encryption_at_rest` | AES-256 with FIPS 140 validation is named |
| `GC-CG-07-TLS` | 07 — Protection of data in transit | `encryption_in_transit` | TLS 1.2 or 1.3 is required and TLS 1.0 / 1.1 is disabled |
| `GC-CG-11-AuditLogging` | 11 — Logging and monitoring | `audit_logging` | Audit logs cover sign-in, config-change, and resource-provisioning events with a stated retention period |

These five guardrails were chosen because:

1. They map cleanly onto headings that *already exist* in the
   `gov-architecture.v1` topic map shipped with lambda-rag — so the corpus
   exercises the projector + selector path end-to-end without bespoke
   extensions.
2. Each has an unambiguous, machine-checkable surface form (e.g. "TLS
   1.2", "Canada Central", "AES-256") that produces a deterministic
   pass / fail lambda over plain text.
3. They cover four distinct severity / control families (identity,
   sovereignty, cryptography, observability), exercising more of the
   pipeline than five rules from a single family would.

## Documents in this corpus

| Doc id | Purpose | Expected outcomes |
|---|---|---|
| `doc-001-protb-saas-with-gaps` | A realistic but **deliberately gappy** Protected B SaaS architecture. Demonstrates the platform's ability to surface both *failed* assertions (rule fired, lambda false) and *gaps* (mandatory rule with no matching section at all). | Mix of `Fail` and `Gap` verdicts; score < 0.5. |
| `doc-002-iaas-partial-compliance` | A more mature IaaS architecture that addresses most controls but leaves audit-log retention vague. | Mostly `Pass`, one `Fail` on audit retention. |
| `doc-003-fully-compliant` | A clean reference architecture — all five guardrails addressed correctly. Doubles as a positive-control regression: if this ever fails, something has regressed in the projector / lambda evaluator. | All `Pass`. Score = 1.0. |

## Format choice — `.md` instead of `.pdf`

Issue #18's Contoso says `source.pdf`. We chose Markdown for the corpus because:

- Markdown is **diffable in code review** — a reviewer can see in a PR
  exactly which sentence in a synthetic candidate document changed.
- Markdown is **byte-stable across operating systems** — no PDF generator
  variance to debug when goldens drift.
- The `lambda-rag` parser supports `.md` natively (see
  `LambdaRag.Parsing` parser registry), so no information is lost.

The trade-off is that `expected-markup.docx` is not produced for this
corpus (markup mode requires `.docx` source). That golden is deferred to
a follow-up issue specifically on .docx markup goldens — the core verdict
regression is what protects us against rule-engine drift.

## Re-deriving the rules from source

```bash
# Re-fetch the raw guardrail markdown and confirm the evidenceQuote /
# sourceContent fields in ruleset.json still match upstream:
curl -fsSL https://raw.githubusercontent.com/canada-ca/cloud-guardrails/master/EN/01_Protect-user-accounts-and-identities.md
curl -fsSL https://raw.githubusercontent.com/canada-ca/cloud-guardrails/master/EN/05_Data-Location.md
curl -fsSL https://raw.githubusercontent.com/canada-ca/cloud-guardrails/master/EN/06_Protect-Data-at-Rest.md
curl -fsSL https://raw.githubusercontent.com/canada-ca/cloud-guardrails/master/EN/07_Protect-Data-in-Transit.md
curl -fsSL https://raw.githubusercontent.com/canada-ca/cloud-guardrails/master/EN/11_Logging-and-Monitoring.md
```

If a guardrail upstream is updated and the diff is material, bump the
ruleset's `version` field, re-derive the affected rule(s), regenerate
the affected `expected-verdict.json` snapshots, and call out the change
in the PR.
