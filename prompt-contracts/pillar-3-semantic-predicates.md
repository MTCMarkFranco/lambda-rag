# Pillar 3 — Semantic predicate primitives (#118)

**Intent.** Replace brittle `Contains("year")` keyword soup with three deterministic
primitives that survive paraphrase while staying byte-identical across runs.

**Inputs.**
- The rule lambda string (text passed through RulesEngine).
- For phrasebooks: the active `RuleSet.Phrasebooks` map (signed, fingerprint-folded).
- For semantic match: existing `SemanticFunctions.ContainsMeaning` /
  `MatchesAnyMeaning` already use precomputed embeddings on rules. No new primitive
  needed — but pin the embedder id in the ruleset header so a runtime mismatch
  fails loud.

**Outputs.**
- New static class `LambdaPrimitives` in `LambdaRag.Core.Semantic`, registered
  alongside `SemanticFunctions` in `WorkflowFactory.CreateReSettings`. Methods:
  - `RegexMatch(string text, string pattern)` — case-insensitive, singleline,
    200ms timeout; throws if pattern is malformed.
  - `PhraseMatch(string text, string phrasebookId)` — looks up the phrasebook
    via the active `PhrasebookAccessor.Current`; throws if missing.
  - `IsTemplateBoilerplate(string text)` (Pillar 5) — signed phrase list;
    section is boilerplate when > 30% of its words live in the phrase list
    OR any single placeholder phrase appears verbatim.
- New `RuleSet.Phrasebooks` (`IReadOnlyDictionary<string, IReadOnlyList<string>>?`),
  fingerprint-folded only when non-empty.
- New `RuleSet.EmbedderId` metadata key check in `EvaluationService`: when set
  and the active vector store's `ModelId` differs, evaluation throws so a
  drifted embedding model can never silently pass.

**Edge cases.**
- Empty text → all primitives return false (no throw).
- Unknown phrasebook id → throw (loud), never silent false.
- Boilerplate list is part of the signed primitive — never user-overridable
  at runtime; changes ship in the next ruleset version.

**Acceptance.**
- "yearly basis" should NOT match a phrasebook for "confidentiality survival period".
- "4 hour recovery objective" SHOULD match the dr_rpo phrasebook.
- "To be completed…" SHOULD trigger `IsTemplateBoilerplate`.
- Fingerprint stable for rulesets without phrasebooks.

Closes #118 (#120 Pillar 5 piggybacks on `IsTemplateBoilerplate`).
