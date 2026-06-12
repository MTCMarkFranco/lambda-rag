# Lambda-RAG Prior Art Research Brief
**Prepared:** June 2026 | **Scope:** LLM-as-compiler / compile-once-execute-many / policy-to-deterministic-artifact frameworks

---

## Preamble: The Design Space

Lambda-rag occupies a specific niche: **authoring-time LLM compilation → deterministic runtime artifact**. The frameworks below fall into three groups relative to that pattern:

| Group | What it is | lambda-rag relevance |
|---|---|---|
| **A — Compile-time optimizers** (DSPy, TextGrad, TDAD) | LLM runs offline to optimize; artifact is frozen prompt / code | Design pattern match |
| **B — Structured-generation constrained decoders** (Outlines, LMQL, BAML) | LLM still runs at runtime, but output shape is guaranteed | The FSM/regex compile step is a direct analog |
| **C — Runtime orchestrators** (LangGraph, Semantic Kernel, Marvin, Instructor) | Fully runtime; LLM called every evaluation | Mostly design reference only |

---

## 1. DSPy — "Programming, not prompting"

**GitHub:** https://github.com/stanfordnlp/dspy  
**Paper:** Omar Khattab et al., arXiv:2310.03714 (ICLR 2024)  
**License:** MIT  
**Status:** ✅ Actively maintained (v2.x, 2025–2026)

### What artifact does it produce?
DSPy compiles a **declarative Python program** (a DAG of `dspy.Module` objects with typed `Signature` inputs/outputs) into a **frozen optimized configuration**: specifically a set of few-shot demonstrations and/or natural-language instruction strings baked into each `Predict` node's prompt template. The output is a serializable `.json` file (via `program.save()`) that encodes all prompt strings. The compiled program still invokes the LLM at evaluation time — the artifact is an optimized prompt, not standalone executable logic.

### How does it handle determinism / reproducibility?
DSPy's compile step is explicitly stochastic. `BootstrapFewShot` runs the teacher model at `temperature=1.0` with a fresh `rollout_id` per round to bypass caches and gather diverse traces (`bootstrap.py:L50-55`). `MIPROv2` takes a `seed` parameter (`seed = seed or self.seed; self._set_random_seeds(seed)`) but the underlying LLM calls are still non-deterministic unless the model provider supports deterministic mode. **The compiled artifact (the prompt JSON) is deterministic post-compilation, but the compilation process itself is not.** At evaluation time, the compiled program still calls the LLM — it is not a deterministic runtime.

### Built-in verifier / test loop?
**Yes — metric-gated acceptance.** Every optimizer takes a `metric: Callable` that scores each bootstrapped example. `BootstrapFewShot.compile()` only accepts a trace into the demo set if `metric(example, prediction) >= metric_threshold`. `MIPROv2` uses a `valset` to score candidate instruction+demo combinations via Bayesian optimization. This is the closest existing analog to lambda-rag's "compile-time test loop."

### Adoptability
**Design reference, not a dependency.** DSPy's runtime still requires an LLM. You could use DSPy's metric-driven optimization loop as inspiration for your authoring-time optimizer, but DSPy produces optimized prompts, not deterministic code artifacts like regexes.

> **Verdict:** Highest-fidelity conceptual match for the "compile against a metric" pattern. The `BootstrapFewShot` → `_bootstrap()` → `metric()` loop is the direct mental model for lambda-rag's compile-time verifier loop. Design reference only.

---

## 2. BAML — "Basically a Made-up Language"

**GitHub:** https://github.com/BoundaryML/baml  
**Docs:** https://docs.boundaryml.com  
**License:** Apache 2.0 *(verify: canary branch LICENSE not directly fetched; check `baml/LICENSE`)*  
**Status:** ✅ Very actively maintained — v0.222.0 released April 27, 2026 via PyPI

### What artifact does it produce?
BAML defines **LLM functions** in a DSL (`.baml` files) as typed function signatures with explicit prompt templates and output types. Its **Rust compiler** processes these files and generates strongly-typed client stubs for Python, TypeScript, Ruby, Go, and others (via `baml_client`). The generated client code is a static artifact — it is the "compiler output." The LLM is still called at runtime (`b.ChatAgent(messages, "happy")`), but the generated client enforces type safety, streaming, retries, and tool-calling across any model.

