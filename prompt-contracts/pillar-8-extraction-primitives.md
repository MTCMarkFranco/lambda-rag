# Pillar 8 — Anchor-bound extraction lambdas (POC)

## Intent

Every prior pillar made rule lambdas better at answering **"does this section
mention X?"** — phrase lists (Pillar 3), template-boilerplate detection
(Pillar 5), per-token semantic bindings (Pillar 6), topic-membership and
synthetic anchors (Pillar 7). All seven pillars share one structural
limitation: the lambda body is a **presence check** over a fixed vocabulary.

Real policy intent is rarely "mention the word RPO". It is "specify an RPO
value, and that value must be ≤ N hours". Today's lambdas can verify the
first half (lexically), not the second (structurally).

Pillar 8 closes that gap by introducing **anchor-bound extraction
primitives**. The lambda's left-hand side cosine-resolves where in the chunk
the policy concept appears; the right-hand side extracts the value adjacent
to that anchor and applies a structural constraint. The structural test is
where policy intent lives, and Pillar 8 lets the lambda actually evaluate
it — deterministically, on frozen embeddings, with no LLM at runtime.

This contract describes the **POC scope** that validates the architecture
end-to-end on one rule (`ARB-PSA-DR-001`, RPO/RTO duration extraction).
The full migration of the remaining 14 ARB-PSA rules and the
vocabulary-independent anchor pass are out of scope here and tracked as
follow-up issues.

## Architecture (target shape, not POC scope)

Long-term, a rule artifact would look like this:

```jsonc
{
  "id": "ARB-PSA-DR-001",
  "lambdaTemplate": "ExtractDuration(input1.text, $rpo_anchor)?.TotalHours <= 4 && ExtractDuration(input1.text, $rto_anchor)?.TotalHours <= 2",
  "anchorBindings": {
    "$rpo_anchor": { "concept": "recovery point objective", "embedding": [...], "cosineThreshold": 0.55 },
    "$rto_anchor": { "concept": "recovery time objective", "embedding": [...], "cosineThreshold": 0.55 }
  }
}
```

At runtime, the evaluator would:

1. For each `$anchor`, cosine-search the chunk's token spans and pick the
   highest-scoring span above the threshold.
2. Substitute the resolved span into the template, producing a concrete
   lambda the existing RulesEngine evaluates as today.
3. Record the bound spans + extracted values + constraint result in the
   `Verdict` for full audit trail.

All three steps are deterministic given frozen embeddings.

## POC scope (this contract)

We deliberately do **not** add template-substitution machinery in the POC.
Instead, we add three primitives that **read the already-populated
`SemanticBindings` scope** (Pillar 6) to locate spans, then extract +
constrain values inline. This proves the architectural value with zero
projection/evaluation engine changes.

### New primitives

All three live in `src/LambdaRag.Core/Semantic/LambdaPrimitives.cs` and
register automatically via the existing `WorkflowFactory.CustomTypes`
exposure.

#### 1. `ResolveAnchorSpan(string anchorName) → TokenMatch?` <br/> `ResolveAnchorSpan(string anchorName, string text) → TokenMatch?`

Returns the highest-cosine `SemanticBindings(anchorName)` entry, or
`null` when no binding exists. Pure read of the Pillar 6 ambient scope.
Tiebreaker for equal cosines: lowest `CharStart`, then ordinal-lower
`Text` (so the same chunk always resolves to the same span). Returns a
`TokenMatch` record (reusing the Pillar 6 type) so no new serialization
shape is introduced.

The two-arg overload additionally falls back to a case-insensitive
literal whole-word regex search of `anchorName` in `text` when the
cosine-based bindings are empty. The leftmost literal match wins and is
returned as a synthetic `TokenMatch` with `Cosine = 1.0`. Acronyms like
`RPO` / `RTO` rarely clear the rule-level cosine threshold against
multi-word anchor texts ("recovery point objective rpo data loss") — the
3-letter token doesn't embed close enough to the full phrase. When the
literal acronym is present in the chunk, that's the strongest possible
"where in the chunk" signal, so we degrade gracefully into a regex match
rather than refusing to extract.

#### 2. `ExtractDurationNear(string text, string anchorName, int windowChars = 120) → TimeSpan?`

1. Calls `ResolveAnchorSpan(anchorName, text)`. If `null`, returns `null`.
2. Identifies the sentence containing the anchor (delimiters: `. ! ? \n \r`).
3. Intersects the sentence span with the `±windowChars` window centered on
   the anchor. This is the search slice.
