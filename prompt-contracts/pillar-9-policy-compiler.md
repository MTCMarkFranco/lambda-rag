# Pillar 9 — Policy compiler (LLM-as-compiler with deterministic runtime)

> **Type:** Research + planning prompt contract.
> Pairs with the strategic plan at [`/research/option-a-policy-compiler-plan.md`](../research/option-a-policy-compiler-plan.md)
> and the three research briefs at [`/research/`](../research/).
> **No implementation in lambda-rag itself** is in this contract's scope.

---

## Intent

Pillars 1–8 all pushed the same architecture forward: a deterministic
runtime that takes hand-authored lambdas, projects them over document
sections, and produces byte-identical reports. That runtime is stable. The
remaining bottleneck on the three pillars (idempotency, 100% determinism,
≥90% accuracy) is **not** the runtime — it is the **manual authoring of
the lambdas**. Encoding ARB-PSA's 15 rules by hand got recall from 0/7 →
3/7 over four pillars of work; manual rule authoring will not scale and
will not cross the 90% accuracy bar in reasonable time.

Pillar 9 changes the *authoring* step. An LLM runs **at compile time**,
translating natural-language policy text into the same deterministic
artifacts (lambdas + anchor concepts + thresholds + tests) the runtime
already consumes. The LLM never executes at evaluation time. The runtime
stays exactly as deterministic as it is today.

Crucially, Pillar 9 starts with a **sandboxed spike**, not a lambda-rag
feature branch. The architecture has to be proven against three falsifiable
gates before it touches the production engine.

This contract governs the **research, planning, and spike** that precede
any lambda-rag source change. Implementation in lambda-rag is gated behind
the spike clearing its Phase 3 exit gate.

---

## What we asked, and what we learned

Three independent research threads were dispatched (briefs at
`/research/01..03`). Their findings, distilled into the three claims this
contract rests on:

1. **The pattern has academic validation.** AlphaCodium (arXiv:2401.08500),
   TDAD (arXiv:2603.08806), and "Compliance-to-Code" (arXiv:2505.x, 2025)
   independently converged on the same architecture: spec →
   `{artifact + AI-generated tests}` → iterate until tests pass. Outlines
   (arXiv:2307.09702) gives the formal foundation for compile-once
   execute-many. Reflexion (arXiv:2303.11366) supplies the verbal-feedback
   retry loop. We are *applying* a known pattern to compliance, not
   inventing one.

2. **Cloud LLMs cannot be made byte-deterministic.** Azure OpenAI's own
   docs admit "it's currently not uncommon to still observe a degree of
   variability in responses" even with `seed` set. The OpenAI Cookbook
   demo: 3/5 calls byte-identical at `temperature=0, seed=123`. The only
   architecturally honest answer is **compile once, cache forever**, keyed
   on `SHA256(model_snapshot + system_fingerprint + canonicalized_prompt +
   sampling_params)`, with a re-compile gate triggered by
   `system_fingerprint` drift and a semantic-equivalence check before any
   new artifact replaces a cached one.

3. **Zero commercial product in the legal/compliance AI market claims
   byte-identical replay, 100% determinism, or has published CUAD scores.**
   Harvey, CoCounsel, Spellbook, Robin AI, Kira, Luminance, Lexion,
   Evisort, Ironclad, LawGeex — all surveyed. LawGeex's playbook
   architecture is closest in spirit, but their rules are authored
   manually. **LLM-as-compiler of manual playbooks is the evolutionary
   step nobody has shipped.**

The "Gaps and Uncertainties" sections in each brief are the known unknowns
the spike must close.

---

## Proposed architecture (in plan §3, summarised here)

```
COMPILE TIME (LLM allowed)                       RUNTIME (no LLM, ever)
─────────────────────────                        ──────────────────────
policy.md                                        document.pdf
   │                                                │
   ├─ 1. Atomic-clause splitter                     ├─ existing lambda-rag
   ├─ 2. Clause classifier                          │   pipeline (parser →
   ├─ 3. Primitive planner                          │   projector → evaluator)
   ├─ 4. Artifact synthesiser                       │
   ├─ 5. Test generator                             ├─ + lambda.json
   ├─ 6. VERIFIER (no LLM, external)                ├─ + embeddings.bin
   │     • AST type-check vs primitive registry     │     (from cache)
   │     • lambda exec vs visible+hidden tests      │
   │     • mutation score gate                      └─ verdict.json
   ├─ 7. Reflexion retry (bounded, ≤3 iters)               │
   └─ 8. Freeze artifact + cache                            ↓
         emits: lambda.json, tests.json,           byte-identical across
                embeddings.bin, metadata.json      replays (proven today
                                                   for hand-coded lambdas;
                                                   must hold for compiled)
```

**Non-negotiable architectural commitments** (each lifted from a specific
research finding):

| Commitment | Source |
|---|---|
| Constrained-LLM extraction at every stage (typed structured output, not free text) | Instructor / BAML — research §5 |
| Primitive registry is the type system; LLM cannot invent new primitives at compile time | Anti-hallucination from research §5 |
| External verifier, not LLM self-verification (arXiv:2310.08118: self-critique degrades quality) | Research brief 1 |
| Hidden test split (TDAD) — withhold held-out tests from the compiler to prevent overfitting | arXiv:2603.08806 |
| Mutation scoring — inject plausible faults, measure detection rate (gate ≥ 75%) | TDAD |
| Reflexion-style verbal feedback on retry, bounded at 3 iterations | arXiv:2303.11366 |
| Compile-once cache-forever, key includes `system_fingerprint` | Research brief 2 |
| Three-tier equivalence check (AST → behavioural → embedding) before replacing a cached artifact | Research brief 2 §6 |