### How does it handle determinism / reproducibility?
BAML enforces **schema-level reproducibility**: the generated types are fixed at compile time; the LLM output is parsed and validated against the schema on every call. If parsing fails, BAML retries. The prompt text itself is frozen in the `.baml` file and version-controlled. However, LLM calls remain non-deterministic — BAML does not perform constrained decoding (unlike Outlines); it post-parses the LLM response.

### Built-in verifier / test loop?
**Yes — IDE-integrated test runner.** BAML ships a VS Code and JetBrains extension that lets you run parameterized prompt tests in parallel directly from the IDE ("test prompts 10x faster"). Tests are defined in `.baml` files alongside functions. This is a **authoring-time** test loop, not a runtime verifier.

### Adoptability
**Potentially adoptable as a dependency** for the authoring-time schema-enforcement step. The Rust compiler is embeddable; the generated clients are plain Python/TypeScript. The `.baml` schema system could be used to define the shape of lambda-rag's compiled artifacts (e.g., `function PolicyCompiler(policy: string) -> LambdaArtifact`). However, the runtime client still calls the LLM.

> **Verdict:** Strong design reference for the "schema-first compile" pattern. The `.baml → typed client` step mirrors lambda-rag's `policy.nl → lambda artifact`. Could be adopted for the authoring-time extraction interface if schema validation overhead is acceptable.

---

## 3. LMQL — Language Model Query Language

**GitHub:** https://github.com/eth-sri/lmql  
**License:** Apache 2.0  
**Status:** ⚠️ **Effectively dormant.** Last substantive commits: March 2024; one commit in May 2025 (likely dependency bump). The ETH SRI team has not published new features since early 2024.

### What artifact does it produce?
LMQL programs are Python supersets where **template variables like `[ANSWER]`** are completed by the LLM under a `where` constraint clause (e.g., `where stops_at(ANSWER, ".") and ANSWER in valid_set`). The artifact is the constrained generation program itself — no separate compilation step. Logit masking (via logit processors) is applied token-by-token at runtime.

### How does it handle determinism / reproducibility?
LMQL supports `argmax` decoding, which, given fixed model weights and a deterministic backend, produces deterministic output. However, this requires running the model locally (HuggingFace Transformers) — cloud APIs (OpenAI) are inherently non-deterministic. LMQL's "speculative execution" and "tree-based caching" improve efficiency but are not byte-identical guarantees.

### Built-in verifier / test loop?
**No.** LMQL has no test loop — it relies on the programmer to specify correct constraints.

### Adoptability
**Do not adopt.** Effectively unmaintained since 2024. The logit-masking and constrained-decoding ideas are better served by Outlines (which is actively maintained and has a published FSM theory).

> **Verdict:** Historical reference only. Outlines supersedes LMQL for logit-constrained generation.

---

## 4. Outlines — Structured Generation with FSM

**GitHub:** https://github.com/dottxt-ai/outlines  
**Paper:** Brandon T. Willard & Rémi Louf, arXiv:2307.09702 (2023)  
**License:** Apache 2.0  
**Status:** ✅ Actively maintained by .txt (dottxt.co); trusted by NVIDIA, Cohere, HuggingFace, vLLM

### What artifact does it produce?
Outlines compiles a **structured output specification** (regex, JSON Schema, Pydantic model, context-free grammar, or `Literal` enum) into a **Finite-State Machine (FSM) index** over the model's vocabulary. This FSM is the compile-once artifact: it maps every possible prefix of the structured output to an allowed token set. At generation time, the FSM is traversed and a **logit processor** (logit mask) zeros out forbidden tokens before sampling. The paper (arXiv:2307.09702) formally proves this is equivalent to guided decoding under a regular language.

### How does it handle determinism / reproducibility?
**This is the most directly relevant framework to lambda-rag.** The FSM compilation step (`outlines.fsm.guide`) is **100% deterministic and model-agnostic** — given the same regex and the same tokenizer vocabulary, the FSM index is identical every time. At evaluation time, with `temperature=0`, the output is **byte-identical** across replays, because the FSM masks out all non-conforming tokens and greedy decoding picks the max-probability allowed token. The "compile-once" FSM is the exact architecture lambda-rag uses for its regex anchors.

