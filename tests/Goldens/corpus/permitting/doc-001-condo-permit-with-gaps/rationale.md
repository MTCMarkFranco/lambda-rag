# Rationale — `doc-001-condo-permit-with-gaps`

## What this document tests

A high-rise residential permit application that addresses many *common*
zoning and OBC items but **omits** four mandatory regulatory items. It
exercises the corpus's ability to flag the difference between a
"complete-looking" application and one that is silent on regulated
topics.

## Expected pattern (locked into `expected-verdict.json`)

- `PERMIT-AODA-001` — **Gap.** No AODA / IASR / Part IV.1 reference
  anywhere in the application despite being a public-facing residential
  building.
- `PERMIT-FIRE-EGRESS-001` — **Pass.** OBC 3.4.2 is implicitly addressed
  by the *Fire Egress* section's travel-distance figure and exit-stair
  description.
- `PERMIT-FIRE-SPRINK-001` — **Fail / Pass mix.** A NFPA 13 reference is
  absent — the *Fire Suppression* section says "wet-pipe sprinkler
  system" but does not invoke NFPA 13 by name nor cite OBC 3.2.2.
- `PERMIT-EIA-001` — **Gap.** The application does not mention the
  Impact Assessment Act, IAAC, or designated projects.
- `PERMIT-INDIG-001` — **Gap.** No mention of duty to consult or
  Indigenous consultation.

The actual locked verdicts live in `expected-verdict.json`.

## Public-source attribution

- Ontario Building Code, O. Reg. 332/12, ss. 3.2.2 and 3.4.2
- IASR O. Reg. 191/11, Part IV.1
- Impact Assessment Act, S.C. 2019, c. 28
- Constitution Act, 1982, s. 35; Haida Nation, 2004 SCC 73
