# `/research` — Strategic research artifacts

This folder collects **non-code, pre-implementation research and planning
artifacts**. Nothing here ships at runtime. Documents here exist to inform
prompt contracts, GitHub issues, and design decisions before any feature
branch is opened.

> **Privacy note.** The lambda-rag repo is private as of 2026-06-12 because
> this folder may contain competitive analysis, unpublished IP, and
> internal strategic positioning. Treat everything in `/research` as
> internal-only.

---

## Index

| File | Purpose |
|---|---|
| [`option-a-policy-compiler-plan.md`](option-a-policy-compiler-plan.md) | The bulletproof spike plan for "LLM-as-compiler" — vision, 8-stage architecture, 3 thought experiments, test harness, 5 phased exit gates, risks, dependencies, open questions for the user |
| [`01-llm-compiler-frameworks.md`](01-llm-compiler-frameworks.md) | Prior-art research — frameworks (DSPy, BAML, LMQL, Outlines, Instructor, Marvin, SK, LangGraph, TextGrad) and academic papers (AlphaCodium, TDAD, Reflexion, Self-Debug). Recommends dependency stack and identifies anti-patterns |
| [`02-llm-determinism.md`](02-llm-determinism.md) | Research on how/whether LLM APIs can produce deterministic output — OpenAI/Azure `seed` + `system_fingerprint`, Azure Foundry Provisioned SKUs, vLLM/llama.cpp/TGI/MLC-LLM byte-identity, caching strategies, semantic-equivalence checking |
| [`03-legal-ai-sota.md`](03-legal-ai-sota.md) | Competitive intelligence on the legal/compliance AI market — Harvey, CoCounsel, Spellbook, Robin AI, Kira, Luminance, Lexion, Evisort, Ironclad, LawGeex, Microsoft Purview, plus academic SOTA (CUAD, LegalBench, Compliance-to-Code) and a determinism scorecard |

---

## How these documents were produced

Three independent research threads were dispatched in parallel (June 2026)
using a research subagent with the following scopes:

1. **LLM-as-compiler frameworks** — survey published frameworks and papers
   that frame an LLM as a compiler/optimizer producing deterministic
   downstream artifacts; identify dependency candidates and anti-patterns.
2. **LLM determinism** — survey what each major LLM platform actually
   guarantees about output reproducibility, how `seed` and `system_fingerprint`
   work in practice, and what self-hosted paths exist to true byte-identity.
3. **Legal/compliance AI SOTA** — survey shipping commercial products,
   their architectures, accuracy claims, determinism posture, audit
   capabilities, and the academic frontier. Identify market white space.

Each brief was synthesized end-to-end by the subagent from primary sources
(arXiv, official docs, vendor sites, blog posts) with citations preserved.
Raw briefs are committed as-is for full traceability; the
`option-a-policy-compiler-plan.md` synthesizes their findings into a single
actionable plan.

---

## Key academic references (cited across briefs)

### Compiler / artifact-generation pattern
- **DSPy: Compiling Declarative Language Model Calls into Self-Improving Pipelines** — arXiv:2310.03714 (Khattab et al., 2024). Framework that treats prompting as compilation; metric-gated bootstrap pattern.
- **Outlines: Provably Correct Structured Output via Finite Automata** — arXiv:2307.09702 (Willard & Louf, 2023). Compiles regex/CFG into FSMs over LLM vocabulary; the formal foundation for compile-once-execute-many.
- **AlphaCodium: Code Generation as a Test-Based, Multi-Stage, Iterative Flow** — arXiv:2401.08500 (Ridnik et al., 2024). AI-generated test synthesis stage; GPT-4 pass@5: 19% → 44% on CodeContests.
- **Reflexion: Language Agents with Verbal Reinforcement Learning** — arXiv:2303.11366 (Shinn et al., 2023). Verbal feedback retry loop; the basis for bounded reflexion retries in the compiler.
- **TDAD: Test-Driven AI Development** — arXiv:2603.08806 (2026). Visible/hidden test splits, semantic mutation testing, spec evolution scenarios. Reports 92% v1 compile success, 97% hidden pass, 86–100% mutation scores.
- **The Limits of LLM Self-Critique** — arXiv:2310.08118. Empirical evidence that LLM self-verification has high false-positive rates; verifiers must be external/symbolic.

### Compliance / legal / domain-specific
- **Compliance-to-Code: Enhancing Financial Compliance Checking via Code Generation** — arXiv:2505.x (Li et al., 2025/2026). Direct prior art for converting compliance rules into executable code for deterministic verification.
- **Trace2Policy: From Expert Behavior Traces to Self-Evolving Decision Agents** — arXiv:2506.x (Zha et al., June 2026). EISR (Error-driven Iterative Skill Refinement) maintains human-readable policy representation.
- **CUAD: An Expert-Annotated NLP Dataset for Legal Contract Review** — arXiv:2103.06268 (Hendrycks et al., NeurIPS 2021). 510 commercial contracts, 41 clause types, 13k+ annotations. The canonical benchmark for contract clause extraction; no commercial product has published scores.
- **LegalBench: A Collaboratively Built Benchmark for Measuring Legal Reasoning in Large Language Models** — arXiv:2308.11462 (Guha et al., 2023). 162 legal reasoning tasks across 6 reasoning types.
- **Better Call GPT: Comparing LLMs Against Lawyers** — arXiv:2401.16212 (2024). GPT-4 matches/exceeds junior lawyers on contract review accuracy with 99.97% cost reduction.

### RAG / hallucination
- **GraphRAG: From Local to Global** — arXiv:2404.16130 (Microsoft Research, 2024).
- **RAGTruth: A Hallucination Corpus for Retrieval-Augmented Generation** — arXiv:2401.00396 (2024). ~18k annotated responses; even with RAG, LLMs produce unsupported claims.

### Determinism / infrastructure
- **OpenAI Cookbook: Reproducible Outputs with the Seed Parameter** — `cookbook.openai.com/examples/reproducible_outputs_with_the_seed_parameter`. Empirical: at `temperature=0, seed=123`, 3/5 calls byte-identical; avg embedding distance 0.0449 (seeded) vs 0.1137 (unseeded).
- **Azure OpenAI Reproducible Output docs** — `learn.microsoft.com/en-us/azure/ai-services/openai/how-to/reproducible-output`. Explicit acknowledgment: "Even in cases where the seed parameter and `system_fingerprint` are the same across API calls it's currently not uncommon to still observe a degree of variability in responses."
- **Azure Model Retirements / Lifecycle** — `learn.microsoft.com/en-us/azure/ai-services/openai/concepts/model-retirements`. 18-month GA fixed lifecycle; "runtime patches don't affect outputs"; 90-120 day deprecation warning.

---

## Provenance

| Brief | Subagent | Elapsed | Sources |
|---|---|---|---|
| 01 | `llm-compiler-frameworks` (research) | 317s | ~30 primary sources (arXiv, framework docs, GitHub repos) |
| 02 | `llm-determinism-research` (research) | 334s | ~25 primary sources (OpenAI/Azure docs, vLLM, llama.cpp, TGI, MLC-LLM) |
| 03 | `legal-ai-sota-research` (research) | 357s | 10 commercial product sites + Microsoft Purview + 7 arXiv papers |

All gaps and unverified claims are flagged within each brief — see the
"Gaps and Uncertainties" / "Gaps and Recommended Follow-Up" sections at the
end of each document.
