# Option A — Policy Compiler Spike Plan
## "LLM-as-compiler" with deterministic runtime

> Authored: 2026-06-12. Research basis: 3 parallel briefs in `files/research-1`,
> `files/research-2`, `files/research-3`. **Do not start coding inside
> lambda-rag.** This plan calls for a sandboxed spike repo first; only after
> the spike clears its exit gates do we touch the production engine.

---

## 1. Vision & success criteria

**Vision.** Move policy → lambda translation from a manual authoring step into
an LLM-driven compiler. The LLM runs **at build time**, produces frozen,
content-addressable artifacts (lambdas + tests + anchor embeddings + metadata),
and never executes at evaluation time. The runtime stays exactly as it is
today: 100% deterministic, byte-identical across replays.

**Three-pillar gates that MUST hold:**

| Pillar | Spike measurement | Target |
|---|---|---|
| **Idempotency** | Compile same policy 10×; SHA-256 of artifact bytes | ≥ 9/10 identical, 10/10 semantically equivalent (AST + behavioural) |
| **Runtime determinism** | 100-run byte-identity test on compiled artifacts | 100/100 byte-identical reports (already proven for hand-coded lambdas; must hold for compiled ones) |
| **Accuracy** | Compile 15 ARB-PSA rules; measure recall vs LLM ground truth on real PSA | ≥ 90% recall, ≤ 1 FP — beating the current 3/7 hand-coded baseline |

If the spike misses any one of these, we do not integrate. We diagnose, iterate
in the spike, or pivot.

**Stretch claim (only if the spike clears all three):** publish a CUAD score —
no commercial product has — and stake out the white space the competitive
intelligence brief identified.

---

## 2. Why this works — distilled from the research

Three findings from the parallel research threads shape the whole plan:

1. **Pattern is academically validated.** AlphaCodium (arXiv:2401.08500),
   TDAD (arXiv:2603.08806), and "Compliance-to-Code" (arXiv:2505.x, 2025) all
   converged on the same architecture: spec → {artifact + AI-generated tests}
   → iterate until tests pass. Outlines (arXiv:2307.09702) is the formal
   foundation for compile-once execute-many regex/FSM artifacts. We are not
   inventing this; we are applying it to compliance.

2. **No cloud LLM is byte-deterministic, even with `seed`.** OpenAI/Azure
   `seed` + `temperature=0` gets ~95% byte-match across calls; the rest needs
   semantic-equivalence checking. The architecturally honest answer is
   **compile once, cache forever**, keyed on `SHA256(model_snapshot_id +
   system_fingerprint + canonicalized_prompt + sampling_params)`. Re-compile
   only when the cache key changes; verify equivalence before accepting the new
   artifact.

3. **Market white space is real.** Zero commercial product (Harvey, CoCounsel,
   Spellbook, Robin, Kira, Luminance, Lexion, Evisort, Ironclad, LawGeex)
   claims byte-identical replay, 100% determinism, or publishes CUAD scores.
   LawGeex's playbook-rule architecture is closest in spirit, but their rules
   are authored manually. **LLM-as-compiler of manual playbooks is the
   evolutionary step nobody has shipped.**

---

## 3. Architecture — the pipeline

