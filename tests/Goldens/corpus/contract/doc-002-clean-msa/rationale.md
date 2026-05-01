# doc-002-clean-msa — rationale

A clean Canadian-law MSA — the positive-control regression for the
contract vertical.

## Expected verdict shape

| Rule | Expected | Why |
|---|---|---|
| `CAN-CONTRACT-PAY-001` | **Pass** | §3 → `payment_terms`; body says "thirty (30) days" + "net 30" → lambda passes. |
| `CAN-CONTRACT-PRIVACY-001` | **Pass** | §4 → `privacy`; body explicitly references PIPEDA and Law 25. |
| `CAN-CONTRACT-LIAB-001` | **Pass** | §5 → `liability`; "aggregate liability … shall not exceed the fees paid … in the twelve (12) months". |
| `CAN-CONTRACT-TERM-001` | **Pass** | §6 → `termination`; both material-breach and for-convenience clauses with notice. |
| `CAN-CONTRACT-GOV-001` | **Pass** | §7 → `governing_law`; "Province of Ontario" + "federal laws of Canada". |

All five rules pass. Score = 1.0.