4. Scans the slice for ALL matches of
   `(?ix) \b (\d+(?:[.,]\d+)?) \s* (h|hr|hrs|hour|hours|min|mins|minute|minutes|sec|secs|second|seconds|day|days|wk|week|weeks) \b`.
   Locale-invariant; 200ms timeout.
5. **Picks the match whose span is nearest the anchor span** (gap = chars
   between spans; 0 if overlap). Tiebreaker: lowest absolute CharStart,
   then ordinal-lower text.
6. Parses the winning match into a `TimeSpan` using a fixed unit→multiplier table:
   ```
   h|hr|hrs|hour|hours          → Hours
   min|mins|minute|minutes      → Minutes
   sec|secs|second|seconds      → Seconds
   day|days                     → Days
   wk|week|weeks                → 7 × Days
   ```
   Bare single-letter units (`m`, `s`, `d`, `w`) are intentionally
   excluded — too ambiguous in technical prose ("Section 4d", "$5m
   budget"). Returns `TimeSpan` or `null` if parsing fails.
7. Returns `null` for empty text, null/empty anchor name, no anchor binding,
   or no in-scope match. Never throws on input shape.

**Why sentence scoping.** A pure nearest-to-anchor metric ties for
`RPO: 4 hours. RTO: 2 hours.` (each duration is equidistant from the
opposite anchor). Constraining to the anchor's own sentence yields the
correct value for each anchor independently — and matches how technical
writing actually associates labels with values.

**Sentence terminator semantics.** `.` / `!` / `?` count as a terminator
only when followed by whitespace or end-of-text; `\n` and `\r` are always
terminators. A bare `.` inside a numeric literal (`4.5`) does NOT split
the sentence, preserving `4.5 hours` as one extractable duration.

**Determinism.** Same `text` + same anchor binding state → byte-identical
return. Tiebreaker: nearest match position; equal gap → leftmost; equal
position → ordinal text.

#### 3. `HasExtractedDurationNear(string text, string anchorName, int windowChars = 120) → bool`

Sugar: returns `ExtractDurationNear(text, anchorName, windowChars) is not
null`. Exists so lambdas read naturally without nullable-bool gymnastics.

**Two arities.** Ship both `(text, anchorName, windowChars)` and
`(text, anchorName)` overloads. The dynamic lambda parser
(`RulesEngine` / `System.Linq.Dynamic.Core`) does **not** bind to methods
with default-valued parameters from rule lambdas, so the 2-arg overload
must exist as a real method, not just a default-arg shortcut.

### Rule update

`ARB-PSA-DR-001` lambda becomes:

```
LambdaPrimitives.HasExtractedDurationNear(input1.text, "rpo")
  && LambdaPrimitives.HasExtractedDurationNear(input1.text, "rto")
```

`!IsTemplateBoilerplate(input1.text)` is **removed** as a guard. Under
Pillar 8 it is structurally redundant: a section that successfully
extracts a real duration ("72 hours") *by definition* made a commitment
and cannot be boilerplate. Keeping the guard would, in practice, reject
real PSA sections whose **opening paragraphs** are template stubs
("Click to read message", "To be completed by …") but whose body
contains the actual values. Extraction-success is the stronger signal.

The `predicate` is loosened from
`HasTopic(input1, "dr_resiliency", 0.5)` to plain
`HasTopic(input1, "dr_resiliency")` (membership, no score gate).
Real-PSA topic projections rarely place secondary topics above 0.5;
extraction is now strong enough that membership alone is the right
predicate.

The other 14 ARB-PSA rules are **untouched** in the POC. They keep their
current Pillar 7 shape.

### POC unit tests

In `tests/LambdaRag.UnitTests/Evaluation/LambdaPrimitivesExtractionTests.cs`:

- `ResolveAnchorSpan_returns_highest_cosine_when_multiple_bindings`
- `ResolveAnchorSpan_returns_null_when_no_bindings`
- `ResolveAnchorSpan_ties_broken_by_offset_then_text`
- `ResolveAnchorSpan_returns_null_when_scope_absent`
- `ExtractDurationNear_finds_hours_after_anchor`
- `ExtractDurationNear_finds_hours_before_anchor_within_window`
- `ExtractDurationNear_returns_null_outside_window`
- `ExtractDurationNear_parses_all_unit_aliases` (theory: 13 unit aliases)
- `ExtractDurationNear_decimal_value`
- `ExtractDurationNear_returns_null_when_anchor_unresolved`
- `ExtractDurationNear_returns_null_when_only_phrase_no_value` (e.g.
  "RPO will be defined later" → null)
- `HasExtractedDurationNear_delegates_to_extract`

### POC integration test

In `tests/LambdaRag.UnitTests/Evaluation/DurationExtractionRuleTests.cs`
(or similar):

Two crafted chunks, evaluated with the updated DR-001 lambda through the
real RulesEngine pipeline + a stub `ITokenEmbedder` that produces clear
high-cosine bindings for "rpo"/"rto":

- **PASS chunk:** "RPO: 4 hours. RTO: 2 hours. Failover via warm standby."
  → lambda evaluates `true`.
- **FAIL chunk:** "We will document RPO and RTO commitments in a future
  release of this PSA." → lambda evaluates `false`.

This is the architectural proof — same lambda, structurally
discriminating presence-of-value from presence-of-term.

### Benchmark signal

After the POC change, re-run `ArbPsaBenchmark` and report:

- DR-001 verdict on the real PSA (PASS / FAIL / Error).
- `dr_resiliency` recall: did Pillar 8 lift it from miss → hit?
- Byte-identity test still passes (the new primitives are pure functions
  and the rule's only structural change is its lambda body).

If `dr_resiliency` does not lift on the real PSA, that is **not** a POC
failure — it surfaces an upstream issue (e.g., the PSA's RPO/RTO mentions
are in a chunk whose `dr_resiliency` topic-score is < 0.5, blocking the
predicate). The POC's architectural claim — *extraction primitives are
viable and deterministic* — is proven by the integration test pair above.
The benchmark delta is supplementary signal.

## Edge cases (must all be tested or documented)

- Anchor name null or empty → return `null` / `false`.
- Text null or empty → return `null` / `false`.
- `windowChars` <= 0 → treat as window = 0 (only spans exactly inside
  binding); document this.
- `SemanticBindings` scope absent (lambda invoked outside evaluation) →
  return `null` / `false`. Never throw.
- Multiple duration matches in window → nearest-to-anchor wins
  (deterministic); ties broken by leftmost, then ordinal text.
- Number formatting: `4`, `4.5`, `4,5` (European decimal) all parse;
  `4 hours` and `4hours` both match; `4-hour` does NOT match (hyphen).
- Mixed-case units: `Hours`, `HOURS`, `hours` all match (regex
  `IgnoreCase`).
- Bare single-letter units (`m`, `s`, `d`, `w`) are NOT matched — too
  ambiguous in technical prose. Use `min`, `sec`, `day`, `wk` etc.
- Negative numbers: `-4 hours` — the regex anchors `\b\d+` so the minus
  is not captured; `TimeSpan` will be positive 4h. Document.

## Determinism / idempotency

- Pure functions. No allocations beyond regex + `TimeSpan` ctor.
- All randomness sources (none) absent.
- Locale-invariant regex (`CultureInvariant` flag).
- Read-only access to `SemanticBindingAccessor.Current` — the populating
  side is Pillar 6, which is already covered by the byte-identity gate.
- Adding the primitives but not invoking them (legacy rules) keeps the
  rule fingerprint unchanged — Pillar 8 is fully additive.

## Acceptance gates

1. Three primitives implemented in `LambdaPrimitives.cs` with XML doc.
2. `SpanRef` record added (locale-invariant, value-equality).
3. All unit tests above pass.
4. POC integration test (PASS/FAIL chunk pair) passes.
5. `ARB-PSA-DR-001` lambda updated; `lambda-rag ruleset validate` clean.
6. `ArbPsaBenchmark.Benchmark_is_byte_identical_across_100_runs` still
   passes (5 min).
7. Real-PSA `ArbPsaBenchmark` recall delta reported in PR description
   (whether positive, neutral, or surfaces an upstream issue).

## Out of scope (tracked as follow-ups)

- **Pillar 8.B — Full rule migration.** Rewriting the other 14 ARB-PSA
  rules in extraction shape (counts, lists, presence-of-field, numeric
  comparisons). Separate issue.
- **Pillar 8.C — Vocabulary-independent anchor resolution.** Today's
  `SemanticBindings` requires per-token cosine ≥ 0.78 (the anchor
  threshold). To bind to chunk-vernacular phrases like "data loss
  budget" → "rpo", we need a lower-threshold windowed-span anchor pass.
  Separate issue.
- **Pillar 8.D — Template substitution at runtime.** The target shape
  in §Architecture above. Separate issue + design doc.
- **Pillar 8.E — Authoring-time LLM compiler.** Producing
  `lambdaTemplate` + `anchorBindings` from natural-language policy text.
  Separate epic.

## Prompt contracts

- `prompt-contracts/pillar-8-extraction-primitives.md` (this file, POC)
- Follow-ups will get their own contracts as they're scoped.

Closes #133.
Epic: #132.

Follow-ups: #134 (rule migration), #135 (vocabulary-independent anchor),
#136 (template substitution at runtime), #137 (authoring LLM compiler).