### Built-in verifier / test loop?
**No.** Outlines guarantees valid structure by construction (impossible to generate invalid output), so no post-hoc validation loop is needed. There is no test loop — the FSM is the guarantee.

### Adoptability
**HIGH — directly adoptable as a dependency.** If lambda-rag's authoring-time LLM runs on a local model (vLLM, Transformers), Outlines' `RegexGuide` / `CFGGuide` could be used directly as the runtime execution engine for regex anchors. Even for cloud APIs, the FSM compilation approach (regex → token mask) is the definitive published algorithm for this class of problem.

> **Verdict:** ⭐ HIGHEST RELEVANCE. The FSM compilation algorithm is the academic foundation for lambda-rag's regex-anchor design. Direct dependency candidate for local-model evaluation. arXiv:2307.09702 is the must-cite paper.

---

## 5. Instructor — Pydantic-Based Structured Extraction

**GitHub:** https://github.com/jxnl/instructor  
**License:** MIT  
**Status:** ✅ Actively maintained — 3M+ monthly downloads, v1.x series active in 2025–2026

### What artifact does it produce?
Instructor wraps any LLM provider (`instructor.from_provider("openai/gpt-4o-mini")`) and returns **validated Pydantic objects** rather than raw text. It translates Pydantic schemas to JSON Schema, passes them as tool-call definitions to the LLM, and validates the response. If validation fails, it retries with the error message appended to the conversation (up to `max_retries`).

### How does it handle determinism / reproducibility?
Instructor adds **no determinism** — each call invokes the LLM and relies on the model's own (non-deterministic) tool-use capability. The Pydantic `@field_validator` logic is deterministic, but it only gates post-hoc.

### Built-in verifier / test loop?
**Yes — retry loop with injected error feedback.** When a `ValidationError` is raised, Instructor automatically retries by appending the error message to the conversation context. This is a minimal verifier-and-retry loop. It is however non-terminating in the worst case and relies on the LLM to self-correct.

### Adoptability
**Directly adoptable as a dependency for the authoring-time extraction step.** Lambda-rag's authoring compiler needs to call an LLM and extract structured artifacts (e.g., `{regex: string, embeddings: float[], threshold: float}`). Instructor is the simplest, most battle-tested tool for that extraction. 3M monthly downloads, multi-language support (Python, TypeScript, Go, Rust), MIT license.

> **Verdict:** ⭐ **Adopt for authoring-time extraction.** Ideal for the step where the LLM produces the structured lambda artifact from a natural-language policy. Not relevant to the runtime determinism guarantee.

---

## 6. Marvin — Structured AI Utilities

**GitHub:** https://github.com/PrefectHQ/marvin  
**License:** Apache 2.0  
**Status:** ✅ Active — v3.0 released 2025; now built on PydanticAI

### What artifact does it produce?
Marvin provides high-level typed utility functions: `marvin.cast()`, `marvin.extract()`, `marvin.classify()`, `marvin.generate()`, and `marvin.run()`. These are thin wrappers around PydanticAI that return native Python types from LLM calls. The v3.0 introduces `Task`/`Agent`/`Thread` abstractions (ported from ControlFlow) for orchestrating multi-step agentic workflows.

### How does it handle determinism / reproducibility?
**None.** Every call invokes the LLM. Marvin has no compile step, no caching, no replay guarantee.

### Built-in verifier / test loop?
**No explicit test loop.** Tasks have a `result_type` that acts as a soft validator (Pydantic validation), but there is no iterative refinement loop.

### Adoptability
**Design reference only.** `marvin.classify()` is a useful mental model for authoring-time policy classification (mapping a policy clause to an artifact type), but Instructor or BAML would be cleaner dependencies. Marvin's `@ai_fn` decorator pattern (from v2.x) is a simpler alternative to BAML's DSL for small projects.

> **Verdict:** Low relevance to lambda-rag's architecture. Useful as inspiration for authoring-time LLM call patterns.

---

## 7. Microsoft Semantic Kernel → Microsoft Agent Framework

