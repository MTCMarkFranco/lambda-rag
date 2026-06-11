# Lambda-RAG Accuracy Improvement Plan

> Goal: close the **44-point accuracy gap** vs LLM-only (14.3% → ≥ LLM 58.3%, target ≥ 75%)
> on the CTC PSA review, **without sacrificing** determinism or idempotency.
> Branch when work starts: `branch-lambda-accuracy-1` (per workflow rule, do NOT push to master).

---

## 1. Root-cause read of the experiment

The 14.3% vs 58.3% gap is **not an engine bug**. Lambda-RAG executed the rules it was
given, exactly as designed. The mismatch lives in 4 places, in order of impact:

| # | Failure mode | Evidence in this experiment | Where it lives in the code |
|---|---|---|---|
| **A** | **Wrong rule profile for the artifact.** Rules CTC-3195-000CONF-001 and -055PAY-001 are *contract clauses* applied to a *PSA architecture doc*. | 3 of 9 verdicts are contract-style false negatives. | `rulesets/architecture-review/ctc-arb.json` was extracted but never gated by doc kind |
| **B** | **Predicate doesn't fire → N/A inflation.** `predicate: input1.category == "confidentiality"` requires the projector to label sections "confidentiality". The PSA has *no* such sections, so every selected section is gated out and the rule emits N/A instead of a real verdict. | 2 of 9 are `NOT APPLICABLE` (Backup/Restore, Encryption-at-Rest) even though the PSA covers both topics under different headings. | `DeterministicContractProjector` uses `contract.v1` topic map even for ARB docs |
| **C** | **Lambdas are keyword soup.** `Contains("year")` passed on "yearly basis" → a *false positive*. Conversely, real RTO/RPO content phrased as "4 hour recovery objective" misses on `"rpo"`. | The 1 PASS is a false positive; the 2 N/As hide real evidence. | `Rule.Lambda` strings are RulesEngine bool expressions over raw text |
| **D** | **Coverage is sparse.** 6 rules cannot adjudicate a 12-dimension PSA. Anything outside those 6 is silently absent from the report. | LLM judged 12 dimensions; rules judged 6 (and 5 of those badly). | `ctc-arb.json` was authored once from the CTC policy PDF and never expanded |

Determinism is fine. **Coverage + classification + matching are the problem.**

---

## 2. Design principles (non-negotiable, lifted from existing `docs/manifesto.md`)

1. **No LLM at runtime in the decision loop.** Same bytes → same verdict, forever.
2. **LLM is allowed only during authoring**, with temp=0, JSON-schema-validated, signed,
   version-locked, and human-reviewed. (Already enforced by `LambdaRag.Authoring.*`.)
3. **Every verdict cites evidence** (charStart/charLength/pageNumber + quote). New rules
   must keep this contract.
4. **Auditability over cleverness.** A rule that "feels smart" but a regulator cannot
   walk in 10 minutes is a regression.

---

## 3. The 5-pillar plan

### Pillar 1 — Doc-kind classifier + ruleset profile gating  *(fixes failure A)*

**Symptom:** PSA artifact got contract rules.

**Change:**
- Add `doc_kind` resolution **before** projection, with this precedence:
  1. Explicit CLI flag / API param (`--doc-kind arb-psa`)
  2. Filename/path heuristic (`samples/architecture/**` → `arb-psa`)
  3. Heading-bigram classifier over the first 3 pages (deterministic, signed dictionary, **no LLM**)
- Extend `RuleSet` JSON with `appliesToDocKinds: ["arb-psa"]` (already loosely modeled
  via `domain`; promote to a first-class field on the rule too so a single ruleset
  can mix doc-kinds when intentional).
- Engine skips rules whose `appliesToDocKinds` does not intersect the resolved kind.
  Skipped rules emit a single `Skipped(reason="doc_kind_mismatch")` verdict so the
  audit trail still cites them — never silently dropped.

**Determinism cost:** zero. Classifier is a signed lookup table.
**Accuracy lift estimate:** kills the 3 contract-style false negatives outright.

---

### Pillar 2 — ARB-PSA topic map + projector profile  *(fixes failure B)*

