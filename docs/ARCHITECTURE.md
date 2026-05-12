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
  policy.pdf,
  *.md  ─► Blob ─► AI Search indexer ─► skillset (Layout,
                                        chunk, embed) ─►
                   WebApiSkill ─► Extract Function
                   (v3 prompt, JSON-schema validate,
                    stamp status=approved + version) ─►
                                lambda-rag-rules  ◄─► rules-iq UI
                                (Azure AI Search)     (SME status/lambda)
                ┴─────────────────────────────────────┘

                ┌──────── RUNTIME (deterministic) ────┐
  contract.docx ─► Parser ─► Projector ─► (cache) ─► ProjectedDocument
                                                            │
            AzureSearchRuleStore filter:                    ▼
            status='approved' AND
            rulesetName=X AND          for each Rule:
            rulesetVersion=Y  ───────► Selector.match ─► RulesEngine.eval
                                          (lambda calls
                                           MatchesAnyMeaning) ─► Verdict
                                                            │
                                                            ▼
                                                   ComplianceReport
                                                            │
                   (optional --rewrite) ◄───────────────────┤
                   Compliance Editor                        ▼
                   (Responses API)            OpenXml Markup ─► reviewed.docx
                ┴─────────────────────────────────────┘
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
   code over a typed input shape) — semantic rules call
   `SemanticFunctions.MatchesAnyMeaning(input, "<concept>|...", threshold)`
   so the determinism contract is "given the same pinned ruleset
   version and the same input, the same verdict comes back".

At runtime there is no LLM in the decision loop. The verdict is a
pure function of the projected graph and the pinned ruleset.

## Why an Azure AI Search index for rules (not a signed JSON file)

The original design stored rules in a signed `ruleset.json` artifact.
We replaced that with a queryable Azure AI Search index
(`lambda-rag-rules`) because:

1. **Hybrid retrieval beats whole-file scan.** Lambda evaluation
   needs to surface candidate rules per document section; BM25 +
   vector + structured filters do that in one query.
2. **Status gating without redeploys.** SMEs can flip a rule from
   `approved` to `draft` via the rules-iq UI; the runtime sees the
   change on the next query without rebuilding any artifact.
3. **Version pinning at query time.** The CLI filters by
   `rulesetVersion = <pinned>`, so multiple ruleset versions can
   co-exist in the index and the runtime never silently upgrades.
4. **Rebuildable from policy.** The index is regenerated from blob
   storage by the indexer; rule content is never hand-edited in
   place. (See the *Reindex contract* in the canonical diagram.)

The "signed boundary" is therefore not a single artifact but a
filtered, versioned query: *give me every approved rule for
`(rulesetName, rulesetVersion)`*. That filter is the contract.

## Module map

- `LambdaRag.Core` — Domain types: `SourceSpan`, `SourceDocument`,
  `ParsedDocument`, `ProjectedDocument`, `Rule`, `RuleSet`, `Verdict`,
  `ComplianceReport`. `ContentHash`, the `Selector` hierarchy with
  tagged-union JSON converter, and `SemanticFunctions.MatchesAnyMeaning`
  used by lambda expressions at runtime.
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
- `LambdaRag.Authoring` — Local extraction agent (used for offline
  eval), `AzureSearchSnapshotPuller` (deterministic index snapshot
  for golden tests), and the **compliance editor** at
  `Editing/ComplianceEditor.cs` — invoked from the CLI when
  `--rewrite` is passed. Takes the failing rule, its remediation, and
  the original clause text, and returns proposed replacement text
  via Azure OpenAI Responses API. Its output is never consulted to
  compute verdicts.
- `LambdaRag.Authoring.ExtractFunction` — Azure Function. The Search
  WebApiSkill calls it per chunk; it runs the v3 system prompt, parses
  the response (unwrapping `{rules:[…]}` envelopes), validates against
  `rule-extraction.schema.json`, then stamps `parentDocumentId`,
  `sectionId`, `status`, `rulesetName`, `rulesetVersion`, `contentHash`,
  `approvedAtUtc`, `approvedBy`. Default `status` is `approved`.
- `LambdaRag.Indexing` — `AzureSearchRuleStore` (hybrid retrieval +
  status/version filter — the runtime's `IRuleStore`) and
  `AzureSearchRuleSemanticIndex` (admin: create/wipe/inspect).
- `LambdaRag.Ui` (`rules-iq`) — SPA for SMEs to review extracted
  rules, edit `lambda` and `predicate`, and toggle `status`
  approved↔draft. PATCHes back to the index — never to rule body
  content (which would be lost on reindex).
- `LambdaRag.Persistence` — SQLite stores: `projections` (cache),
  `evaluations` (run history with input/output hashes for the audit
  trail). Rules no longer live here.
- `LambdaRag.Api` — ASP.NET Core minimal API exposing `/extract`,
  `/project`, `/evaluate`, `/review`, `/rules`.
- `LambdaRag.Cli` — `lambda-rag` command-line tool for the same five
  actions plus `rules diff`. Reads `(rulesetName, rulesetVersion)`
  from `lambdarag.config.json` or the `--ruleset-version` flag.

## Plug-in points

- **New document kinds**: implement `IDocumentParser`.
- **New domains**: implement `IDocumentProjector`, populate the
  policies blob container, and run the indexer.
- **Custom selectors**: extend the `Selector` sealed hierarchy.
- **Alternate rule store**: implement `IRuleStore`. The Azure Search
  implementation is the production default; a `FileRuleStore` and an
  in-memory test double also exist.

## Out of v1

- Distributed evaluation (the runtime is fast and single-node).
- Streaming markup of multi-thousand-page documents (we materialize the
  full `.docx` in memory).
- Direct write-through from rules-iq to the indexer (today the UI
  PATCHes the index; the indexer is the only path that creates new
  rule bodies).
