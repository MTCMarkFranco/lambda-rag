# Semantic predicates — deterministic embedding-backed rule application

Tracks issue #67.

## Problem

After eval-003 (issue #63 / PR #66) we proved that **deterministic projection +
compiled lambda predicates** can take ruleset accuracy from 25% to 100% on the
structured Contoso demo contract. The catch: compiled `Contains`-style leaves
are brittle on natural language. Enumerating every paraphrase of "work for
hire" or "indemnification" in a lambda is not feasible.

Pure-LLM rule application is the opposite tradeoff: flexible on language, but
**non-deterministic, non-repeatable, non-idempotent, and not auditable** — all
unacceptable in a contract-review / compliance context.

## Decision

Three-tier predicate model with an **applicability gate** in front of the lambda:

| Tier | Use for | Backed by |
|------|---------|-----------|
| 1. Exact / structural | currency, dates, durations, section numbers, projector clause types | pure code |
| 2. Lexical (closed vocab) | defined terms, jurisdictions, country codes, regulator names | `Contains` / regex / dictionaries |
| 3. Semantic (open NL) | "is this a work-for-hire clause?", "is liability uncapped?" | **embedding cosine over precomputed vectors** |

```
Section → applicability gate (cosine(rule.descVec, section.vec) ≥ rule.gateThreshold)
       → if applies: run compiled lambda
              → leaves: Tier 1 (code) + Tier 2 (lexical) + Tier 3 (Semantic.Matches)
       → verdict + structured reasons
```

The verdict path contains **zero LLM calls**. All embedding work happens at
authoring time (rules) or projection time (documents) and is snapshotted.

## Determinism, repeatability, idempotency

Primary goals, alongside accuracy. Speed is explicitly **not** primary.

- **Pinned model**: `embeddingModel` (id + deployment + api version) stored in
  the rules JSON and the projection JSON. The model is part of the rule
  artifact.
- **Default model**: `text-embedding-3-large` via Azure Foundry projects v2 SDK
  (accuracy > speed).
- **Snapshotted vectors**: rule description vectors live in the rules JSON;
  section vectors live in the projection JSON. Both are reproducible from
  source on re-embed and identical across runs given a pinned model.
- **Runtime is pure math**: cosine + threshold compare. No cloud calls, no
  classifier head, no sampler.
- **Hash-keyed cache** for embeddings: key = `sha256(model_id || normalized_text)`.
- **Deterministic tie-break**: `>=` at the threshold (never `>`).
- **Per-rule thresholds**, frozen in the rules JSON. No global tuning, no
  runtime tuning.

## Replay guarantee

Given:

- the rules JSON (with model id + description vectors + thresholds + lambdas), and
- the projection JSON (with model id + section vectors),

evaluation must run **fully offline** with byte-identical verdicts to the
authoring run. Cloud calls happen **only** when re-embedding from source.

## Components

- `Semantic.Matches(section, conceptDescription, threshold)` — new lambda DSL leaf.
- `IEmbeddingProvider` — abstraction; first impl: `AzureFoundryEmbeddingProvider`
  using the projects v2 SDK.
- Rule loader — embeds each rule description once; persists vectors next to the rule.
- Projector — embeds each projected section once; persists vectors next to the projection.
- File-backed embedding cache keyed by `(model_id, sha256(text))`.
- Eval harness — reports applicability-gate skip rate; runs shadow mode to
  detect gate disagreements.

## Acceptance criteria (from issue #67)

- [ ] eval-003 still passes at 24/24 (100%).
- [ ] At least one rule (suggested: `IP-WORKFORHIRE`) migrated from `Contains`
      to `Semantic.Matches`, still passing.
- [ ] Idempotency: two consecutive eval runs produce byte-identical projection
      vectors and verdict JSON.
- [ ] Replay: vectors + rules JSON snapshotted → evaluation runs with **zero**
      cloud calls.
- [ ] Applicability gate documented and exercised in ≥1 rule; shadow-mode
      disagreement check reported in the eval output.

## Open questions (to resolve during implementation)

1. Vector storage shape: inline arrays in JSON vs. side-car `.vec` files.
   Inline keeps everything one-file; side-car keeps JSON diff-friendly.
   *Leaning side-car with a `vectorRef` pointer in the JSON.*
2. Normalization of text before hashing: NFC + trim + collapse whitespace at
   minimum. Anything else (lowercasing, stripping punctuation) costs us
   semantic fidelity for embeddings.
3. Initial gate threshold default: `0.78` cosine (3-large). Per-rule overrides
   expected during migration.
4. Should the gate threshold and the per-leaf `Semantic.Matches` threshold be
   distinct? *Proposed: yes. Gate is broader, leaves are tighter.*
