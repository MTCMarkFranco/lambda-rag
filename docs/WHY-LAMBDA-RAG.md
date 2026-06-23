# Why Lambda-RAG

> A one-page primer on **what lambda-rag is, why it exists, where it came from, and why it is materially better than an LLM alone for legal-grade evaluation of documents against industry or custom policies.**

---

## The problem

Generative LLMs are *spectacular* at reading documents and *terrible* at deciding things about them.

The same prompt run twice yields different verdicts. Citations are produced by the same generative process that produced the verdict — so they can hallucinate. Trivial reordering of paragraphs flips the answer. None of these are bugs. They are inherent to the architecture: an autoregressive sampler over a high-dimensional probability distribution is not a function in the mathematical sense.

For **regulator-facing review** — contract review, HIPAA/GDPR/Law 25 privacy assessment, OSFI model-risk attestation, government cloud architecture review, permit assessment, pipeline-safety conformance — the *deciding* step has to:

1. **Reproduce.** Same inputs → same verdict, byte-identical, forever.
2. **Cite.** Every verdict points to a rule, a source span, and the inputs used.
3. **Survive perturbation.** Whitespace, capitalisation, paragraph re-order ⇒ no change.
4. **Be defensible.** A regulator can re-run it in 18 months and get the same answer.

Pure RAG-on-LLM fails all four. Lambda-RAG was built specifically to satisfy all four.

---

## The idea: rule projection

Lambda-RAG separates AI work into two phases that **never overlap at runtime**:

| Phase | What runs | When | Output |
|-------|-----------|------|--------|
| **Authoring** (offline, AI-heavy) | LLMs, embeddings, AI Search, human review | When a policy changes | A signed, fingerprinted **`RuleSet.json`** |
| **Runtime** (online, AI-free) | Deterministic projector + compiled lambdas | On every document review | Verdict report + redlined `.docx` |

The boundary between them is a **fingerprinted ruleset that crosses in one direction, once.** Runtime never calls a model. It loads the ruleset, projects the document into a typed graph (sections, topics, numeric features), and evaluates each rule as a pure function over that graph. Same input → same output. Always.

Optional LLM-driven *rewrite* (the `--rewrite` flag in markup mode) is a strictly post-verdict editorial layer. The verdict was already decided before the model was consulted, and if the LLM is unavailable the verdict and the redlined comments are still produced (the CLI tells you the rewrite path was skipped and why).

---

## The intellectual lineage

Rule projection is a recombination of three settled bodies of research, not a new invention:

1. **Lambda calculus and typed functional evaluation.** Each compliance rule is a small, pure, total function over a typed projection of the document. The runtime is, by construction, a referentially-transparent evaluator — which is the only known class of system that gives you reproducibility *for free*. The "lambda" in lambda-rag is literal.
2. **Compiler theory and the separation between *elaboration* (typing, expansion, macro hygiene) and *evaluation* (running the elaborated core).** Authoring is elaboration; runtime is evaluation. The same pattern that lets a compiler emit byte-identical binaries lets lambda-rag emit byte-identical verdict reports.
3. **Neuro-symbolic AI.** The neural side (the LLM) is used for what it is good at — reading messy prose, surfacing candidate obligations, generating natural-language remediation. The symbolic side (the projector + rule engine) is used for what *it* is good at — exact, auditable, deterministic decisions over a structured representation. The handoff is the ruleset.

> ⓘ **Compiler-spike provenance.** This pattern was first prototyped in the internal `compiler-spike` repo (Python, ~Spring 2026) as a research probe into whether deterministic rule evaluation could match an LLM-only baseline on accuracy while gaining idempotency and citation faithfulness. The spike concluded *yes* on a single-vertical corpus (ARB-PSA architecture review). Lambda-RAG ports those v0.1.1 invariants to a production-grade .NET 9 codebase and extends them to 8 industries, 17 corpus documents, and an LLM-vs-runtime accuracy harness. Foundational academic references that informed the spike's design are pending re-attachment (`[CITATION NEEDED]` — to be filled from the spike's bibliography).

---

## How it works (60 seconds)

