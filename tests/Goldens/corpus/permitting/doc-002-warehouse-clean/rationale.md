# Rationale — `doc-002-warehouse-clean`

## What this document tests

A clean, professionally-prepared industrial permit application that
addresses every mandatory rule in the corpus by name. It is the
"all-Pass" reference document for this vertical.

## Expected pattern (locked into `expected-verdict.json`)

All five mandatory rules should resolve to **Pass** on at least one
matched section, because each section explicitly cites the controlling
statute / regulation:

- `PERMIT-AODA-001` — *Accessibility — AODA / IASR Compliance* names
  IASR O. Reg. 191/11 and the accessible path of travel.
- `PERMIT-FIRE-EGRESS-001` — *Fire Egress* names OBC 3.4.2 and a
  travel-distance figure with exit reference.
- `PERMIT-FIRE-SPRINK-001` — *Fire Suppression* names NFPA 13 and OBC
  3.2.2.
- `PERMIT-EIA-001` — *Environmental Impact Assessment — Federal
  Screening* names the Impact Assessment Act and Physical Activities
  Regulations.
- `PERMIT-INDIG-001` — *Indigenous Consultation and Accommodation*
  names duty to consult, s. 35, and *Haida Nation*.

## Public-source attribution

- Ontario Building Code, O. Reg. 332/12, ss. 3.2.2 and 3.4.2
- IASR O. Reg. 191/11, Part IV.1
- Impact Assessment Act, S.C. 2019, c. 28
- Constitution Act, 1982, s. 35; *Haida Nation*, 2004 SCC 73
