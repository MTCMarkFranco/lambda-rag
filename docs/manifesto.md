# Rule Projection: Deterministic Reasoning over Documents

**A manifesto for AI you can defend in front of a regulator.**

> *Phase 1 / [P1.1](https://github.com/MTCMarkFranco/lambda-rag/issues/11). This is the canonical anchor document for the lambda-rag pattern — written for engineers, architects, and policy people who need a single page to point at.*

---

## TL;DR

Generative AI changed the asking. It did not change the deciding.

For any review that has to withstand legal, regulatory, or audit
scrutiny — contract review, model-risk attestation, permit assessment,
architectural compliance, automated administrative decisions — the
deciding step still has to produce **the same answer twice**, with a
**citation to the source**, on **demand**, **forever**.

Generative LLMs do none of those things. They produce different
outputs for identical inputs. They hallucinate citations. They can be
prompted into different verdicts by trivial reordering of text. None
of those properties are bugs. They are inherent to the architecture.

**Rule projection** is a pattern that uses generative AI where it is
appropriate (offline, in authoring) and refuses to use it where it is
not (online, in deciding). The handoff between the two halves is a
**signed, fingerprinted ruleset** that crosses one boundary, in one
direction, and never reaches back.

This document explains what the pattern is, why it works, where it
applies, where it doesn't, and how lambda-rag implements it as a
reference architecture.

---

## 1. The problem: AI for review is a defensibility problem

Regulator-facing review has three properties that are non-negotiable:

1. **Reproducibility.** Same inputs → same verdict. Always. If a
   reviewer challenges the verdict in 18 months, the system must
   re-produce it byte-for-byte.
2. **Auditability.** Every conclusion must cite a rule, a source span,
   and the inputs to that rule's evaluation. "The model said so" is
   not a citation.
3. **Stability under text perturbation.** Reordering paragraphs,
   changing capitalization, or adding white-space must not change the
   verdict for any rule that did not depend on the perturbation.

Pure RAG-on-LLM fails all three:

| Property | Pure RAG-on-LLM | Why |
|---|---|---|
| Reproducibility | ❌ | Sampling, temperature, model versioning, retrieval-set drift, prompt-cache eviction. |
| Auditability | ❌ | Citations are produced by the same generative process that produced the verdict. The citation can hallucinate. |
| Stability | ❌ | Embedding-based retrieval is sensitive to sentence-level rewording; the LLM is sensitive to ordering. |

The response from many vendors is "well, we set temperature to 0 and
log the prompt." That is not enough. Temperature 0 is *less* random,
not *deterministic*; KV-cache reuse, GPU non-determinism, and silent
model upgrades all break exact reproducibility. And logging the
prompt does not give you a defensible explanation — it gives you a
*replay* that is only valid against the exact same model weights, in
exactly the same serving stack, on exactly the same hardware. None of
those things are guaranteed in production over multi-year audit
windows.

We need a different shape.

---

## 2. The shape: separate authoring from runtime, by contract

The pattern has two halves:

```
┌──────── AUTHORING (offline, AI-assisted) ────────┐    ┌──────── RUNTIME (online, deterministic) ────┐
│                                                  │    │                                             │
│  policy.pdf ─► Parse ─► Extract ─► Normalize ─►  │    │  document ─► Parse ─► Project ─► Select ─► │
│                          (LLM,        (validate, │    │                                             │
│                          temp=0,      dedup)     │    │            ─► Lambda eval ─► Verdict        │
│                          schema)                 │    │                                             │
│                            ↓                     │    │                                             │
│                        Human SME                 │    │                                             │
│                        review                    │    │                                             │
│                            ↓                     │    │                                             │
│                       🔒 SIGNED RULESET ─────────┼────┼──► (the ONLY thing that crosses)             │
│                                                  │    │                                             │
└──────────────────────────────────────────────────┘    └─────────────────────────────────────────────┘
```

> Full Mermaid version: [`docs/diagrams/authoring-vs-runtime.md`](diagrams/authoring-vs-runtime.md).

**Authoring** is where the LLM lives. A subject-matter expert points
the system at a regulation, contract template, or standard. An LLM
extraction agent — running with temperature 0 against a strict
JSON schema — proposes candidate rules. The SME reviews, edits, and
accepts. The result is a `RuleSet` whose every entry has:

- A **selector** — a deterministic predicate over a structured
  *projection* of the document (JSONPath / regex / topic-map / heading-path).
- A **lambda** — a Microsoft RulesEngine expression evaluated by pure
  code over a typed input shape.
- A **citation** — the original regulatory text, with page and
  span coordinates.

The whole `RuleSet` is canonicalized and SHA-256-hashed; that hash is
the **fingerprint** that travels with every downstream verdict.

**Runtime** is where the LLM does not live. A document arrives. It is
parsed into a structured graph (paragraphs, headings, tables,
metadata). The graph is *projected* — assigned to a domain ontology
(e.g., `fsi.v1`, `gov-architecture.v1`, `permitting.v1`) using
deterministic topic-map rules with optional cached AI assistance only
for first-pass projection. For each rule in the `RuleSet`:

1. The selector is matched against the projected graph.
2. For each match, the lambda is evaluated by RulesEngine.
3. The verdict (`Pass` / `Fail` / `Gap` / `Error`) is recorded with
   the rule ID, the rule-set fingerprint, and the matching source
   span.

There is no LLM call in the decision loop. The whole runtime is a
pure function of `(document_bytes, ruleset)`.

---

## 3. The five tenets

Rule projection is defined by five commitments. Any implementation
that breaks any of them is, by definition, not rule projection — it's
something else, and that something else is unlikely to survive a
serious regulatory review.

### Tenet 1 — Authoring may use AI; runtime may not

The LLM is the most useful tool we have for turning unstructured
regulatory prose into structured candidate rules. Forbidding it from
authoring would throw away most of the value. But once the rule is
authored, signed, and accepted by a human, **the LLM has done its
job**. The runtime that applies the rule must be a pure function.

This is not a stylistic preference. It is the only way to satisfy
reproducibility, auditability, and stability over multi-year audit
windows.

### Tenet 2 — One artifact crosses the boundary, in one direction

The signed `RuleSet` is the only thing the runtime depends on from
the authoring pipeline. The runtime must not call back into the
authoring environment, the LLM, or any retrieval system to "decide"
anything.

If a verdict can change because something changed in a system the
runtime *reaches back into*, the verdict is not defensible.

### Tenet 3 — The fingerprint is the audit trail

Every verdict carries a `ruleSetFingerprint`. If you can re-produce
the document bytes and find a rule set with that fingerprint, you can
re-produce the verdict — byte-for-byte — at any point in the future.

The fingerprint is not a metadata field. It is the **commitment
device**. It is what makes the system defensible.

### Tenet 4 — Citations come from the source, not the model

The selector match cites the *source span* of the matched section in
the document under review. The rule citation points to the *source
content* in the regulation — captured at authoring time and locked
into the rule set.

Neither citation is generated at runtime. Neither can hallucinate.

### Tenet 5 — Gaps are first-class verdicts

Most production review pipelines pretend there are two outcomes: pass
and fail. Reality has three. The third is *gap* — the rule is
applicable, but the document is silent. A regulator will ask whether
your tower has a roof. "We don't know" is the right answer when the
section that would describe the roof does not exist. Calling it a
pass because no failing text was found is fraud.

Lambda-rag's `Verdict` enum has `Pass`, `Fail`, **`Gap`**, and
`Error`. Treating gap as first-class is what lets the pattern handle
permit applications, missing controls in architecture reviews, and
unaddressed obligations in contracts — which is, in practice, where
most of the value lives.

---

## 4. Why this is not RAG

Retrieval-Augmented Generation chains an embedder, a vector store,
and an LLM. The retrieved chunks become context for the LLM, which
then *generates* the answer.

Rule projection chains a parser, a projector, a selector, and a
RulesEngine evaluator. The matched sections become *inputs* to the
RulesEngine, which then *evaluates* the lambda. There is no
generation step at runtime.

| Aspect | RAG | Rule projection |
|---|---|---|
| Runtime decider | LLM | RulesEngine lambda |
| Retrieval | Vector / hybrid search | Pure-code selectors over a projected graph |
| Output | Generated text | Typed verdict + evidence span |
| Same inputs → same outputs | No | Yes (byte-identical) |
| Citation | Generated alongside text (can hallucinate) | Captured at authoring + matched at runtime |
| Failure mode | Plausible but wrong answer | `Gap` or `Error` — never a wrong answer dressed as a right one |

Rule projection borrows two ideas from RAG — *structured projection of
documents into a queryable shape*, and *grounding decisions in
specific spans of source* — but it uses them in different roles. The
LLM is moved from runtime to authoring, where its non-determinism is
absorbed by human review.

The practical consequence: a rule-projection system can defend a
verdict in court. A pure-RAG system cannot.

---

## 5. Why this is not just a rules engine

Microsoft RulesEngine, JBoss Drools, OPA, jsonnet — there is a long
tradition of writing rules in code and applying them to structured
data. Rule projection inherits from this tradition but adds two
things that matter:

1. **Authoring scales because authoring uses an LLM.** Hand-coding 30
   rules from a regulation takes days. With an extraction agent
   running off the regulator's PDF + a JSON schema + temp=0 + human
   SME review, it takes hours. The LLM is the productivity multiplier
   that makes domain-by-domain rule authoring economically viable.
2. **The runtime works on unstructured input.** Traditional rules
   engines need structured inputs. Real review subjects — contracts,
   permits, design documents, ADM impact assessments — are
   unstructured prose. The *projector* is the bridge: it turns prose
   into a graph that selectors and lambdas can reason over.

Take either piece away and the pattern collapses. Without the
authoring pipeline, hand-coding rules doesn't scale. Without the
projector, the runtime can only review pre-structured documents,
which are exactly the documents that don't need this pattern in the
first place.

---

## 6. Why this is not symbolic AI

Symbolic AI — Cyc, expert systems, ontology-based reasoners — tried
to encode all knowledge as logical formulas. It failed to scale
because the knowledge bottleneck was real: encoding the world by hand
is hopeless.

Rule projection makes a much more modest claim: *encode one
regulation*, *for one domain*, *with LLM assistance*, *under human
review*. The unit of work is not the world; it is one numbered
clause of one numbered guideline. That fits in a lambda. That has a
citation. That has a fingerprint. That can be defended.

---

## 7. What lambda-rag adds on top of the pattern

The pattern is platform-neutral. lambda-rag is one reference
implementation, with these specific design choices:

- **.NET runtime + Microsoft RulesEngine.** Chosen because RulesEngine
  is the user's required engine; .NET keeps authoring (Microsoft
  Agent Framework) and runtime in the same type system. Treating the
  lambda evaluator as a *swappable component* is part of the design —
  see [`docs/dependencies/rules-engine-risk.md`](dependencies/rules-engine-risk.md)
  for the Roslyn-script alternate path.
