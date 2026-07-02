# The Four Pillars of Lambda-RAG

> Every new feature in lambda-rag is evaluated against these four
> non-negotiable properties. If a feature strengthens one at the cost of
> another, that trade-off must be surfaced, documented, and consciously
> approved. If a feature weakens any of them silently, it does not ship.

## The pillars

### 1. Determinism
Same code + same inputs → **byte-identical outputs**, forever. No
temperature, no seeds we can't verify, no wall-clock leakage, no
non-deterministic ordering, no LLM in the runtime decision loop. Every
byte of every emitted artifact (report, redlined docx, sidecar) must be
reproducible from the fingerprint of its inputs, in the same code
version, on any machine, for the audit window of any regulator we ever
serve.

The `docs/DETERMINISM.md` document is the operational proof of this
pillar.

### 2. Idempotency
Re-executing the same review must produce **identical outputs** — not
merely "the same verdict text" but the same JSON bytes, the same OOXML
parts, the same file hashes. Idempotency is Determinism, verified over
time.

Where LLMs must run (authoring, fact extraction), their output is
**cached in signed, fingerprinted artifacts** (rulesets, sidecars). The
cache **is** the idempotency boundary — the model beneath it is not.
Any drift in the fingerprint components must fail LOUD, never silently
recompute.

### 3. Accuracy
The verdict must reflect the document's **actual conformance to the
policy**. Passes must be earned. Fails must cite the failing evidence.
Gaps must reflect a real absence of required content. Not-Applicable
must reflect a real out-of-scope condition.

Accuracy is measured on real documents from multiple industries, not on
lab examples. The `bench-results/cross-industry-ledger.csv` is the
running receipt of accuracy across the corpus.

### 4. Flexibility  *(new — Pillar 12, 2026-07-02)*
The engine must produce **honest verdicts across arbitrary documents**
without per-document tuning. Rules, fact schemas, prompts, and gating
logic must be authored from the **policy**, not from any specific
reviewed document. A ruleset that scores 90% on Doc A and 10% on
comparably-written Doc B is a Flexibility failure, not an Accuracy
win.

**Anti-patterns that violate Flexibility:**
- Reading a sample document and adding concepts to the fact schema
  because the doc happened to discuss them
- Writing paraphrase examples in a prompt using a sample doc's
  vocabulary
- Tuning gate thresholds, floors, or tolerances until *this* document
  scores well
- Converting rules to fact-mode because their facts are visible in a
  target doc, rather than because their policy-domain warrants it
- Baking doc-kind-specific keywords into shared code paths

**Concrete tests every Flexibility-affecting feature must pass:**
- **Adversarial paraphrase**: the extractor or matcher emits the same
  fact / classification for 10+ synthetic phrasings of the same
  requirement. No overfitting to one surface form.
- **Wrong-ruleset**: running an out-of-domain ruleset (e.g. arch rules
  against a healthcare doc) resolves to ≥80% NotApplicable, near-zero
  Pass, ≤5% Fail. The system must recognize when its rules are
  irrelevant, not manufacture failures.
- **Cross-industry corpus stability**: outcome distributions across
  the `tests/Goldens/corpus/*` docs track ground-truth conformance,
  not doc verbosity or vocabulary.

## How to apply the pillars in engineering

When designing any feature, ship a prompt contract that names, in
order:

1. **Which pillar(s) the feature strengthens** and how it's measured
2. **Which pillar(s) it puts at risk** and the safeguards
3. **The falsifiable test that would prove a violation of each pillar**
4. **The by-default-off / opt-in / byte-identity story** for existing
   consumers

A feature that cannot articulate #3 for all four pillars is not
production-ready.

## History

- Pillars 1–3 (Determinism, Idempotency, Accuracy) were the founding
  design commitment, articulated in `docs/manifesto.md` and
  operationalized across Pillar 1 (doc-kind) through Pillar 11
  (fact-schema-in-ruleset).
- Pillar 4 (Flexibility) was added on 2026-07-02 during the Pillar 12
  design review, in direct response to the observation that a
  demonstrated 100% pass rate on a hand-picked rule subset on a
  single document proves the *mechanism* works but says nothing about
  whether the system *generalizes*. Flexibility exists to keep every
  future feature honest against that failure mode.
