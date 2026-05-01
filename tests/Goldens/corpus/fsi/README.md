# fsi corpus — OSFI Guideline B-10 (Third-Party Risk Management)

## Source attribution

The rules in `ruleset.json` are derived directly from the **Office of the
Superintendent of Financial Institutions (OSFI) Guideline B-10:
Third-Party Risk Management**, the binding regulatory expectations on
federally regulated financial institutions (FRFIs) for any third-party
or outsourcing arrangement.

| Field | Value |
|---|---|
| Source | OSFI Guideline B-10 — Third-Party Risk Management |
| Canonical URL | https://www.osfi-bsif.gc.ca/en/guidance/guidance-library/third-party-risk-management-guideline |
| Custodian | Office of the Superintendent of Financial Institutions Canada |
| Status | Public regulatory guideline |
| Sanitisation | None required — public guideline. Quoted text is verbatim from the OSFI website. |
| Topic map | `fsi.v1` |

## Rules in this set

Five **Mandatory** rules covering the core contractual / governance
expectations of B-10:

| ID | OSFI Section | Topic | What it checks |
|---|---|---|---|
| `OSFI-B10-TPRMF` | 1.2 (Principle 2) | `third_party_risk` | Arrangement is governed by an enterprise-wide Third-Party Risk Management Framework with criticality / risk-tiering |
| `OSFI-B10-Subcontracting` | 3.2 | `third_party_risk` | Contract requires FRFI prior consent / notice for subcontracting |
| `OSFI-B10-AuditAccess` | A. Overview | `third_party_risk` | Audit / examination rights extend to OSFI |
| `OSFI-B10-BCP` | A3 (Criticality), §3 | `business_continuity` | BCP / DR with stated RTO and RPO |
| `OSFI-B10-ExitPlan` | A3 (Exit plan) | `business_continuity` | Exit plan with invocation triggers + transition assistance |

These five were chosen because they are the contractual provisions OSFI
most consistently flags during supervisory reviews, and because each can
be evaluated against the visible surface of an MSA / cloud agreement
without requiring access to the underlying TPRMF documentation.

## Documents in this corpus

| Doc id | Purpose | Expected outcomes |
|---|---|---|
| `doc-001-vendor-msa-with-gaps` | A typical first-draft critical-vendor MSA. Names a TPRMF only in passing, mentions subcontracting without a consent obligation, includes audit rights but not for OSFI, has a BCP section without quantitative recovery objectives, and never mentions an exit plan. | Mix of `Fail` / `Gap` verdicts. Mid-low score. |
| `doc-002-tpra-cloud` | A more mature third-party risk assessment for a cloud SaaS that addresses TPRMF, audit, BCP and exit plan correctly but is silent on subcontracting consent. | Mostly `Pass`, one targeted `Fail` on subcontracting. |

## What's deliberately *not* claimed

- **B-10 is one ruleset of many.** A real FRFI compliance review would
  also apply Guideline E-21 (Operational Risk), B-13 (Tech and Cyber
  Risk), and the Corporate Governance Guideline. This corpus exercises
  the projector + selector + lambda evaluator on B-10 because B-10 maps
  cleanly onto observable surface text. It is **not** an attestation
  that passing all five rules makes a vendor MSA OSFI-compliant.
- The lambdas use **simple keyword conjunctions**. They are intentionally
  brittle — that brittleness is a feature for an idempotency / regression
  corpus. A production ruleset would extend the lambdas with structural
  selectors (e.g. did the audit-rights clause survive into the executed
  contract?). This corpus is the regression backbone, not the production
  ruleset.

## Re-deriving the rules from source

```bash
# Re-fetch the OSFI B-10 page and confirm the evidenceQuote / sourceContent
# fields in ruleset.json still match the upstream guideline:
curl -fsSL https://www.osfi-bsif.gc.ca/en/guidance/guidance-library/third-party-risk-management-guideline > /tmp/b10.html
```