- **Topic-map projection.** Not LLM-by-default. Topic maps are pure
  code, version-locked, shipped in repo. AI fallback is allowed for
  topic-map *misses* but cached on first hit and locked thereafter.
- **OOXML markup pipeline with byte-determinism guarantees.** The
  reviewed `.docx` is locked by a golden-master idempotency test that
  hashes every inner OOXML part. See
  [`docs/DETERMINISM.md`](DETERMINISM.md).
- **First-class `Gap` semantics.** Both in the verdict type and in
  the markup output (top-of-document GAP ANALYSIS section).
- **A regression corpus.** [`tests/Goldens/corpus/`](../tests/Goldens/corpus/)
  holds five public-source-grounded vertical packs (gov-architecture,
  fsi, contract, permitting, oil-gas) with frozen verdicts. Drift in
  the engine breaks the build.
- **Explicit non-claims.** [`docs/what-lambda-rag-is-not.md`](what-lambda-rag-is-not.md)
  is the most-linked page in the repo. It tells you what the system
  does *not* do — which is more useful than yet another marketing list
  of what it *does*.

---

## 8. Where the pattern fits — and where it does not

### Strong fit

- **Regulatory review.** OSFI E-23, B-10; TBS Directive on ADM;
  Ontario Building Code permitting; CER pipeline regulations. The
  regulation is finite, citable, and auditable; the document under
  review is unstructured prose; the verdict has to survive scrutiny.
  See [`docs/regulatory/`](regulatory/) for clause-by-clause mappings.
