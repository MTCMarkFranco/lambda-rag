# doc-001-vendor-msa-with-gaps — rationale

## What this document tests

A first-draft critical-vendor MSA for a payment-processing arrangement
— the archetypal B-10 outsourcing scenario. The agreement *looks*
serviceable on a quick read but fails several B-10 contractual
expectations.

## Expected verdict shape (high-level)

| Rule | Expected | Why |
|---|---|---|
| `OSFI-B10-TPRMF` | **Fail** | §3 heading "Third-Party Risk Acknowledgement" categorises as `third_party_risk`. Body name-checks "third-party arrangement" but never references a `TPRMF` or `Third-Party Risk Management Framework`, and never invokes risk-tiering / criticality. Lambda fails. |
| `OSFI-B10-Subcontracting` | **Pass** *or* **Fail** | §4 heading "Subcontractors and Outsourcing" categorises as `third_party_risk`. Body mentions "subcontract" but says "may subcontract … to any affiliate or qualified third party" — i.e. **no consent or prior notice obligation**. The lambda checks for `subcontract && (consent || prior notice || approval)` so this fails. |
| `OSFI-B10-AuditAccess` | **Fail** | §5 categorises as `third_party_risk`. Body grants audit rights only to "the Bank's internal audit team" — does not extend to OSFI. Lambda requires `audit && OSFI` → fails. |
| `OSFI-B10-BCP` | **Fail** | §6 heading "Business Continuity" → categorises. Body mentions "BCP" ✓ but no `RTO` / `RPO` / `recovery time` / `recovery point` → lambda fails. |
| `OSFI-B10-ExitPlan` | **Gap** | No section heading or body contains the phrase `exit plan`. The mandatory rule has no candidate match → emitted as `Gap`. |

This is exactly the spread we want: the platform highlights five
distinct B-10 contractual issues out of one document, each with a
remediation pointer.

## Pedagogical value

The MSA in this file is **plausible.** A reviewer skimming it without
B-10 in front of them would likely sign it. That is the point — it's
the value lambda-rag adds over an LLM-only review: deterministic, rule-
backed identification of regulatory gaps that a casual read misses.
