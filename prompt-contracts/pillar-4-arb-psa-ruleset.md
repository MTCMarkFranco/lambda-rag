# Pillar 4 — ARB-PSA ruleset authoring (#119)

**Intent.** Author the ruleset that turns the new primitives into real coverage.
Beats the LLM baseline on PSA review (`out/analysis-llm.md`: 7 PASS, 5 FAIL across
12 dims) while staying deterministic.

**Inputs.**
- `policies/CTC/CTC EA Information-all-policies.pdf` — source policy.
- `out/analysis-llm.md` — the 12 dimensions and what "PASS" means for each.
- ARB-PSA topic map (Pillar 2): every section the projector emits carries
  `category` ∈ {12 dimensions}, so predicates can be written as
  `input1.category == "<dim>"`.

**Outputs.**
- `rulesets/architecture-review/arb-psa.json` — ~15 rules, all gated to
  `appliesToDocKinds: ["arb-psa"]`. Mix of:
  - Section-presence rules (one per dim) — `applicability: Mandatory` so
    missing dims emit a `Gap`.
  - Quality-floor rules — minimum content size + boilerplate-free check using
    `LambdaPrimitives.IsTemplateBoilerplate`.
  - Standards-alignment rules — `LambdaPrimitives.PhraseMatch(text, "nist_pci")`
    and friends.
  - DR rules — `LambdaPrimitives.PhraseMatch(text, "dr_rpo")` catches "4 hour
    recovery objective" even when "RPO" isn't present.
  - IaC rules — `LambdaPrimitives.PhraseMatch(text, "iac_tools")`.

- One signed phrasebook bundle declared at the ruleset level:
  `dr_rpo`, `nist_pci`, `iac_tools`, `pattern_library`, `governance_evidence`.

**Edge cases.**
- A section that exists but is >30% boilerplate → FAIL (not PASS), proving
  Pillar 5.
- A section that exists with adequate non-boilerplate content but lacks
  required standards reference → FAIL.
- A section the PSA doesn't have at all → Gap (Mandatory) — counts against the
  score, which is how the rules engine matches the LLM's "FAIL" verdict.

**Acceptance.**
- ≥ 8/12 PASS recall vs the LLM baseline.
- 0 false positives on LLM's FAIL set.
- `RuleSelfValidator.ValidateStructural(ruleset).Count == 0` (every rule has
  evidenceQuote + sourceSpan).
- Hand-written; no LLM author run needed for this PR.

Closes #119, closes #120 (template-detector usage shipped here).
