# Locked Oracle Pattern

**Status:** Phase 1 shipped (#177). Phase 0 empirical result: GREEN (100.0% raw byte-identity across 1200 calls). See [#175](https://github.com/MTCMarkFranco/lambda-rag/issues/175) and [PR #176](https://github.com/MTCMarkFranco/lambda-rag/pull/176).

## The problem

lambda-rag's determinism story rests on the rules engine being pure (given the same rule set + projected document → the same verdict, every time). But rule *content* — schema concepts, verdict predicates, remediation text — is authored by humans reading source documents. Scaling that authoring step past a handful of documents means using an LLM to project source text into a structured fact schema. The LLM step is non-deterministic in general.

The FID-Lottery paper ([arXiv 2606.20536](https://arxiv.org/pdf/2606.20536)) enumerates five sources of LLM inference non-determinism. At the constrained end (temperature=0, top_p=1, fixed seed, structured JSON, single provider, single region), how much of that non-determinism survives?

## The pattern

**"Locked Oracle" = the LLM is used *only* at a well-defined authoring seam, with sampling knobs pinned, and a content-addressed sidecar cache in front of it.** Any drift in the tuple `(doc, schema, model, prompt, sampling settings, section ordering)` produces a different cache key. Loud fingerprint mismatch on load. Never a silent regeneration.

```
┌──────────────────────────────────────────────────────────────┐
│                      RULES ENGINE (deterministic)             │
│                                                                │
│  RuleSet + ProjectedDocument + SectionFactSidecar             │
│         │                                                      │
│         ▼                                                      │
│   Verdict                                                      │
└──────────────────▲───────────────────────────────────────────┘
                   │
     ┌─────────────┴─────────────┐
     │  SectionFactSidecar cache  │
     │  (file/SQLite, keyed on   │
     │   fingerprint tuple)       │
     └─────────────▲─────────────┘
                   │ miss
     ┌─────────────┴─────────────┐
     │  Locked Oracle — LLM call  │
     │  temp=0, top_p=1, seed=42, │
     │  response_format=json      │
     └────────────────────────────┘
```

## Fingerprint composition

The cache key folds in every input the extractor observes, so a change to any of them forces regeneration:

| Component | Source | What it protects against |
|---|---|---|
| `documentId` | `ContentHash.OfBytes(doc)` | Any change to the source doc |
| `factSchemaHash` | `FactSchema.Fingerprint()` | Adding/removing concepts, changing enum values, changing normalizers |
| `modelId` | `deployment` name (stable) | Deploying a different model behind the same code |
| `promptHash` | System prompt + `PromptVersion` + normalizer table hash + `LockedOracleSettings.Fingerprint()` | Prompt drift, sampling-knob drift, unit conversion drift |
| `sectionOrderingHash` | Section ids + text lengths in order | Projector version change that reorders or re-splits sections |

Any mismatch throws `SectionFactSidecarMismatchException` at load time with the drifted component named. Operator resolution: rerun with `--refresh-facts` (accept the drift) or pin the drifted component (revert the change).

## Determinism knobs (Phase 1)

`LockedOracleSettings.Default`:

- `Temperature = 0.0f`
- `TopP = 1.0f`
- `Seed = 42`

Set to `LockedOracleSettings.Unpinned` (all null) only for models that reject sampling parameters (some reasoning models do). Doing so weakens the idempotency guarantee — treat resulting sidecars as best-effort.

The `Fingerprint()` of these settings is folded into `PromptHash`, so:
- Bumping the seed constant → all cached sidecars invalidate
- Adding a new knob (e.g. `TopK`) → all cached sidecars invalidate
- Toggling `Unpinned` on/off → all cached sidecars invalidate

## Cost & performance envelope (measured)

From Phase 0 on `gpt-5.4-mini` (Azure GA, Canada Central):

- **Latency:** 747–828 ms/call (P95 ~1 s), consistent across doc sizes
- **Tokens per typical section extraction:** ~700 input / ~50 output
- **Cost per section:** ~$0.0008 USD ($0.75 in / $4.50 out per 1M)
- **Cache-hit path:** ~1 ms (SQLite roundtrip, no LLM)

## When to bypass the cache

- **`--refresh-facts` CLI flag** — one-shot bypass; forces regeneration and rewrites the sidecar
- **`SectionFactSidecarMismatchException`** — loud fail; operator must decide whether to `--refresh-facts` or fix the drifted component

**Never** silently regenerate when the fingerprint mismatches. That's how you end up with correlated-but-different verdicts that no reviewer can reproduce.

## Regression detection

Run `LockedOracleLiveIdempotencyTests` monthly (or after any Azure model version rollout you suspect):

```powershell
$env:LAMBDA_RAG_LOCKED_ORACLE_LIVE_TESTS = "1"
$env:LAMBDA_RAG_FACTS_ENDPOINT           = "https://<name>.cognitiveservices.azure.com/"
$env:LAMBDA_RAG_FACTS_DEPLOYMENT         = "gpt-5.4-mini"
dotnet test tests/LambdaRag.IdempotencyTests --filter Category=LockedOracle
```

Asserts ≥99% canonical-JSON identity across 5 cache-miss extractions. Failure = P0: model has drifted or determinism knobs are unpinned somewhere.

For deeper investigation, run the `LockedOracleProbe` spike with N=100+ on a stress document — see [`tools/LockedOracleProbe/README.md`](../tools/LockedOracleProbe/README.md).

## Not in scope

- **N=3 majority-vote fallback for `idempotencyClass: "strict"` rules.** Deferred. Phase 0 argmax was empirically stable at 100%; the mechanism is spec'd in #175 but not implemented. Add when a real rule surfaces a flip-prone concept.
- **Multi-provider Locked Oracle.** Phase 1 is Azure OpenAI only. Anthropic / self-hosted / other regions each need their own Phase 0 probe run.
- **Cache eviction / TTL.** Sidecars are small and content-addressed; keep everything until real cache-size numbers say otherwise.

## Related docs

- [DETERMINISM.md](DETERMINISM.md) — the four-pillar determinism story lambda-rag rests on
- [FOUR-PILLARS.md](FOUR-PILLARS.md) — the whole model, including Flexibility (Pillar 4)
- [Issue #175](https://github.com/MTCMarkFranco/lambda-rag/issues/175) — Locked Oracle spec + Phase 0 harness
- [PR #176](https://github.com/MTCMarkFranco/lambda-rag/pull/176) — `tools/LockedOracleProbe` empirical harness
- [Issue #177](https://github.com/MTCMarkFranco/lambda-rag/issues/177) — Phase 1 (this doc's implementation)
