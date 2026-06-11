# Pillar 1 — Doc-kind classifier + ruleset profile gating (#116)

**Intent.** Stop running rules authored for one doc kind (e.g. contract clauses) against
a different doc kind (e.g. an ARB PSA architecture doc). Resolve `doc_kind` *before*
evaluation; skip rules whose `appliesToDocKinds` does not intersect — but always emit a
`Skipped` verdict with `reason="doc_kind_mismatch"` so the audit trail still cites the rule.

**Inputs.**
1. CLI flag `--doc-kind <id>` (highest precedence).
2. Filename / path heuristic (e.g. `samples/architecture/**` → `arb-psa`).
3. Heading-bigram classifier over the first 3 pages — deterministic signed dictionary, no LLM.

**Outputs.**
- New `VerdictOutcome.Skipped` value.
- New optional `Rule.AppliesToDocKinds` (`IReadOnlyList<string>?`).
- New optional `RuleSet.AppliesToDocKinds` (`IReadOnlyList<string>?`).
- New static `DocKindResolver` in `LambdaRag.Core`.
- `EvaluationService.EvaluateAsync(ruleset, doc, docKind?, ct)` overload.

**Edge cases.**
- Rules with no `appliesToDocKinds` apply to every kind (backward compat).
- A rule whose ruleset-level kinds disagree with its rule-level kinds is treated as union.
- Unknown doc-kind → behave as `null` (no gating).

**Acceptance.**
- All 35 existing idempotency / corpus tests stay byte-identical (no field emitted when null).
- Running ARB-PSA ruleset against a contract sample produces all `Skipped` verdicts.
- Fingerprint stays stable for rulesets that don't use the new field.

Closes #116 (in part — feature wired but Pillar 4 ruleset adds first real `appliesToDocKinds` payload).