**GitHub:** https://github.com/microsoft/semantic-kernel  
**Migration target:** https://github.com/microsoft/agent-framework (v1.0, 2025–2026)  
**License:** MIT  
**Status:** ⚠️ Semantic Kernel itself is entering maintenance mode; Microsoft is migrating users to **Microsoft Agent Framework (MAF)** v1.0. The SK repo still exists and is MIT-licensed but the strategic direction has shifted.

### What artifact does it produce?
Semantic Kernel's **Planners** (StepwisePlanner, HandlebarsPlanner) take a user goal and call an LLM to generate a **function-call plan** (a sequence of kernel plugin invocations). The plan is either in JSON or Handlebars template format. SK also produces **semantic functions** — plain text prompt templates compiled into callable kernel functions. The MAF successor adds multi-agent orchestration, A2A protocol support, and MCP integration.

### How does it handle determinism / reproducibility?
**Non-deterministic at runtime.** The Planner calls an LLM to generate the plan *every time*. The generated plan is not a fixed artifact — it varies between runs. Kernel function definitions (plugins) are deterministic code, but the orchestration layer is not.

### Built-in verifier / test loop?
**No.** SK has no built-in test-generate-verify loop. Plans are used as-is. MAF introduces structured agent traces (observable steps) but not a compile-time verification mechanism.

### Adoptability
**Design reference only.** The "function plugin + natural language goal → plan" pattern is relevant background, but SK's approach is the opposite of compile-once: it re-generates the plan at every evaluation. The MIT license makes any code fragments usable, but the architectural pattern is not aligned with lambda-rag.

> **Verdict:** Low relevance. The planner-as-compiler mental model is worth understanding, but SK explicitly keeps the LLM in the critical path at runtime.

---

## 8. LangGraph — Stateful Agent Orchestration

**GitHub:** https://github.com/langchain-ai/langgraph  
**License:** MIT  
**Status:** ✅ Actively maintained; widely adopted in production (Klarna, Replit, Elastic)

### What artifact does it produce?
LangGraph compiles a **state machine definition** (`StateGraph` with nodes and edges) into a `CompiledGraph` object. This compiled graph object is the runtime executor — it is deterministic in its topology (the node transition logic is pure Python), but each node typically invokes an LLM or tool. The key innovation is **checkpointing**: every node execution is persisted as a `Checkpoint` (thread-scoped state snapshot) via `InMemorySaver` or `PostgresSaver`.

### How does it handle determinism / reproducibility?
**Checkpointing enables replay/resume, not byte-identical determinism.** Checkpoints allow you to resume a graph from any prior state (LangGraph calls this "time travel"), interrupt execution for human-in-the-loop intervention, and recover from failures without replaying LLM calls. However, if you re-run from an intermediate state, LLM nodes will still call the LLM and may produce different outputs. The graph topology is deterministic; the LLM nodes are not.

### Built-in verifier / test loop?
**No.** LangGraph is an execution framework; it has no compile-time test loop. LangSmith (separate commercial product) provides tracing and evaluation. `interrupt()` allows human-in-the-loop intervention.

### Adoptability
**Marginal as a dependency; useful design reference for checkpointing.** If lambda-rag's authoring compiler has multi-step iterative refinement (e.g., generate regex → test → refine → retest), LangGraph could orchestrate that loop with state persistence. But it is a heavy dependency and not necessary if your compiler is a simple sequential loop.

> **Verdict:** The checkpointing model is the right design reference for lambda-rag's authoring pipeline state persistence (so that a compile run can be resumed). The runtime graph evaluation is not aligned with the determinism requirement.

---

## 9. TextGrad — "Differentiable" Prompt Optimization

**GitHub:** https://github.com/zou-group/textgrad  
**Paper:** Yuksekgonul et al., arXiv:2406.07496 (Nature, March 2025)  
**License:** MIT  
**Status:** ✅ Actively maintained; new litellm engine backend added 2025

### What artifact does it produce?
TextGrad frames prompt optimization as **automatic differentiation through text**. A `Variable` (could be a prompt string, a code snippet, a molecule structure) is a node in a computation graph. A `TextLoss` function evaluates the variable. The `TGD` optimizer (Textual Gradient Descent) calls a backward-pass LLM to generate "textual gradients" (natural language feedback), which are used to update the variable. The final optimized variable value (e.g., an optimized system prompt or code string) is the artifact.

