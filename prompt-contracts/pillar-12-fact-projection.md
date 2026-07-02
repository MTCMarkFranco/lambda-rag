# Pillar 12 — Section-fact projection (LLM as pass-1 classifier, deterministic pass-2 evaluator)

> **Type:** Design proposal + prompt contract.
> **Pairs with:** Pillar 9 (`/prompt-contracts/pillar-9-policy-compiler.md`),
> Pillar 6 (semantic bindings), Pillar 10 (applicability floor).
> **Status:** Proposal — no implementation until this is reviewed and approved.
> **Depends on:** the merged Pillar 10 baseline (PR #152 / commit `0904702`).
> **Measured against the four pillars** ([`/docs/FOUR-PILLARS.md`](../docs/FOUR-PILLARS.md)):
> Determinism, Idempotency, Accuracy, and — added on 2026-07-02 in direct
> response to the Pillar 12 overfit review — **Flexibility**. Every design
> choice below must be defensible against all four, not just the first three.

---

## Intent

Pillars 1–11 have progressively narrowed *where* and *what* the lambda evaluates against:
Pillar 1 gated by doc-kind, Pillar 6 aligned anchor vectors, Pillar 7 admitted
under-tagged sections, Pillar 9 auto-authored the lambdas, Pillar 10 filtered
irrelevant sections. Each pillar improved retrieval or applicability without
letting the LLM decide Pass/Fail.

Pillar 12 addresses the last two known ceilings on `ruleScore`:

1. **Vocabulary breadth.** The auto-generated lambdas are literal
   `input1.text.Contains("...")`. Ten paraphrases of the same requirement
   ("must be encrypted and rotated every 90 days") fail the lambda because
   only one surface form was compiled in. Real docs paraphrase; the engine
   doesn't.
2. **Cross-section evidence.** A compound requirement — "MUST be encrypted
   **AND** rotated every 90 days" — is legitimately satisfied when
   encryption is discussed in §4.2 and key rotation in §11.3. Today's
   engine can't compose evidence across sections; it evaluates one rule
   against one chunk in isolation, black-box style.

The wedge is a two-pass architecture with a **frozen fact schema** in between:

- **Pass 1 (LLM-as-classifier, cached).** For each section, an LLM
  populates a fact bag whose keys are drawn from a closed enum declared by
  the ruleset. The LLM only decides "is concept X discussed here?" — never
  Pass/Fail. Output is validated against the fact schema and written to a
  signed sidecar keyed on `(doc-content-hash, fact-schema-hash, model-id,
  prompt-hash)`. Subsequent reviews replay the sidecar byte-identically.
- **Pass 2 (deterministic evaluator, no LLM).** For each rule, the engine
  unions the fact bags across the rule's scoped sections and runs the
  compiled lambda over the resulting fact set. The lambda operates on
  typed facts (`facts.encryption_declared`, `facts.key_rotation_days`),
  not raw text. Pass/Fail is deterministic given the fact bags.

The LLM sits **between** the two passes as a compiler. It never adjudicates.

---

## Non-goals

- **Not** replacing the existing text-lambda path. Simple rules with clean
  literals (e.g. path checks like `docs/adr/`) continue to work exactly as
  today. Pillar 12 opts in per rule via a new `evaluationMode: "facts"`
  marker.
- **Not** an unbounded LLM pipeline. The fact schema is a **finite, ruleset-
  declared enum**. Facts outside the schema are silently dropped. There is
  no free-form LLM output that reaches the lambda.
- **Not** a numeric-reasoning engine. The LLM extracts the verbatim
  phrase ("every 90 days", "quarterly"); a **deterministic date/duration
  normalizer** on our side maps to ISO-8601 (`P90D`). The LLM never emits
  a number of its own choosing.
- **Not** a rewrite of Pillar 9's ruleset authoring. Pillar 12's fact
  schemas can be authored by the same `FoundryRuleAuthoringAgent` in a
  new pass, but that authoring is out of scope for this contract.

---

## The three claims Pillar 12 rests on

1. **Concept classification is a well-scoped LLM task.** Deciding "does
   this paragraph discuss encryption?" is a paraphrase-invariant
   classification with high inter-model agreement. It's the same task
   family as topic tagging, which we already do with Pillar 6 anchors.
   The LLM does *not* have to reason about compliance.
2. **A finite fact schema is compilable.** If the ruleset declares its
   concepts upfront (`{encryption_declared: bool, key_rotation_days: int,
   ...}`), the LLM's output surface shrinks to a schema-conformant JSON
   object. STJ validation rejects everything else. The `schemaHash` is
   folded into the fingerprint so schema drift invalidates the cache.
3. **Cross-section composition is a lambda-DSL problem, not an LLM
   problem.** Once each section produces facts, unioning them is
   deterministic set-merge. The compound lambda (`facts.encryption_declared
   && facts.key_rotation_days == 90`) is authored once, replays forever.
   The "which sections belong together for rule R?" mapping is emitted by
   Pass 1 as a byproduct — not by heuristics.

---

## Architecture

```
                        AUTHORING TIME
┌─────────────────────────────────────────────────────────────────────┐
│  1. Policy doc  ──►  FoundryRuleAuthoringAgent                      │
│                       │                                             │
│                       ├──► Rule (lambda operates on facts)          │
│                       │                                             │
│                       └──► FactSchema (concepts + normalizers)      │
│                              │                                      │
│                              ▼                                      │
│                       RuleSet (fingerprints include factSchemaHash) │
└─────────────────────────────────────────────────────────────────────┘

                          REVIEW TIME
┌─────────────────────────────────────────────────────────────────────┐
│  2. Reviewed doc  ──►  Parser  ──►  Projector  ──►  sections[]      │
│                                                                     │
│  3. Cache check on (docHash, factSchemaHash, modelId, promptHash)   │
│         ├── hit  ──►  load sidecar  ─┐                              │
│         └── miss ──►  Pass 1 (LLM classifier per section)           │
│                        │                                            │
│                        ├── validate against FactSchema              │
│                        ├── normalize numerics/durations (offline)   │
│                        └── emit SectionFactSidecar                  │
│                              │                                      │
│                              ▼                                      │
│  4. Pass 2 (deterministic)                                          │
│     for each rule:                                                  │
│         relevantSections = sidecar.ruleScope[rule.id]               │
│         factsUnion = merge(facts[s] for s in relevantSections)      │
│         verdict = compiledLambda(factsUnion)                        │
│                                                                     │
│  5. ComplianceReport (byte-identical replay if sidecar cached)      │
└─────────────────────────────────────────────────────────────────────┘
```

**Key invariants**

- No LLM in pass 2.
- The sidecar is signed and fingerprinted. Any change to `docHash`,
  `factSchemaHash`, `modelId`, or `promptHash` invalidates the cache
  **loudly** (the review fails-safe with a clear rebuild message).
- The sidecar is committable — teams can pin fact-extraction results in
  git alongside golden reports for reproducible replay in CI.
- Pass 2 receives ONLY typed facts. It does not see raw section text.
  This is what makes the fingerprint invariant tractable.

---

## Data model additions

### `FactSchema` (new, per ruleset)

```csharp
public sealed record FactSchema(
    string Id,                                    // "es-v1-facts"
    string Version,                               // "1.0.0"
    IReadOnlyList<FactConcept> Concepts)          // closed enum
{
    public ContentHash Fingerprint();             // folded into RuleSet.Fingerprint
}

public sealed record FactConcept(
    string Name,                                  // "encryption_declared"
    FactType Type,                                // Boolean | Enum | Integer | Duration | Text
    string Description,                           // for the LLM prompt
    IReadOnlyList<string> Examples,               // few-shot exemplars (paraphrases)
    IReadOnlyList<string>? EnumValues = null,     // when Type == Enum
    string? Normalizer = null);                   // "duration-iso8601", "integer-days", ...
```

**Type invariants:**
- `Boolean` — LLM emits `true`/`false`; ambiguous → `null` (concept undecided in this section).
- `Enum` — LLM must pick one of `EnumValues` or emit `null`.
- `Integer`/`Duration` — LLM emits verbatim phrase; the named `Normalizer`
  maps to canonical form. Bad phrase → `null` with an audit trail entry.
- `Text` — free string, capped at 200 chars, no downstream logic (used for
  provenance only, e.g. capturing the sentence that supported a boolean).

### `Rule` additions

```csharp
public sealed record Rule(...)
{
    // Pillar 12 (#153) — when non-null, this rule is evaluated in the
    // fact-based path. The lambda operates on `facts.<concept>` instead
    // of `input1.text.Contains(...)`. Null = classic path (unchanged).
    public string? EvaluationMode { get; init; }  // "facts" | null

    // Pillar 12 (#153) — the fact concepts this rule reads. Constrains
    // which sections are "relevant" for cross-section composition: only
    // sections whose fact bags mention at least one of these concepts
    // (as true / non-null) participate in the union.
    public IReadOnlyList<string>? RequiredFacts { get; init; }
}
```

**Byte-identity guarantee**: both fields are nullable and defaulted to
`null` on all existing rules. Fingerprint contribution only fires when
non-null.

### `SectionFactSidecar` (new artifact, `<doc-basename>.facts.json`)

```json
{
  "sidecarVersion": "1.0",
  "documentId": "lr1:abc123...",
  "factSchemaId": "es-v1-facts",
  "factSchemaHash": "sha256:def456...",
  "modelId": "gpt-5.3-chat-1",
  "modelSnapshot": "2026-06-15",
  "promptHash": "sha256:ghi789...",
  "generatedAt": "2026-07-01T22:00:00Z",
  "sections": {
    "s_00000004": {
      "encryption_declared": true,
      "encryption_algorithm": "AES-256",
      "key_rotation_days": null,
      "supporting_quote": "All data at rest is encrypted using AES-256."
    },
    "s_00000011": {
      "encryption_declared": null,
      "key_rotation_days": 90,
      "supporting_quote": "Keys are rotated on a 90-day cycle."
    }
  },
  "ruleScope": {
    "EA-DATA-018": ["s_00000004", "s_00000011"]
  }
}
```

`ruleScope` is emitted by Pass 1 as a byproduct of fact extraction: any
section whose fact bag makes at least one `RequiredFacts` concept
non-null becomes part of that rule's scope. This is deterministic given
the sidecar.

---

## Prompt contract (Pass 1 — section classifier)

**System prompt (deterministic, fingerprinted):**

```
You are a policy-fact extractor. Your job is to read one section of a
document and populate a fixed JSON schema of concepts. You do NOT decide
compliance, adequacy, or applicability. You ONLY report what the section
discusses.

Rules:
1. Emit ONLY the JSON object matching the schema below. No prose, no
   markdown, no code fences.
2. For each concept, emit either:
   - the value the section supports (boolean, enum, integer, or verbatim
     phrase), OR
   - null if the section does not discuss the concept.
3. Never infer a value across sections. If the section is silent on a
   concept, emit null. Cross-section composition happens elsewhere.
4. For every non-null value, emit `supporting_quote` — a verbatim quote
   from the section (max 200 characters) that supports it.
5. If the section contains a number/date/duration, emit the VERBATIM
   phrase from the text ("every 90 days", "quarterly"). Do NOT convert.
6. If you are less than confident a concept applies, emit null. Silent
   is safer than wrong.

Schema:
{schema-json-inline}

Section text:
{section-text}

Emit the JSON object now.
```

**Schema (per fact concept, emitted inline in the prompt):**

```json
{
  "encryption_declared": {
    "type": "boolean|null",
    "description": "Does the section state that data or keys are encrypted?",
    "examples": [
      "All data at rest is encrypted → true",
      "Keys are stored in a vault → null (vault ≠ encryption)"
    ]
  },
  "key_rotation_phrase": {
    "type": "string|null",
    "description": "Verbatim phrase describing key rotation cadence.",
    "examples": [
      "'every 90 days'",
      "'on a 90-day cycle'",
      "'quarterly'"
    ]
  }
}
```

**Deterministic constraints applied on our side:**
- `response_format = { "type": "json_object" }` (Foundry `gpt-5.3-chat-1`).
- `temperature` — not supported by target model; omitted.
- Fingerprint the prompt inputs verbatim (`SHA256(system+schema+section-text)`);
  cache lookup key.
- Reject any non-JSON output; retry once with the same seed; on second
  failure, mark section as `_extraction_failed: true` in the sidecar and
  Pass 2 treats affected rules as `Error` (not silent-pass).

**Numeric/duration normalizer (deterministic, offline):**

- `every 90 days` / `on a 90-day cycle` / `every ninety (90) days` → `P90D`
- `quarterly` → `P90D` (documented in the normalizer's mapping table)
- `annually` / `every year` → `P365D`
- Anything the normalizer can't map → the fact stays as the verbatim
  string; the lambda that reads it must handle both shapes or Fail.

The normalizer's mapping table is versioned (`normalizer.v1.json`) and
folded into the sidecar's `promptHash` so a normalizer update
invalidates old sidecars.

---

## Compound lambda examples

**Compound requirement (encryption AND rotation ≤ 90 days):**

```csharp
// Rule EA-DATA-018
Lambda = "facts.encryption_declared == true && facts.key_rotation_days <= 90"
EvaluationMode = "facts"
RequiredFacts = ["encryption_declared", "key_rotation_days"]
```

**Multi-source / multi-key composition (residency):**

```csharp
// Rule EA-PRIV-DATA-RESIDENCY-1
Lambda = "facts.data_classification in [\"Confidential\",\"Restricted\"] " +
         "&& facts.storage_region == \"Canada\""
EvaluationMode = "facts"
RequiredFacts = ["data_classification", "storage_region"]
```

Pass 2 semantics: `facts` is the union of every scoped section's fact bag.
When two sections declare conflicting values for the same concept (§4
says AES-256, §7 says "no encryption"), the resolver rule is
**Boolean OR** for booleans (once declared true anywhere, it's true),
**MIN** for durations (tightest requirement wins), **first-non-null** for
enums (audited). Every conflict is logged in the verdict's `EvaluatedInput`
so audit trails show the resolution.

---

## Fingerprint & cache guarantees

The sidecar is a byte-identity artifact. Cache hit requires ALL of:

| Fingerprint input | Change effect |
|---|---|
| `documentId` (SHA256 of doc bytes) | Different doc → cache miss |
| `factSchemaHash` | Schema evolved → cache miss (loud) |
| `modelId` + `modelSnapshot` | Model version rev'd → cache miss (loud) |
| `promptHash` (system + schema + normalizer version) | Prompt evolved → cache miss (loud) |
| `sectionOrderingHash` | Projector output changed → cache miss |

**Loud** means: the CLI does NOT silently recompute. It emits:

```
ERROR: fact sidecar mismatch (factSchemaHash drift)
       expected: sha256:abc...
       cached:   sha256:def...
       rerun with --refresh-facts to invalidate the cache, or pin
       your model + schema versions if this run must replay.
```

New CLI flag `--refresh-facts` explicitly opts into re-running Pass 1.
Absent that flag, byte-identity replay is guaranteed.

**Sidecar location:** `out/<review-name>/facts.json`, but also
committable to git for reproducible CI. A future flag `--facts-cache-dir
<path>` lets teams share sidecars across CI runs.

---

## Failure modes & their handling

| Failure | Detection | Response |
|---|---|---|
| LLM emits non-JSON | JSON parse fail | Retry once; on second fail, mark section `_extraction_failed: true`. Rules reading that section emit `Error` verdict (not silent-pass). |
| LLM emits schema-violating JSON | Schema validator | Same as non-JSON — treated as extraction failure. |
| LLM hallucinates a value (over-confident) | Verified via `supporting_quote` presence + substring check against section text | If quote isn't in the section, drop the fact (nulled). Logged in the sidecar's `warnings[]`. |
| Normalizer can't map a phrase | Table lookup miss | Fact preserved as verbatim string; downstream lambdas that expected a duration will fail predictably. |
| Cross-section conflict (§4 vs §7) | Merge-time detection | Resolved by documented merge rules; conflict recorded in verdict's `EvaluatedInput.conflicts[]`. |
| Sidecar-schema mismatch on replay | Fingerprint check at load | CLI errors loudly. `--refresh-facts` required to proceed. |
| LLM refusal / safety block | Model returns refusal token | Extraction failure; identical to non-JSON path. Never silently maps to Pass. |

---

## Pillar 9 byte-identity boundary

Every pre-Pillar-12 golden master must remain byte-identical. Approach:

- All new fields on `Rule` (`EvaluationMode`, `RequiredFacts`) default to
  null and don't contribute to `Rule.Fingerprint()` unless non-null.
  Existing rulesets serialize identically.
- All new fields on `ComplianceReport` (fact bag echoes) default to null.
- No new fields injected into `Verdict` on the classic-lambda path.
- `EvaluationService` gets a new opt-in parameter
  `factExtractor: IFactExtractor?`. When null (default), the fact path is
  disabled entirely and only classic-lambda rules run.
- Corpus regression tests run in default-off mode. A separate opt-in test
  suite exercises fact-path goldens.

---

## Phased delivery

**Phase 1 — fact schema + data model + fingerprints (no LLM yet).**
- `FactSchema`, `FactConcept`, `FactType`, `Rule.EvaluationMode`,
  `Rule.RequiredFacts`.
- Fingerprint plumbing.
- Deterministic normalizer for durations + integers (mapping table shipped
  as `normalizer.v1.json`).
- Unit tests: 20+ around fingerprint stability, schema validation,
  normalizer edge cases.
- **Gate**: full existing suite (461 unit + 68 idempotency) stays green.

**Phase 2 — fact-driven lambda DSL.**
- New DSL primitive: `facts.<concept>` reads from the unioned fact bag.
- Lambda parser accepts `facts.encryption_declared`, etc.
- Cross-section merge implementation (Boolean OR, duration MIN, enum
  first-non-null).
- Unit tests: 30+ around compound lambdas over synthetic fact bags.
- **Gate**: no LLM yet; still deterministic. Suite stays green.

**Phase 3 — LLM classifier + sidecar caching.**
- `FoundrySectionFactExtractor : IFactExtractor` calls Pass 1.
- Sidecar read/write + fingerprint validation.
- CLI flags: `--facts-cache-dir`, `--refresh-facts`.
- Integration tests using stubbed `IFactExtractor` for determinism.
- One end-to-end test hitting real Foundry, marked `[Trait("Category", "LLM")]`,
  gated by env var (mirrors Pillar 6 pattern).
- **Gate**: byte-identity replay proven — same doc + cached sidecar →
  identical report across 5 consecutive runs.

**Phase 4 — measured impact on the CTC arch doc.**
- Reauthor a subset (~20) of `enterprise-architecture-v1` rules to use
  `EvaluationMode: "facts"`. Focus on the compound rules (encryption+rotation,
  data+residency, multi-clause requirements).
- Re-run against the arch doc. Target: rule-level pass rate on the
  reauthored subset moves from ~10% to ≥50%.
- Publish findings doc `docs/findings/pillar-12-fact-projection-results.md`.

---

## Decisions locked in (from 2026-07-01 design review)

1. **Fact schema authoring** — `FoundryRuleAuthoringAgent` will be
   extended to emit both the ruleset and its `FactSchema` in one pass.
   No hand-authored schemas ship. The agent's system prompt gains a
   fact-schema section; the output JSON gains a top-level `factSchema`
   node alongside `rules[]`.
2. **Sidecar location** — Global cache at `~/.lambda-rag/facts/`
   (Windows: `%USERPROFILE%\.lambda-rag\facts\`). Sidecars are keyed on
   `docHash` and shared across reviews of the same doc. CLI opt-in flag
   `--facts-cache-dir <path>` overrides for CI or ephemeral runs.
3. **Model choice for Pass 1** — Reuse `gpt-5.3-chat-1` (the same
   Foundry deployment `FoundryRuleAuthoringAgent` uses). Same parameter
   constraints: no `temperature`, `response_format: { type:
   "json_object" }`, `max_output_tokens` bounded per section. Same
   `AzureOpenAIClient` + `DefaultAzureCredential` wiring — no new
   credential surface.

---

## Success criteria

Pillar 12 ships successfully when all four hold:

1. **Byte-identity**: every pre-Pillar-12 golden master unchanged, all
   existing tests green, sidecar-cached replay is deterministic across 5
   consecutive runs on the same inputs.
2. **Correctness on paraphrase**: a targeted test set of 10 paraphrases
   of "must be encrypted AND rotated every 90 days" all resolve to Pass
   when the section satisfies the requirement, regardless of surface form.
3. **Cross-section composition**: a test doc with encryption in §A and
   rotation in §B produces a Pass on the compound rule.
4. **Measured impact**: on the CTC arch doc, reauthored-subset rule-level
   pass rate ≥ 50% (up from ~10% today).

Every one of these is falsifiable and testable in CI.

---

## What we're NOT committing to yet

- Emitting `FactSchema` automatically from policy docs (Pillar 13
  territory).
- Multi-doc composition (evidence spanning two different reviewed docs).
- Numeric reasoning beyond durations + integers (regex ranges, dates,
  percentages) — a follow-up if the first cut proves out.
- A GA public API for `IFactExtractor` — the first cut is internal.

---

## Ask

Approve or push back on:

1. The two-pass split (LLM classifier → deterministic evaluator).
2. Facts as a closed, ruleset-declared enum (not free-form).
3. Sidecar caching keyed on fingerprints, with `--refresh-facts` as the
   only escape hatch.
4. Phased delivery order (schema → DSL → LLM → measured impact).
5. Success criteria as written.

Once approved, Phase 1 is ~2 days of work (schema + fingerprints + tests,
zero LLM). Phase 2 another ~2 days. Phase 3 (LLM) another ~3 days. Phase
4 (measurement + findings doc) ~1 day. Total ~8 engineering days.

