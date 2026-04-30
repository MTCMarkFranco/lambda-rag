# Architecture

## High level

```
                        ┌──────── AUTHORING (offline) ────────┐
  policy.pdf ─►Parser─►Chunker─►Extraction Agent─►Normalizer─►RuleStore
                                  (MAF, temp=0)    (validate, dedup)
                        └─────────────────────────────────────┘

                        ┌──────── RUNTIME (deterministic) ────┐
  contract.docx ─►Parser─►Projector─►(cache)─►ProjectedDocument
                                                        │
                              for each Rule:            ▼
                          Selector.match ─►RulesEngine.eval ─►Verdict
                                                        │
                                                        ▼
                                               ComplianceReport
                                                        │
                                                        ▼
                                            OpenXml Markup ─► reviewed.docx
                        └─────────────────────────────────────┘
```

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
- `LambdaRag.Authoring` — Microsoft Agent Framework agents that read a
  policy document and emit `RuleCandidate`s with selectors, lambdas,
  applies-to schemas, and source spans. Locked prompts; temperature=0;
  schema-validated output.
- `LambdaRag.Persistence` — SQLite stores: `rule_sets` (versioned),
  `projections` (cache), `evaluations` (run history with input/output
  hashes for the audit trail).
- `LambdaRag.Api` — ASP.NET Core minimal API exposing `/extract`,
  `/project`, `/evaluate`, `/review`, `/rules`.
- `LambdaRag.Cli` — `lambda-rag` command-line tool for the same five
  actions plus `rules diff`.

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
