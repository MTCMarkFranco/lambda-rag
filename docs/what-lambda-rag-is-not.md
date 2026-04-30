# What lambda-rag is **not**

> Honest scoping is the strongest sales tool for regulator-facing tech.
> If you're evaluating lambda-rag for a compliance, contract-review, or
> architecture-review use case, please read this page **before** the
> README. It tells you the things lambda-rag refuses to claim.

This page is intentionally short and blunt. If anything below ever drifts
out of sync with what the code actually does, the doc — not the code —
is the source of truth and we'll change the code.

---

## 1. We do **not** guarantee 100% projection precision / recall

The "projection" step decides which span(s) of an incoming document a
given rule applies to. It is driven by a **topic map** authored by a
human (or an LLM-assisted author with human sign-off). Projection
quality is therefore bounded by topic-map quality, which is bounded by
**author judgment** — not by an algorithm we own.

What we **do** guarantee:

- Projection is a **pure function** of `(document_bytes, topic_map,
  projector_version)`. Given the same inputs you get the same spans
  every time, byte-for-byte.
- Every projected span is **inspectable** and human-overridable before a
  verdict is finalised.

What we do **not** guarantee:

- That every rule will land on the "right" section the first time.
- That a rule with no good topical anchor will project at all (we'd
  rather emit *no projection* than a wrong one — see below).

## 2. We do **not** eliminate human review

Rules whose projector binding is empty, ambiguous, or below a confidence
floor are returned to the caller with a `requires_human_disposition`
verdict. They are **never** silently auto-resolved. The human-review
loop is a first-class output of the pipeline, not an afterthought.

## 3. We do **not** replace legal counsel, compliance officers, or SMEs

lambda-rag is a **decision-support** tool. It produces:

- a verdict per rule (`pass` / `fail` / `requires_human_disposition`),
- the exact source span the verdict is anchored to,
- the exact lambda expression that was evaluated,
- the values it was evaluated against,
- the rule version, ruleset version, and content hash of the input.

A human with the appropriate professional qualification still owns the
final call. The audit trail is designed to make that human's job
**defensible**, not to make their job go away.

## 4. We are **not** a generic LLM agent

At runtime, lambda-rag is deterministic .NET code: a parser, a
projector, a selector matcher, the Microsoft RulesEngine, and an OOXML
markup writer. **Zero LLM calls happen at runtime.**

LLMs are used during **authoring** (turning natural-language policy
into typed rules + lambdas + projector bindings) under a human
review gate. That output is then frozen, content-hashed, and version
controlled. The runtime never re-asks the model anything.

If your shortlist includes "agentic" tools that re-plan on every
request, lambda-rag is the opposite of that on purpose.

## 5. We do **not** auto-update rules

Rule changes are **explicit**:

- Source policy changes → re-run the authoring pipeline.
- Authoring output is reviewed by a human and merged via PR.
- The new ruleset gets a new version + content hash.
- Existing reports remain pinned to the ruleset version they were run
  against; nothing retroactively re-evaluates.

There is no background job that "improves" rules over time. There is
no model drift.

## 6. We are **not** a search engine, a chatbot, or a "Copilot for X"

lambda-rag answers exactly one question:

> *Given this document and this ruleset, for each rule, does the
> document comply — and where exactly is the evidence?*

If the question you're trying to answer is "summarise this contract"
or "what should clause 4.2 say?", you want a different tool. We're
happy to recommend one.

---

## How to challenge this list

If you believe one of the above non-claims is wrong — i.e., the code
actually *does* claim or do something this doc says it doesn't —
please file an issue. We'd rather correct the doc (or the code) than
let the gap stand.