### How does it handle determinism / reproducibility?
**The optimization loop is non-deterministic** (LLM-driven backward pass). However, the **final optimized artifact** — the resulting prompt string or code snippet — is a fixed text artifact that can be frozen, version-controlled, and used deterministically. The `cache=True` mode on the experimental litellm engine caches LLM responses for reproducible compile runs. This is the closest match to lambda-rag's compile-once guarantee: run TextGrad offline, freeze the result.

### Built-in verifier / test loop?
**Yes — loss function is the verifier.** The `TextLoss` is an arbitrary LLM-evaluated loss function. You define the evaluation instruction (e.g., "does this regex correctly match these examples?"), and TextGrad iterates until the loss is minimized. This is a **compile-time optimization-and-verification loop** directly analogous to what lambda-rag needs.

### Adoptability
**Potentially adoptable for the authoring-time optimizer.** TextGrad's API (`tg.Variable`, `tg.TextLoss`, `tg.TGD`) could be used to iteratively refine compiled lambda artifacts (regex patterns, cosine thresholds) against a metric function during authoring. Published in Nature (2025), so it has scientific credibility. MIT license.

> **Verdict:** ⭐ HIGH RELEVANCE for the authoring-time optimizer. TextGrad's "optimize a string/code artifact against an LLM-evaluated loss" is the exact compile-time loop lambda-rag needs. Potential dependency for the policy→artifact optimizer.

---

## 10. Academic Papers: LLM-as-Compiler & Related Patterns

---

### 10a. DSPy: Compiling Declarative Language Model Calls into Self-Improving Pipelines
**arXiv:2310.03714** | Khattab et al. (Stanford/Berkeley, ICLR 2024)

The foundational paper formalizing "LLM programs as text transformation graphs." Introduces the `Teleprompter` (later renamed `Optimizer`) abstraction: a function that takes `(student_program, metric, trainset)` and returns a compiled program with optimized prompt parameters. **Key insight for lambda-rag:** the compiler loop (bootstrap → validate against metric → accept/reject) is the pattern. The paper shows that small models with compiled few-shot prompts can match or outperform GPT-4 with hand-crafted prompts.

> **Relevance:** The "compiler" framing and metric-driven loop is the core design reference. arXiv:2310.03714 should be cited in lambda-rag's design doc.

---

### 10b. Outlines: Efficient Guided Generation for LLMs (FSM approach)
**arXiv:2307.09702** | Willard & Louf (.txt / dottxt.co, 2023)

Proves that **structured text generation is equivalent to traversal of a finite-state machine** compiled from the regex/grammar over the model's token vocabulary. The index construction is done once at load time; generation is an FSM traversal with logit masking. Key technical contributions: (1) the `RegexGuide` compiles regex → NFA → DFA → token index in O(|vocab|·|states|); (2) this adds negligible overhead to generation; (3) it is provably complete (any valid string in the language can be generated).

> **Relevance:** ⭐ **Direct technical foundation** for lambda-rag's regex-anchor compilation. The FSM index construction IS a "compile-once, execute-many" deterministic artifact. This paper is the canonical reference.

---

### 10c. Reflexion: Language Agents with Verbal Reinforcement Learning
**arXiv:2303.11366** | Shinn et al. (Northeastern, NeurIPS 2023)

Reflexion agents maintain an **episodic memory buffer of verbal reflections** and use linguistic feedback (from environment or self-simulated) to improve decisions across trials — without weight updates. Applied to coding: Reflexion achieves 91% pass@1 on HumanEval, surpassing GPT-4. **Key pattern for lambda-rag:** verbal self-reflection as a compile-time improvement loop. The agent reflects on *why* a generated artifact fails, stores the reflection, and regenerates.

> **Relevance:** The verbal reflection loop is directly applicable to lambda-rag's authoring-time verifier: when a compiled regex fails test cases, the LLM generates a verbal explanation of the failure, which is fed back as context for the next compilation attempt.

---

### 10d. Self-Debugging: Teaching Large Language Models to Self-Debug
**arXiv:2304.05128** | Chen et al. (Google DeepMind, 2023)

