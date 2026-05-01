# OSFI E-23 — *Enterprise-Wide Model Risk Management* — Lambda-RAG Mapping

> **Status:** Phase 1 / [P1.2](https://github.com/MTCMarkFranco/lambda-rag/issues/12) — clause-by-clause mapping doc.
>
> **Source:** [OSFI Guideline E-23, *Enterprise-Wide Model Risk Management for Deposit-Taking Institutions*](https://www.osfi-bsif.gc.ca/en/guidance/guidance-library/enterprise-wide-model-risk-management-deposit-taking-institutions) (Office of the Superintendent of Financial Institutions). Crown copyright. Quoted excerpts are used for the purpose of regulatory mapping under fair-dealing.
>
> **Audience:** authoring-time SMEs converting E-23 into a `RuleSet` for federally-regulated Canadian deposit-taking institutions (banks, federally regulated trust and loan companies, cooperative retail associations).
>
> **Disclaimer:** This is a *technical mapping document*, not legal advice. Lambda-rag does not certify E-23 compliance — it produces an auditable, deterministic verdict against the rules an institution authored. The institution remains accountable for rule fidelity and for OSFI engagement. See [`docs/what-lambda-rag-is-not.md`](../what-lambda-rag-is-not.md).

---

## How to read this doc

Each row maps an E-23 clause to a candidate lambda-rag rule:

| Field | Meaning |
|---|---|
| **Clause** | E-23 section / paragraph reference |
| **Obligation** | What the regulated institution must show |
| **Suggested rule ID** | Stable ID for use in `ruleset.json` |
| **Topic map** | Which projection the rule selects against (`fsi.v1` for E-23) |
| **Selector** | What sections of a candidate doc to evaluate |
| **Lambda** | Pure-code predicate evaluated by Microsoft RulesEngine |
| **Severity / Applicability** | `Critical` / `High` / `Medium` / `Low` × `Mandatory` / `Conditional` (proportional to IMAI vs SI) |
| **Evidence** | What text in the candidate doc satisfies the rule |

The same rule shape used by the corpus tests under [`tests/Goldens/corpus/fsi/`](../../tests/Goldens/corpus/fsi/ruleset.json) applies here — see that file for the canonical wire format.

---

## §1 — Introduction (scope, IMAI vs SI, proportionality)

### §1.1 Scope of application

> *"This Guideline outlines OSFI's expectations for the establishment of an enterprise-wide model risk management framework at institutions [...] regulatory capital models, internal risk management models, valuation/pricing models (including those used for accounting purposes), business decision-making models for risk management (such as credit adjudication and scoring models), and stress testing models."*

| Field | Mapping |
|---|---|
| Rule ID | `E23-SCOPE-001` |
| Obligation | The MRM framework must explicitly state it covers **all** model classes: regulatory capital, internal risk, valuation/pricing, business decision-making, stress testing. |
| Topic map | `fsi.v1` → category `model_risk_governance` |
| Selector | `$.sections[*]` where `category == "model_risk_governance"` |
| Lambda (sketch) | `input1.text.Contains("regulatory capital") && input1.text.Contains("valuation") && input1.text.Contains("pricing") && input1.text.Contains("decision") && input1.text.Contains("stress")` |
| Severity / Applicability | `Critical` / `Mandatory` |
| Evidence | The framework's *Scope* section enumerates the five model classes. |

### §1.2 IMAI vs SI distinction (proportionality)

> *"OSFI will distinguish between internal models approved institutions (IMAIs) and other standardized institutions (SIs)."*

| Field | Mapping |
|---|---|
| Rule ID | `E23-PROPORTIONALITY-001` |
| Obligation | Framework must state which cohort the institution is in (IMAI or SI), and which provisions apply by reference. |
| Severity / Applicability | `High` / `Mandatory` |
| Lambda hint | `(input1.text.Contains("IMAI") || input1.text.Contains("internal models approved")) || input1.text.Contains("standardized institution")` |

---

## §2 — Definitions

E-23 supplies six normative definitions. Lambda-rag rules treat these as *vocabulary anchors*, not standalone obligations — they get pulled in by reference from §4–§9 rules.

| Term | Mapped to |
|---|---|
| Model | `Rule.metadata.term = "model"` |
| Model risk | Drives `severity` mapping in any rule |
| Model user / developer / owner / reviewer / approver | `appliesToSchema.required` for any §4–§5 rule that needs role attribution |

> 📌 **Pattern note:** definitions never become Critical rules. They appear in `metadata.glossary` and are referenced from rule `naturalLanguage` fields.

---

## §3 — Scope and Key Characteristics (7 framework characteristics)

E-23 §3 enumerates seven characteristics the framework must exhibit. Each becomes its own rule because they are independently testable and independently failable.

| # | Characteristic | Rule ID | Severity | Selector category |
|---|---|---|---|---|
| 1 | Appropriate and commensurate **governance** systems over model usage | `E23-GOV-001` | Critical | `governance` |
| 2 | Model **materiality** classifications and limitations | `E23-MAT-001` | Critical | `model_materiality` |
| 3 | Policies/processes around **model selection and development** | `E23-DEV-001` | High | `model_development` |
| 4 | Independent **vetting and ongoing validation/review** | `E23-VAL-001` | Critical | `model_validation` |
| 5 | **Change control** processes covering each lifecycle stage | `E23-CHG-001` | High | `change_control` |
| 6 | **Internal audit** functions (independent third-line) | `E23-AUDIT-001` | High | `internal_audit` |
| 7 | **Model inventory** (catalogue of all models since inception) | `E23-INV-001` | Critical | `model_inventory` |

Each rule has the same shape: `lambda` checks for the named obligation in the `model_risk_governance`-category section text and falls back to `Gap` if no candidate section is found. Worked example below for #1.

### §3 example — `E23-GOV-001` (governance characteristic)

```json
{
  "id": "E23-GOV-001",
  "version": "1.0.0",
  "naturalLanguage": "The MRM framework must describe an appropriate and commensurate governance system over model usage, including roles, responsibilities, and reporting lines for the model owner, model reviewer, and model approver.",
  "severity": "Critical",
  "applicability": "Mandatory",
  "appliesToSchema": {
    "type": "object",
    "properties": {
      "category": { "type": "string" },
      "text": { "type": "string" }
    },
    "required": ["category", "text"]
  },
  "selector": { "kind": "path", "path": "$.sections[*]" },
  "predicate": "input1.category == \"governance\"",
  "lambda": "input1.text.Contains(\"model owner\") && input1.text.Contains(\"model reviewer\") && input1.text.Contains(\"model approver\")",
  "remediation": "Document the three model-risk roles (owner, reviewer, approver) and their reporting lines per OSFI E-23 §3 and §4.",
  "evidenceQuote": "Senior Management should implement an appropriate model risk materiality classification scheme...",
  "sourceContent": "OSFI E-23 §3 (key characteristic #1) and §4 (Model Risk Framework).",
  "metadata": {
    "regulator": "OSFI",
    "guideline": "E-23",
    "section": "3",
    "sourceUrl": "https://www.osfi-bsif.gc.ca/en/guidance/guidance-library/enterprise-wide-model-risk-management-deposit-taking-institutions"
  }
}
```

---

## §4 — Model Risk Framework

### §4 (general) — three-lines-of-defence governance

> *"In order to ensure effective control over model risk it is important that the governance structure vests internal approval and oversight authority primarily with parties who are independent from individuals with a direct stake [...]"*

| Field | Mapping |
|---|---|
| Rule ID | `E23-3LOD-001` |
| Obligation | Framework explicitly identifies first / second / third line and asserts independence between them. |
| Severity / Applicability | `Critical` / `Mandatory` |
| Lambda hint | `(input1.text.Contains("first line") && input1.text.Contains("second line") && input1.text.Contains("third line")) || input1.text.Contains("three lines of defence")` |

### §4.1 — Model risk materiality

> *"Senior Management should implement an appropriate model risk materiality classification scheme [...] Ideally, an institution should design a system that is capable of ranking the level of risk posed by each of the models used."*

| Field | Mapping |
|---|---|
| Rule ID | `E23-MAT-002` |
| Obligation | Framework must include a documented materiality classification scheme, with both quantitative and qualitative inputs (where institution sophistication permits). |
| Severity | `Critical` |
| Triggers (from footnote) | "changes in underlying business environment; increases in size or scope of a business line; deterioration in model performance; material model modifications" → all four must be enumerated as re-assessment triggers. |
| Lambda hint | `input1.text.Contains("materiality") && (input1.text.Contains("ranking") || input1.text.Contains("classification scheme"))` |

---

## §5 — Model management cycle (six lifecycle phases)

E-23 §5 enumerates six lifecycle phases. **Each phase becomes a Critical mandatory rule for IMAIs, Conditional for SIs.**

| Phase | Rule ID | Section ref |
|---|---|---|
| §5.1 — Rationale for modeling | `E23-LC-RATIONALE-001` | "[F]irst line of defence business area should identify an economic or business rationale" |
| §5.2 — Model development | `E23-LC-DEV-001` | "the determination of suitable data ... methodology ... programming ... formatting of outputs" |
| §5.3 — Independent review (vetting) | `E23-LC-VET-001` | "Verification and assessment ... Secondary review (conceptual soundness, sensitivity testing)" |
| §5.4 — Approval | `E23-LC-APPROVE-001` | "should not [...] be approved for operational use without first undergoing an independent review" |
| §5.5 — Ongoing monitoring (validation) | `E23-LC-VAL-001` | "annually for models that exhibit the highest degree of model risk" |
| §5.6 — Modifications and decommission | `E23-LC-MOD-001` | "When such a modification is undertaken, institutions should apply the same level of rigour" |

### §5.5 — sub-rule: exceptions and escalations

> *"For models that pose material levels of model risk, institutions should have policies and processes in place to manage model exceptions [...] escalation processes in place so that the model risk committee and/or Senior Management are promptly made aware."*

| Field | Mapping |
|---|---|
| Rule ID | `E23-EXC-001` |
| Severity | `High` |
| Applicability | `Mandatory` for material models, `Conditional` otherwise |
| Lambda hint | `input1.text.Contains("exception") && input1.text.Contains("escalation")` |

### §5.5 — sub-rule: testing techniques

> *"backtesting, discriminatory analysis, stress-testing, sensitivity analysis"*

| Field | Mapping |
|---|---|
| Rule ID | `E23-TEST-001` |
| Severity | `High` |
| Lambda hint | `input1.text.Contains("backtest") && input1.text.Contains("stress") && input1.text.Contains("sensitivity")` |
| Note | Discriminatory analysis is conditional on credit/scoring models. Add `predicate` filter on model class. |

### §5.6 — sub-rule: change-control authorization

> *"No individuals should have the authority to change a model or model use without re-approval of the changed model or use."*

| Field | Mapping |
|---|---|
| Rule ID | `E23-CHG-002` |
| Severity | `Critical` |
| Lambda hint | `input1.text.Contains("re-approval") || (input1.text.Contains("change control") && input1.text.Contains("authoriz"))` |

---

## §6 — Vendor products

> *"Aside from outsourcing the model development phase, adopting a vendor product does not eliminate the need to apply a similar process for vetting, approval, ongoing validation, decommissioning and overall documentation."*

| Field | Mapping |
|---|---|
| Rule ID | `E23-VENDOR-001` |
| Severity / Applicability | `Critical` / `Conditional` (only when vendor models are used) |
| Topic map | `fsi.v1` → `vendor_models` |
| Lambda hint | `input1.text.Contains("vendor") && input1.text.Contains("validation") && input1.text.Contains("documentation")` |
| Cross-ref | OSFI Guideline B-10 (Outsourcing) — already covered in [`tests/Goldens/corpus/fsi/`](../../tests/Goldens/corpus/fsi/) |

### §6 — sub-rule: contingency plan for vendor inadequacy

| Field | Mapping |
|---|---|
| Rule ID | `E23-VENDOR-002` |
| Severity | `High` |
| Lambda hint | `input1.text.Contains("contingency") && input1.text.Contains("vendor")` |

---

## §7 — Foreign bank subsidiaries

| Field | Mapping |
|---|---|
| Rule ID | `E23-FBS-001` |
| Severity | `High` |
| Applicability | `Conditional` — applies only when the institution is a foreign bank subsidiary |
| Predicate | `input1.metadata.institutionType == "foreign_bank_subsidiary"` |
| Lambda hint | `input1.text.Contains("parent") && input1.text.Contains("technical documentation")` |

---

## §8 — Internal audit (third line of defence)

E-23 §8 specifies three audit obligations. **All three are Critical mandatory.**

| # | Obligation | Rule ID |
|---|---|---|
| 1 | Policy existence — model approval, modification, decommission processes are documented; change-control authorizations specified | `E23-AUDIT-002` |
| 2 | Policy adherence — validation work is sufficiently independent and on-schedule; exception/escalation record matches policy | `E23-AUDIT-003` |
| 3 | Documentation — consistency and completeness of model inventory records | `E23-AUDIT-004` |

### §8 example — `E23-AUDIT-002`

```json
{
  "id": "E23-AUDIT-002",
  "version": "1.0.0",
  "naturalLanguage": "Internal audit must confirm that documented model approval, modification, and decommission processes exist, and that change-control authorizations are clearly specified.",
  "severity": "Critical",
  "applicability": "Mandatory",
  "selector": { "kind": "path", "path": "$.sections[*]" },
  "predicate": "input1.category == \"internal_audit\"",
  "lambda": "input1.text.Contains(\"approval\") && input1.text.Contains(\"modification\") && input1.text.Contains(\"decommission\") && input1.text.Contains(\"change control\")",
  "remediation": "Internal audit charter must explicitly cover all four MRM lifecycle events.",
  "evidenceQuote": "...confirm there are model approval, modification and decommission processes...",
  "sourceContent": "OSFI E-23 §8 (Internal Audit), bullet 1.",
  "metadata": { "regulator": "OSFI", "guideline": "E-23", "section": "8" }
}
```

---

## §9 — Model inventory (10 mandatory components)

E-23 §9 lists ten components a model inventory entry must contain. Each becomes a sub-rule.

| # | Component | Rule ID |
|---|---|---|
| 1 | Model name and key features | `E23-INV-002` |
| 2 | Risk ranking and materiality assessment | `E23-INV-003` |
| 3 | Owner / developer | `E23-INV-004` |
| 4 | Data type and sources | `E23-INV-005` |
| 5 | Approved products and business lines | `E23-INV-006` |
| 6 | Vetting / validation report references + deficiencies and limitations | `E23-INV-007` |
| 7 | Inception date, approval date, exception history | `E23-INV-008` |
| 8 | Material modification summary | `E23-INV-009` |
| 9 | Outcomes analysis references (e.g., backtesting) | `E23-INV-010` |
| 10 | Internal audit findings references | `E23-INV-011` |

> 📌 **Authoring note:** these ten map cleanly onto a JSON Schema `required` array on the `appliesToSchema` of `E23-INV-001` (the parent inventory rule). Use one rule per row above to surface a missing component as its own line item in the verdict report — institutions need the granularity for remediation.

---

## Appendix A — references to other OSFI guidelines

E-23 Appendix A pulls in seven other OSFI references. **These are *cross-reference* obligations, not standalone rules.** They surface as `metadata.crossReferences` on the relevant lifecycle / model-class rules:

| Cross-ref | Surfaced on rules |
|---|---|
| Capital Adequacy Requirements (CAR) Ch. 6 — IRB | All credit-risk model rules |
| CAR Ch. 7 — Securitisation SFA | Securitisation model rules |
| CAR Ch. 4 — CCR / CVA | Counterparty credit rules |
| CAR Ch. 8 — Operational risk | Operational risk model rules |
| CAR Ch. 9 — Market risk (VaR, IRC, comprehensive risk) | Market risk model rules |
| Guideline B-12 — IRRBB | IRRBB model rules |
| Guideline E-19 — ICAAP | ICAAP-related model rules |
| Guideline E-22 — Margin requirements | Initial margin internal model rules |

Add `metadata.crossReferences` as an array of `{guideline, section, url}` entries on each affected rule.

---

## Suggested authoring sequence

For an institution converting E-23 into a `RuleSet`:

1. **Start with §3's seven characteristics** — these are the framework-level checks every MRM document must pass.
2. **Layer §5's six lifecycle phases** — these are the per-model checks.
3. **Add §9's ten inventory fields** — these are the per-inventory-row checks.
4. **Conditional rules** for §6 (vendor), §7 (foreign subsidiary), §1.2 (IMAI).
5. **Cross-references** to other OSFI guidelines as `metadata.crossReferences` only.

Total candidate rule count: **~30 rules** (7 + 6 lifecycle + 10 inventory + 7 cross-cutting).

This puts E-23 firmly in the "non-trivial but tractable" bucket — well within the size lambda-rag's `RuleSet` schema is designed for, and an excellent fit for the `fsi.v1` topic map already shipped.

---

## Test coverage hand-off

The `tests/Goldens/corpus/fsi/` corpus already covers OSFI Guideline B-10. To extend coverage to E-23, add documents under `tests/Goldens/corpus/fsi/`:

- `doc-003-mrm-framework-clean/` — clean MRM framework hitting all §3 characteristics
- `doc-004-mrm-framework-with-gaps/` — missing internal audit + missing inventory fields
- `doc-005-vendor-model-policy/` — exercises §6 vendor rules

Each follows the same `source.md` + `rationale.md` + `expected-verdict.json` shape used in [P1.8](https://github.com/MTCMarkFranco/lambda-rag/issues/18).

---

## Source attribution

OSFI Guideline E-23 — *Enterprise-Wide Model Risk Management for Deposit-Taking Institutions*, Office of the Superintendent of Financial Institutions, Government of Canada. © His Majesty the King in Right of Canada. Quoted excerpts are reproduced under fair-dealing for the purpose of regulatory mapping; canonical text remains at the [OSFI guidance library](https://www.osfi-bsif.gc.ca/en/guidance/guidance-library).