---

## Spike scope (Phase 0–3) — what's IN this contract

The spike lives in a **separate repository**, `policy-compiler-spike`. It
deliberately does not touch lambda-rag source. Its interface to lambda-rag
is read-only: it reads `rulesets/architecture-review/arb-psa.json` and the
PSA sample, and it shells out to `dotnet test` to run compiled lambdas
through the real lambda-rag evaluator.

### Phase 0 — Spike scaffold
- Spike repo created under MTCMarkFranco (private)
- Azure Foundry auth wired up, `gpt-4.1-2025-04-14` pinned with
  `seed=42, temperature=0, top_p=1.0`
- SQLite cache layer with cache key per research brief 2 §7
- All **5 stage-level prompt contracts** authored in the spike repo:
  01-clause-splitter, 02-clause-classifier, 03-primitive-planner,
  04-artifact-synthesiser, 05-test-generator. Reviewed by user before any
  stage code is written.

### Phase 1 — One clause end-to-end (`ARB-PSA-DR-001`)
- All 8 stages + verifier + reflexion implemented for the one rule we
  hand-coded in Pillar 8
- 6 hidden test chunks authored by hand
- `compile-spike replay --n 10` measures idempotency + semantic
  equivalence
- **Exit gate:** byte-idem ≥ 9/10 OR semantic-equiv 10/10; hidden tests
  pass; mutation score ≥ 75%.

### Phase 2 — Five clauses, mixed classifier types
- One clause each of presence / threshold / structural / conditional /
  nested
- ~30 hidden test chunks
- Behavioural equivalence vs hand-coded lambdas measured on the real PSA
- **Exit gate:** 4/5 clean compile in ≤ 3 reflexion iters; hidden-pass ≥
  90%; behaviour-equiv with hand-coded ≥ 80%.

### Phase 3 — Full ARB-PSA (15 rules) — the go/no-go
- All 15 rules compiled
- Side-loaded into `ArbPsaBenchmark.cs` via a temporary ruleset JSON pointer
- **Exit gate (real one):** recall ≥ 6/7 on LLM PASS dimensions (today:
  3/7), FPs ≤ 1 (today: 2), 100-run byte-identity holds, ≥ 12/15 rules
  compile unaided.

---

## What's OUT of this contract (deferred until P3 clears)

- Any modification to `LambdaRag.Core`, `LambdaRag.Cli`, or any other
  lambda-rag source assembly
- Any change to the existing ruleset JSON files in
  `rulesets/architecture-review/`
- Any new GitHub workflow / CI job in lambda-rag
- Self-hosted byte-deterministic compiler model (llama.cpp / vLLM /
  MLC-LLM) — this is research brief 2 §4 territory and only becomes
  relevant in Phase 5+ if the cloud compile-and-cache pattern proves
  insufficient
- Outlines / FSM-based local evaluation — only relevant if Phase 5+ goes
  self-hosted
- CUAD benchmark run + whitepaper — Phase 5 deliverable, after lambda-rag
  integration in Phase 4
- TextGrad-based compile-time threshold optimization — Phase 2+ option
- Any go-to-market activity (sales pages, demos, public claims)

---

## Acceptance criteria for THIS contract (the research + planning artifact)

This contract is satisfied when all of the following are true:

1. **Research briefs are committed** at `/research/01..03.md` with clean
   headings (no agent-runtime preambles) and citations intact
2. **Strategic plan is committed** at `/research/option-a-policy-compiler-plan.md`
   with §1–11 complete (vision, why-it-works, architecture, thought
   experiments, test harness, spike repo layout, phased execution with
   gates, risks, dependencies, open questions, success picture)
3. **Research README is committed** at `/research/README.md` indexing all
   artifacts with academic reference list
4. **New GitHub issue exists** (Pillar 9 epic) linking to this contract
   and the plan, marking #137 as superseded
5. **PR is merged to main** so the strategic plan is the canonical source
   of truth for the spike work

Implementation acceptance (the spike's exit gates) lives in the plan
document §7, not here.

---

## Follow-ups (tracked separately, NOT in this contract)

These become real GitHub issues only after Phase 3 exit gate clears:

- **Pillar 9.A — Integrate spike compiler into lambda-rag** (becomes the
  Phase 4 feature branch `branch-lambda-policy-compiler-1`, DRAFT PR
  workflow, hand-coded lambdas kept as fallback for one release)
- **Pillar 9.B — Publish CUAD benchmark scores** (Phase 5)
- **Pillar 9.C — Architecture whitepaper** for lambda-rag.io
- **Pillar 9.D — Evaluate self-hosted byte-deterministic compiler**
  (llama.cpp single-threaded CPU path; only if cloud cache-forever proves
  insufficient)

Issues #134, #135, #136 (the Pillar 8 follow-ups) remain valid independent
of Pillar 9 and are not blocked by it. Issue #137 ("LLM compiler from
policy text") is **superseded** by this contract and the Pillar 9 epic.

---

## Open questions waiting on user input (also in plan §10)

Before Phase 0 starts:

1. Compiler model — default `gpt-4.1-2025-04-14`. OK, or compare against
   `gpt-5.x` once it's GA on Foundry?
2. Compile-cost cap — default $5/rule (above which we abandon and
   re-engineer). Tighter?
3. Hidden-test authoring — user authors, or me-drafts-user-reviews?
4. Spike repo — default `C:\projects\policy-compiler-spike\` (private,
   under MTCMarkFranco). OK?
5. Re-start priority on return from break — P0 prompt contracts first, or
   something else?