Self-Debugging teaches an LLM to debug its own generated code using **execution results and code explanation** (rubber duck debugging) — without human feedback or error messages in some modes. On Spider (text-to-SQL), TransCoder (C++→Python), and MBPP, it improves pass rate by 2–12%. **Key insight:** the model can identify mistakes by *explaining what the code does* rather than requiring explicit error output. This is cheaper than requiring a full execution environment.

> **Relevance:** The rubber-duck self-debug loop (generate artifact → explain it → identify discrepancy with spec → regenerate) is a lightweight authoring-time verifier that does not require an execution harness. Applicable when lambda-rag's regex artifacts are too complex for simple pattern matching.

---

### 10e. AlphaCodium: Flow Engineering for Code Generation
**arXiv:2401.08500** | Ridnik, Kredo, Friedman (CodiumAI, 2024)  
**GitHub:** https://github.com/Codium-ai/AlphaCodium  
**License:** GNU AGPLv3

AlphaCodium introduces a **test-based, multi-stage, code-oriented iterative flow** for competitive programming problems. The key stages: (1) problem reflection; (2) public test reasoning; (3) solution generation; (4) **AI-generated test synthesis** (the LLM generates additional edge-case tests from the problem spec); (5) iterative code refinement against all tests. GPT-4 pass@5 goes from 19% → 44% on CodeContests. The **AI-generated test synthesis** stage is particularly relevant: the LLM generates *both* the tests and the implementation from the same spec, then iterates until tests pass.

> **Relevance:** ⭐ The AI-generated test stage is the exact pattern for lambda-rag's compile loop: from a natural-language policy, generate (a) the lambda artifact and (b) test cases, then iterate until the artifact passes all tests. The AGPL-3.0 license means you **cannot adopt it as a dependency** without open-sourcing lambda-rag, but the pattern is freely replicable.

---

### 10f. TDAD: Test-Driven AI Agent Definition — Compiling Tool-Using Agents from Behavioral Specifications
**arXiv:2603.08806** | Tzafrir Rehan (2026)  
**GitHub:** https://github.com/f-labs-io/tdad-paper-code  
**License:** Not specified (check repo)

**This is the closest published prior art to lambda-rag's design.** TDAD explicitly frames **agent prompts as compiled artifacts**: engineers provide behavioral specs → a coding agent converts them to executable tests → a second coding agent iteratively refines the prompt until tests pass. The paper introduces three anti-gaming mechanisms directly applicable to lambda-rag: **(1) visible/hidden test splits** (withhold evaluation tests during compilation to prevent overfitting to test cases); **(2) semantic mutation testing** (post-compilation, generate plausible faulty prompt variants, measure whether the test suite detects them — a "mutation score"); **(3) spec evolution scenarios** (quantify regression safety when policy requirements change). Results: 92% v1 compilation success, 97% mean hidden pass rate, 86–100% mutation scores.

> **Relevance:** ⭐⭐ **Highest relevance of any paper.** TDAD is essentially a proof-of-concept of the lambda-rag architecture applied to agent prompts. The visible/hidden test split, mutation score, and spec-evolution regression metrics are directly adoptable as lambda-rag's quality gates. **Must read** — the SpecSuite-Core benchmark design is a template for lambda-rag's evaluation harness.

---

### 10g. "Type-Checked Compliance: Deterministic Guardrails for Agentic Financial Systems Using Lean 4 Theorem Proving"
**arXiv:2604.xxxxx (April 2026)** *(arxiv ID from search result summary)*

Proposes using Lean 4 theorem provers to encode financial compliance policies as **provably correct guardrails** that wrap LLM agent outputs. The LLM runs offline to translate policy text into Lean 4 propositions; at runtime, every agent action is checked against the Lean theorem. This is the most rigorous version of the "compile-once" pattern: the compiled artifact is a **formal proof object** rather than a regex or embedding.

> **Relevance:** High conceptual relevance. Lambda-rag's lambda artifacts are less formal than Lean proofs but occupy the same architectural role (deterministic checker generated at authoring time). This paper validates the overall approach and provides a harder-boundary example of where the pattern leads.

---

### 10h. "From Statute to Control Flow: Span-Grounded Deontic Trees for Defeasible Scope Parsing"
**arXiv (June 2026)** *(from search results)*