```
┌───────────────────────────── COMPILE TIME (LLM allowed) ─────────────────────────────┐
│                                                                                      │
│  policy.md  ─►  Stage 1: Atomic-clause splitter (LLM, structured output)             │
│                  output: [{ id, natural_language, applicability, severity }]         │
│                                                                                      │
│              ─►  Stage 2: Clause classifier (LLM, enum output)                       │
│                  classes: presence | threshold | structural | conditional | nested   │
│                                                                                      │
│              ─►  Stage 3: Primitive planner (LLM, registry-constrained)              │
│                  output: required primitives + proposed new-primitive specs          │
│                                                                                      │
│              ─►  Stage 4: Artifact synthesiser (LLM, schema-typed)                   │
│                  output: { lambda_text, anchor_concepts[], threshold, predicate }    │
│                                                                                      │
│              ─►  Stage 5: Test generator (LLM, schema-typed)                         │
│                  output: 3 PASS + 3 FAIL + 2 ADVERSARIAL chunks per clause           │
│                                                                                      │
│              ─►  Stage 6: VERIFIER (no LLM)                                          │
│                  • parse lambda → AST; reject if uses unregistered primitives        │
│                  • execute lambda against all 8 tests via the real runtime           │
│                  • must pass ≥ 7/8 (visible) AND ≥ 90% on hidden test split          │
│                  • compute mutation score (TDAD): inject plausible faults; measure   │
│                    detection rate. Reject if < 75%.                                  │
│                                                                                      │
│              ─►  Stage 7: Reflexion loop (LLM, ≤ 3 iterations)                       │
│                  on verifier failure, feed back failing examples + verbal            │
│                  diagnosis; regenerate artifact + tests. Bounded retry.              │
│                                                                                      │
│              ─►  Stage 8: Freeze artifact + cache                                    │
│                  emit: lambda.json, tests.json, embeddings.bin, metadata.json        │
│                  metadata includes: model_snapshot, system_fingerprint, seed,        │
│                  prompt_hash, verifier_score, mutation_score, compile_iterations    │
│                  cache key: SHA256(canonicalized prompt + model + fingerprint)       │
│                                                                                      │
└──────────────────────────────────────────────────────────────────────────────────────┘

┌───────────────────────────── RUNTIME (no LLM, ever) ─────────────────────────────────┐
│                                                                                      │
│  document.pdf ─► existing lambda-rag pipeline (parser → projector → evaluator)       │
│  + lambda.json + embeddings.bin from cache ─► verdict.json                           │
│                                                                                      │
│  byte-identical across replays; this is already proven for hand-coded lambdas        │
│  and must remain proven for compiled ones.                                           │
│                                                                                      │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

**Key architectural commitments (from the research, not invented here):**

- **Constrained-LLM extraction at every stage.** Use `instructor` (MIT) or
  `BAML` (Apache 2.0, verify) so every LLM call returns a typed Pydantic /
  schema object — never free text. Eliminates "the model produced something
  shaped weird" failure mode.
- **Primitive registry is the type system.** The LLM can only emit lambdas
  using primitives in `LambdaPrimitives.cs`. Stage 3 surfaces "missing
  primitive" as a flagged human-review item; we never let the LLM invent
  primitives at compile time.
- **External verifier, not LLM self-verification.** arXiv:2310.08118 is
  explicit: LLM self-critique *degrades* output quality vs deterministic
  verifiers. The verifier is real lambda execution against generated tests
  plus mutation testing — no LLM is allowed in the accept/reject decision.
- **Hidden test split (TDAD).** The LLM generates the visible tests; the
  spike harness adds a hidden test split (held out, withheld from the
  compiler) that gates final acceptance. Prevents test-case overfitting.
- **Reflexion-style verbal feedback on retry.** When the verifier rejects,
  feed back the *specific* failing test and a one-line verbal diagnosis.
  Bounded at 3 iterations to keep compile cost predictable.
- **Compile-once, cache forever.** Cache key includes `system_fingerprint`
  to detect silent backend drift. Cache is a git-committable SQLite +
  content-addressable blob layout.

---

## 4. The three thought experiments that have to hold

Before writing any code, walk through these adversarial scenarios. If the plan
above doesn't handle them, the architecture is wrong.

### TE-1: "The plausibly wrong lambda"
A policy clause is "RPO must be ≤ 4 hours." The LLM generates a lambda
`HasExtractedDurationNear(text, "rpo")` (presence only, missing the ≤4
constraint). The visible LLM-generated tests are:
- PASS: "RPO: 2 hours" → lambda returns true ✓
- PASS: "RPO: 4 hours" → lambda returns true ✓
- FAIL: "RPO is to be determined" → lambda returns false ✓
All visible tests pass. Lambda ships. In production, a chunk with "RPO: 48
hours" returns PASS — wrong.

**How the architecture catches this:**
1. **Stage 3 (classifier)** marks the clause as `threshold`, not `presence`.
   If Stage 4 generates a `presence` lambda for a `threshold` clause, the
   verifier rejects it on type mismatch.
2. **Hidden test split** must include adversarial-direction examples
   ("RPO: 48 hours" → should FAIL). The spike harness curates these from the
   real PSA corpus, not the LLM.
3. **Mutation testing**: synthesise variant chunks where the value violates
   the threshold; if the lambda still passes them, the mutation score
   collapses and we reject.

**If all three of those mechanisms fail simultaneously, the architecture is
genuinely broken** and we pivot. The thought experiment proves the layered
defense is meaningful.

### TE-2: "The same policy compiles two different ways"
We compile `ARB-PSA-DR-001` on Monday → lambda A. On Wednesday the LLM
backend ships a silent infra patch → `system_fingerprint` changes →
re-compile triggers → lambda B. A ≠ B textually. Which one wins?

**How the architecture handles this:**
1. AST-normalize both. If structurally identical, accept B (with metadata
   update — no behavioural change).
2. If AST-different, run BOTH against the full hidden test corpus. If both
   pass at the same rate, prefer the older artifact (stability); log B as
   an alternative for human review.
3. If B passes meaningfully better than A, escalate to human review — never
   silently swap a shipping artifact based on one compile run.
4. The cache stores `(cache_key → artifact)` AND `(policy_hash → list of
   accepted artifacts)`. We never lose a previously-validated artifact.

**The key principle:** the LLM compiler proposes; the test corpus + human
disposes. Drift is detected, never silently accepted.

### TE-3: "The unfaithful policy decomposition"
A policy section is:
> "DR & Resiliency must document RTO, RPO, and DR design including failover.
> Exception: SaaS-only architectures may rely on the vendor's stated SLA in
> lieu of an explicit RTO/RPO if the SLA is contractually attached."

The LLM compiler at Stage 1 might silently drop the exception clause —
"Silent Scope Omission" (arXiv 2606 deontic-trees paper). A SaaS-only PSA
with a vendor SLA would then fail the lambda when it shouldn't.

**How the architecture catches this:**
1. **Stage 1's structured output schema requires `exceptions: list[Clause]`.**
   Empty list is allowed but the LLM is forced to consider the field.
2. **Coverage gate**: the prompt also requires the LLM to emit a
   `verbatim_coverage` field — bytes of source text accounted for vs total.
   < 95% coverage triggers a re-prompt with the uncovered span highlighted.
3. **Conditional decomposition**: if exceptions are present, the lambda is
   not a single boolean but a tree: `main_rule OR exception_1 OR ...`. The
   classifier (Stage 2) tags this as `conditional` and routes to a different
   synthesiser template.
4. **Human review checkpoint** for any clause classified as `conditional` or
   `nested` in the spike's first phase — once we measure how often the LLM
   gets these right unaided, we can decide whether to keep the checkpoint.

---

## 5. The test harness — measurable from day 1

Build the harness BEFORE the compiler. The harness is what proves the
pattern works; the compiler is the thing under test.

### 5.1 Corpus

**Policy corpus (input):** the 15 existing ARB-PSA rules in
`rulesets/architecture-review/arb-psa.json`. Each rule already has natural
language, predicate, lambda — perfect for "compile the NL, compare the
compiled lambda to the human-authored ground truth."

**Document corpus (evaluation):** the real PSA at the existing
`PsaSamplePath` (the gitignored sample the benchmark uses) plus a synthesized
corpus of 100+ chunks generated by an LLM offline from the policy text —
labelled PASS / FAIL / ADVERSARIAL.

### 5.2 Metrics, all auto-computed by the harness

| Metric | Definition | Gate |
|---|---|---|
| **Byte-idempotency rate** | Of N compile re-runs on same policy with same `(model, seed, fingerprint)`, how many produce byte-identical lambdas | ≥ 9/10 |
| **Semantic-equivalence rate** | Of N compile re-runs, how many produce AST-equivalent OR behaviourally-equivalent lambdas (passes/fails same way on the hidden test corpus) | 10/10 |
| **Visible-test pass rate** | Per compiled artifact, fraction of LLM-generated visible tests it passes | ≥ 7/8 |
| **Hidden-test pass rate** | Per compiled artifact, fraction of human-curated held-out tests it passes | ≥ 90% |
| **Mutation score** | Per compiled artifact, fraction of injected faults the test suite detects | ≥ 75% |
| **Recall vs hand-coded lambdas** | On the real PSA, % of dimensions PASSed by compiled rules vs human rules | ≥ parity (target: better) |
| **Runtime byte-identity** | 100 re-runs of the full eval produce SHA-256-identical reports | 100/100 |
| **Compile cost** | $ per rule compiled, including reflexion retries | track, no gate (informational) |
| **Compile time** | Wall-clock per rule | track |

### 5.3 What the harness does (concrete deliverables)

The harness is a CLI tool (Python, separate repo — see §6) that:

1. **`compile-spike compile <policy.md>`** — runs all 8 compile stages,
   emits the artifact bundle to `artifacts/<policy_hash>/`.
2. **`compile-spike replay <policy.md> --n 10`** — runs Stage 1–8 ten
   times, reports byte-idempotency + semantic-equivalence rates with diffs
   on divergence.
3. **`compile-spike verify <artifact>`** — runs the artifact through the
   verifier (visible + hidden + mutation), prints scores.
4. **`compile-spike bench <ruleset.json> <document.pdf>`** — compiles ALL
   rules, runs them via the real lambda-rag evaluator on the document,
   compares verdicts to a baseline (the hand-coded rules), prints recall/FP
   deltas.
5. **`compile-spike report`** — emits the full metric matrix above as
   markdown + JSON for the go/no-go decision.

---

## 6. Spike repo layout — separate from lambda-rag

Per your explicit instruction ("before making big changes to lambda-rag"),
the spike lives in its own repo:

```
C:\projects\policy-compiler-spike\
├── README.md
├── prompt-contracts\
│   ├── 01-clause-splitter.md
│   ├── 02-clause-classifier.md
│   ├── 03-primitive-planner.md
│   ├── 04-artifact-synthesiser.md
│   └── 05-test-generator.md
├── src\policy_compiler\
│   ├── __init__.py
│   ├── stages\         # one module per pipeline stage
│   ├── cache.py        # SQLite + content-addressable blob
│   ├── verifier.py     # AST + behavioural + mutation
│   ├── reflexion.py    # bounded retry loop
│   └── cli.py
├── tests\
│   ├── unit\
│   ├── integration\
│   └── golden\         # frozen sample compilations
├── corpus\
│   ├── policies\       # 15 ARB-PSA rules, one .md each
│   ├── documents\      # symlink or copy of real PSA
│   └── hidden-tests\   # curated held-out test chunks (human-authored)
├── artifacts\          # output of compile-spike compile
├── bench-results\      # output of compile-spike bench
└── pyproject.toml      # uv/pip-compatible
```

Spike interfaces with **lambda-rag only via** (a) reading
`rulesets/architecture-review/arb-psa.json` and the PSA sample; (b)
shelling out to `dotnet test` to run compiled lambdas through the real
lambda-rag evaluator. **No lambda-rag source changes during the spike.**

---

## 7. Phased execution — exit gates between phases

Five phases, each ending with a decision gate. Stop and re-plan at any failed
gate.

### Phase 0 — Spike scaffolding (~3-5 days)
- Create `C:\projects\policy-compiler-spike\` repo with the layout above
- Set up Azure Foundry SDK auth (reuse existing credentials from lambda-rag)
- Pin `gpt-4.1-2025-04-14` (verify exact dated snapshot is GA) as compiler
  model; set `seed=42, temperature=0, top_p=1.0`
- Set up SQLite cache layer; cache key per the determinism brief
- Author **all 5 prompt contracts** (one per LLM stage) BEFORE writing code,
  per your standing preference

**Exit gate P0:** prompt contracts reviewed by you; spike repo scaffold
opens and basic LLM connectivity smoke-tests pass; `gpt-4.1` call returns
parsed structured output via `instructor`.

### Phase 1 — Single-clause end-to-end (~1 week)
- Implement Stage 1 → 8 for ONE policy clause (the same `ARB-PSA-DR-001` we
  just hand-coded)
- Implement the verifier + reflexion loop
- Author 6 hidden test chunks (3 PASS, 3 FAIL) by hand for this rule
- Run `compile-spike replay --n 10` → measure idempotency and semantic
  equivalence

**Exit gate P1:** byte-idempotency ≥ 9/10 OR semantic-equivalence 10/10;
compiled lambda passes all 6 hidden tests; mutation score ≥ 75%. If gate
fails, diagnose: cache key wrong? prompt contract under-specified?
verifier too weak? Iterate inside the spike.

### Phase 2 — Five clauses, mixed types (~1-2 weeks)
- Compile 5 clauses spanning all classifier types (presence, threshold,
  structural, conditional, nested)
- Curate hidden test chunks for each (~30 total)
- Measure all metrics from §5.2 across the 5
- Compare compiled lambdas to hand-coded ones via behavioural equivalence
  on the real PSA

**Exit gate P2:** ≥ 4/5 rules compile cleanly within 3 reflexion iterations;
hidden-test pass rate ≥ 90% across the 5; mutation score ≥ 75% across the 5;
behavioural equivalence with hand-coded lambdas ≥ 80%. If a specific
clause type fails consistently, document why — it informs whether a new
primitive is needed.

### Phase 3 — Full ARB-PSA (15 rules) (~2-3 weeks)
- Compile all 15 rules
- Run the real `ArbPsaBenchmark.cs` test suite using the compiled lambdas
  (via a temporary side-load — still no lambda-rag source changes; we just
  point the benchmark at a different ruleset JSON)
- Measure recall, FP, byte-identity

**Exit gate P3 (the real one):**
- Recall ≥ 6/7 on LLM PASS dimensions (today: 3/7)
- FPs ≤ 1 (today: 2)
- 100-run byte-identity passes (today: passes; must still pass)
- ≥ 12/15 rules compiled without human intervention
- Idempotency + semantic-equivalence holding across all 15

**This is the go/no-go gate.** If we hit it, we move to Phase 4. If we miss
on accuracy, we know exactly which rule types fail and can decide whether
to add primitives (engineering) or escalate to a stronger compiler model.

### Phase 4 — Integration into lambda-rag (~2 weeks)
Only after P3 passes. Open as a normal lambda-rag feature:
- New issue: `Pillar 9 — policy compiler integration`
- Branch `branch-lambda-policy-compiler-1`
- Add `lambda-rag compile` CLI command that invokes the spike code
  (vendored in or kept as a sibling package — TBD based on spike maturity)
- Migrate ARB-PSA ruleset to compiled form; keep hand-coded as fallback
  reference for one release
- DRAFT PR for your review/merge — same workflow as always

### Phase 5 — Publication / market posture (parallel with P4)
- Run on CUAD benchmark and publish — first product to do so
- Write a 2-page architecture whitepaper (lambda-rag.io/whitepaper)
- This is the moment the work becomes a market claim, not just an
  engineering improvement

---

## 8. Risks & mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| OpenAI/Azure changes `system_fingerprint` mid-spike, breaking idempotency measurement | M | M | Pin model snapshot + opt out of auto-upgrades + log fingerprint on every call; treat as data, not failure |
| LLM consistently fails on `conditional` / `nested` clauses | M | H | TE-3 mitigation: structured output forces consideration of exceptions; human review checkpoint in P1-P2 surfaces this early |
| Compiled lambdas pass tests but fail behaviourally on real PSA | M | H | Hidden test split + mutation testing + behavioural-equivalence check vs hand-coded baseline; Phase 2 catches this before Phase 3 commits to 15 rules |
| Compile cost is too high to be commercially viable | L | M | Track $/rule from day 1; if > $5/rule with retries we look at smaller model + better prompting; if > $20/rule architecture is wrong |
| Outlines-style FSM dependency turns out incompatible with .NET runtime | L | L | We use Outlines only if we go local-model in Phase 5+; Phases 0-4 stay on Azure cloud APIs with structured-output mode |
| Hidden test corpus has bias / blindspots we don't see | M | H | Use 2 independent humans (you + one teammate) to author hidden tests; rotate which chunks are visible vs hidden between Phases |
| Spike succeeds but integration introduces regressions in 100-run byte-identity | L | H | P4 reuses the existing `Benchmark_is_byte_identical_across_100_runs` test as the integration gate; refuse PR merge until it stays green |
| User intuition disagrees with spike findings ("this lambda looks wrong to me") | M | M | All compiler artifacts are human-readable JSON+code; reviewable in PRs; spike adds a `compile-spike explain <artifact>` command that prints the derivation chain |

---

## 9. Dependencies (from research, all permissive licenses)

- **Python ≥ 3.11** (compiler runs as a separate process; doesn't need to be
  C#-native)
- **`openai` + `azure-identity`** — Azure Foundry SDK (already in use by
  lambda-rag indirectly)
- **`instructor` (MIT)** — typed structured output extraction (research §5)
- **`pydantic`** — schemas for every stage
- **`sqlite3` (stdlib)** — compile cache
- **`hypothesis` (MPL)** — property-based test generation for mutation testing
- **`textgrad` (MIT)** — *optional, Phase 2+*, for compile-time optimization
  of anchor thresholds against the verifier metric
- **`outlines` (Apache 2.0)** — *optional, Phase 5+*, only if we self-host
  the compiler model

Citations to bake into the eventual whitepaper (from research):
- arXiv:2310.03714 (DSPy — compiler framing)
- arXiv:2307.09702 (Outlines — FSM theory)
- arXiv:2303.11366 (Reflexion — verbal retry)
- arXiv:2401.08500 (AlphaCodium — AI-generated tests)
- arXiv:2603.08806 (TDAD — visible/hidden + mutation)
- arXiv:2310.08118 (LLM self-critique limits — why verifier is external)
- arXiv:2505.x "Compliance-to-Code" (closest prior art)

---

## 10. Open questions for you (answer before Phase 0 starts)

1. **Compiler model.** Default plan is `gpt-4.1-2025-04-14` on Azure
   Foundry. Acceptable, or do you want me to compare against `gpt-5.x` once
   it's GA on Foundry?
2. **Compile cost cap.** Above what $/rule do we abandon? My default is $5;
   if you want tighter (sub-$1) we'll need to engineer prompts more
   aggressively.
3. **Hidden test authoring.** Will you author the hidden tests yourself, or
   want me to draft them from the real PSA and you review?
4. **Spike location.** Default is `C:\projects\policy-compiler-spike\` as a
   private GitHub repo under MTCMarkFranco. OK?
5. **Time horizon.** Phases 0-3 ≈ 5-7 weeks of focused work. You said you're
   away next week — when you're back, do you want to start with P0 prompt
   contracts, or do something else first?

---

## 11. What "golden" looks like

If the spike clears Phase 3:
- We have an architecture **nobody in the legal/compliance AI market has shipped**
- We can publish CUAD scores **before anyone else**
- The runtime stays as deterministic as it is today (100-run byte-identity)
- Recall climbs from 3/7 to ≥ 6/7 on the real PSA
- The compiler is reusable for any policy domain, not just ARB-PSA
- The architecture is defensible under EU AI Act Article 13 (transparency
  for high-risk AI) in a way no other product is

That is the market claim. The spike's only job is to find out whether it's
true.