```
                  ┌───────────────────────────────────┐
   Policy docs ──▶│  AUTHORING (offline, LLM + human) │──┐
   (PDF, DOCX,    │  • topic-map elaboration          │  │   Signed,
    standards)    │  • rule extraction + lambdas      │  │   fingerprinted
                  │  • semantic-anchor binding        │  │   RuleSet.json
                  │  • human review + sign-off        │  │   (one direction,
                  └───────────────────────────────────┘  │    one boundary)
                                                         ▼
                  ┌───────────────────────────────────┐
   Customer doc ─▶│  RUNTIME (online, AI-FREE)        │──▶ verdict report
   (.docx, .md,   │  • parse → canonical text         │   (JSON, byte-identical)
    .pdf)         │  • project → typed graph          │
                  │  • evaluate λ-rules over graph    │──▶ reviewed.docx
                  │  • emit verdicts + citations      │   (comments + tracked
                  └───────────────────────────────────┘    changes, optional
                                                            LLM rewrite layer)
```

Every verdict carries: **rule id · outcome · source span (file:char-offset) · evidence quotes · rule-text citation · ruleset fingerprint.** A regulator can independently re-evaluate any verdict offline.

---

## Lambda-RAG vs. an LLM-only review

| Property | LLM-only review | Lambda-RAG |
|----------|----------------|------------|
| Same input → same output | ❌ Stochastic sampler | ✅ Pure function over typed graph |
| Citation fidelity | ❌ Generated alongside text (hallucinates) | ✅ Captured at authoring, matched at runtime |
| Stability under text perturbation | ❌ Re-ordering flips verdicts | ✅ Topology-invariant projector + canonicalised JSON |
| Auditability after 18 months | ❌ Model version drifts | ✅ Ruleset fingerprint + frozen golden corpus on CI |
| Cost per document | $$ (per token) | $ (CPU-only at runtime) |
| Cloud dependency at decision time | ☁ Required | 🚫 Banned (CI guardrail enforces) |
| Accuracy on the 8-industry corpus | LLM ground truth = our gate | ≥ LLM baseline: 17 / 17 scenarios pass `recall ≥ 0.85`, `FP = 0`, `F1 ≥ 0.85` |

The accuracy claim is not aspirational. It is enforced by the `AccuracyHarness` CI gate (`tests/LambdaRag.IdempotencyTests/AccuracyHarness.cs`), which compares the deterministic runtime's verdicts against LLM ground truth across **contract, FSI, gov-architecture, oil-gas, permitting, healthcare (HIPAA), privacy-gdpr, privacy-law25** every commit.

---

## Bringing your own policy

The same path that produced the 8 bundled industries is available end-to-end via the CLI. There is no "import to SaaS" step — your policy documents never leave your machine.

```pwsh
# 1. Extract a ruleset from a folder of policy documents (LLM-assisted authoring)
dotnet run --project src/LambdaRag.Cli -- ruleset extract `
  --policy-folder ./my-policies/ `
  --topic-map     contract.v1 `
  --out           rulesets/my-industry/ruleset.json

# 2. Review a target document against it (deterministic runtime, no LLM)
dotnet run --project src/LambdaRag.Cli -- review `
  --document  customer-doc.docx `
  --ruleset   rulesets/my-industry/ruleset.json `
  --out       out/customer-review `
  --mode      both `
  --annotate-pass

# 3. (Optional) Promote it into the regression corpus so every future build is gated on it
#    See README → "Promote it into the regression corpus".
```

If the standard topic maps don't fit your domain, clone `contract.v1.json` to `my-industry.v1.json`, list your headings/aliases, rebuild — full instructions in the README.

---

## What lambda-rag is *not*

- **Not a chatbot.** It does not answer free-form questions about a document.
- **Not a rule editor.** Rules cannot be hand-edited at runtime; they are re-extracted, diffed, and signed. Governance is by overlay, not by mutation.
- **Not a one-size-fits-all classifier.** It needs a written policy. Where the policy lives in someone's head, lambda-rag is the wrong tool.
- **Not better than an LLM at *reading*.** It is dramatically better than an LLM at *deciding* — once a human-reviewed policy has been compiled into rules.

---

## Where to read more

- [`docs/manifesto.md`](./manifesto.md) — the five tenets of rule projection, in full.
- [`docs/ARCHITECTURE.md`](./ARCHITECTURE.md) — runtime / authoring split, services, dependencies.
- [`docs/DETERMINISM.md`](./DETERMINISM.md) — the determinism contract, byte-identity proofs, CI guardrails.
- [`docs/blog/lambda-rag-deterministic-llm-review.md`](./blog/lambda-rag-deterministic-llm-review.md) — long-form narrative.
- [`docs/what-lambda-rag-is-not.md`](./what-lambda-rag-is-not.md) — explicit non-goals.
- [`tests/Goldens/corpus/`](../tests/Goldens/corpus/) — the 8-industry / 17-document frozen regression corpus.

> *Lambda-RAG is MIT-licensed. Issues and PRs welcome.*
