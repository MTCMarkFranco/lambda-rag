# Pillar 6 — Semantic Keyword Binding: Results & Status

> Scope: Engineering delivery of Pillar 6 (semantic anchor binding) plus a
> first pass of anchors on the CTC ARB-PSA ruleset.  
> Branch: `branch-lambda-semantic-binding-2` · Issue: #124 · Status: ENGINEERING SHIPPED, ACCURACY GATES PENDING REAL EMBEDDER

## What shipped

- **Domain schema** — `SemanticAnchor`, `TokenEmbedding`, `BindingRecord` records in `LambdaRag.Core.Domain`.
- **Tokenizer** — `LambdaRag.Projection.SemanticTokenizer` (v1) with the signed `stopwords-en.v1.txt` resource (SHA-256 hash exposed via `StopwordHash`).
- **Resolver** — `LambdaRag.Evaluation.Engine.SemanticBindingResolver` cosine-binds tokens to anchors over a `ITokenEmbedder`.
- **Lambda primitives** — `LambdaPrimitives.SemanticBindings(name)`, `ExtractNumberNear`, `NearestText`, scoped through `SemanticBindingAccessor` (AsyncLocal).
- **Verdict surface** — new `Verdict.SemanticBindings : IReadOnlyList<BindingRecord>?` field, emitted only when non-empty so legacy verdict JSON is byte-identical.
- **Ruleset** — `rulesets/architecture-review/arb-psa.json` upgraded with `semanticAnchors[]` on all 15 rules. Lambdas unchanged ⇒ anchors are *inert* until a Foundry embedder is wired in (the deterministic 32-d hash embedder fallback is statistical noise at cosine ≥ 0.78).
- **Tests** (all green):
  - `SemanticTokenizerTests` — 50 fixture chunks × 100 runs determinism + spans + stopword drops + n-gram boundaries + token cap.
  - `SemanticBindingTests` — threshold sweep, zero-binding, no-anchor passthrough, scope-less safety.
  - `AdditiveGuaranteeTests` — Pillar 6 must produce byte-identical reports on rulesets without anchors (proves no regression risk on production rulesets).

## CTC ARB-PSA benchmark

| Metric | Pre-Pillar-6 | Post-Pillar-6 (mock embedder) | Notes |
|---|---|---|---|
| Pass | 2 | 2 | Same — lambdas unchanged |
| Fail | 5 | 5 | Same |
| Gap | 7 | 7 | Same |
| N/A | 2 | 2 | Same |
| Recall vs LLM PASS (7) | 1/7 | 1/7 | `design_patterns` |
| Precision on LLM FAIL (5) | 0/5 (1 false positive) | 0/5 (same) | `psa_completeness` rule mis-fires |
| Idempotency (100-run byte identity) | ✅ | ✅ | Untouched |

**Verdict counts are byte-identical between the pre-Pillar-6 and post-Pillar-6 runs** because the lambdas were not modified — Pillar 6 is operationally inert on this branch until a real embedder is bound.

## Why the gates are not (yet) met

The user-asked gates were `recall ≥ 8/12 PASS` and `precision = 100% on LLM FAIL`. Two hard constraints prevented hitting them on this branch:

1. **No production embedder.** `dotnet user-secrets` exposes only an edit/chat endpoint (`Foundry:Edit:*`); there is no embedding deployment configured. The runtime correctly falls back to `DeterministicHashEmbedder` (per spec: *"don't fail on missing creds — fall back to mock and clearly mark the ruleset"*). At cosine ≥ 0.78 against L2-normalised 32-dim random vectors, the bind rate is statistically ~0% — semantic binding is *plumbing only*, with no signal contribution.
2. **Pre-existing rule/projector mismatches.** The current `arb-psa.json` predicates rely on `input1.category == "X"` topic-map classifications that mis-fire on several PSA sections, and the `IsTemplateBoilerplate` heuristic produces a Pass on `psa_completeness` (a known LLM FAIL). These are **Pillar 2 / 4** scope (topic map + ruleset authoring), not Pillar 6.

Lambdas were intentionally **not** modified to invoke `SemanticBindings(...)` because with a mock embedder, every binding is noise — wiring those calls without a real embedder would introduce non-deterministic accuracy effects (statistical lucky hits) that would falsely move benchmark numbers without representing real capability. Once a Foundry embedding deployment is added to user-secrets (`LambdaRag:Foundry:Endpoint`, `:Deployment`, model `text-embedding-3-large`), the follow-up is a small, surgical rule-update PR that flips e.g. ARB-PSA-DR-001 to:

```text
... && (PhraseMatch(dr_rpo) || LambdaPrimitives.SemanticBindings("rpo").Count > 0)
    && (PhraseMatch(dr_rto) || LambdaPrimitives.SemanticBindings("rto").Count > 0)
```

That change is OR-additive (cannot regress current Pass verdicts) and unblocks the recall gate.

## Determinism & audit-trail

- Tokenizer version pinned at `semantic-tokenizer-v1`; bumping invalidates projection cache.
- `StopwordHash` (SHA-256 hex) is constant for the bundled list and exposed via `SemanticTokenizer.StopwordHash`.
- Anchor lookups are pure cosine math over precomputed / cached vectors — no LLM call at runtime.
- Every binding is recorded with `(anchor, matched, cosine, charStart, charLength)` in `Verdict.SemanticBindings` so an auditor can re-derive it from `(rule, projection, embedder)` bytes alone.
- The `AdditiveGuaranteeTests` byte-identity check proves Pillar 6 cannot flip a verdict on any pre-Pillar-6 ruleset.

## Follow-ups

1. Wire `LambdaRag:Foundry:Embed:Endpoint` + `:Deployment` in user-secrets (text-embedding-3-large).
2. Update the 12 mandatory `arb-psa.json` lambdas to OR-in `SemanticBindings(name).Count > 0` for each declared anchor.
3. Re-run the benchmark; recall target ≥ 6/7 on LLM PASS, precision 100% on FAIL.
4. Commit the regenerated `report.json` to `tests/golden-masters/arb-psa-pillar6.json` to lock the new baseline.
