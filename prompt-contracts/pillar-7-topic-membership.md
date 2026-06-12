# Pillar 7.A — Topic-membership rule matching (#129)

**Intent.** Let an ARB-PSA rule fire on any section whose multi-label
`topics[]` array contains its target dimension, not only when that dimension
is the section's strict `primary_topic`. This unlocks the multi-label
classification work already done by `DeterministicContractProjector` and
recovers 5 of the 7 ARB-2 dimensions Pillar 6 (#126) leaves on the table.

**Inputs.**
- Section input passed by the projector (the `input1` parameter Microsoft
  RulesEngine binds to the lambda). Must expose a `topics` field that is
  either a `System.Text.Json.Nodes.JsonArray` of strings or a runtime
  collection of strings depending on the rule pipeline. The primitive must
  accept both shapes.
- A topic id (e.g. `"decision_records"`, `"security_architecture"`,
  `"platform:azure"`). Ordinal compare; no normalization.

**Outputs.**
- `LambdaPrimitives.HasTopic(object input, string topic)` returning `bool`.
- Returns `true` iff:
  1. `input` is not null, AND
  2. The input exposes a `topics` member (case-sensitive `topics`), AND
  3. That member is enumerable, AND
  4. At least one element is a string equal to `topic` (ordinal).
- Returns `false` in every other case — no exceptions, ever.

**Rule predicate update.** The 15 ARB-PSA rules under
`rulesets/architecture-review/arb-psa.json` change their `predicate` field
from `input1.category == "X"` to `LambdaPrimitives.HasTopic(input1, "X")`.
Lambda bodies, severities, anchors, and remediations are unchanged.

**Edge cases.**
- `HasTopic(null, "X")` → `false`.
- `HasTopic(input1, null)` → `false`.
- `HasTopic(input1, "")` → `false`.
- `input1` without a `topics` member → `false`.
- `topics` is `null` → `false`.
- `topics` is empty → `false`.
- `topics` contains non-string elements → those elements are skipped, not
  thrown on. A match on a sibling string element still returns `true`.
- Axis-qualified topic like `platform:azure` — exact-match only; no prefix
  or substring matching.
- A section whose `primary_topic == "X"` but with `topics == []` — returns
  `false` (the contract is `topics[]` membership, not primary equality).
  Existing predicates that want primary-only equality must keep the
  `input1.category == "X"` form.

**Determinism / idempotency.**
- Pure function: no allocations beyond enumeration, no I/O, no state.
- Predicate edits do not change `ruleset.metadata` other than what
  `RuleSetIO.Save` updates automatically — the byte-identity test in
  `ArbPsaBenchmark.Benchmark_is_byte_identical_across_100_runs` must
  continue to pass.

**Acceptance.**
- Primitive lives in `src/LambdaRag.Core/Semantic/LambdaPrimitives.cs`
  with XML doc.
- Unit tests in `tests/LambdaRag.UnitTests` cover every edge case above.
- All 15 ARB-PSA rule predicates updated; `lambda-rag ruleset validate`
  is clean.
- `ArbPsaBenchmark` recall improves to ≥5/7 vs LLM PASS after this
  change alone (the residual 2 dimensions are #130's responsibility).
- 100-run byte-identity test still passes.
- No changes to non-ARB-PSA rulesets.

**Out of scope.** Fuzzy topic match, partial substring match,
threshold-based topic scoring, changes to anchor sets or rule bodies.

Closes #129.
