# Dependency risk note: `microsoft/RulesEngine`

> **Status:** managed risk. We have a documented contingency and a
> working spike for the swap-out path. See
> [`spikes/roslyn-eval/`](../../spikes/roslyn-eval).

## What we depend on

We use [`Microsoft.RulesEngine`](https://github.com/microsoft/RulesEngine)
as the runtime that evaluates rule lambdas (`LambdaExpression` strings)
against a fact object and returns a boolean verdict + computed outputs.
Our usage surface is intentionally narrow:

- `RulesEngine.RulesEngine` — single instance, configured per ruleset.
- `Workflow` / `Rule` — populated from our own JSON ruleset format
  (we do **not** consume the upstream JSON schema directly; we map
  to it).
- `ExecuteAllRulesAsync(string workflowName, params object[] inputs)`
  — the only entry point we call.
- Built-in expression language (lambdas over the input fact graph).

That's it. We do **not** use chaining, custom actions, the local
parameters facility, or the JSON schema validator that ships in the
package.

## Upstream state of the project

As of the time of writing, `microsoft/RulesEngine`:

- Is hosted under the Microsoft GitHub org but is community-maintained
  in practice — there is no SLA from a Microsoft product team.
- Has long stretches between releases and a small contributor pool.
- Has no public roadmap.

This is **not** a "deprecated" project — it still receives PR merges
and security updates — but it is fair to call it
**borderline-abandoned upstream** for the purpose of multi-year risk
planning. A regulated customer who has to certify their stack will
ask us about this. We need an answer.

## Why we still chose it

- It is exactly the right shape for our use case: take a lambda
  expression as a string, evaluate it against a fact, return a verdict.
- It is owned by Microsoft, which lowers the supply-chain bar for
  Microsoft-centric customers.
- The user explicitly designed `rules-iq` (the predecessor pattern)
  around it; switching off it for v1 would be expensive churn.
- Our usage surface is small enough that a swap is bounded.

## Contingency: Roslyn scripting

If upstream goes fully unmaintained or develops an unfixable
defect, we replace it with a Roslyn-scripting-based evaluator:

- `Microsoft.CodeAnalysis.CSharp.Scripting` compiles the lambda string
  into a delegate at ruleset-load time.
- A small wrapper presents the same internal interface our pipeline
  uses (`Task<RuleVerdict> EvaluateAsync(string ruleId, object fact)`).
- Compiled scripts are cached per-ruleset by content hash, so
  hot-path evaluation cost is comparable.

A working proof of this lives at
[`spikes/roslyn-eval/`](../../spikes/roslyn-eval). It is intentionally
small (~200 LOC) and answers the only two questions that matter:

1. Can we compile the same lambda strings we already store?
2. Is single-rule evaluation latency acceptable?

The spike answers **yes** to both for the lambda dialect we currently
emit (boolean expressions over a typed fact graph; no closures, no
external types beyond the fact's own).

## Estimated cost of a full swap

- Replace the `IRuleEvaluator` implementation behind our existing
  abstraction: **~2–3 engineering days.**
- Re-run the full idempotency / golden-master test suite to confirm
  byte-identical outputs across the swap: **~1 day.**
- Update docs + CHANGELOG: **~0.5 day.**

Total: **~1 working week** for one engineer with context. We are
comfortable accepting this as the contingency cost.

## Decision log

| Date       | Decision                                                                  |
|------------|---------------------------------------------------------------------------|
| Phase 0    | Keep `Microsoft.RulesEngine` for v1. Maintain Roslyn spike. Re-evaluate at v1.1. |

When this decision is revisited, append a row here — do not edit the
existing rows.
