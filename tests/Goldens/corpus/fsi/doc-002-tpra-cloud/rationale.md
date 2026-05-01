# doc-002-tpra-cloud — rationale

## What this document tests

A more mature third-party risk assessment from a regulated bank. It
addresses most B-10 expectations correctly but leaves one specific gap
that B-10 explicitly calls out: **subcontracting must be subject to
prior consent or notice**, not merely "kept informed via the standard
quarterly service review".

## Expected verdict shape (high-level)

| Rule | Expected | Why |
|---|---|---|
| `OSFI-B10-TPRMF` | **Pass** | §2 heading contains "outsourcing" → categorises as `third_party_risk`; body explicitly names `TPRMF`, `Third-Party Risk Management Framework`, and `risk-tiered`. Lambda passes. |
| `OSFI-B10-Subcontracting` | **Fail** | §3 heading contains "outsourcing" → categorises. Body mentions "subcontract" but the Bank is only "kept informed" — there is no `consent`, `prior notice`, or `approval` obligation. Lambda fails. |
| `OSFI-B10-AuditAccess` | **Pass** | §4 heading contains "vendor risk" → categorises; body explicitly extends audit rights to `OSFI`. |
| `OSFI-B10-BCP` | **Pass** | §5 heading contains "business continuity" → categorises; body names both `RTO` and `RPO`. |
| `OSFI-B10-ExitPlan` | **Pass** | §6 heading contains "operational resilience" → categorises; body uses the exact phrase `exit plan` plus `triggers` and `transition`. |

Result: **4 Pass, 1 Fail.** The single targeted finding is the
subcontracting clause — a real-world OSFI-typical observation.

## Pedagogical value

Demonstrates that a B-10 review of a *good* third-party risk assessment
is short and actionable, not a flood of red. This is the document we
hand to a regulated-FI stakeholder when they ask "what does the
*non-failure mode* look like?"
