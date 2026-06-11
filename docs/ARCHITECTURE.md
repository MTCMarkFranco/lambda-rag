# Architecture

> 📌 **The canonical diagram lives at
> [`docs/diagrams/authoring-vs-runtime.md`](diagrams/authoring-vs-runtime.md)**
> (Mermaid + module map + anti-patterns). This page expands on the
> module-level details below.

## High level

See [`docs/diagrams/authoring-vs-runtime.md`](diagrams/authoring-vs-runtime.md)
for the canonical Mermaid diagram. The ASCII summary is preserved here
for terminal-only consumers:

```
                        ┌──────── AUTHORING (offline) ────────┐
  policy.pdf ─►Parser─►Chunker─►Extraction Agent─►Normalizer─►RuleStore
                                  (MAF, temp=0)    (validate, dedup)
                        └─────────────────────────────────────┘

                        ┌──────── RUNTIME (deterministic) ────┐
  contract.docx ─►Parser─►Projector─►(cache)─►ProjectedDocument
                                                        │
                              for each Rule:            ▼
                          DocKindGate ─► Selector.match ─►RulesEngine.eval ─►Verdict
                                                        │
                                                        ▼
                                               ComplianceReport
                                                        │
                                                        ▼
                                            OpenXml Markup ─► reviewed.docx
                        └─────────────────────────────────────┘
```

> **Pillar 1 — doc-kind gating (#116).** Before selector match, the
> evaluator resolves a deterministic `doc_kind` from (CLI flag → path
> heuristic → heading-bigram classifier) and short-circuits any rule
> whose `appliesToDocKinds` list does not contain the resolved kind.
> Skipped rules emit a `Skipped` verdict (never silent), and the
> report carries `wrong_profile: true` when every rule got skipped.
> This is what protects an ARB-PSA artifact from being graded against
> contract-clause rules — and vice versa. See
> [`PIPELINE.md`](PIPELINE.md) §"Pillar 1 — doc-kind gating".

## Why .NET

Microsoft RulesEngine is the user's chosen rule engine. It runs in
.NET. Microsoft Agent Framework has first-class .NET. Putting both
runtime and authoring on .NET avoids cross-process serialization in
the hottest path and lets the same domain types be referenced
everywhere.

## Why "selector + lambda" instead of pure RAG

A pure RAG approach asks the LLM at runtime: *"given this contract
section, does it comply with rule X?"*. That is non-deterministic —
the LLM may say yes today and no tomorrow. Worse, there is no
structured input the verdict can be defended against.

Lambda-RAG's authoring step decomposes a natural-language rule into:

1. A **selector** (deterministic JSONPath-style predicate over the
   projected graph), and
2. A **lambda expression** (Microsoft RulesEngine, evaluated by pure
   code over a typed input shape).

At runtime there is no LLM. The verdict is a pure function of the
projected graph.

## Module map

- `LambdaRag.Core` — Domain types: `SourceSpan`, `SourceDocument`,
  `ParsedDocument`, `ProjectedDocument`, `Rule`, `RuleSet`, `Verdict`,
  `ComplianceReport`. `ContentHash` and the `Selector` hierarchy with
  tagged-union JSON converter.
- `LambdaRag.Parsing` — `IDocumentParser` implementations for PDF
  (UglyToad.PdfPig), DOCX (DocumentFormat.OpenXml), and Markdown
  (Markdig).
- `LambdaRag.Projection` — `IDocumentProjector` implementations,
  starting with `DeterministicContractProjector` (heading-driven,
  rule-based classification, no LLM).
- `LambdaRag.Selectors` — `JsonPathSelectorMatcher` implementing
  `ISelectorMatcher` over `ProjectedDocument`.
- `LambdaRag.Evaluation` — `EvaluationService` builds a one-rule
  RulesEngine `Workflow` per rule, converts matched JSON to
  `ExpandoObject`, runs `ExecuteAllRulesAsync`, builds `Verdict`s with
  stable ids, aggregates into `ComplianceReport`.
- `LambdaRag.Markup` — `OpenXmlMarkupService` walks paragraphs of the
  source `.docx`, anchors comments at character offsets, and emits
  tracked-change inserts/deletes when annotations request them.
  `IClauseRewriter` consumes `ComplianceEditor` output to render
  concrete `w:del`/`w:ins` replacements for Fail verdicts when
  `--rewrite` is set.
- `LambdaRag.Authoring` — Microsoft Agent Framework agents that read a
  policy document and emit `RuleCandidate`s with selectors, lambdas,
  applies-to schemas, and source spans. Locked prompts; temperature=0;
  schema-validated output. Also hosts `ComplianceEditor` /
  `DeterministicMockClauseRewriter`, which render the
  remediated-clause text the markup stage swaps in under `--rewrite`.
- `LambdaRag.Authoring.ExtractFunction` — Azure Function exposing the
  rule-extraction agent as a Web API custom skill, suitable for
  indexer-driven authoring pipelines. **Authoring-side only.**
- `LambdaRag.Indexing` — Azure AI Search adapters
  (`AzureSearchRuleSemanticIndex`, `IRuleSignatureIndex`,
  `IDocumentSectionIndex`) plus in-memory equivalents.
  **Authoring-side only**: used to seed and inspect rule indexes
  during extraction. The runtime evaluation pipeline does *not* read
  rules through these adapters — see
  [`../wrong-path-search-index.md`](../wrong-path-search-index.md) for
  the rationale.
- `LambdaRag.Persistence` — SQLite stores: `rule_sets` (versioned),
  `projections` (cache), `evaluations` (run history with input/output
  hashes for the audit trail).
- `LambdaRag.Api` — ASP.NET Core minimal API. Today: `GET /` health
  probe and `POST /review` (same pipeline as `lambda-rag review`).
  Additional `/extract`, `/project`, `/evaluate`, `/rules` endpoints
  are roadmap.
- `LambdaRag.Cli` — `lambda-rag` command-line tool. Commands:
  `review`, `project`, `parse`, `coverage`, `author`, `index`,
  `topic-map`, `extract-rules`, `rules`, `ruleset`.

## Plug-in points

- **New document kinds**: implement `IDocumentParser`.
- **New domains**: implement `IDocumentProjector` and ship a starter
  `RuleSet.json`.
- **Custom selectors**: extend the `Selector` sealed hierarchy.
- **Storage backends**: replace `LambdaRag.Persistence` with a
  Postgres or Cosmos adapter (interfaces in `LambdaRag.Core`).

## Out of v1

- Distributed evaluation (the runtime is fast and single-node).
- Streaming markup of multi-thousand-page documents (we materialize the
  full `.docx` in memory).
- Real-time authoring UI (today the loop is CLI-driven; an
  ASP.NET-served review UI is planned).