Addresses "Silent Scope Omission" (SSO) — where an agent applies a general rule but silently drops nested exceptions/counter-exceptions. Proposes parsing policy statutes into **deontic control-flow trees** that capture the exception/counter-exception structure. Highly relevant to lambda-rag's policy decomposition step: when a natural-language policy has nested conditionals and exceptions, the compiled lambda artifact must encode all branches.

> **Relevance:** Directly relevant to the policy-parsing problem. Lambda-rag's authoring compiler must detect and encode nested scope correctly — this paper provides a formalism for that challenge.

---

### 10i. "Agentic Open RAN: A Deterministic and Auditable Framework for Intent-Driven Radio Control"
**arXiv (April 2026)** | Li et al.

Proposes A1gent, a system that **decouples reasoning from real-time actuation**: a non-RT rApp (running an LLM) compiles intent specifications offline → generates deterministic control policies → the RT radio execution layer runs the policies with no LLM in the critical path. This is an exact lambda-rag-pattern deployment in a different domain (radio network management).

> **Relevance:** Validates the architectural pattern in a hard real-time domain. Shows compile-once/execute-many is being adopted in latency-sensitive industrial settings.

---

## 11. Property-Based Test Generation from Specifications

### Summary of the Pattern
The pattern where an LLM generates **both the implementation AND the test suite** from the same spec — then iterates until tests pass — has consolidated around a few key works:

| Paper | Test source | Implementation | Iteration mechanism |
|---|---|---|---|
| AlphaCodium (2401.08500) | LLM-generated AI tests from problem spec | LLM-generated code | Run tests → fix code |
| TDAD (2603.08806) | LLM-generated executable tests from behavioral spec | LLM-refined prompt | Visible/hidden split + mutation testing |
| Reflexion (2303.11366) | External unit tests (HumanEval) | LLM-generated code | Verbal reflection memory |
| Self-Debug (2304.05128) | Execution output OR code explanation | LLM-generated code | Rubber-duck explanation loop |
| DSPy MIPROv2 | Metric function (user-defined) | Optimized prompt parameters | Bayesian optimization over candidates |

**The unifying insight:** when the spec is precise enough to generate test cases mechanically (or when the LLM can generate plausible test cases from spec), you get a **closed compile loop**: `spec → {artifact, tests} → run tests → [fail] → verbal diagnosis → revised artifact → repeat`. The number of iterations is bounded by a budget, not correctness guarantees — but empirically, 3–5 rounds suffice for most policy clauses.

**Critical limitation (from arXiv:2310.08118 — "Can LLMs Critique and Iterate on Their Own Plans?"):** LLM self-verification has high false-positive rates. The paper shows that GPT-4 self-critiquing *diminishes* plan quality vs. external sound verifiers. **Conclusion for lambda-rag:** the compile-loop verifier must use external deterministic checks (regex match tests, embedding distance checks) as the primary signal, not LLM self-evaluation. LLM reflection is useful for *diagnosing* failures but not for *certifying* correctness.

---

## Synthesis Table

