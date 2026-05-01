# Corpus — `permitting`

Topic map: **`permitting.v1`**.

## Public sources

All rules in `ruleset.json` are derived from public Canadian regulatory text:

- **Ontario Building Code (O. Reg. 332/12)** — egress, fire-resistance, sprinkler requirements (NBC/OBC). Public regulation under the Ontario Building Code Act, 1992.
  - https://www.ontario.ca/laws/regulation/120332
- **Integrated Accessibility Standards Regulation (O. Reg. 191/11)** under the *Accessibility for Ontarians with Disabilities Act, 2005* (AODA) — Part IV.1 *Design of Public Spaces* and Part III *Information and Communications*.
  - https://www.ontario.ca/laws/regulation/110191
- **Impact Assessment Act, S.C. 2019, c. 28, s. 1** — federal designated-projects EIA regime (s. 9 prohibition on carrying out a designated project without IA).
  - https://laws-lois.justice.gc.ca/eng/acts/I-2.75/
- **Constitution Act, 1982, s. 35** — duty to consult and accommodate Indigenous peoples (Haida Nation v. British Columbia (Minister of Forests), 2004 SCC 73).
- **MECP Publication NPC-300** (Ontario Ministry of the Environment) — environmental noise compliance for stationary sources.

No customer content, no private documents. All synthetic candidate documents
(`source.md`) are written specifically to exercise the rules, with citations to
public sources in the companion `rationale.md`.

## Rules at a glance

| Rule id | Source | Mandate level |
|---|---|---|
| `PERMIT-AODA-001` | IASR O. Reg. 191/11 Part IV.1 (accessible exterior paths of travel) | Mandatory |
| `PERMIT-FIRE-EGRESS-001` | OBC 3.4.2 (travel distance to exit) | Mandatory |
| `PERMIT-FIRE-SPRINK-001` | OBC 3.2.2 / NFPA 13 (sprinkler protection) | Mandatory |
| `PERMIT-EIA-001` | IAA s. 9 (designated projects) | Mandatory |
| `PERMIT-INDIG-001` | s. 35 + *Haida Nation* (duty to consult) | Mandatory |

## Documents

- `doc-001-condo-permit-with-gaps/` — high-rise residential building permit application missing AODA, EIA, and consultation sections (multiple Fail / Gap).
- `doc-002-warehouse-clean/` — distribution-warehouse permit application addressing every rule (all Pass on mandatory rules; some N/A).
