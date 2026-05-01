# Bill C-27 / AIDA — Lambda-RAG Mapping

> ## ⚠️ Volatility notice — read first
>
> The **Artificial Intelligence and Data Act (AIDA)** is **Part 3 of Bill C-27**
> (*Digital Charter Implementation Act, 2022*). At the time of writing, **Bill C-27
> has not been enacted into law.** It died on the Order Paper when the 44th
> Parliament was prorogued and subsequently dissolved. Any future Canadian federal
> AI statute may differ materially from AIDA — different scope, different
> definitions, different substantive obligations, different enforcement regime.
>
> **What this document is:** a clause-by-clause structural mapping of AIDA *as
> introduced* (First Reading text), with rule sketches that follow the same
> shape as the OSFI E-23 and TBS ADM mappings in this folder. It is intended as
> a **starting point** for whatever Canadian federal AI statute eventually
> passes — many of the structural obligations (impact assessments, mitigation
> measures, monitoring, harm notification, record-keeping, public description)
> appear in similar form in EU AI Act, Colorado AI Act, and the TBS Directive
> already in force. Re-mapping when the actual statute clears Royal Assent will
> be a *focused diff*, not a rewrite.
>
> **What this document is not:** legal advice; a current statement of Canadian
> AI law (none exists at the federal level beyond the TBS Directive — see
> [`tbs-adm-mapping.md`](tbs-adm-mapping.md)); a guarantee any of these clause
> numbers will survive into a future bill.

