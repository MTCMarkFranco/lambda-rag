# Pillar 7.B — Anchor-driven synthetic child sections (#130)

**Intent.** Recover the ARB-2 dimensions that the keyword-based
classifier never tags on the ARB-PSA sample (`decision_records`,
`technology_standards`) by letting the ruleset's own semantic anchors
"pull" the matching section into existence as a synthetic child.

**Inputs.**
- The existing projection output (list of section nodes) produced by
  `DeterministicContractProjector` after its standard keyword classifier
  + wrapper expansion + operative selection passes.
- Optional `RuleSet` reference (new optional parameter on the
  projector — nullable).
- Optional `IRuleEmbedder` (new optional parameter on the projector —
  nullable; threaded through the projection service registration so
  Pillar 6's wiring picks it up automatically).
- Cosine threshold (default `0.30`; constructor-overridable).

**Outputs.**
- Zero or more synthetic section nodes appended to the section list,
  each with:
  - `id` = `s_synthetic_<topic>_<nnnn>` (zero-padded counter, scoped
    to the projection run for determinism).
  - `heading`, `heading_path`, `text`, `text_raw`, `text_char_start`,
    `paragraphs` = copied verbatim from the source section.
  - `category` = `primary_topic` = the target topic `T`.
  - `topics` = `[T]`.
  - `topic_scores` = `{ T: <best_cosine_rounded_to_4dp> }`.
  - `topic_density` = computed via existing `ComputeDensity(T, body)`.
  - `is_operative_for_topic` = `false` (set by the operative-selection
    post-pass; synthetic sections never auto-elect).
  - `is_country_supplement` = same as source.
  - `is_synthetic_anchor` = `true` (new flag, only on synthetic
    sections).
  - `synthetic_from` = source section id.
  - `synthetic_anchor` = name of the best-scoring anchor.

**Trigger conditions (all required).**
1. `RuleSet` is non-null and has at least one rule with non-empty
   `SemanticAnchors`.
2. `IRuleEmbedder` is non-null.
3. For some topic `T` declared by an anchor:
   - No projected section has `primary_topic == T`, AND
   - No projected section has `T` in its `topics[]` array.

When any condition fails for a given topic, no synthetic section is
emitted for that topic. When all three fail globally (no ruleset, no
embedder, or every topic is already represented), the post-pass is a
no-op and the projection output is byte-identical to legacy.

**Algorithm.**
1. Gather the candidate topics: distinct anchor target-topics across the
   ruleset minus topics already represented in any section's `topics[]`
   or `primary_topic`.
2. For each candidate topic `T`:
   a. Collect the unique anchor vectors for that topic (one set per
      anchor name; dedupe by name).
   b. For each non-synthetic section in the current projection with a
      non-empty body:
      - Embed the section body once via `IRuleEmbedder.EmbedAsync`
        (cached per section id within this projection call).
      - Compute `cosine(anchor, section_body)` for every anchor.
      - Track `(section_id, anchor_name, cosine)` triples.
   c. Filter to triples whose cosine ≥ threshold.
   d. If any triples remain, group by `section_id` and keep the
      highest cosine per section. Emit one synthetic section per
      qualifying source section, ordered by `(topic_id ordinal,
      source_section_index)`.
3. Insert each synthetic section into the section list immediately
   after its source section, and update the `s_XXXXXXXX` id sequence
   so downstream code (e.g. operative selection) sees a contiguous
   list.

**Determinism requirements.**
- Same inputs → byte-identical output. Cosines rounded to 4 decimals
  before being stored. Anchor vectors are loaded from the ruleset, not
  re-embedded, so identical ruleset + identical document = identical
  cosines.
- Synthetic section ids include a per-topic counter so two topics with
  the same source section don't collide.
- Topic iteration order is deterministic: sorted by topic id ordinal.
- Tiebreak on equal cosine: lower section index wins; on equal section
  index, anchor name ordinal compare.

**Failure modes.**
- `IRuleEmbedder.EmbedAsync` throws (network error / quota / etc.):
  catch, log a warning via `ILogger`, and return the section list
  unchanged. Projection must not fail because the optional post-pass
  blew up.
- Section body is empty or whitespace: skip silently.
- Anchor vector dimensions mismatch the embedder's dimensions:
  log a warning, skip that anchor (do not throw).

**Performance.**
- Section body embedding is the dominant cost. Cache per `section_id`
  inside the post-pass.
- Skip the entire post-pass when there are no candidate topics, so
  legacy callers pay nothing.

**Acceptance.**
- New post-pass in `DeterministicContractProjector`.
- Constructor overload accepting optional `RuleSet` and `IRuleEmbedder`
  (and optional threshold); existing constructors keep working.
- DI registration in `LambdaRag.Projection.ProjectionServiceCollectionExtensions`
  picks up the ambient `IRuleEmbedder` when present (Pillar 6 wiring),
  no-op otherwise.
- Unit tests in `tests/LambdaRag.UnitTests` cover:
  - No ruleset → no synthetic sections; output byte-identical to
    legacy projector.
  - Ruleset without anchors → no synthetic sections.
  - No embedder → no synthetic sections.
  - Topic already in some `topics[]` → no synthetic sections for that
    topic.
  - Topic absent, cosine above threshold → exactly one synthetic
    section per qualifying source section, with the right id /
    primary_topic / `is_synthetic_anchor: true`.
  - Cosine below threshold → no synthetic section.
  - Embedder throws → graceful no-op, warning logged.
  - Determinism: 10 runs produce byte-identical projection output.
- `ArbPsaBenchmark` recall reaches ≥6/7 vs LLM PASS.
- False positives on LLM FAIL remains 0.
- 100-run byte-identity test passes.

**Out of scope.**
- TOC-anchored segmentation.
- Multi-pass / iterative re-classification.
- Section splitting (synthetic section's body is the full source body).
- Threshold tuning beyond an initial empirical sweep.
- Changes to non-anchor-bearing flows.

Closes #130.