**Symptom:** PSA sections never get classified into the categories rules expect, so
predicates fail and rules emit N/A.

**Change:**
- Author `src/LambdaRag.Projection/TopicMaps/arb-psa.v1.json` covering the 12
  dimensions the LLM evaluated (PSA completeness, architecture constraints,
  architecture risks, decision records, technology standards, design patterns,
  data security, integrations, infrastructure architecture, security architecture,
  information governance, DR & resiliency).
- Each topic maps **both** a primary heading pattern *and* a content-keyword fallback.
- Register the topic map via `TopicMapRegistry.Load("arb-psa.v1")` and wire it through
  the projector when `doc_kind = arb-psa`.
- Add a `--projection-cache-key` that includes `topic_map_id@version` so changing the
  topic map invalidates cached projections (the cache already keys on bytes + projector
  id+version; this just makes the topic map a first-class part of the key).

**Determinism cost:** zero. Topic map is JSON, fingerprinted, version-locked.
**Accuracy lift estimate:** eliminates both N/A verdicts and unlocks the
predicate gates so the remaining 6 rules actually run.

---

### Pillar 3 — Semantic predicates with bounded, signed evidence  *(fixes failure C)*

**Symptom:** `Contains("year")` matches "yearly basis"; `Contains("rpo")` misses
"recovery point objective". Pure-string lambdas are too brittle for prose.

**Change:** introduce **three** new lambda primitives, all pure-code, all auditable:

| Primitive | Replaces | Determinism contract |
|---|---|---|
| `Regex(input1.text, "(?i)\\b(?:rpo\\|recovery\\s+point\\s+objective)\\b")` | Single-keyword `Contains` | Pinned regex is part of the rule fingerprint; same text → same match |
| `Phrase(input1.text, ruleset.phrasebooks.dr_rpo)` | OR-soup of synonyms inside the lambda | Phrasebook lives in the signed ruleset, not the lambda → easier diffing |
| `SemanticMatchCached(input1.text, ruleset.rules["X"].sourceEmbedding, threshold=0.78)` | Catches semantic intent when phrasing varies | **Embeddings are precomputed at authoring time** (already supported by `RuleSetEmbedder` + `SemanticVectorStoreSnapshot`). At runtime: cosine compare against frozen vector. Threshold is part of the rule, byte-identical across runs. |

The third primitive is the key one — it lets us say "the section talks about *DR
RPO* even if the wording differs" without ever calling an LLM at runtime. The
authoring pipeline already produces `sourceEmbedding` on every rule (see
`ctc-arb.json` line 48+); we just need to actually use it in evaluation.

**Determinism cost:** zero **if** we freeze the embedding model id + version in the
ruleset header (already part of `RuleSetEmbedder` design) and refuse to evaluate
when the configured model doesn't match. Add a startup check + a unit test.
**Accuracy lift estimate:** kills the "yearly basis" false positive and the
"4 hour recovery objective" false negative.

---

### Pillar 4 — Coverage expansion to match the 12-dim PSA rubric  *(fixes failure D)*

**Symptom:** 6 rules cannot cover what the LLM judged on 12 dimensions.

**Change:** use the existing offline authoring pipeline
(`LambdaRag.Authoring.ExtractFunction` + `IRuleAuthoringAgent`) to extract a
**second ruleset** specifically for ARB-PSA scope, gated to `appliesToDocKinds:
["arb-psa"]`. Target ~25–30 rules covering:

- Section-presence rules (12, one per dim): "Section *Architecture Risks* MUST be present and non-template."
- Quality-floor rules (8): minimum bytes / required sub-fields per ARB-2 template
  (e.g. each risk row needs severity + mitigation + owner).
- Standards-alignment rules (5–6): NIST CSF / PCI / MVS named-reference checks.
- DR & integration depth rules (3–5): semantic-match RTO/RPO/failover + integration
  pattern references.

Authoring follows the workflow we already use everywhere else: prompt contracts first
(per user preference, see memory), GitHub issue per rule cluster, human review, signed
publish.