> **Status:** Phase 1 / [P1.3](https://github.com/MTCMarkFranco/lambda-rag/issues/13).
>
> **Source:** [Bill C-27, *Digital Charter Implementation Act, 2022* — First Reading](https://www.parl.ca/DocumentViewer/en/44-1/bill/C-27/first-reading), 44th Parliament, 1st Session. Crown copyright. Quoted excerpts are reproduced under fair-dealing for the purpose of regulatory mapping.
>
> **Audience:** organizations preparing for Canadian federal AI regulation; reviewers comparing AIDA to the EU AI Act / Colorado AI Act / TBS Directive on ADM.

---

## Why this mapping is still worth doing

Even with AIDA's uncertain legislative status, the substantive
**concepts** are stable across the global AI-governance landscape:

| AIDA clause | EU AI Act analogue | Colorado AI Act analogue | TBS Directive analogue |
|---|---|---|---|
| s.7 — high-impact system assessment | Art. 9 (risk-management system) | Sec. 6-1-1703 (risk management policy) | §6.1 (AIA) |
| s.8 — risk mitigation measures | Art. 9, 10, 14, 15 | Sec. 6-1-1703 | §6.3.1, §6.3.2 |
| s.9 — monitoring of mitigation measures | Art. 17, 72 (post-market monitoring) | Sec. 6-1-1704 | §6.3.2 |
| s.10 — record-keeping | Art. 12, Annex IV | Sec. 6-1-1703 | §6.2.7 |
| s.11 — public description | Art. 13 (transparency) | Sec. 6-1-1705 | §6.2.1–6.2.3 |
| s.12 — material-harm notification | Art. 73 (serious-incident reporting) | Sec. 6-1-1704(3) | (no direct analogue) |

So a `RuleSet` authored against the AIDA structure is **mostly portable**
to whichever statute eventually ships, with selector / lambda detail
adjusted to the specific clause language.

---

## How to read this doc

Each row maps an AIDA section to a candidate lambda-rag rule:

| Field | Meaning |
|---|---|
| **Section** | AIDA section reference (First Reading) |
| **Obligation** | What a regulated *person* / *operator* must show |
| **Suggested rule ID** | Stable ID for use in `ruleset.json` |
| **Topic map** | `gov-architecture.v1` (closest existing); a future `aida.v1` would be more precise |
| **Severity / Applicability** | `Mandatory` for high-impact systems; `Conditional` otherwise |

The same rule shape used by the corpus tests under [`tests/Goldens/corpus/`](../../tests/Goldens/corpus/) applies. See [`osfi-e23-mapping.md`](osfi-e23-mapping.md) §3 example for canonical wire format.

---

## Definitions (s.2, s.5) — vocabulary anchors

Key terms drive predicate filters; they don't become standalone rules:

| Term | Effect on rules |
|---|---|
| **Artificial intelligence system** — *technological system that, autonomously or partly autonomously, processes data related to human activities through the use of a genetic algorithm, a neural network, machine learning or another technique in order to generate content or make decisions, recommendations or predictions* | Rule applicability requires the system meet this definition. |
| **High-impact system** — *defined by regulation* (currently empty; the *Companion document* released by ISED in 2023 listed candidate categories: employment, services to individuals, biometric ID, content moderation, healthcare, courts, law enforcement, critical infrastructure) | Most §6–§12 obligations apply *only* to high-impact systems. |
| **Person responsible** — anyone who designs, develops, makes available for use, or manages the operations of an AI system | The duty-bearer in every §6–§12 rule. |
| **Harm** — *physical or psychological harm to an individual; damage to an individual's property; or economic loss to an individual* | Drives `severity` mapping in s.12 / s.39 rules. |

> 📌 **Predicate template:** `input1.metadata.systemClass == "high-impact"` should
> guard most §6–§12 rules. Otherwise the rule should evaluate to N/A.

---

## §4 — Purposes (framework anchors)

| Rule ID | Obligation | Severity |
|---|---|---|
| `AIDA-PURPOSE-001` | Design / governance documentation must state the system's role in trade and commerce + measures consistent with national and international AI standards. | High / Mandatory |

Lambda hint: `input1.text.Contains("national and international standards") || input1.text.Contains("trade and commerce")`.

---

## §6 — Anonymized data

> *"A person who carries out any regulated activity and who processes or makes available for use anonymized data in the course of that activity must, in accordance with the regulations, establish measures with respect to (a) the manner in which data is anonymized; and (b) the use or management of anonymized data."*

| Rule ID | Obligation | Severity |
|---|---|---|
| `AIDA-ANON-001` | Documented measures for *how* data is anonymized (technique, validation, residual-risk threshold). | Critical / Mandatory |
| `AIDA-ANON-002` | Documented measures for *use and management* of anonymized data (access controls, re-identification testing, retention). | Critical / Mandatory |

Lambda hint: `input1.text.Contains("anonymiz") && (input1.text.Contains("technique") || input1.text.Contains("retention") || input1.text.Contains("re-identification"))`.

---

## §7 — Assessment of high-impact systems

> *"A person who is responsible for an artificial intelligence system must, in accordance with the regulations, assess whether it is a high-impact system."*

| Rule ID | Obligation | Severity |
|---|---|---|
| `AIDA-IMPACT-001` | Documented impact assessment determining whether the system is high-impact. Must follow regulatory criteria when issued. | Critical / Mandatory |
| `AIDA-IMPACT-002` | If high-impact: criteria, evidence, and reasoning recorded. | Critical / Mandatory |

> 📌 Until s.7 regulations are issued, lambda-rag rules should require *some*
> documented impact methodology — TBS AIA, EU AI Act Annex III mapping, ISO/IEC
> 23894, NIST AI RMF — and accept any of them as evidence.

---

## §8 — Measures related to risks

> *"A person who is responsible for a high-impact system must, in accordance with the regulations, establish measures to identify, assess and mitigate the risks of harm or biased output that could result from the use of the system."*

This is the **substantive heart** of AIDA. Lambda-rag should split this into three rules tracking the three verbs:

| # | Verb | Rule ID | Severity |
|---|---|---|---|
| 1 | **Identify** risks of harm + biased output | `AIDA-RISK-IDENTIFY-001` | Critical |
| 2 | **Assess** risks of harm + biased output | `AIDA-RISK-ASSESS-001` | Critical |
| 3 | **Mitigate** risks of harm + biased output | `AIDA-RISK-MITIGATE-001` | Critical |

### Worked example — `AIDA-RISK-MITIGATE-001`

```json
{
  "id": "AIDA-RISK-MITIGATE-001",
  "version": "1.0.0",
  "naturalLanguage": "A person responsible for a high-impact AI system must establish measures to MITIGATE the risks of harm or biased output that could result from the use of the system.",
  "severity": "Critical",
  "applicability": "Mandatory",
  "appliesToSchema": {
    "type": "object",
    "properties": { "category": { "type": "string" }, "text": { "type": "string" } },
    "required": ["category", "text"]
  },
  "selector": { "kind": "path", "path": "$.sections[*]" },
  "predicate": "input1.category == \"governance\" || input1.category == \"compliance\"",
  "lambda": "(input1.text.Contains(\"mitigation\") || input1.text.Contains(\"mitigate\")) && (input1.text.Contains(\"harm\") || input1.text.Contains(\"bias\"))",
  "remediation": "Document concrete mitigation measures for each identified harm and bias category. AIDA s.8 anticipates that regulations will prescribe minimum mitigation standards; until those issue, evidence-based controls aligned to NIST AI RMF Manage, ISO/IEC 23894 §6.5, or EU AI Act Art. 9 are acceptable.",
  "evidenceQuote": "must establish measures to identify, assess and mitigate the risks of harm or biased output",
  "sourceContent": "Bill C-27 (44-1) Part 3 — Artificial Intelligence and Data Act, s.8.",
  "metadata": {
    "regulator": "ISED Canada",
    "statute": "Artificial Intelligence and Data Act (Bill C-27, Part 3)",
    "section": "8",
    "status": "draft / not in force",
    "sourceUrl": "https://www.parl.ca/DocumentViewer/en/44-1/bill/C-27/first-reading"
  }
}
```

---

## §9 — Monitoring of mitigation measures

> *"A person who is responsible for a high-impact system must, in accordance with the regulations, establish measures to monitor compliance with the mitigation measures that they are required to establish under section 8 and the effectiveness of those mitigation measures."*

| Rule ID | Obligation | Severity |
|---|---|---|
| `AIDA-MONITOR-001` | Monitor *compliance* with the mitigation measures. | Critical / Mandatory |
| `AIDA-MONITOR-002` | Monitor *effectiveness* of the mitigation measures. | Critical / Mandatory |

> 📌 The split-the-verbs pattern matters. Audits routinely find systems where
> compliance is monitored but effectiveness is not, or vice versa.

---

## §10 — Keeping general records

> *"A person who carries out any regulated activity must, in accordance with the regulations, keep records describing in general terms* (a) for anonymized data — *the way it is anonymized and use*; (b) for high-impact systems — *the reasons supporting the high-impact assessment, the mitigation measures established, and any other information related to the system that is provided for by regulation."*

| Rule ID | Obligation | Severity |
|---|---|---|
| `AIDA-RECORDS-001` | Records describe the anonymization manner and use (cross-ref s.6). | High / Mandatory |
| `AIDA-RECORDS-002` | Records describe the impact-assessment reasoning (cross-ref s.7). | Critical / Mandatory |
| `AIDA-RECORDS-003` | Records describe the mitigation measures (cross-ref s.8). | Critical / Mandatory |

---

## §11 — Publication of description

> *"A person who is responsible for a high-impact system must, in accordance with the regulations, publish on a publicly available website a plain-language description of the system that explains (a) how the system is intended to be used; (b) the types of content that it is intended to generate and the decisions, recommendations or predictions that it is intended to make; (c) the mitigation measures established under section 8; and (d) any other information that may be prescribed by regulation."*

This is the **public-transparency rule** — four mandatory sub-clauses:

| # | Required content | Rule ID |
|---|---|---|
| (a) | Intended use | `AIDA-PUB-001` |
| (b) | Intended outputs (content / decisions / recommendations / predictions) | `AIDA-PUB-002` |
| (c) | Mitigation measures (cross-ref s.8) | `AIDA-PUB-003` |
| (d) | Anything else prescribed by regulation | `AIDA-PUB-004` |

Severity: `Critical` / `Mandatory` for high-impact systems. All four must
be present in a *plain-language*, *publicly available* description.
Lambda predicate should check that the document references a public URL,
not just internal documentation.

---

## §12 — Notification of material harm

> *"A person who is responsible for a high-impact system must, as soon as feasible, notify the Minister if the use of the system results or is likely to result in material harm."*

| Rule ID | Obligation | Severity |
|---|---|---|
| `AIDA-NOTIFY-001` | Documented incident-response procedure that triggers ministerial notification on material harm. | Critical / Mandatory |
| `AIDA-NOTIFY-002` | "As soon as feasible" timeline target stated in the procedure (organization's own SLA, since AIDA does not give a numeric deadline). | High / Mandatory |

Lambda hint: `input1.text.Contains("Minister") && (input1.text.Contains("notif") || input1.text.Contains("incident"))`.

---

## §13–§28 — Ministerial orders and information regime

These are **enforcement mechanics**, not substantive obligations on the
operator. They become rules only if the operator is *responding to* an
order. Recommended treatment: leave out of the default `RuleSet` and add
on demand for incident-response playbooks.

---

## §29 — Administrative monetary penalties (AMP)

| Rule ID | Obligation | Severity |
|---|---|---|
| `AIDA-AMP-AWARENESS-001` | Governance documentation acknowledges AMP regime + max amounts (to be set by regulation). | Medium / Conditional |

Not a substantive obligation; included for completeness for governance docs that need to enumerate consequences.

---

## §30, §38, §39 — Offences

| Section | Offence | Rule ID | Severity |
|---|---|---|---|
| s.30 | Contravention of s.6–s.12 | `AIDA-OFF-001` | (no rule — derivative) |
| s.38 | Possession or use of personal information unlawfully obtained for the purpose of designing / developing / using / making available an AI system | `AIDA-OFF-PI-001` | Critical / Mandatory |
| s.39 | Making available a system whose use causes serious harm to individuals (with mens rea: knowledge / recklessness) | `AIDA-OFF-HARM-001` | Critical / Mandatory |

Lambda for `AIDA-OFF-PI-001`: `input1.text.Contains("personal information") && input1.text.Contains("lawfully obtained")` (positive proof required).

---

## Application carve-outs (s.3)

> *"This Act does not apply with respect to a government institution as defined in section 3 of the Privacy Act."*

Predicate guard for *all* rules: `input1.metadata.organizationType != "federalGovernmentInstitution"`. Federal departments are governed by the [TBS Directive on ADM](tbs-adm-mapping.md) instead.

---

## Suggested authoring sequence

1. **§7 impact assessment gate** — nothing else applies until the system is classified.
2. **§8 risk identify / assess / mitigate** — three Critical mandatory rules.
3. **§9 monitoring** — two Critical mandatory rules.
4. **§10 records** — three rules cross-referencing §6–§8.
5. **§11 public description** — four sub-clauses.
6. **§12 harm notification** — two rules.
7. **§6 anonymized data** — two rules (only when anonymization is in scope).
8. **§38–§39 offences** — two rules acting as guardrails.

Total candidate rule count: **~20 rules** for the substantive AIDA core, plus optional governance / AMP-awareness rules.

---

## Test coverage hand-off

Once an actual statute is enacted, add documents under
`tests/Goldens/corpus/` in a new vertical (e.g.,
`tests/Goldens/corpus/ai-act/`). Until then, the same `RuleSet` schema
can be unit-tested standalone — the regulations don't need to be in
force for the rule shape to be valid.

Suggested initial corpus once enacted:

- `doc-001-high-impact-system-clean/` — clean AI system documentation hitting all §6–§12 obligations
- `doc-002-high-impact-system-with-gaps/` — missing s.11 public description + missing s.12 notification procedure
- `doc-003-anonymization-policy/` — exercises §6 rules

---

## Source attribution & legal status disclaimer

Bill C-27, *An Act to enact the Consumer Privacy Protection Act, the Personal Information and Data Protection Tribunal Act and the Artificial Intelligence and Data Act and to make consequential and related amendments to other Acts*, 44th Parliament, 1st Session — First Reading. Government of Canada. © His Majesty the King in Right of Canada. Quoted excerpts are reproduced under fair-dealing.

**Legal status as of authoring:** Bill C-27 is **not in force**. It died on the Order Paper following prorogation of the 44th Parliament. This mapping is **prospective** and will require revision when (or if) a Canadian federal AI statute receives Royal Assent. Track `parl.ca` and `ised-isde.canada.ca` for the actual enacted text.
