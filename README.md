# Lambda-RAG

> A deterministic, auditable, plug-in **rules-over-documents accelerator**.
> Combine the "compiler not RAG" thesis from rules-iq with the
> idempotent multi-agent review of contoso and the
> rules-from-PDFs ingestion of architecture-review-board into a single
> reusable engine for contract review, architecture review, or any
> domain that needs structured rules applied to free-form documents.

## Why

Generative LLMs are non-deterministic. For contract review, audit, or
compliance you cannot defend a verdict that changes between runs. The
remedy in this accelerator is a strict separation:

| Phase | Mode | Allowed to use AI? | Determinism guarantee |
|------|------|--------------------|------------------------|
| **Authoring** (offline) | One-time per rule | Yes (temp=0, JSON-schema-validated, human-reviewed) | Output is signed and version-locked |
| **Projection** (runtime) | Per document | Cached pure-code first; AI only when no pure-code projector exists, with full caching | Same bytes ⇒ same projection |
| **Selection** (runtime) | Per rule × document | **Never** | Pure-code JSONPath/regex match |
| **Evaluation** (runtime) | Per rule × matched section | **Never** | Microsoft RulesEngine lambda |
| **Markup** (runtime) | Per verdict | **Never** | OpenXml tracked changes with stable ids |

At runtime no LLM is in the decision loop. Re-running the same review
produces byte-identical artifacts.

## Quickstart

```pwsh
dotnet build
dotnet test

# Review the bundled sample contract against the bundled ruleset
dotnet run --project src/LambdaRag.Cli -- review `
  --document samples/contracts/contract.md `
  --ruleset samples/contracts/ruleset.json `
  --out out/

# Re-run; outputs are byte-identical
dotnet run --project src/LambdaRag.Cli -- review `
  --document samples/contracts/contract.md `
  --ruleset samples/contracts/ruleset.json `
  --out out2/

# Compare
fc /b out/report.json out2/report.json   # zero differences
```

## Solution layout

```
src/
  LambdaRag.Core/         Domain, hashing, selectors, abstractions
  LambdaRag.Parsing/      PDF/DOCX/MD parsers → ParsedDocument
  LambdaRag.Projection/   ParsedDocument → ProjectedDocument (typed graph)
  LambdaRag.Selectors/    JSONPath-subset matcher
  LambdaRag.Evaluation/   Microsoft RulesEngine wrapper, verdict aggregator
  LambdaRag.Markup/       OpenXml tracked-changes annotator
  LambdaRag.Authoring/    MAF agents: extract rules from policy docs
  LambdaRag.Persistence/  SQLite stores: rules, projections, evaluations
  LambdaRag.Api/          ASP.NET Core minimal API
  LambdaRag.Cli/          `lambda-rag` command-line tool
tests/
  LambdaRag.UnitTests/
  LambdaRag.IdempotencyTests/  Run-twice byte-equality proofs
samples/contracts/        contract.md + ruleset.json
docs/                     ARCHITECTURE.md, DETERMINISM.md, SELECTORS.md
```

## Contributing a new domain

1. Implement `IDocumentProjector` for your domain (or reuse an existing one).
2. Provide an authoring policy document and let `lambda-rag extract` build a `RuleSet.json`.
3. Review the generated rules — every rule cites its source span and natural-language statement.
4. Run `lambda-rag review` against target documents.

## License

MIT.