**Determinism cost:** zero at runtime; cost is paid in authoring.
**Accuracy lift estimate:** this is the biggest single lever — gets us from 6
adjudicated dimensions to 12.

---

### Pillar 5 — "Completeness vs Template" detector  *(matches LLM's strongest signal)*

**Symptom:** LLM's strongest discriminator was *"section contains placeholder
text like 'To be completed…'"*. Rules engine has no equivalent.

**Change:** add a deterministic `IsTemplateBoilerplate(input1.text)` lambda primitive
backed by a signed phrase list (`"To be completed", "TBD", "[insert", "Lorem ipsum",
"<placeholder>", "INSERT TEXT HERE", "FILL IN", "to be defined"`).
Use it inside the section-presence rules: a section that exists but is >80%
boilerplate → FAIL, not PASS.

**Determinism cost:** zero. Phrase list is in the signed ruleset.
**Accuracy lift estimate:** turns the LLM's qualitative judgement into a
reproducible test.

---

## 4. Validation / acceptance gates

Each pillar ships with a **golden-master test** under `tests/`:

1. **Re-run the exact CTC PSA case** as a baseline test. Target:
   - ≥ 8/12 PASS where the LLM also passed (recall ≥ 67%)
   - 0 false positives on the LLM's clear FAILs (precision = 100% on the FAIL set)
   - Same input → byte-identical `report.json` across 100 runs (existing
     idempotency harness)
2. **Determinism CI gate:** 35 existing idempotency / golden-master proofs must
   stay green. New primitives get their own determinism tests (regex pinning,
   embedding model pinning, phrasebook pinning).
3. **Audit-trail gate:** every new rule must emit `evidenceQuote` + `sourceSpan`.
   Rule-self-validator (`RuleSelfValidator`) is extended to reject rules that don't.
4. **Profile-gating gate:** running the ARB-PSA ruleset against a contract sample
   must produce all `Skipped(doc_kind_mismatch)` verdicts and a top-level
   `wrong_profile=true` flag — never partial false data.

---

## 5. Sequencing  *(no dates, per repo convention)*

Issues will be created tightly-scoped and grouped (per user workflow rule).
Suggested grouping:

| Issue group | Pillar(s) | Notes |
|---|---|---|
| **Group 1 — Doc kind** | 1 | Classifier + ruleset `appliesToDocKinds` + Skipped verdict. Smallest, unblocks all others. |
| **Group 2 — ARB-PSA topic map** | 2 | New topic map JSON + projector wiring + projection cache key change. |
| **Group 3 — Semantic predicates** | 3 | Three lambda primitives + frozen-embedding startup check + tests. |
| **Group 4 — ARB-PSA ruleset authoring** | 4 + 5 | Prompt contracts → extract → human review → publish. Depends on 1 & 2 being merged so we can test against the gated path. |
| **Group 5 — End-to-end accuracy benchmark** | all | Re-run the user's exact prompt; commit the golden-master report; pin the score in CI. |

Workflow per group: prompt contracts → issues → `branch-lambda-accuracy-<N>` →
PR → user review/merge. Nothing pushed to master.

---

## 6. What this gives us that an LLM-only solution cannot

| Property | LLM-only | Lambda-RAG today | Lambda-RAG after this plan |
|---|---|---|---|
| Same input → same verdict, forever | ❌ | ✅ | ✅ |
| Evidence span anchored to source bytes | ⚠️ (post-hoc, hallucinatable) | ✅ | ✅ |
| Defensible to a regulator without re-running the model | ❌ | ✅ | ✅ |
| Rule changes are diffable / signed | ❌ | ✅ | ✅ |
| Coverage of 12-dim PSA rubric | ✅ | ❌ | ✅ |
| Catches semantic intent across phrasing | ✅ | ❌ | ✅ (via signed embeddings) |
| Catches template-boilerplate | ✅ | ❌ | ✅ |
| Runs in <1s with no API key | ❌ | ✅ | ✅ |

**Net:** we keep all three pillars LLM-only cannot deliver (determinism,
idempotency, defensibility) and we close the accuracy gap by attacking
classification and coverage — not by adding inference at runtime.
