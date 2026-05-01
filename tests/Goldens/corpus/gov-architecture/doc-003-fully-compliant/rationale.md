# doc-003-fully-compliant — rationale

## What this document tests

A clean reference architecture that addresses every one of the five
guardrails covered by this ruleset. It serves two purposes:

1. **Positive control.** If this ever produces a non-`Pass` verdict, a
   regression has been introduced in the projector or the lambda
   evaluator.
2. **Demo asset.** This is the architecture we hand to a stakeholder
   when they ask "show me what a passing review looks like."

## Expected verdict shape

All five rules: **Pass**. Score: **1.0**. No `Fail`, no `Gap`.

## Pedagogical value

It is just as important for a deterministic compliance engine to assert
that *clean* documents pass cleanly as it is to flag broken ones.
Without this test, a regression that broke `Pass` matching for, say,
audit-log retention would only be caught indirectly by a `Fail` case
also passing — much weaker signal.
