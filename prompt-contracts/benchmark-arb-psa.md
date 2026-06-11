# Pillar Benchmark — End-to-end CTC PSA accuracy benchmark (#121)

**Intent.** Programmatically reproduce the user's original prompt — review
`samples/architecture/Example PSA - 16614 Shipping 360 ARB2 v2.1.pdf` against the
new `arb-psa.json` ruleset — and validate three acceptance gates:

1. **Recall vs LLM baseline.** Compare verdicts to `out/analysis-llm.md`. Target:
   the rules-engine PASS set is a superset of the LLM PASS set (recall on
   LLM-PASS ≥ 7/7, i.e. 100%). Plan also asks for "≥ 8/12 PASS recall";
   since the LLM only passes 7/12, that gate is unreachable on LLM-PASS alone
   — we interpret it as "≥ 8/12 total adjudicated dimensions PASS overall".
2. **Precision on LLM-FAIL set.** Zero rules-engine PASS verdicts on dimensions
   the LLM marked FAIL — precision = 100%.
3. **Byte-identical replay.** 100 consecutive runs of the same review produce
   the same `report.json` SHA-256.

**Inputs.**
- `samples/architecture/Example PSA - 16614 Shipping 360 ARB2 v2.1.pdf`
- `rulesets/architecture-review/arb-psa.json`
- `arb-psa.v1` topic map (Pillar 2)
- `out/analysis-llm.md` PASS/FAIL ground truth (12 dims)

**Outputs.**
- `tests/Goldens/arb-psa/expected-report.json` — frozen golden master that pins
  the verdict set for CI regression.
- Test file: `tests/LambdaRag.IdempotencyTests/ArbPsaBenchmark.cs`
- The benchmark itself is a Theory + Fact pair so per-acceptance-gate failures
  are individually visible.

**Edge cases.**
- If the PDF parse / projection produces fewer than 12 categorised sections,
  the benchmark records that fact instead of pretending coverage is met.
- Score thresholds are explicit constants at the top of the test file so
  iteration is just a number change, not test rewrite.
- The benchmark accepts that some LLM-FAIL dimensions may emit `Gap` (Mandatory
  rule, no section matched). Gap is treated as "not-PASS" — never as a
  PASS-on-FAIL false positive.

**Acceptance.** All three gates green; report committed under
`tests/Goldens/arb-psa/`.

Closes #121.
