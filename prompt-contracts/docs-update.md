# Docs update (#122)

**Intent.** Reflect the Pillar 1–5 changes in the public docs so a new
reader sees the doc-kind gating, ARB-PSA topic map, semantic primitives,
template detector, and the new ARB-PSA ruleset — without having to read
the diff.

**Touched files.**

| File | Change |
|---|---|
| `README.md` | New row in the "Built-in industry topic maps" table for `arb-psa.v1`. CLI cheat sheet shows new `--doc-kind` flag. |
| `docs/ARCHITECTURE.md` | New paragraph on doc-kind gating placed in the runtime pipeline section. |
| `docs/PIPELINE.md` | Insert a step between Project and Evaluate: "Resolve doc kind". |
| `docs/DETERMINISM.md` | Add `LambdaPrimitives` to the "registered custom types" list; note that phrasebooks are part of the ruleset fingerprint when present. |
| `docs/manifesto.md` | No change required (the pattern is unchanged). |
| `docs/blog/lambda-rag-deterministic-llm-review.md` | Update the CTC PSA case study with the new before/after numbers. |

The before/after numbers will be filled in from the benchmark output —
where the benchmark can run (i.e. with the local PSA sample available).

Closes #122.
