# Quebec Law 25 / Loi 25 — Lambda-RAG Mapping (EN)

> **Status:** Phase 1 / [P1.4](https://github.com/MTCMarkFranco/lambda-rag/issues/14).
> Loi 25 is **in force** in Quebec — unlike Bill C-27 / AIDA. This is therefore a
> **current-law** mapping, not a prospective one. SME engagement is *pending*
> (see [§ SME reviewers](#sme-reviewer-recommendations-pending-engagement)).
>
> **Sources (public, no paywalls):** [LégisQuébec — Act respecting the protection
> of personal information in the private sector (P-39.1)](https://www.legisquebec.gouv.qc.ca/en/document/cs/p-39.1)
> · [LégisQuébec — Act respecting access to documents held by public bodies
> and the protection of personal information (A-2.1)](https://www.legisquebec.gouv.qc.ca/en/document/cs/a-2.1)
> · [Commission d'accès à l'information du Québec (CAI)](https://www.cai.gouv.qc.ca/) —
> guidance, bulletins, decisions · [Loi 25 (2021, c.25)](https://www.publicationsduquebec.gouv.qc.ca/fileadmin/Fichiers_client/lois_et_reglements/LoisAnnuelles/fr/2021/2021C25F.PDF) (consolidating amendment).
> Quoted excerpts are reproduced under fair-dealing for the purpose of
> regulatory mapping. Full primary text is consolidated to **2026-03-31**
> via the public `legisquebec.gouv.qc.ca` consolidation chain.
>
> **Audience:** organizations subject to Loi 25 (any "person carrying on an
> enterprise" in Quebec, plus "public bodies" subject to A-2.1); reviewers
> comparing Loi 25 to PIPEDA / Bill C-27 / GDPR.
>
> **Companion docs:** [`loi-25-mapping.fr.md`](loi-25-mapping.fr.md) (full
> French mirror, with FR text primary); [`bill-c27-aida-mapping.md`](bill-c27-aida-mapping.md);
> [`osfi-e23-mapping.md`](osfi-e23-mapping.md); [`tbs-adm-mapping.md`](tbs-adm-mapping.md).
>
> **Ruleset:** [`samples/contracts/loi-25-ruleset.json`](../../samples/contracts/loi-25-ruleset.json) — 25 hand-authored `QC-LOI25-*` rules.

---

## Why this mapping matters

Loi 25 is the strictest Canadian privacy statute currently in force. For Quebec
financial services (Desjardins, National Bank, iA Financial Group, SSQ),
public bodies (ministères, RAMQ, Hydro-Québec, municipalities), and any
business processing the personal information of Quebec residents, Loi 25
obligations now drive contract clauses, vendor due diligence, and product
design. Several Loi 25 obligations — privacy by default (art. 9.1),
automated-decision human review (art. 12.1), TIA before cross-border transfer
(art. 17), administrative monetary penalties up to **\$10M or 2 % of worldwide
turnover** (penal up to **\$25M or 4 %**) — are stricter than PIPEDA and either
match or exceed CPPA / GDPR.

A bilingual lambda-rag ruleset gives Quebec organizations (a) a deterministic
gap report against contracts / DPAs / privacy policies, and (b) a French-first
review experience that Anglophone-only tools cannot replicate.

## How to read this doc

Each row maps a Loi 25 article to a candidate `QC-LOI25-*` rule shipped in
[`samples/contracts/loi-25-ruleset.json`](../../samples/contracts/loi-25-ruleset.json):

| Field | Meaning |
|---|---|
| **Article** | Statutory citation — `P-39.1 art. X` (private sector) or `A-2.1 art. X` (public sector) |
| **Obligation** | What the regulated entity must show |
| **Rule ID** | Stable `QC-LOI25-*` ID in the ruleset JSON |
| **Severity** | `Critical` (clear legal violation; AMP risk) / `Violation` (definite non-compliance) / `Deviation` (operational artifact missing) / `Suggestion` (hardening) |
| **Reviewer** | `qc-privacy` (private sector) or `qc-public-sector` (A-2.1 specific) |

The same wire format used by [`ac-demo-ruleset.json`](../../samples/contracts/ac-demo-ruleset.json)
applies. Each rule carries a French one-liner via `metadata.naturalLanguageFr`
(the engine treats metadata as opaque key/value strings — adding the field
required no engine change).

---

## Definitions / vocabulary anchors (FR + EN)

These terms drive predicate filters; they don't become standalone rules.

| French (canonical) | English | Statutory anchor |
|---|---|---|
| **Renseignement personnel** | Personal information — any information that, *seul ou en combinaison avec d'autres*, identifies a natural person | P-39.1 art. 2 ; A-2.1 art. 54 |
| **Renseignement personnel sensible** | Sensitive personal information — information whose use, communication, or destruction reveals intimate / medical / financial / biometric facts giving rise to a heightened expectation of privacy | P-39.1 art. 12 ¶3 |
| **Responsable de la protection des renseignements personnels (RPRP)** | Person in charge of the protection of personal information / Privacy Officer / DPO | P-39.1 art. 3.1 ; A-2.1 art. 8 |
| **Évaluation des facteurs relatifs à la vie privée (ÉFVP)** | Privacy Impact Assessment (PIA) | P-39.1 art. 3.3 ; A-2.1 art. 63.5 |
| **Décision fondée exclusivement sur un traitement automatisé** | Decision based exclusively on automated processing | P-39.1 art. 12.1 |
| **Profilage** | Profiling — collection and use of PI to assess characteristics of a natural person (work, economic situation, health, preferences, behaviour) | P-39.1 art. 8.1 ¶2 |
| **Incident de confidentialité** | Confidentiality incident — access, use, communication, or loss of PI not authorized by law, *or* any other breach of PI protection | P-39.1 art. 3.6 ; A-2.1 art. 63.8 |
| **Consentement manifeste, libre, éclairé, donné à des fins spécifiques** | Manifest, free, informed consent given for specific purposes | P-39.1 art. 14 |
| **Désindexation / Cessation de la diffusion** | De-indexing / cessation of dissemination | P-39.1 art. 28.1 |
| **Portabilité** | Portability — right to obtain computerized PI in a structured, commonly used format | P-39.1 art. 27 |
| **Anonymisation** | Anonymization — irreversibly preventing identification per generally-recognized practices and the regulation criteria (distinct from de-identification) | P-39.1 art. 23 ¶2 |
| **Commission d'accès à l'information (CAI)** | Quebec's privacy regulator and adjudicator | Statute-wide |
| **Sanction administrative pécuniaire (SAP)** | Administrative monetary penalty (AMP) | P-39.1 arts. 90.1–90.13 |

> 📌 **Predicate template:** for a Quebec-only rule, gate on `input1.topics.Contains("privacy")`
> plus a Quebec / Loi 25 / French-keyword filter, e.g.
> `input1.text.Contains("Quebec") || input1.text.Contains("Québec") || input1.text.Contains("Loi 25") || input1.text.Contains("P-39.1")`.
> No engine change required.

---

## Effective-dates timeline

Loi 25 was assented to on **2021-09-22** but its provisions came into force
in three staged tranches:

| Date | Tranche | Highlights |
|---|---|---|
| **2022-09-22** | Phase 1 | RPRP designation (art. 3.1) ; confidentiality-incident response, register, and breach notification to CAI + individuals (arts. 3.5–3.8 ; A-2.1 arts. 63.8–63.11). |
| **2023-09-22** | Phase 2 | Governance framework + publication (art. 3.2) ; ÉFVP for new IT projects (art. 3.3) ; cross-border transfer assessment (art. 17) ; profiling notice (art. 8.1) ; privacy-policy publication (art. 8.2) ; privacy-by-default (art. 9.1) ; granular consent (art. 14) ; automated-decision disclosure + human review (art. 12.1) ; biometric-bank disclosure to CAI ≥ 60 days (LCCJTI art. 45). |
| **2024-09-22** | Phase 3 | Right to data portability (art. 27) ; right to cessation of dissemination / de-indexing (art. 28.1) ; full activation of the AMP regime. |

CAI's enforcement powers and AMP sanctions are fully active as of **2024-09-22**.

---

## Clause → rule table

### Private-sector Act (P-39.1)

| Article | Obligation | Rule ID | Severity |
|---|---|---|---|
| **art. 3.1** | Designate and publish RPRP / Privacy Officer with title and contact info | `QC-LOI25-DPO-001` | Critical |
| **art. 3.1 ¶2** | Written delegation if RPRP is not the highest-ranking person | `QC-LOI25-DPO-002` | Deviation |
| **art. 3.2** | Establish + publish PI governance framework (roles, retention, complaints, training) | `QC-LOI25-GOV-001` | Violation |
| **art. 3.3** | Conduct ÉFVP / PIA for new IT projects involving PI | `QC-LOI25-PIA-001` | Violation |
| **arts. 3.5–3.7** | Written incident-response procedure (risk assessment, mitigation, CAI + individual notification on serious-injury risk) | `QC-LOI25-INC-PROC-001` | Critical |
| **art. 3.8** | Maintain confidentiality-incident register; share copy with CAI on request | `QC-LOI25-INC-REG-001` | Violation |
| **art. 8 ¶2** | Inform data subject if PI may be communicated outside Quebec | `QC-LOI25-XBORDER-NOTICE-001` | Violation |
| **art. 8.1** | Notice + deactivation mechanism for identification / location / profiling tech | `QC-LOI25-PROFILE-001` | Critical |
| **art. 8.2** | Publish a plain-language privacy policy on the public website | `QC-LOI25-POLICY-PUB-001` | Violation |
| **art. 9.1** | Privacy by default (highest privacy settings, no user action) | `QC-LOI25-DEFAULT-001` | Violation |
| **art. 11** | Retain decision-supporting employee records ≥ 1 year after the decision | `QC-LOI25-HR-RETENTION-001` | Deviation |
| **art. 12.1 ¶1** | Inform data subject of fully-automated decisions at the time of communication | `QC-LOI25-AUTODEC-001` | Critical |
| **art. 12.1 ¶2** | On request, disclose PI used + principal factors and parameters + rectification right | `QC-LOI25-AUTODEC-002` | Critical |
| **art. 12.1 ¶3** | Provide opportunity to submit observations to a personnel member able to review the decision | `QC-LOI25-AUTODEC-003` | Critical |
| **art. 14** | Manifest, free, informed, purpose-specific, separately-presented consent | `QC-LOI25-CONSENT-001` | Critical |
| **art. 14 ¶3** | Parental / tutor consent for minors under 14 | `QC-LOI25-CONSENT-MINOR-001` | Critical |
| **art. 17** | Privacy Impact Assessment + written agreement before transferring PI outside Quebec | `QC-LOI25-XBORDER-001` | Critical |
| **art. 18 ¶ derogations** | Log PI disclosures made without consent under statutory exceptions | `QC-LOI25-DISCLOSE-LOG-001` | Deviation |
| **art. 23** | Destroy or anonymize PI once collection purposes are achieved | `QC-LOI25-RETENTION-001` | Violation |
| **art. 27** | Provide computerized PI to data subject in structured, commonly used format on request (portability) | `QC-LOI25-PORTABILITY-001` | Violation |
| **art. 28.1** | Procedure to handle cessation-of-dissemination, de-indexing, or re-indexing requests | `QC-LOI25-DEINDEX-001` | Violation |

### Public-sector Act (A-2.1)

| Article | Obligation | Rule ID | Severity |
|---|---|---|---|
| **art. 8.1** | Form an access and privacy committee | `QC-LOI25-PUB-COMMITTEE-001` | Violation |
| **arts. 67.3–67.4** | Maintain and provide public access to a register of PI communications under arts. 67.1 / 67.2.1 | `QC-LOI25-PUB-COMMS-REG-001` | Violation |

### LCCJTI cross-reference (biometrics)

| Article | Obligation | Rule ID | Severity |
|---|---|---|---|
| **LCCJTI art. 45** | Disclose any biometric database to CAI ≥ 60 days before deployment | `QC-LOI25-BIOMETRIC-CAI-001` | Critical |

### Cross-references in the vendor / DPA layer

| Trigger | Obligation | Rule ID | Severity |
|---|---|---|---|
| **arts. 3.5, 3.6, 17 (private) ; art. 67.2 (public)** | Vendor / DPA agreements impose Loi 25–equivalent obligations + prompt incident notification | `QC-LOI25-VENDOR-DPA-001` | Violation |

---

## Worked example — `QC-LOI25-AUTODEC-002`

```json
{
  "id": "QC-LOI25-AUTODEC-002",
  "version": "1.0.0",
  "naturalLanguage": "On request, disclose the personal information used, the principal factors and parameters that led to the decision, and the data subject's right to have inaccurate input data corrected.",
  "predicate": "(input1.topics.Contains(\"ai\") || input1.topics.Contains(\"privacy\")) && (input1.text.Contains(\"automat\") || input1.text.Contains(\"algorithm\") || input1.text.Contains(\"algorithme\"))",
  "lambda": "(input1.text.Contains(\"factors\") || input1.text.Contains(\"facteurs\") || input1.text.Contains(\"parameters\") || input1.text.Contains(\"paramètres\") || input1.text.Contains(\"principal\") || input1.text.Contains(\"principaux\")) && (input1.text.Contains(\"correct\") || input1.text.Contains(\"rectif\") || input1.text.Contains(\"update\") || input1.text.Contains(\"mettre à jour\"))",
  "severity": "Critical",
  "evidenceQuote": "Elle doit aussi, à la demande de la personne concernée, l'informer : 1° des renseignements personnels utilisés pour rendre la décision ; 2° des raisons et des principaux facteurs et paramètres ayant mené à la décision ; 3° du droit de la personne concernée de faire rectifier les renseignements personnels utilisés pour rendre la décision.",
  "metadata": {
    "reviewer": "qc-privacy",
    "lawReference": "P-39.1 art. 12.1 ¶2",
    "naturalLanguageFr": "À la demande, divulguer les renseignements personnels utilisés, les principaux facteurs et paramètres de la décision et le droit de rectification."
  }
}
```

---

## Comparisons table — Loi 25 vs PIPEDA / CPPA-AIDA / GDPR / Loi A-2.1

| Theme | Loi 25 (P-39.1) | PIPEDA | CPPA / AIDA (Bill C-27, draft) | GDPR | Loi A-2.1 (public) |
|---|---|---|---|---|---|
| Privacy Officer / DPO | **Mandatory + public contact info** (art. 3.1) | Accountability principle; not specifically mandated by name/contact | Required (CPPA) | DPO required when Art. 37 thresholds met | Mandatory; CAI-notified |
| Governance framework | **Mandatory + published** (art. 3.2) | Implied | Required (CPPA) | Art. 24/30 records of processing | Required, regulation-driven |
| PIA / DPIA | **Mandatory** for new IT projects + cross-border transfers (arts. 3.3, 17) | Not mandatory | High-risk only (CPPA); high-impact AI (AIDA) | DPIA when Art. 35 risk applies | Required + committee consulted (art. 63.5) |
| Privacy by default | **Explicit** (art. 9.1) | Not explicit | Not explicit | Art. 25 (broader, less specific) | Mirror in art. 63.7 |
| Consent | **Manifest, free, informed, purpose-specific, separately presented** (art. 14) | Knowledge + consent | Expanded business-activity exceptions (CPPA) | Art. 6/7 — consent or other legal bases | Mostly statutory authority; consent residual |
| Minors | **< 14: parental/tutor consent** | Age-neutral | Sensitivity attaches to all minors | Art. 8 — Member-State age (13–16) | Same age-14 cap as P-39.1 |
| Automated decisions | **Notice at decision time + factors + human review** (art. 12.1) | None | Human-review right (CPPA) | Art. 22 — right not to be subject + safeguards | No direct analogue |
| Cross-border transfers | **TIA + written agreement** (art. 17) | Comparable-protection accountability | Not explicit | Chapter V (SCCs / adequacy) | Mirror in art. 70.1 |
| Breach notification | CAI + individuals on **risk of serious injury** (art. 3.5) | Real risk of significant harm (PIPEDA s.10.1) | Same | Art. 33/34 (72-hour clock) | Mirror in arts. 63.8–63.10 |
| Incident register | **Mandatory; copy to CAI on request** (art. 3.8) | Not specified | Not specified | Art. 33 ¶5 internal records | Mirror in art. 63.11 |
| Portability | **Mandatory** (art. 27 — since 2024-09-22) | Not present | Present (CPPA) | Art. 20 | Mirror via art. 84 modifications |
| De-indexing / cessation | **Mandatory** (art. 28.1 — since 2024-09-22) | Not present | Not present | Art. 17 (broader erasure) | Mirror in A-2.1 modifications |
| AMPs (max) | **\$10M or 2 % WW turnover**; penal **\$25M or 4 %** | n/a (FCA can issue compliance orders) | CPPA: 3 % WW turnover (AMP) / 5 % (penal) | 4 % WW turnover | Same as P-39.1 |
| Biometrics-specific | **CAI prior disclosure ≥ 60 days** (LCCJTI art. 45) | None | None | Art. 9 (special category) | Same |

---

## Effective-dates → AMP-severity mapping

The ruleset's `severity` field is grounded in CAI's enforcement framework:

| Severity | Use when… | Example rule |
|---|---|---|
| **Critical** | Hard violation likely to attract an AMP or penal sanction (no DPO, no consent, profiling without notice, automated decision without disclosure, transfer outside Quebec without TIA) | `QC-LOI25-DPO-001`, `QC-LOI25-CONSENT-001`, `QC-LOI25-AUTODEC-001`, `QC-LOI25-XBORDER-001` |
| **Violation** | Required disclosure / publication is missing | `QC-LOI25-POLICY-PUB-001`, `QC-LOI25-PORTABILITY-001`, `QC-LOI25-DEINDEX-001` |
| **Deviation** | Operational artifact (register, written delegation, retention schedule) is missing | `QC-LOI25-INC-REG-001`, `QC-LOI25-DPO-002`, `QC-LOI25-DISCLOSE-LOG-001` |
| **Suggestion** | Hardening recommended above the floor (none in v1.0.0; reserved for forthcoming Loi 25 amendments) | — |

---

## Pointer to the ruleset JSON

Run a review against the bundled ruleset:

```pwsh
dotnet run --project src/LambdaRag.Cli -- review `
  --document path/to/your/contract-or-policy.docx `
  --ruleset  samples/contracts/loi-25-ruleset.json `
  --out      out/loi-25 `
  --mode     both
```

The output `report.json` cites the rule ID, the matching section, the
French + English natural-language statement, the law reference, and the
remediation. `reviewed.docx` carries tracked changes + comments anchored to
the offending clauses.

---

## Ambiguities + open questions

The Researcher pack identified several areas where Loi 25 doctrine is still
settling:

1. **Cross-border TIA depth.** P-39.1 art. 17 requires evaluating the foreign
   legal regime, but the CAI has not published a model TIA or a destination-
   country adequacy list. Practice in Quebec FSI converges on a written
   questionnaire + Standard-Contractual-Clauses-style addendum, but expect
   variance until CAI publishes formal guidance.
2. **AMP scaling.** Phase-3 AMP powers became active 2024-09-22. As of the
   2026-03-31 consolidation, only a handful of public AMP decisions exist (the
   largest in the ~CAD 7 000 range plus a CAD 15 000 penal fine). The size
   distribution at scale is not yet observable; severity classifications in
   this ruleset will be revisited once CAI publishes a multi-year enforcement
   summary.
3. **"Profiling" scope.** Art. 8.1 covers identification, location, and
   profiling. CAI's 2023 bulletins and webinar content treat cookies and
   site-analytics as in-scope; FSI practice has converged on cookie-banner
   updates. The line between "analytics" and "profiling" is not fully
   stable.
4. **Anonymization threshold.** Art. 23 ¶2 requires irreversible non-
   identification per "*pratiques généralement reconnues et critères prévus
   par règlement*". The implementing regulation came into force in 2024 but
   leaves several sector-specific thresholds open. Conservative practice
   treats anonymized data as still-personal until a formal third-party
   attestation is in place.
5. **De-indexing criteria balancing.** Art. 28.1's public-interest balancing
   test (inaccurate / outdated / serious harm vs ongoing public interest) is
   newly active (2024-09-22). Expect significant CAI clarifications and case
   law over 2025–2027.
6. **Interaction with federal CPPA.** If/when CPPA passes, several Loi 25
   provisions (consent, breach notification) will be substantially similar
   but Quebec retains primacy under the "substantially similar" exemption
   procedure. Cross-border (Quebec → ROC) data flows may need a dual-regime
   analysis until the federal regime stabilizes.

---

## SME reviewer recommendations *(pending engagement)*

The Researcher pack surfaced several public-domain experts whose published
work covers Loi 25 in depth. These are **suggestions only — none have been
engaged at the time of authoring**. Engagement is tracked under
[issue #14 follow-ups](https://github.com/MTCMarkFranco/lambda-rag/issues/14).

| Reviewer | Affiliation (public role) | Why |
|---|---|---|
| **Me Antoine Aylwin** | Partner, Fasken — privacy and access-to-information practice | Public-facing Loi 25 commentary, member of CAI working groups |
| **Me Charles Morgan** | Partner, McCarthy Tétrault — National Cyber/Data Group co-chair | Published cross-border TIA practice notes for Quebec FSI |
| **Me Patrick Cormier** | DPO Canada / privacy training community | Maintains widely-cited public Loi 25 readiness curricula in FR + EN |
| **Pr. Karim Benyekhlef** | Université de Montréal, CRDP — director, Cyberjustice Laboratory | Academic depth on automated-decision and privacy-by-design constructs |
| **Mme Diane Poitras** | *(former)* President, CAI | Former regulator perspective on enforcement framing (post-mandate; consult published statements only) |

> 📌 **Engagement protocol:** before any reviewer attribution lands in
> a published version of this document, secure written confirmation. Until
> then, the names above stand as **public-source signposts**, not citations.

---

## Test coverage hand-off

The Loi 25 ruleset is exercised by:

- **`QuebecLaw25RulesetParserTests`** — parses `loi-25-ruleset.json`, asserts
  schema validity, rule count ≥ 20, every rule carries
  `metadata.naturalLanguageFr` + `metadata.lawReference` + a non-empty
  `evidenceQuote`.
- **`GenericQuebecRuleEvaluationTests`** — synthesizes a non-Quebec document
  (a generic vendor MSA) and a Quebec-relevant document, runs the QC-LOI25
  ruleset against both, and asserts (a) the engine produces verdicts
  identically to any other ruleset (no Quebec-specific code path) and (b)
  Quebec-keyword-gated rules trigger only on the Quebec-relevant document.

These two tests are the **genericity guard**: any future change that
hardcodes Quebec behaviour in `src/` will fail them.

---

## Source attribution & legal status disclaimer

LégisQuébec, *Loi sur la protection des renseignements personnels dans le
secteur privé* (P-39.1) and *Loi sur l'accès aux documents des organismes
publics et sur la protection des renseignements personnels* (A-2.1), as
amended by the *Act to modernize legislative provisions as regards the
protection of personal information* (Loi 25, 2021 c.25). © Government of
Quebec. Quoted excerpts are reproduced under fair-dealing.

**Legal status as of authoring:** Loi 25 is fully **in force**. The
mapping reflects the LégisQuébec consolidation as of **2026-03-31**.
Reviewers should re-validate against the current LégisQuébec consolidation
before relying on this document for compliance decisions.

This document is **not legal advice**. It is a structural mapping intended
to bootstrap a deterministic ruleset; engage qualified Quebec counsel for
binding interpretation.
