# TBS Directive on Automated Decision-Making — Lambda-RAG Mapping

> **Status:** Phase 1 / [P1.5](https://github.com/MTCMarkFranco/lambda-rag/issues/15) — clause-by-clause mapping doc.
>
> **Source:** [Treasury Board of Canada Secretariat — *Directive on Automated Decision-Making*](https://www.tbs-sct.canada.ca/pol/doc-eng.aspx?id=32592). Date modified 2025-06-24. Crown copyright. Quoted excerpts are used for the purpose of regulatory mapping under fair-dealing.
>
> **Audience:** Government of Canada departments and agencies subject to the *Policy on Service and Digital* (and others adopting it as good practice). The Directive applies to **automated decision systems** (ADS) used to make or assist administrative decisions about clients.
>
> **Disclaimer:** This is a *technical mapping document*, not legal advice. Lambda-rag does not certify TBS Directive compliance — it produces an auditable, deterministic verdict against rules an institution authored. The institution remains accountable for rule fidelity and for departmental governance. See [`docs/what-lambda-rag-is-not.md`](../what-lambda-rag-is-not.md).

---

## Why this directive matters for lambda-rag

The TBS Directive on ADM is the closest thing Canada has to a binding,
enforceable AI-governance regime today (Bill C-27 / AIDA is still a
draft — see [`bill-c27-aida-mapping.md`](bill-c27-aida-mapping.md)).
It applies *now*, to *real systems in production*, with *real
compliance dates* (existing systems: by **2026-06-24**; agents of
Parliament: by **2026-06-24**).

For a federal department evaluating lambda-rag itself, this directive
is also **directly applicable** — lambda-rag is the kind of system the
Directive is written to govern. Two reads:

1. **Lambda-rag as the subject** — if a department uses lambda-rag to
   make or assist administrative decisions, the Directive applies to
   *the lambda-rag deployment*.
2. **Lambda-rag as the tool** — a department can use lambda-rag to
   review its own ADS designs / Algorithmic Impact Assessments (AIAs)
   against the Directive's clauses.

This mapping supports both reads.

---

## How to read this doc

Each row maps a Directive clause to a candidate lambda-rag rule:

| Field | Meaning |
|---|---|
| **Clause** | Directive section reference (matches the canonical text) |
| **Obligation** | What the regulated department must show |
| **Suggested rule ID** | Stable ID for use in `ruleset.json` |
| **Topic map** | `gov-architecture.v1` (closest existing); a future `tbs-ai.v1` would be more precise |
| **Selector** / **Lambda** | Pure-code matchers (see [`docs/SELECTORS.md`](../SELECTORS.md)) |
| **Severity** / **Applicability** | Driven by the Algorithmic Impact Assessment level (I–IV) — see Appendix B section below |

The same rule shape used by the corpus tests under [`tests/Goldens/corpus/gov-architecture/`](../../tests/Goldens/corpus/gov-architecture/ruleset.json) applies here.

---

## §4 — Objectives (framework anchors)

| Field | Mapping |
|---|---|
| Rule ID | `TBS-ADM-OBJ-001` |
| Obligation | The ADS design document explicitly states the three expected results: (a) decisions are data-driven and comply with procedural fairness / due process; (b) impacts assessed and negative outcomes reduced; (c) data and information made available to the public, while protecting privacy / security / IP. |
| Severity / Applicability | `High` / `Mandatory` |
| Lambda hint | `input1.text.Contains("procedural fairness") && input1.text.Contains("due process") && input1.text.Contains("Open Government Portal")` |

---

## §5 — Scope

| Rule ID | Obligation | Severity |
|---|---|---|
| `TBS-ADM-SCOPE-001` | Design doc names whether the ADS is in production (vs. research / sandbox / test environment). | Critical |

> 📌 The Directive's `production` definition is broad: *"in use and has impacts on real clients [...] including when it is in beta or user testing and producing outputs that impact clients."* The lambda predicate must therefore not exempt beta releases that affect clients.

---

## §6.1 — Algorithmic Impact Assessment (AIA)

The AIA is the **gating artifact**. Three obligations:

| # | Clause | Rule ID | Severity |
|---|---|---|---|
| 1 | §6.1.1 — AIA completed, approved, **published on Open Government Portal** *prior to production* | `TBS-ADM-AIA-001` | Critical / Mandatory |
| 2 | §6.1.2 — Apply the Appendix C requirements *as determined by the AIA level* | `TBS-ADM-AIA-002` | Critical / Mandatory |
| 3 | §6.1.3 — Review / approve / **update** AIA on schedule, including when functionality or scope changes | `TBS-ADM-AIA-003` | Critical / Mandatory |

### Worked example — `TBS-ADM-AIA-001`

```json
{
  "id": "TBS-ADM-AIA-001",
  "version": "1.0.0",
  "naturalLanguage": "An Algorithmic Impact Assessment (AIA) must be completed, approved, and published on the Open Government Portal prior to the automated decision system entering production.",
  "severity": "Critical",
  "applicability": "Mandatory",
  "appliesToSchema": {
    "type": "object",
    "properties": { "category": { "type": "string" }, "text": { "type": "string" } },
    "required": ["category", "text"]
  },
  "selector": { "kind": "path", "path": "$.sections[*]" },
  "predicate": "input1.category == \"governance\" || input1.category == \"compliance\"",
  "lambda": "input1.text.Contains(\"Algorithmic Impact Assessment\") && (input1.text.Contains(\"Open Government Portal\") || input1.text.Contains(\"open.canada.ca\"))",
  "remediation": "Complete an AIA using the TBS tool, publish the final results on the Open Government Portal, and reference the publication URL in the design document. See https://www.canada.ca/en/government/system/digital-government/digital-government-innovations/responsible-use-ai/algorithmic-impact-assessment.html",
  "evidenceQuote": "Completing, approving and publishing the final results of an algorithmic impact assessment in an accessible format on the Open Government Portal prior to the production of any automated decision system.",
  "sourceContent": "TBS Directive on Automated Decision-Making §6.1.1.",
  "metadata": {
    "regulator": "TBS",
    "directive": "Directive on Automated Decision-Making",
    "section": "6.1.1",
    "sourceUrl": "https://www.tbs-sct.canada.ca/pol/doc-eng.aspx?id=32592",
    "complianceDate": "2026-06-24"
  }
}
```

---

## §6.2 — Transparency

Eight obligations split across four sub-themes:

### 6.2.1–6.2.2 — Notice *before* decisions

| Rule ID | Obligation |
|---|---|
| `TBS-ADM-NOTICE-001` | Notice provided through **all service-delivery channels in use** that decisions will be made or assisted by an ADS. |
| `TBS-ADM-NOTICE-002` | Notice is **prominent** and in **plain language**, per the Canada.ca Content Style Guide. |

### 6.2.3 — Explanation *after* decisions

| Rule ID | Obligation |
|---|---|
| `TBS-ADM-EXPLAIN-001` | Meaningful explanation provided to clients of how and why the decision was made — content depth scales by AIA level (Appendix C). |

### 6.2.4–6.2.6 — Access to components (open source vs proprietary)

| Rule ID | Obligation |
|---|---|
| `TBS-ADM-LICENCE-001` | Licence determined for software components (with explicit consideration of OSS). |
| `TBS-ADM-RELEASES-001` | All released versions of software components obtained and safeguarded. |
| `TBS-ADM-PROPRIETARY-001` | If proprietary: department retains right to access / test / monitor for audit, investigation, judicial proceeding, with safeguards; right to authorize external review preserved. |

### 6.2.7 — Documenting decisions

| Rule ID | Obligation |
|---|---|
| `TBS-ADM-DOC-001` | Decisions / assessments documented per the *Directive on Service and Digital*, in support of testing / monitoring / data governance / reporting. |

---

## §6.3 — Quality assurance (the heart of the Directive — 13 sub-clauses)

| Clause | Sub-theme | Rule ID | Severity |
|---|---|---|---|
| 6.3.1 | Pre-production testing for accuracy + unintended bias + factors that may unfairly impact outcomes / violate human rights | `TBS-ADM-TEST-001` | Critical |
| 6.3.2 | Outcome **monitoring** on a scheduled basis, against human-rights / legislative / Directive obligations | `TBS-ADM-MONITOR-001` | Critical |
| 6.3.3 | Testing + monitoring **explicitly assess human rights**, consistent with the *Charter*, *CHRA*, and *UNDRIP Act* | `TBS-ADM-HR-001` | Critical |
| 6.3.4 | Documentation of client feedback, unexpected impacts, **human overrides**, system failures + corrective actions | `TBS-ADM-OVERRIDE-001` | High |
| 6.3.5 | Data quality — **relevant, accurate, up-to-date**, per *Privacy Act* | `TBS-ADM-DATA-001` | Critical |
| 6.3.6 | Data governance — traceable, protected, lawfully collected/used/retained/disclosed/disposed | `TBS-ADM-DATAGOV-001` | Critical |
| 6.3.7 | **Peer review** by qualified experts; review or plain-language summary published prior to production | `TBS-ADM-PEER-001` | Critical |
| 6.3.8 | **Gender-based Analysis Plus (GBA Plus)** during development / modification | `TBS-ADM-GBAP-001` | High |
| 6.3.9 | **Employee training** for everyone involved in development / use / management | `TBS-ADM-TRAIN-001` | High |
| 6.3.10 | Risk assessments + IM/IT security protections per *Policy on Government Security* and *Policy on Service and Digital* | `TBS-ADM-SEC-001` | Critical |
| 6.3.11 | Measures to secure **data and model integrity** against tampering / unauthorized modification | `TBS-ADM-INTEGRITY-001` | Critical |
| 6.3.12 | **Legal services consulted from concept stage** | `TBS-ADM-LEGAL-001` | High |
| 6.3.13–14 | **Human involvement** allowed; appropriate level of approvals obtained prior to production | `TBS-ADM-HUMAN-001`, `TBS-ADM-APPROVE-001` | Critical |

### Worked example — `TBS-ADM-PEER-001` (peer review)

```json
{
  "id": "TBS-ADM-PEER-001",
  "version": "1.0.0",
  "naturalLanguage": "The automated decision system, its AIA, and supporting documentation must be reviewed by appropriate qualified experts. The complete review or a plain-language summary must be published prior to the system entering production.",
  "severity": "Critical",
  "applicability": "Mandatory",
  "selector": { "kind": "path", "path": "$.sections[*]" },
  "predicate": "input1.category == \"governance\" || input1.category == \"compliance\"",
  "lambda": "input1.text.Contains(\"peer review\") && (input1.text.Contains(\"published\") || input1.text.Contains(\"plain-language summary\"))",
  "remediation": "Engage qualified external experts to review the ADS / AIA / documentation. Publish either the full review or a plain-language summary on the Open Government Portal before production. See TBS Guide to Peer Review of Automated Decision Systems.",
  "evidenceQuote": "Consulting the appropriate qualified experts to review the automated decision system, algorithmic impact assessment and supporting documentation, and publishing the complete review or a plain language summary prior to the automated decision system's production.",
  "sourceContent": "TBS Directive on ADM §6.3.7.",
  "metadata": { "regulator": "TBS", "directive": "Directive on ADM", "section": "6.3.7" }
}
```

---

## §6.4 — Recourse

| Rule ID | Obligation | Severity |
|---|---|---|
| `TBS-ADM-RECOURSE-001` | Clients informed of recourse options to challenge the administrative decision; recourse must be **timely, effective, and easy to access**. | Critical / Mandatory |

Lambda hint: `input1.text.Contains("recourse") && input1.text.Contains("timely") && input1.text.Contains("effective")`.

---

## §6.5 — Reporting

| Rule ID | Obligation |
|---|---|
| `TBS-ADM-REPORT-001` | Effectiveness / efficiency of the ADS in meeting program objectives published on the Open Government Portal. |
| `TBS-ADM-REPORT-002` | How the ADS is fair, transparent, and does not violate human rights / freedoms — published on the Open Government Portal. |

> 📌 §8.3.4 — agents of Parliament are *exempt* from publishing on Open Government Portal. The lambda predicate must therefore conditionalize on `input1.metadata.organizationType != "agentOfParliament"`.

---

## Appendix B — Impact assessment levels (I–IV) drive severity

The Directive uses a **four-level** impact scheme. Lambda-rag should
mirror this in `metadata.aiaLevel` on each rule and in the
`applicability`:

| AIA level | Risk profile | Lambda-rag mapping |
|---|---|---|
| **I** | Low | Most §6.3 rules become `Conditional`. Notice + AIA still `Mandatory`. |
| **II** | Moderate | All §6.1, §6.2, §6.3.1–6.3.6, §6.3.10–6.3.14 `Mandatory`. Peer review (§6.3.7) `Conditional`. |
| **III** | High | All `Mandatory`. Peer review **explicit external publication** required. |
| **IV** | Very high | All `Mandatory` + **deputy-head-level approval** (or agent-of-Parliament-head). Override permissions in §8.3.5. |

Recommended encoding: each rule's `metadata.aiaLevels` is an array
(e.g., `["II","III","IV"]`) listing levels at which the rule is
Mandatory.

---

## Appendix A — Definitions (vocabulary anchors)

These don't become standalone rules but are pulled into rule
`naturalLanguage` and `metadata.glossary`:

- **administrative decision** — decision pursuant to powers conferred by an Act of Parliament that affects legal rights, privileges, or interests.
- **automated decision system** — *any* technology that assists or replaces the judgment of human decision-makers (rules-based systems explicitly included — lambda-rag itself qualifies).
- **production** — in use with impacts on real clients (including beta).
- **proprietary** — closed-source / owned components.

---

## §8 — Application & exclusions (predicates)

| Clause | Predicate effect |
|---|---|
| 8.1 | Applies to all institutions subject to *Policy on Service and Digital*. |
| 8.2 | Other departments encouraged but not bound. → `applicability: "Conditional"` for those. |
| 8.3.1–8.3.5 | Agents of Parliament — special exemptions. → predicate `input1.metadata.organizationType != "agentOfParliament"` on §6.2.2.1, §6.5.1, §6.5.2, §7.4. |

---

## Cross-references — other instruments to surface in `metadata.crossReferences`

| Reference | Surfaced on |
|---|---|
| *Policy on Service and Digital* | §6.3.5, §6.3.6, §6.3.10 |
| *Directive on Service and Digital* | §6.2.7, §6.3.6 |
| *Directive on Privacy Practices* | §6.3.6 |
| *Directive on Security Management* | §6.3.6 |
| *Privacy Act* (R.S.C., 1985, c. P-21) | §6.3.5 |
| *Canadian Charter of Rights and Freedoms* | §6.3.3 |
| *Canadian Human Rights Act* | §6.3.3 |
| *United Nations Declaration on the Rights of Indigenous Peoples Act* | §6.3.3 |
| Algorithmic Impact Assessment tool | §6.1.1, §6.1.3 |
| Guide to Peer Review of Automated Decision Systems | §6.3.7 |

---

## Suggested authoring sequence

1. **§6.1 AIA gates** — three rules; nothing else applies until the AIA is published.
2. **§6.4 recourse** — single Critical mandatory rule.
3. **§6.2 transparency** — eight rules.
4. **§6.3 quality assurance** — thirteen rules.
5. **§6.5 reporting** — two rules.
6. **§8 conditionals** — agents-of-Parliament exemptions, *Policy on Service and Digital* applicability.

Total candidate rule count: **~30 rules**, all selectable by the
existing `gov-architecture.v1` topic map (a future `tbs-ai.v1`
would be more precise — categories like `aia`, `peer_review`,
`human_oversight`, `recourse`, `gba_plus`, `data_governance`).

---

## Test coverage hand-off

Add documents under `tests/Goldens/corpus/gov-architecture/`:

- `doc-004-ads-design-clean/` — clean ADS design hitting all §6 obligations
- `doc-005-ads-design-with-gaps/` — missing AIA publication + peer review + GBA Plus
- `doc-006-ads-level-iv/` — Level-IV system needing deputy-head approval

Each follows the `source.md` + `rationale.md` + `expected-verdict.json` shape established in [P1.8](https://github.com/MTCMarkFranco/lambda-rag/issues/18).

---

## Source attribution

*Directive on Automated Decision-Making* — Treasury Board of Canada Secretariat, Government of Canada. Date modified 2025-06-24. © His Majesty the King in Right of Canada. Quoted excerpts are reproduced under fair-dealing for the purpose of regulatory mapping; canonical text remains at [tbs-sct.canada.ca](https://www.tbs-sct.canada.ca/pol/doc-eng.aspx?id=32592).