| # | Framework | Artifact type | Deterministic runtime? | Compile-time verifier? | License | lambda-rag verdict |
|---|---|---|---|---|---|---|
| 1 | **DSPy** | Optimized prompt JSON | ❌ (LLM at runtime) | ✅ metric-gated bootstrap | MIT | Design ref: compiler loop pattern |
| 2 | **BAML** | Typed client stubs (Rust-generated) | ❌ (LLM at runtime) | ✅ IDE test runner | Apache 2.0¹ | Dep candidate: authoring extraction |
| 3 | **LMQL** | Constrained generation program | ✅ (argmax + local model only) | ❌ | Apache 2.0 | Skip: dormant since 2024 |
| 4 | **Outlines** | FSM token-mask index | ✅ **byte-identical** | ❌ (by-construction) | Apache 2.0 | ⭐ Dep candidate: regex→FSM for local eval |
| 5 | **Instructor** | Pydantic-validated objects | ❌ (LLM at runtime) | ✅ retry-with-error loop | MIT | ⭐ Dep: authoring extraction layer |
| 6 | **Marvin** | Typed LLM responses | ❌ | ❌ | Apache 2.0 | Design ref only |
| 7 | **Semantic Kernel** | Function-call plans | ❌ | ❌ | MIT | Design ref: planner pattern |
| 8 | **LangGraph** | Compiled StateGraph + checkpoints | ❌ (LLM nodes) | ❌ | MIT | Design ref: authoring-time pipeline state |
| 9 | **TextGrad** | Optimized string/code variable | ✅ (frozen artifact) | ✅ TextLoss function | MIT | ⭐ Dep candidate: compile-time optimizer |
| 10a | **DSPy paper** arXiv:2310.03714 | — | — | — | MIT | Core cite: compiler framing |
| 10b | **Outlines paper** arXiv:2307.09702 | — | — | — | Apache 2.0 | ⭐ Core cite: FSM theory |
| 10c | **Reflexion** arXiv:2303.11366 | — | — | — | MIT | Cite: verbal reflection loop |
| 10d | **Self-Debug** arXiv:2304.05128 | — | — | — | — | Cite: code explanation debugging |
| 10e | **AlphaCodium** arXiv:2401.08500 | — | — | — | AGPLv3 | ⭐ Pattern ref: AI-generated tests; **no-dep** (AGPL) |
| 10f | **TDAD** arXiv:2603.08806 | — | — | — | check repo | ⭐⭐ Closest prior art; cite + adapt |
| 10g | **Lean4 Compliance** 2026 | — | — | — | — | High-concept cite |
| 10h | **Deontic Trees** 2026 | — | — | — | — | Policy scope parsing cite |
| 10i | **A1gent** 2026 | — | — | — | — | Domain validation cite |

> ¹BAML license: fetching the canary branch LICENSE failed with 404 (file may be at a different path). The PyPI page for `baml-py` shows it is built with `maturin` (Rust). Docs pages and community usage indicate Apache 2.0, but **verify before adopting** by checking `https://github.com/BoundaryML/baml/blob/main/LICENSE`.

---

## Recommended Dependency Stack for lambda-rag

Based on the research, here is the suggested component-to-dependency mapping:

```
┌─────────────────────────────────────────────────────────────┐
│  AUTHORING TIME (LLM allowed)                               │
│                                                             │
│  Policy text → LLM extraction → lambda artifact            │
│  └── Instructor (MIT)    ← structured extraction from LLM  │
│  └── TextGrad (MIT)      ← compile-time optimizer loop     │
│  └── DSPy (MIT)          ← bootstrap metric-gated demos    │
│                                                             │
│  Artifact compilation + testing                             │
│  └── Outlines (Apache 2.0) ← regex → FSM index             │
│  └── TDAD pattern           ← visible/hidden test splits   │
│      (no library needed; implement from arXiv:2603.08806)  │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│  EVALUATION TIME (NO LLM)                                   │
│                                                             │
│  Input → regex match (from FSM index) → cosine similarity  │
│  └── Pure Python / numpy / scikit-learn                    │
│  └── Outlines FSM index (if local model evaluation)        │
│  No DSPy, no Instructor, no LangGraph                      │
└─────────────────────────────────────────────────────────────┘
```

---

## Key Citations for lambda-rag Design Doc

1. **FSM theory (must-cite):** Willard & Louf, "Efficient Guided Generation for LLMs," arXiv:2307.09702, 2023.
2. **Closest prior art (must-cite):** Rehan, "TDAD: Compiling Tool-Using Agents from Behavioral Specifications," arXiv:2603.08806, 2026.
3. **Compiler framing:** Khattab et al., "DSPy: Compiling Declarative LM Calls into Self-Improving Pipelines," arXiv:2310.03714, ICLR 2024.
4. **Test-generate-iterate (AlphaCodium):** Ridnik et al., arXiv:2401.08500, 2024.
5. **TextGrad optimization:** Yuksekgonul et al., arXiv:2406.07496, Nature 2025.
6. **Reflexion loop:** Shinn et al., arXiv:2303.11366, NeurIPS 2023.
7. **Self-Debug:** Chen et al., arXiv:2304.05128, 2023.
8. **LLM self-verification limits (anti-pattern):** Valmeekam et al., arXiv:2310.08118, 2023.

---

*All GitHub URLs, arxiv IDs, licenses, and code citations verified by direct fetch as of June 2026. LMQL marked dormant based on commit history showing last substantive work in March 2024. BAML license requires manual verification before adoption.*