- **Contract review against a corporate playbook.** The playbook is
  authored once, applied a thousand times, and every redline must
  trace to a clause.
- **Architecture / control-framework attestation.** Cloud Guardrails,
  Protected B reviews, ITSG-33 — every rule is a yes/no over the
  architecture document, with a citation in the framework.
- **Permit / planning applications.** AODA, fire-egress, environmental
  assessment — the regulation is text, the application is text, and
  the verdict has to be defensible to a tribunal.

### Strong misfit

- **Open-ended summarization.** "Summarize this contract" is exactly
  the use case generative AI is best at. Use an LLM. Don't
  rule-project.
- **Subjective quality judgments.** "Is this a well-written email?"
  has no source citation. Don't rule-project.
- **Tasks where the rule is the document.** Rule projection assumes
  *the regulation* and *the document under review* are different
  artifacts. If you're drafting the regulation itself, this pattern
  does not help you.
- **Tasks with no defensibility requirement.** If the cost of a wrong
  answer is "the user shrugs and refreshes," you don't need rule
  projection. Use the cheapest LLM call you can find.

The line between strong-fit and strong-misfit is **whether you would
defend the answer to someone with subpoena power.** If yes, rule
projection. If no, do whatever is cheapest.

---

## 9. Open questions and honest limits

