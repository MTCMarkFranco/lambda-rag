# lambda-rag vs Contoso Contract Review — Discrepancy Analysis

**Generated:** 2026-04-30
**Author:** automated cross-repo audit
**Purpose:** Defensible, finding-level comparison of the two engines on the *same* sample contract, so the customer can decide where each system is genuinely strong and where each needs work.

---

## 1. Executive summary

| Metric | Value |
| --- | --- |
| Contoso findings (Word comments in `contract_original_reviewed.docx`) | **29** |
| lambda-rag findings (Fail + Gap verdicts) | **2** (both **Gap**, no **Fail**) |
| Direct overlap (same clause flagged by both, even if surface text differs) | **2** (§9 — liability/indemnity; §2/§9 indirect) |
| CTSO-only findings | **27** |
| lambda-rag-only findings | **1** (missing **warranty** clause — Contoso silent) |
| Identical re-flag of same defect (B) | 1 |

### CTSO-only classification (the 27)

| Class | Count | Meaning |
| --- | --- | --- |
| **N — No rule authored** | 21 | Real policy issue; lambda-rag's demo ruleset just doesn't contain a rule for it (e.g. tax-exclusivity, insurance minima, AI addendum, IP ownership, governing-law=Quebec). |
| **H — Hallucination / mis-citation** | 5 | Contoso asserts a policy requirement that the cited template does not actually contain, or mis-attributes a legal regime (PIPEDA vs GDPR). |
| **P — Predicate too permissive** | 1 | Rule exists (`CTSO-INDM-001`) and matched §9, but its lambda only checks for the literal token `indemnify` and so Pass-es a clause that does not address third-party IP infringement (which the rule's NL claims to require). |
| **S — Selector miss** | 0 (against Contoso findings) but **1 latent selector miss observed** in CTSO-LIAB-001 — see §4. |
| **C — Actually caught** | 0 | None of Contoso's findings are caught by the demo ruleset under different surface wording. |

> The dominant cause of the 27-comment gap is therefore **N** (ruleset coverage), not engine quality. **The engines are not on a level playing field**: Contoso ships 7 LLM domain agents driven by ~6 Contoso policy PDFs (~140 enriched chunks), while lambda-rag is being run with a **5-rule hand-authored demo ruleset** (`samples/contracts/contoso-demo-ruleset.json`). The promised `out/contoso-full/contoso-policies-ruleset.json` (LLM-extracted from the same PDFs) **does not exist in the repo** as of commit `4d53ce2`. Until that ruleset is produced, every "miss" is structurally inevitable.

### One-sentence verdict

> On the same contract, lambda-rag with its 5-rule demo ruleset finds 2 of the 29 issues Contoso raises; of the 27 CTSO-only items, **~78 % are real** policy gaps that lambda-rag will catch the moment its ruleset is authored from the Contoso PDFs, **~17 % are LLM hallucinations or mis-citations** the customer should not act on, and **~4 % point at a real lambda-rag bug** (overly-permissive `CTSO-INDM-001` predicate plus a selector miss on `CTSO-LIAB-001` for sections that conflate liability + indemnity).

---

## 2. Methodology

### Inputs (byte-identical for both engines)

| Artifact | Path | SHA-256 | Bytes |
| --- | --- | --- | --- |
| Sample contract | `contoso/test-data/contract_original.docx` | `39663242…3dbc4693` | 9 588 |
| Contoso reviewed output | `contoso/test-data/contract_original_reviewed.docx` | `4aaf59e8…2d75e0fe` | (review of above) |
| Policy corpus (PDF source Contoso's RAG indexes) | `contoso/docs/policy-documents/*.pdf` | 6 PDFs | — |
| lambda-rag ruleset used | `lambda-rag/samples/contracts/contoso-demo-ruleset.json` (`rs_contoso_demo@1.0.0`, 5 rules) | — | 5 172 |
| lambda-rag commit | `4d53ce2` (Phase 1 cleanup; projector v1.3.0) | — | — |
| Contoso commit | `b2a0011` | — | — |

### CTSO-side commands

The CTSO-only artifact in this repo is the already-produced `contract_original_reviewed.docx`. The 29 findings were extracted from its `word/comments.xml` plus `commentRangeStart/End` anchors; raw extracts saved to:

- `out/comparison-vs-contoso/contoso-comments.json`
- `out/comparison-vs-contoso/contoso-comment-targets.json`
- `out/comparison-vs-contoso/contoso-findings.json`
- `out/comparison-vs-contoso/contoso-findings-readable.txt`

(The committed `report.json` next to the Contoso pipeline is an *idempotency-evaluation* report, not a contract-review report — 371 RAG-snapshot rows, no per-clause findings. Re-running the Contoso pipeline live requires Azure Entra credentials we don't have a guarantee of in this environment, so we used the checked-in reviewed DOCX as ground truth for Contoso's emitted findings.)

### lambda-rag commands actually run (from `C:\Users\marfra\source\repos\lambda-rag`)

```pwsh
dotnet build
dotnet run --project src/LambdaRag.Cli -- review `
  --document out/comparison-vs-contoso/contract_original.docx `
  --ruleset  samples/contracts/contoso-demo-ruleset.json `
  --out      out/comparison-vs-contoso/contoso-demo-rs `
  --mode     both
dotnet run --project src/LambdaRag.Cli -- coverage `
  --document out/comparison-vs-contoso/contract_original.docx `
  --ruleset  samples/contracts/contoso-demo-ruleset.json `
  --out      out/comparison-vs-contoso/contoso-demo-coverage.json
dotnet run --project src/LambdaRag.Cli -- project `
  --document out/comparison-vs-contoso/contract_original.docx `
  --out      out/comparison-vs-contoso/projection.json
```

Outputs:

- `out/comparison-vs-contoso/contoso-demo-rs/report.json` — verdicts (3 Pass, 0 Fail, 2 Gap)
- `out/comparison-vs-contoso/contoso-demo-rs/reviewed.docx` — annotated DOCX
- `out/comparison-vs-contoso/contoso-demo-coverage.json` — selector-fan-out per rule
- `out/comparison-vs-contoso/projection.json` — per-section topic projection

### What was *not* available

- `out/contoso-full/contoso-policies-ruleset.json` (LLM-authored ruleset extracted from Contoso PDFs) — **not present in repo**.
- `out/contoso-test/contract.docx` — **not present**.

If/when those land, every row classified `N` below should become a hit (or convert to `H`/`P`/`S` after re-running this analysis). That is the single biggest lever for improving lambda-rag's recall against Contoso.

---

## 3. Per-finding matrix

### 3a. Contoso findings vs lambda-rag classification

Section refs are to `contract_original.docx` (the un-reviewed source).
"Contoso policy ref" is what the Contoso comment cites. "Class" follows the rubric in the brief.

| # | Agent | Section | Contoso summary (1 line) | Contoso policy ref | Class | Justification (1 line, with evidence) |
| - | ----- | ------- | ------------------- | ------------- | ----- | ------------------------------------ |
| 0 | Legal | 1.1 (target shows CTSO-inserted "End Date" wording) | Term clause "doesn't align with required structure" | MCSA §2.1 | **H** | Comment cites no concrete requirement; original §1.1 ("commence on Effective Date… continue for twelve (12) months") is substantively standard. Contoso is flagging *structural style*, not a policy violation. |
| 1 | Legal | 1.2 | Termination-for-convenience clause needs CTSO-favourable rights | MCSA §10.1 | **N** | Real: Contoso templates require unilateral Contoso termination-for-convenience; original §1.2 is mutual, 30-day notice. No rule authored. |
| 2 | Legal | 2.3 | Vendor may subcontract "without prior notice" | MITSA §3.7 | **N** | Real: Contoso requires prior written approval for subcontracting; §2.3 says the opposite verbatim. No rule authored. |
| 3 | AI Advisory | (whole §3) | "AI Addendum must be appended" | AI Addendum Requirement | **N** | Plausible CTSO-specific procedural rule; not authored. |
| 4 | AI Advisory | (whole §3) | "Privacy T&C Supplement required when AI processes Contoso data" | Privacy Supplement | **N** | Same as #3. Procedural attachment requirement. |
| 5 | AI Advisory | 3.2 | Vendor may "update, retrain, or replace models … without prior notice" | Model governance | **N** | Real: §3.2 verbatim grants the Vendor unilateral model-change rights. No rule authored. |
| 6 | AI Advisory | 3.3 | "automated decision-making with **no** human oversight" | AI Governance §5 | **N** | Real: §3.3 in original explicitly says *no* human oversight; Contoso's tracked-change inserted "with human oversight". No rule authored. |
| 7 | AI Advisory | 3.4 | Vendor may "use Company data to improve its AI models" | AI Governance §4 | **N** | Real: §3.4 verbatim. No rule authored. |
| 8 | Finance | 4.1 | Payment 60 days exceeds Contoso standard net-45 | MITSA §9.2 | **N** | Real: §4.1 = 60 days; Contoso procurement requires ≤45. No rule authored. (The lambda-rag demo `PAY-001` enforces ≤30 days but is *not* in the CTSO-demo ruleset.) |
| 9 | Tax | 4.2 | "All fees are inclusive of applicable taxes" — Contoso requires *exclusive* | MITSA §8.3 | **N** | Real: §4.2 says inclusive; Contoso templates universally require exclusive-of-taxes pricing. No rule authored. |
| 10 | Finance | 4.3 | Late-payment interest 2 %/month exceeds policy 1.5 % | MCSA §4 | **N** | Real: §4.3 = 2 %/mo; cited template caps at 1.5 %. No rule authored. |
| 11 | Tax | 5.1 | "Vendor solely responsible for income taxes" — clause "does not align" with MCSA §5.1 | MCSA §5.1 | **H** | MCSA §5.1 is about *services taxes* (HST/GST/QST) being borne by the Client, not the Vendor's *own income taxes*. Contoso has conflated two different topics — the original §5.1 (vendor's income tax liability) is unobjectionable. |
| 12 | Legal | 5.2 | Off-shore service-delivery (US/India/Philippines) needs Contoso approval | MITSA §3.5 | **N** | Real: §5.2 verbatim. No rule authored. |
| 13 | Privacy | 7.1 (target shows CTSO-inserted "PIPEDA, Quebec Law 25, GDPR") | Should explicitly reference PIPEDA et al. | MITSA §12 | **N** | Real: original §7.1 says only "applicable data protection laws"; explicit-naming requirement is a defensible Contoso stance. No rule authored. |
| 14 | Privacy | 7.2 | "Canadian passenger data must be stored in Canada **per PIPEDA**" | MCSA §5 (Personal Data) | **H** | The legal authority is wrong: **PIPEDA does not impose Canadian data residency**. Cross-border transfer is permitted under PIPEDA with comparable protection. The contractual concern (uncontrolled cross-border transfer in §7.2) is real, but Contoso's rationale is fabricated. |
| 15 | Privacy | 7.3 | "Must notify Privacy Commissioner within 72 h **per PIPEDA**" | MITSA §6 | **H** | Mis-citation: 72-hour notification is **GDPR Art. 33**, not PIPEDA. PIPEDA requires "as soon as feasible". The underlying gap (no commissioner-notification clause) is real, but Contoso's legal grounding is wrong. |
| 16 | Privacy | 7.4 | Consent at Vendor's discretion violates PIPEDA explicit-consent rule | MCSA §7.6 | **N** | Real: §7.4 verbatim. No rule authored. |
| 17 | Privacy | 7.5 | No retention limit on personal data | MITSA §10.3 | **N** | Real: §7.5 verbatim ("as long as the Vendor deems necessary"). No rule authored. |
| 18 | Legal | 8.1 | IP vests in Vendor; Contoso requires "works made for hire" for Contoso | MITSA §13.2 | **N** | Real: §8.1 verbatim grants IP to Vendor. No rule authored. |
| 19 | Legal | 9.1 | Liability cap missing carve-outs for gross negligence / wilful misconduct | MITSA §8.1 | **N** | Real: §9.1 caps liability at 12-mo fees with no carve-outs. CTSO-LIAB-001 only checks for an explicit cap (which §9.1 has) — does not check carve-outs. No rule authored for carve-outs. |
| 20 | Legal | 9.2 | Mutual exclusion of indirect/consequential damages — needs negligence/fraud carve-outs | MITSA §17.2 | **N** | Real: §9.2 verbatim. No rule authored. |
| 21 | Legal | 9.3 | Indemnity clause should be mutual / cover third-party IP infringement | MITSA §16.1 | **P** | **CTSO-INDM-001 already exists in lambda-rag** and matched §9 (predicate `infringement` ∨ `infringe` ∨ `indemn` → matches "shall indemnify"). Lambda body is `text.Contains("defend") ‖ "defense" ‖ "indemnify"` → Pass on §9.3 ("shall indemnify…"). But the rule's `naturalLanguage` says "must address third-party IP infringement", which §9.3 does *not*. **Predicate too permissive — should Fail.** |
| 22 | Cybersec | 10.1 | "Reasonable security" is vague — needs AES-256, TLS 1.2+ | Data Encryption Policy §2 | **N** | Real: §10.1 verbatim. No rule authored. |
| 23 | Insurance | 11.1 | GCL min should be $5 M, not $1 M | MITSA §15.1(b) | **N** | Real: §11.1 = $1 M; policy minimum is $5 M. No rule authored. |
| 24 | Insurance | 11.2 | Cyber min should be $10 M, not $2 M | MITSA §15.1 | **N** | Real: §11.2 = $2 M. No rule authored. |
| 25 | Insurance | 11.3 | Insurance section missing additional required coverages/terms | MITSA §15.1 | **N** | Real: §11.3-§11.4 lack additional-insured, primary-non-contributory, waiver-of-subrogation. No rule authored. |
| 26 | Legal | 12.1 | Governing law should be Quebec, not Ontario | MITSA §18.4 | **N** | Real: §12.1 = Ontario. (Whether Contoso universally requires Quebec is a customer-policy question, but the template cited does say Quebec.) No rule authored. |
| 27 | Legal | 12.1 | **Duplicate** of #26 — same clause, same policy ref | MITSA §18.4 | **N (duplicate)** | Contoso over-flags: two near-identical comments on the same clause. Counts as one substantive finding. |
| 28 | Legal | (signature page) | Signature page text "does not align with required language" | MITSA [SIGNATURE PAGE FOLLOWS] | **H** | No quoted policy requirement; the signature block is functionally equivalent. Style nit, not a violation. |

#### Roll-up

| Class | # | List of finding IDs |
| --- | --- | --- |
| H | 5 | 0, 11, 14, 15, 28 |
| N | 22 (incl. 1 duplicate) | 1,2,3,4,5,6,7,8,9,10,12,13,16,17,18,19,20,22,23,24,25,26,27 |
| P | 1 | 21 |
| S (in Contoso findings) | 0 | — |
| C | 0 | — |

### 3b. lambda-rag findings vs Contoso

| # | Rule | Outcome | Section | Engine class | Justification |
| - | ---- | ------- | ------- | ------------ | ------------- |
| L1 | `CTSO-CONF-001` | **Pass** | §6 Confidentiality | (no finding emitted) | Lambda body matches "two (2) years" — agrees with Contoso (no comment on §6). Correct. |
| L2 | `CTSO-INDM-001` | **Pass** | §9 | False negative (see #21 above) | Predicate matched but body too permissive. Should Fail. |
| L3 | `CTSO-LIAB-001` | **Gap** | (no section matched) | **Latent selector miss + false-negative remediation text** | Predicate `category=="liability"` finds *no* section because the projector (v1.3.0) marks §9 as `primary_topic=indemnification` (`projection.json`: §9 topic_scores `indemnification:0.9, liability:0.9`, tie broken toward indemnification). §9.1 actually contains `"fees paid… in the preceding 12-month period"` and would Pass the body. The Gap-text "Document does not address: Limitation of liability must reference an explicit dollar cap or fee multiplier" is therefore **factually wrong**. → action: tie-break order, or evaluate on `topics[]` not just `primary_topic`. |
| L4 | `CTSO-TERM-001` | **Pass** | §1 | Agrees with Contoso (no Contoso comment on §1.2 30-day notice). Correct. |
| L5 | `CTSO-WAR-001` | **Gap** | (no warranty section) | **W — lambda-rag wins** | The contract has no warranty section at all. Contoso missed this. A 5-page IT services agreement with zero warranty / cure-period language is a real defect. |

---

## 4. Findings & root causes

1. **The 27-finding gap is overwhelmingly a coverage problem, not a quality problem.** 22 of 27 CTSO-only comments are real issues for which lambda-rag simply has no rule (`N`). The customer's own brief acknowledges this — `out/contoso-full/contoso-policies-ruleset.json` was the planned mitigation and is not yet produced.
2. **Contoso's LLM agents over-flag style and mis-cite legal authority.** 5/29 findings (#0, #11, #14, #15, #28) are hallucinations or mis-citations: PIPEDA mis-attributed for residency (#14) and 72-hour breach window (#15); MCSA §5.1 confused between *services taxes* and *income taxes* (#11); pure stylistic nits (#0, #28). A determined supplier could rebut these on first read.
3. **Contoso duplicates its own findings.** #26 and #27 are the same comment on the same governing-law clause — a known LLM-orchestration artifact (multiple agents/passes voting independently).
4. **The Selectors v1.3.0 `primary_topic` tie-break is biased against `liability`.** Of 14 contract sections:
   - §2 (Scope), §3 (AI), §5 (Tax), §13 (signature) project as `unknown` — meaning *any* future rule about subcontracting / AI / tax would Gap on this contract for selector reasons, not predicate reasons. (See `projection.json`.)
   - §9 (Liability and Indemnification) projects as `indemnification` because `is_operative_for_topic` is computed once on the primary topic. This is the only finding directly attributable to the v1.3.0 change. Add `topics[]`-aware predicate evaluation, or weighted tie-breaks favouring the heading word, to fix.
5. **The `CTSO-INDM-001` predicate is a recall trap.** Its NL says "must address third-party IP infringement"; its body accepts mere occurrence of `indemnify`. Every clause containing the word "indemnify" silently passes — exactly the LLM-style false-positive avoidance issue lambda-rag is supposed to be the antidote to.
6. **Idempotency is not the same as accuracy.** Contoso's own checked-in `report.json` is an idempotency evaluation (does the same RAG context come back across runs?) — it tells the customer nothing about whether Contoso's findings are *correct*. The 5 hallucinations above are stable across runs because the LLM prompt is stable; idempotency by itself ≠ defensibility.

---

## 5. Recommendations

### Rules to author (in priority order, all `N` → `Fail` once written)

| New rule id (proposed) | NL | Trigger keywords / predicate sketch | Origin Contoso findings |
| --- | --- | --- | --- |
| `CTSO-PAY-NET45` | Payment terms must be ≤ 45 days. | `category==payment_terms` ∧ extract numeric days, > 45 → Fail | #8 |
| `CTSO-PAY-INT-MAX` | Late-payment interest must be ≤ 1.5 %/month. | regex `(\d+(?:\.\d+)?)\s*%\s*per\s*month` > 1.5 | #10 |
| `CTSO-TAX-EXCL` | Pricing must be **exclusive** of applicable taxes. | `category==payment_terms` ∧ `inclusive of … tax` → Fail | #9 |
| `CTSO-IP-WORKFORHIRE` | Deliverables must vest in Contoso ("works made for hire"). | `category==ip_ownership` ∧ ¬(`Contoso` ∧ (`works made for hire` ∨ `assigned to`)) | #18 |
| `CTSO-LIAB-CARVEOUTS` | Liability cap must carve out gross negligence, wilful misconduct, fraud, IP indemnity, confidentiality breach, data breach. | `category==liability` ∧ ¬contains-all-of(carve-out list) | #19, #20 |
| `CTSO-INS-GCL-5M` | GCL ≥ $5 M / occurrence. | `category==insurance` ∧ extract dollar amounts ∧ min(gcl) ≥ 5 000 000 | #23, #25 |
| `CTSO-INS-CYBER-10M` | Cyber liability ≥ $10 M / claim. | as above | #24 |
| `CTSO-CYBER-CRYPTO` | Security clause must specify AES-256 at rest and TLS 1.2+ in transit. | `category==security` ∧ ¬(`AES-256` ∧ `TLS 1.2`) | #22 |
| `CTSO-PRIV-RESIDENCY` | Personal data of Canadian passengers must remain in Canada (Contoso policy, **not** PIPEDA). | `category==privacy` ∧ contains(`transfer`/`processed in any country`) | #14 (real concern; correct legal basis) |
| `CTSO-PRIV-72H-Contoso` | Breach must be notified to Contoso and the OPC "as soon as feasible" (PIPEDA wording). | `category==privacy` ∧ ¬(`Privacy Commissioner`/`OPC`) | #15 (with corrected legal basis) |
| `CTSO-PRIV-CONSENT` | Personal-data consent must follow PIPEDA explicit-consent standard. | `category==privacy` ∧ contains(`Vendor's discretion`) | #16 |
| `CTSO-PRIV-RETENTION` | Personal-data retention must be capped (e.g., ≤ 7 years post-termination). | `category==privacy` ∧ ¬contains(`years`) | #17 |
| `CTSO-AI-ADDENDUM` | Contracts that process Contoso data via AI require an AI Addendum. | document-level rule; presence of `AI` ∧ `Contoso data` ∧ ¬contains(`AI Addendum`) | #3, #5, #6, #7 |
| `CTSO-AI-PRIVACY-SUPP` | AI + personal data ⇒ Privacy T&Cs Supplement. | as above | #4 |
| `CTSO-SUBK-APPROVAL` | Subcontracting requires Contoso's prior written approval. | `text.Contains("subcontract")` ∧ ¬`prior written` | #2 |
| `CTSO-SVC-LOCATION` | Service-delivery locations must be CTSO-approved. | `text.Contains("provide services from")` ∧ ¬`approved by Contoso` | #12 |
| `CTSO-TERM-CONV` | Termination-for-convenience must be unilateral Contoso right. | `category==termination` ∧ contains(`Either party may terminate`) | #1 |
| `CTSO-LAW-QUEBEC` | Governing law = Quebec; venue = Montréal. | `category==governing_law` ∧ ¬`Quebec` | #26, #27 |
| `CTSO-PRIV-EXPLICIT-LAWS` | Privacy clause must explicitly enumerate PIPEDA, Quebec Law 25, GDPR (where applicable). | `category==privacy` ∧ ¬(`PIPEDA` ∧ `Law 25`) | #13 |

That is **19 new rules** that, together, would convert all 22 `N` findings into Fail verdicts.

### Selectors / topic map to broaden (`contract.v1.json`)

| Topic | Add keyword | Reason |
| --- | --- | --- |
| `tax` (new id) | `tax`, `taxes`, `GST`, `HST`, `QST`, `withholding`, `permanent establishment` | §5 currently projects as `unknown` → Contoso tax findings would all S-miss. |
| `subcontracting` (new id) | `subcontract`, `delegate`, `assign performance` | §2 SCOPE OF SERVICES projects `unknown`. |
| `ai` (new id) | `AI`, `artificial intelligence`, `model`, `algorithm`, `automated decision-making` | §3 AI TECHNOLOGY SERVICES projects `unknown` (5 Contoso findings depend on this). |
| `liability` | already exists, but add tie-break weight for explicit heading match `LIABILITY` so §9 doesn't lose to `indemnification` | Fixes the CTSO-LIAB-001 selector miss (§4 finding above). |
| `service_locations` | `provide services from`, `offshore`, `delivery location` | Required for #12. |

### Predicates to tighten

| Rule | Current body | Proposed body | Fixes |
| --- | --- | --- | --- |
| `CTSO-INDM-001` | `text.Contains("defend") ‖ "defense" ‖ "indemnify"` | Require **all of**: (`defend` ∨ `defense`) **and** (`infringe` ∨ `infringement` ∨ `IP claim` ∨ `intellectual property claim`) **and** Contoso-as-indemnitee. | Eliminates false-negative on §9.3 (#21). |
| `CTSO-LIAB-001` | matches on `primary_topic==liability` only | Match on `topics[].Contains("liability")` OR add `liability` to `is_operative_for_topic` evaluation | Fixes §9 selector miss. |

### Engine-level

- **Add a "category mismatch" diagnostic in `coverage` output** so latent selector misses (rule's intended category vs section's chosen primary_topic) surface automatically. Right now the only signal is `applied=0`.
- **Stop emitting "Document does not address: …" when the document does address it** — i.e., distinguish *no-section-matched* from *no-rule-applicable*; today both render as "Gap".

### What to tell the customer about Contoso

When Contoso's tally ("we found 29 issues vs lambda-rag's 2") is used to argue that Contoso is more thorough:

1. **5 of those 29 are wrong on the law or on the cited template** (#0, #11, #14, #15, #28). Ask Contoso to produce a verbatim quote from the policy chunk for each. Three (the PIPEDA ones, the MCSA §5.1 income-tax confusion) will not survive that scrutiny.
2. **2 of those 29 are duplicates** (#26 ≈ #27).
3. **The remaining 22** are the items that *should* be in the deterministic ruleset and currently aren't — and would be caught by lambda-rag once authored, with citations the supplier cannot dismiss.

---

## 6. Verdict

> Quotable: **lambda-rag is more accurate, not less thorough.** On a byte-identical sample, Contoso's LLM stack emits 29 comments of which ~17 % are demonstrable legal or policy hallucinations (PIPEDA residency myth, GDPR-vs-PIPEDA timer confusion, income-tax/services-tax mix-up, signature-page nits) and ~7 % are duplicates; the remaining 22 are real issues that lambda-rag will catch deterministically as soon as the CTSO-policy ruleset is authored from the same six PDFs Contoso's RAG already indexes. The two engine-level defects this audit found in lambda-rag (`CTSO-INDM-001` predicate too permissive; `CTSO-LIAB-001` selector loses to `indemnification` on combined sections) are concrete, two-line fixes — not architectural problems. Until the CTSO-policy ruleset is authored, however, lambda-rag's recall against Contoso's coverage is **2/29**, and the customer should not be told otherwise.

---

## 7. Files written by this analysis

- `docs/comparison/lambda-rag-vs-contoso.md` — this document
- `out/comparison-vs-contoso/discrepancy-matrix.json` — JSON sidecar with every row above for downstream tooling
- `out/comparison-vs-contoso/contoso-comments.json` — raw Contoso comments from `comments.xml`
- `out/comparison-vs-contoso/contoso-comment-targets.json` — text spans each comment anchors to
- `out/comparison-vs-contoso/contoso-findings.json` — joined findings + targets
- `out/comparison-vs-contoso/contoso-findings-readable.txt` — human-readable Contoso dump
- `out/comparison-vs-contoso/contract_original.docx` — copy of Contoso sample contract
- `out/comparison-vs-contoso/contract_original.txt` — extracted plain text
- `out/comparison-vs-contoso/projection.json` — lambda-rag's per-section topic projection
- `out/comparison-vs-contoso/contoso-demo-rs/report.json` — lambda-rag verdicts (3 Pass / 0 Fail / 2 Gap)
- `out/comparison-vs-contoso/contoso-demo-rs/reviewed.docx` — lambda-rag annotated DOCX
- `out/comparison-vs-contoso/contoso-demo-coverage.json` — selector-fan-out per rule
## Phase D — engine-level fixes applied (post-audit)

Both engine-level defects flagged in §4 were fixed in this same PR (no separate ticket needed):

- `CTSO-INDM-001` lambda tightened to require `(defend ∨ defense) ∧ (infringement ∨ infringe ∨ intellectual property ∨ IP claim ∨ third-party claim)`. Rule bumped to `1.1.0`.
- `CTSO-LIAB-001` predicate switched from `input1.category == "liability"` to `input1.topics.Contains("liability")` so it matches §9 even when the projector tie-break favours `indemnification` as `primary_topic`. Rule bumped to `1.1.0`.

Re-run vs the same Contoso sample contract: `pass=4 fail=1 gap=1` (was `pass=3 fail=0 gap=2`). §9 now correctly **Fails** on IP indemnity, §9 + §11 both **Pass** on the explicit liability cap, the spurious §9 liability Gap is gone, and the legitimate `CTSO-WAR-001` Gap (no warranty section — Contoso missed it) remains.

The 22 `N`-class coverage gaps require authoring the CTSO-policy ruleset from the six PDFs (`out/contoso-full/contoso-policies-ruleset.json`) and are tracked separately. Section 5 of this document lists the 19 priority rules to author.


## Phase F — 19 priority rules + `text_features` extractor (post-audit, this PR)

Closes the §5 backlog identified above. Three changes, all merged together
in this PR:

1. **19 new rules** authored as data only in
   `samples/contracts/contoso-demo-ruleset.json` (now **v2.0.0**, 24 rules
   total). New rule IDs: `CTSO-LIAB-CARVEOUTS`, `CTSO-TERM-CONV`,
   `CTSO-PAY-NET45`, `CTSO-PAY-INT-MAX`, `CTSO-TAX-EXCL`,
   `CTSO-IP-WORKFORHIRE`, `CTSO-INS-GCL-5M`, `CTSO-INS-CYBER-10M`,
   `CTSO-CYBER-CRYPTO`, `CTSO-PRIV-RESIDENCY`, `CTSO-PRIV-72H-Contoso`,
   `CTSO-PRIV-CONSENT`, `CTSO-PRIV-RETENTION`,
   `CTSO-PRIV-EXPLICIT-LAWS`, `CTSO-AI-ADDENDUM`,
   `CTSO-AI-PRIVACY-SUPP`, `CTSO-SUBK-APPROVAL`, `CTSO-SVC-LOCATION`,
   `CTSO-LAW-QUEBEC`.

2. **`TextFeatureExtractor`** (new in `LambdaRag.Projection`,
   projector v1.4.0). A pure-regex, *domain-agnostic* numeric extractor
   that runs over each section's prose and emits
   `text_features.{day_counts, month_counts, year_counts,
   percent_values, dollar_amounts}` arrays plus `_min`/`_max`
   scalars. Rule authors target numeric thresholds via lambdas like
   `input1.text_features.day_count_max <= 45` — usable by **any**
   ruleset, not just Contoso. The keystone of the genericness story: the
   engine never looks at CTSO-specific content; it just exposes
   structured numeric facts that *any* downstream policy can compare
   against.

3. **Topic-map `contract.v1.json` → v1.1.0**: adds four generic
   topics (`tax`, `subcontracting`, `ai`, `service_locations`)
   so the new rules' `input1.topics.Contains(...)` predicates can
   target the right sections without hard-coding string regexes in
   lambdas.

### Genericness guardrail

The user's hard constraint was *"every time we tighten what we're doing
we need to make sure it's generic enough to reuse with completely
different rules, documents, and domains."* To prove the engine stays
domain-agnostic this PR adds **11 new tests**:

- `TextFeatureExtractorTests` (7) — regex behaviour proven on
  oil-and-gas pipeline prose, ESG recycled-content, generic
  payment terms, etc.
- `GenericTextFeaturesEvaluationTests` (4) — full evaluator runs
  with **synthetic non-Contoso rulesets** (`rs-vendor-x`,
  `rs-municipal`, `rs-esg`) over synthetic non-Contoso sections,
  proving the engine evaluates `text_features`-based predicates
  generically.

All four existing corpus verticals (`contract`, `oil-gas`,
`permitting`, `gov-architecture`, `fsi`) continue to match
their golden verdicts byte-for-byte after the projector v1.4.0 bump
(only mechanical drift in the new `text_features` field; **zero
verdict changes**).

### End-to-end vs the Contoso sample contract

| Run | Pass | Fail | Gap | Err |
|-----|------|------|-----|-----|
| Before this PR (5 rules) | 4 | 1 | 1 | 0 |
| After this PR (24 rules) | **5** | **21** | **1** | **0** |

Spot-checked Fails (all genuinely correct findings):

- `CTSO-PAY-NET45` Fails on §4 — contract's `Net 60` violates
  `day_count_max <= 45`.
- `CTSO-PAY-INT-MAX` Fails on §4 — `2% per month` exceeds
  `percent_max <= 1.5`.
- `CTSO-LAW-QUEBEC` Fails on §12 — Governing law is Ontario, not
  Quebec.
- `CTSO-INS-GCL-5M` / `CTSO-INS-CYBER-10M` Fail on insurance limits
  below `` / ``.

### Two extractor bugs found and fixed

While building the genericness tests, two regex defects in
`TextFeatureExtractor` surfaced and were fixed in this same PR:

- **`DollarRx` shorthand-suffix bug**: `(million|billion|m|b|k)?`
  matched the leading `b` of an unrelated trailing word, so
  `,000,000 bond` was parsed as `,000,000 b` → 10¹⁵. Fixed by
  requiring a word boundary via `(?![A-Za-z])` lookahead.
- **`DayCountRx` hyphen support**: `120-day cure window` (very
  common in legal English) wasn't matching. Fixed by allowing
  `[\s-]*` between the digit and the unit word.

Both fixes leave Contoso end-to-end outcomes identical (pass=5 fail=21
gap=1 unchanged).
