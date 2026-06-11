# Determinism — How Lambda-RAG Guarantees Byte-Identical Output

This document is the proof that Lambda-RAG's runtime is deterministic.
It exists so a legal or audit reviewer can satisfy themselves that the
verdicts produced for a given (document, ruleset) pair are reproducible.

## Definitions

- **Idempotent run.** Re-executing `lambda-rag review` on the same
  input bytes (document + ruleset) and the same code version produces
  byte-identical outputs (`report.json` and reviewed `.docx`).
- **Auditable verdict.** Every verdict carries: the rule id+version,
  the ruleset id+version, the matched source span, the lambda text, and
  the input values that were fed to RulesEngine.

## The five guarantees

### 1. Inputs are content-hashed
`ContentHash` (SHA-256, `lr1:` prefix, NFC-normalized) is computed for
- the source document bytes,
- the ruleset (RuleSet.Fingerprint = ordered hash of each rule's
  fingerprint = hash of rule id+version+lambda+schema+severity),
- the projector id+version,
- the system prompt(s) used at authoring (if any).

Any change to any of these forces a fresh evaluation.

### 2. Projection is deterministic and cached
The default projectors are pure code (no LLM). They walk the parsed
document in fixed order and apply rule-based classification. Output
JSON is canonical (sorted keys, indented, `UnsafeRelaxedJsonEscaping`).
Optional LLM-assisted projectors run with temperature=0 and their
results are cached in SQLite under
`hash(doc_bytes ⊕ projector_id ⊕ projector_version ⊕ model_id ⊕
prompt_hash ⊕ schema_hash)`. A cache hit returns the original bytes.

### 3. Selector match is pure code
Selectors are a small DSL (`path`, `regex`, `hasField`, `valueIn`,
`all`, `any`, `not`). The matcher is implemented in C# with no
external state. Match order is fixed by JSONPath traversal of the
projected graph.

### 4. RulesEngine evaluation is pure
For each (rule, matched section) we build a one-rule Workflow with a
fixed `WorkflowName = "lambda-rag.rule"` and `RuleName = "rule"`,
convert the matched JSON to an `ExpandoObject`, and call
`ExecuteAllRulesAsync`. RulesEngine itself is deterministic given the
inputs; we never cross-evaluate or share state across rules.

Custom types registered on every workflow:

- `LambdaRag.Core.Semantic.SemanticFunctions` — `ContainsMeaning` /
  `MatchesAnyMeaning` against precomputed vectors (authoring-time).
- `LambdaRag.Core.Semantic.LambdaPrimitives` (Pillar 3 / 5, #118 / #120)
  — `RegexMatch`, `PhraseMatch`, `IsTemplateBoilerplate`. All pure
  code, no I/O. `PhraseMatch` resolves phrasebooks via the per-evaluation
  `PhrasebookAccessor` which the engine populates from
  `RuleSet.Phrasebooks` — and folds them into the ruleset fingerprint
  when present so a phrasebook change is a content-addressed change.
- **Pillar 6 semantic anchors (#124).** Tokens are produced by
  `SemanticTokenizer` whose `TokenizerVersion` and `StopwordHash`
  (SHA-256 of the signed `stopwords-en.v1.txt` list) are pinned. Anchor
  embeddings flow through the same file-backed embedding cache as
  section vectors; cache keys fold `(tokenizer_version, embedder_id,
  text)` so a drift in any of the three invalidates entries rather than
  silently mixing. Bindings are pure cosine math; every binding is
  emitted in `Verdict.SemanticBindings` with `(anchor, matched, cosine,
  span)` so an auditor can replay the computation from bytes alone.
  When a rule declares no `semanticAnchors[]`, the entire binding code
  path is skipped and report bytes remain byte-identical to the
  pre-Pillar-6 baseline (proven by `AdditiveGuaranteeTests`).

### 5. Markup is deterministic
- Annotations are sorted by `(span.charStart, annotation.id)` before
  application.
- Comment ids are derived from the run-stable counter (annotations
  are already in stable order, so the counter is deterministic).
- The OOXML `w:date` attribute on tracked changes uses a fixed UTC
  timestamp `2000-01-01T00:00:00Z` so file diffs are reproducible.

## Verdict id derivation

```
Verdict.Id = ContentHash.Compose(
    "verdict",
    rule.Id, rule.Version,
    ruleSet.Id, ruleSet.Version,
    span.DocumentId, span.CharStart, span.CharLength,
    outcome)
```

The verdict id is therefore a function of (rule × ruleset × source
span × outcome) — re-running yields the same ids in the same order.

## Score formula

```
score = pass / (pass + fail)
```

`NotApplicable` and `Error` outcomes are excluded from the
denominator. Score is `1.0` when the denominator is zero (the
ruleset had nothing applicable to the document).

## What we do NOT guarantee

- **Cross-version determinism.** Upgrading any of: this codebase, the
  Microsoft RulesEngine NuGet, .NET runtime, or the projector model
  may change verdicts. That's why every artifact records the versions
  it was produced under.
- **NL-quality of authoring.** Rule extraction quality depends on
  the policy document, the prompt, and the LLM. We mitigate by
  requiring authoring runs to pass a JSON-schema validator and by
  exposing diff tooling for human review before publish.

## How to verify

```pwsh
dotnet test tests/LambdaRag.IdempotencyTests
```

This runs the full pipeline twice on the bundled samples and asserts
SHA-256 equality of every output file.
