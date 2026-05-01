# doc-001-msa-with-gaps — rationale

A US-drafted MSA template dropped onto a Canadian customer without
local-law adaptation — the recurring real-world failure mode this
corpus is designed to flag.

## Expected verdict shape

| Rule | Expected | Why |
|---|---|---|
| `CAN-CONTRACT-PAY-001` | **Fail** | §3 categorises as `payment_terms` (heading "Payment and Compensation"); body says 45 days, lambda checks for "30 days" / "Net 30" / "15 days" → fails. |
| `CAN-CONTRACT-PRIVACY-001` | **Fail** | §4 categorises as `privacy` (heading contains "data processing", "personal data"); body cites GDPR only — no PIPEDA / Law 25 / provincial-PIPA reference → lambda fails. |
| `CAN-CONTRACT-LIAB-001` | **Fail** | §5 heading "Liability" → categorises; body has no cap, no fees-based limitation → lambda fails. |
| `CAN-CONTRACT-TERM-001` | **Fail** | §6 categorises as `termination`; body covers material breach but provides no termination-for-convenience by Customer → lambda fails. |
| `CAN-CONTRACT-GOV-001` | **Fail** | §7 categorises as `governing_law`; body cites Delaware only, no Canadian jurisdiction → lambda fails. |

This document is intentionally a near-clean sweep of failures. It is
the demo doc we point to when explaining why Canadian customers should
not accept US-template MSAs without scrutiny.