Rule projection is not a finished pattern. The lambda-rag reference
implementation has known limits:

1. **Authoring depends on LLM quality at extraction time.** A bad
   extraction is a bad rule, and a bad rule is a bad verdict. Human
   review is the only mitigation. We have not solved authoring.
2. **Topic maps are written by humans.** Adding a new vertical
   currently means writing a new topic map. This is solvable
   (LLM-assisted topic-map drafting + corpus regression) but not yet
   solved.
3. **The lambda DSL is bounded by what the rules engine can express.**
   Microsoft RulesEngine is expressive but not Turing-complete by
   choice; some clauses (especially numeric thresholds with complex
   conditional logic) are awkward. The Roslyn-script alternate path
   addresses this; see
   [`docs/dependencies/rules-engine-risk.md`](dependencies/rules-engine-risk.md).
4. **Proof-of-determinism is end-to-end-tested, not formally proved.**
   The golden-master idempotency tests cover the implemented
   pipeline. They do not prove the absence of non-determinism in
   *any* implementation of the pattern.
5. **The pattern is monolingual at runtime today.** French regulatory
   text is supported at authoring (Quebec Law 25 mapping is in flight
   — see [`tbs-adm-mapping.md`](regulatory/tbs-adm-mapping.md) and
   [issue #14](https://github.com/MTCMarkFranco/lambda-rag/issues/14))
   but the parser and projector ship with English defaults. French
   parser tuning is a known gap.

These are honest limits, listed here so anyone evaluating the pattern
can decide whether they apply. None of them break the core
defensibility argument.

---

## 10. The bet

The bet behind rule projection is simple:

> **Generative AI is the best authoring tool we have ever had. It is
> the worst possible deciding tool for anything that has to be
> defended.**

The right architecture splits the two. Authoring uses AI, hard.
Runtime refuses to. The signed ruleset is the contract.

Everything else — the projector, the selector DSL, the lambda
evaluator, the markup pipeline, the determinism guarantees, the
regression corpus, the regulatory mappings — is *engineering*
in service of that bet.

If the bet is right, this pattern is what regulator-facing AI looks
like for the next decade. If it's wrong — if generative models
become reproducible, citation-faithful, and stable under perturbation
— this pattern collapses back into "just use the LLM." We are not
holding our breath.

---

## 11. Where to go next

For implementers:

- **Architecture diagram:** [`docs/diagrams/authoring-vs-runtime.md`](diagrams/authoring-vs-runtime.md)
- **Determinism proof:** [`docs/DETERMINISM.md`](DETERMINISM.md)
- **Selector semantics:** [`docs/SELECTORS.md`](SELECTORS.md)
- **Module-level architecture:** [`docs/ARCHITECTURE.md`](ARCHITECTURE.md)
- **What this is not:** [`docs/what-lambda-rag-is-not.md`](what-lambda-rag-is-not.md)
- **Dependency risk (RulesEngine):** [`docs/dependencies/rules-engine-risk.md`](dependencies/rules-engine-risk.md)

For regulatory reviewers:

- **OSFI E-23 mapping:** [`docs/regulatory/osfi-e23-mapping.md`](regulatory/osfi-e23-mapping.md)
- **TBS Directive on ADM mapping:** [`docs/regulatory/tbs-adm-mapping.md`](regulatory/tbs-adm-mapping.md)
- **Bill C-27 / AIDA mapping (prospective):** [`docs/regulatory/bill-c27-aida-mapping.md`](regulatory/bill-c27-aida-mapping.md)
- **The regression corpus:** [`tests/Goldens/corpus/`](../tests/Goldens/corpus/)

For sceptics:

- Re-read §9 above.
- Then re-read [`docs/what-lambda-rag-is-not.md`](what-lambda-rag-is-not.md).
- If you can break the determinism proof, file an issue. We will buy
  you a coffee.

---

*This manifesto is the canonical anchor for the lambda-rag pattern. It
is intended to be cited, linked, and disagreed with. If something here
is wrong, file an issue — the rule of the project is that the proof is
in the regression suite, not in the prose.*
