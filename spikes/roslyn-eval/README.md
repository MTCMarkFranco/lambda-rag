# Roslyn-scripting predicate evaluator — spike

> **Status:** proof-of-concept. Not wired into the main pipeline.
> Lives here as the documented contingency for
> [`microsoft/RulesEngine`](https://github.com/microsoft/RulesEngine)
> going unmaintained. See
> [`docs/dependencies/rules-engine-risk.md`](../../docs/dependencies/rules-engine-risk.md).

## What this proves

A self-contained ~200-LOC console program that:

1. Loads a tiny inline ruleset (mirroring the lambda dialect we already
   emit — boolean expressions over a typed fact).
2. Compiles each lambda string into a delegate via
   `Microsoft.CodeAnalysis.CSharp.Scripting`.
3. Evaluates a sample fact against every rule.
4. Asserts the verdicts match the expected output and reports the
   per-rule compile / eval time.

This is enough evidence to claim — credibly, with a working artifact —
that we can swap out `Microsoft.RulesEngine` in roughly one engineer-week
without changing the wire-format of our rules.

## Run it

```powershell
cd spikes\roslyn-eval
dotnet run
```

Expected output (numbers will vary by machine):

```
PAY-001  expected=False  actual=False  compile=412ms eval=  3ms  PASS
GOV-001  expected=True   actual=True   compile= 38ms eval=  0ms  PASS
DPA-001  expected=False  actual=False  compile= 31ms eval=  0ms  PASS
all 3 rules passed
```

The first compile pays the Roslyn warm-up cost (~hundreds of ms);
subsequent compiles are fast and cached.

## What it deliberately does NOT cover

- Parity with every operator the upstream RulesEngine supports
  (we don't use most of them — see `docs/dependencies/rules-engine-risk.md`).
- Hot-reload of rulesets at runtime (we always pin a version per run).
- Sandboxing of the compiled script — Roslyn scripting is full C#.
  Production swap-in would either (a) constrain script imports to a
  whitelist via custom `ScriptOptions`, or (b) keep the upstream
  RulesEngine's expression-only sandbox. The spike runs trusted,
  authored-and-reviewed lambdas only, so this is acceptable.

## Why this lives in a spike folder, not in `src/`

`src/` is what customers consume. Spikes are throwaway proofs that
inform decisions. If we ever execute the swap, the production code
will be a fresh implementation behind our existing `IRuleEvaluator`
abstraction — not a copy-paste of this file.
