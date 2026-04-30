# Lambda-RAG

> **A deterministic, auditable, plug-in platform for *all things rules-based document review.***
> One engine. Many domains. Same input → same verdict, every time.
> Built to withstand legal, regulatory and audit scrutiny.

Lambda-RAG turns *any* policy / regulation / contract template into an
executable rule set, then projects those rules over a target document
(contract, architecture design, MOU, permit application, ITSM runbook,
etc.) and produces:

1. 📊 **A structured verdict report** — score, per-rule pass / fail / **gap** / N/A, remediation text, full audit trail
2. 📝 **A redlined Word document** — tracked-changes + comments anchored to the offending clause, with a top-of-document **GAP ANALYSIS** summary
3. 🔀 **Or both** — emitted from the same deterministic pipeline

## Why this exists

Generative LLMs are non-deterministic. For contract review, regulatory
compliance, audit, or permitting you cannot defend a verdict that
changes between runs. Lambda-RAG enforces a strict separation:

| Phase | When | LLM allowed? | Determinism guarantee |
|------|------|--------------|------------------------|
| **Authoring** | Offline, once per rule | ✅ Yes (temp=0, JSON-schema-validated, human-reviewed) | Output is signed, fingerprinted, version-locked |
| **Projection** | Runtime, per document | ⚠️ Pure-code first; AI fallback only when no projector exists, with full caching | Same bytes → same projection |
| **Selection** | Runtime, per rule × section | ❌ Never | Pure-code JSONPath / regex / topic-map match |
| **Evaluation** | Runtime, per matched section | ❌ Never | Microsoft RulesEngine lambda |
| **Markup** | Runtime, per verdict | ❌ Never | OpenXml tracked changes, fixed timestamp, pinned IDs |

**At runtime no LLM is in the decision loop.** Re-running the same
review against the same ruleset produces byte-identical OOXML parts
inside `reviewed.docx` and a byte-identical `report.json`.

> 📌 **Before evaluating lambda-rag, please read
> [`docs/what-lambda-rag-is-not.md`](docs/what-lambda-rag-is-not.md).**
> It is the explicit non-claims sheet — what we deliberately do *not*
> guarantee — and is the most useful single page for anyone deciding
> whether this tool fits a regulator-facing use case.

## Built-in industry topic maps

Out of the box, Lambda-RAG ships with topic ontologies for several
high-review-burden industries. Each maps free-form section headings
and keywords onto canonical topic IDs that rules can be authored
against:

| Topic map | Use cases |
|-----------|-----------|
| `contract.v1` | Commercial contract review (payment terms, governing law, warranty, IP, liability, …) |
| `architecture-review.v1` | Cloud architecture / ASD review (security, network, compliance, performance, …) |
| `fsi.v1` | Financial services (basel, AML, KYC, capital adequacy, model risk, …) |
| `oil-gas.v1` | Upstream / downstream policies (HSE, well integrity, asset integrity, environmental, …) |
| `business-review.v1` | MOUs, SOWs, business cases, vendor reviews |
| `gov-architecture.v1` | Government cloud architecture review |
| `permitting.v1` | Government permit / planning application review |

List them at any time:

```pwsh
dotnet run --project src/LambdaRag.Cli -- topic-map list
```

## 🚀 Try it on the bundled sample

```pwsh
dotnet build
dotnet test    # 100 unit + 2 idempotency proofs

# Review the bundled sample contract → JSON report
dotnet run --project src/LambdaRag.Cli -- review `
  --document samples/contracts/contract.md `
  --ruleset  samples/contracts/ruleset.json `
  --out      out/sample `
  --mode     report

# Same review → redlined Word document with tracked changes
dotnet run --project src/LambdaRag.Cli -- review `
  --document out/ac-test/contract.docx `
  --ruleset  out/ac-full/ac-policies-ruleset.json `
  --out      out/sample `
  --mode     markup

# Both at once
dotnet run --project src/LambdaRag.Cli -- review `
  --document out/ac-test/contract.docx `
  --ruleset  out/ac-full/ac-policies-ruleset.json `
  --out      out/sample `
  --mode     both
```

Outputs land in `out/sample/`:
- `report.json` — verdict, score, per-rule outcome, remediation, full provenance
- `reviewed.docx` — original document with tracked changes + comments + gap-analysis summary

## 📥 How do I plug in a new ruleset?

The platform is designed so you can drop in *any* set of policy
documents (PDF, Word, Markdown, JSON) for *any* industry / customer
and have an executable ruleset out the other end.

### Option A — Extract rules from a folder of policy documents

Best when you have customer / regulator policy PDFs or Word docs.

```pwsh
# 1. Drop your policy files into a folder
mkdir policies\acme-corp
# copy ACME-Procurement-Policy.pdf, ACME-DataProtection.docx, etc. into it

# 2. Run the deterministic extractor
dotnet run --project src/LambdaRag.Cli -- extract-rules `
  --policy-dir policies/acme-corp `
  --domain     contract `
  --id         rs_acme_procurement `
  --out        rulesets/acme-procurement.json `
  --prefix     ACME `
  --min-chars  200
```

Output: `rulesets/acme-procurement.json` — every rule includes:
- A natural-language statement
- A typed predicate (lambda) the engine evaluates
- A pointer to the source span in the originating policy document
- An applicability tag (Mandatory / Conditional / Optional, inferred at authoring time)
- A content-addressed fingerprint

Review it, edit it, commit it, version it — it's plain JSON.

### Option B — Author rules directly (chunk-by-chunk)

When you have one policy clause and want a single rule:

```pwsh
dotnet run --project src/LambdaRag.Cli -- author `
  --chunk  policies/acme-corp/clause-7.txt `
  --domain contract `
  --prefix ACME `
  --out    rulesets/clause-7-rule.json
```

### Option C — Hand-write a ruleset

Look at `samples/contracts/ruleset.json`. The schema is small and
documented in `docs/`. Anything you can express as a typed predicate
over a projected document graph can be a rule.

### Then test it

```pwsh
# Sanity-check coverage of your ruleset against a target document
dotnet run --project src/LambdaRag.Cli -- coverage `
  --document my-customer-doc.docx `
  --ruleset  rulesets/acme-procurement.json `
  --out      out/acme/coverage.json

# Run the full review
dotnet run --project src/LambdaRag.Cli -- review `
  --document my-customer-doc.docx `
  --ruleset  rulesets/acme-procurement.json `
  --out      out/acme `
  --mode     both
```

### Adding a brand-new industry topic map

If the ontology you need isn't in the table above, copy
`src/LambdaRag.Projection/TopicMaps/contract.v1.json` to
`my-industry.v1.json`, add your headings/aliases per topic, rebuild,
and pass `--topic-map my-industry.v1` to the extractor.

## CLI cheat sheet

```
lambda-rag review        --document <path> --ruleset <path> --out <dir> [--mode report|markup|both] [--overlay <path>]
lambda-rag extract-rules --policy-dir <dir> --domain <name> --id <ruleset-id> --out <path>
lambda-rag author        --chunk <path> --domain <name> --prefix <id-prefix> --out <path>
lambda-rag coverage      --document <path> --ruleset <path> --out <path>
lambda-rag project       --document <path> --out <path>
lambda-rag parse         --document <path> --out <path>
lambda-rag index         --ruleset <path> [--out <path>]
lambda-rag topic-map     <list|show|coverage> [args]

# Governance — never edits the ruleset; works through diffs and overlays
lambda-rag rules diff     <old.json> <new.json> [--out diff.json]
lambda-rag rules show     --ruleset <path> --rule <id>
lambda-rag rules disable  --ruleset <path> --overlay <path> --rule <id> --reason "..." [--by <name>]
lambda-rag rules enable   --ruleset <path> --overlay <path> --rule <id>
lambda-rag rules annotate --ruleset <path> --overlay <path> --rule <id> --note "..." [--by <name>]
```

> A web UI is on the roadmap. For now everything runs from the CLI
> and produces files you can diff, hash, sign, and ship.

## 🛡️ Rule governance — *no rule editor by design*

Lambda-RAG **deliberately ships without an in-place rule editor**.
The legal-defensibility chain is:

```
Signed policy PDF  →  extract-rules  →  RuleSet.json (in git)  →  review  →  Verdict
```

Editing a rule directly in the index would break the cited source span,
silently invalidate idempotency, and create two competing sources of
truth. So the platform is opinionated:

> **The policy document is law. The RuleSet is its compiled form. Both
> are versioned. Neither is edited in production.**

When a rule legitimately needs to change, edit the policy doc and
re-run `extract-rules`. To see what changed:

```pwsh
lambda-rag rules diff old-ruleset.json new-ruleset.json --out delta.json
```

You'll get added / removed / changed rules, and for each *changed* rule
the exact list of fields that drifted (`predicate`, `lambda`,
`severity`, `applicability`, `schema`, `naturalLanguage`, `version`).
Exit code is `2` when there are deltas — wire it into CI to gate
ruleset promotions.

### When you legitimately need to "edit a rule" without re-extracting

There are exactly two such cases, and both are handled via a
**RuleOverlay** sidecar — *never* by mutating the ruleset:

1. **Suppress a rule** — e.g. "rule X is superseded by a side-letter"

   ```pwsh
   lambda-rag rules disable `
     --ruleset rulesets/acme.json `
     --overlay rulesets/acme.overlay.json `
     --rule    ACME-PAY-003 `
     --reason  "superseded by 2026-Q2 side-letter clause 4.2" `
     --by      legal@acme.com
   ```

2. **Annotate a rule** — reviewer commentary that does *not* change the verdict

   ```pwsh
   lambda-rag rules annotate `
     --ruleset rulesets/acme.json `
     --overlay rulesets/acme.overlay.json `
     --rule    ACME-LIAB-001 `
     --note    "see clause 7.2 in MSA — capped at fees paid in prior 12 months" `
     --by      legal@acme.com
   ```

Then run a review with the overlay applied:

```pwsh
lambda-rag review `
  --document customer-doc.docx `
  --ruleset  rulesets/acme.json `
  --overlay  rulesets/acme.overlay.json `
  --out      out/customer
```

Properties of overlays that make them **safe**:

- 🔒 **Bound to a specific RuleSet id + version** — refuse to apply to a different ruleset
- 🧾 **Every disable carries a `reason` and an `at` timestamp** (and optionally `by`) — `--reason` is required
- 🔍 **Recorded on the report** — `report.json` has an `overlayApplied` block with the overlay's SHA-256 fingerprint, the disabled list, and the annotations, so any reviewer can see exactly which governance decisions were active for that run
- 📁 **Sidecar JSON, not a database** — store next to the ruleset in git; review via PR; revert via `rules enable`
- ➖ **Never edits a rule's predicate, lambda, severity, or applicability** — those changes have to flow through the policy → extract pipeline

This is the pattern used by signed-binary release management, applied
to rules. You get all the practical value of an "editor" (turn a rule
off, attach a note) with none of the chain-of-custody risk.

## Solution layout

```
src/
  LambdaRag.Core/         Domain, hashing, selectors, abstractions
  LambdaRag.Parsing/      PDF/DOCX/MD parsers → ParsedDocument
  LambdaRag.Projection/   ParsedDocument → ProjectedDocument + topic maps
  LambdaRag.Selectors/    JSONPath-subset matcher
  LambdaRag.Evaluation/   Microsoft RulesEngine wrapper, verdict aggregator
  LambdaRag.Markup/       OpenXml tracked-changes annotator (deterministic)
  LambdaRag.Authoring/    MAF agents: extract rules from policy docs
  LambdaRag.Persistence/  SQLite stores: rules, projections, evaluations
  LambdaRag.Api/          ASP.NET Core minimal API (future-facing)
  LambdaRag.Cli/          `lambda-rag` command-line tool
tests/
  LambdaRag.UnitTests/             106 unit tests
  LambdaRag.IdempotencyTests/      4 run-twice + golden-master byte-equality proofs (report.json + reviewed.docx)
samples/contracts/                 contract.md + ruleset.json
docs/                              ARCHITECTURE.md, DETERMINISM.md, SELECTORS.md
```

## Roadmap

> **Phase 0 (credibility close-out) — ✅ complete.** AC gap analysis,
> reviewed.docx golden-master idempotency, defensible accuracy framing,
> [`what-lambda-rag-is-not.md`](docs/what-lambda-rag-is-not.md), and a
> [Roslyn-scripting contingency](docs/dependencies/rules-engine-risk.md)
> for the RulesEngine dependency are all shipped. See
> [`CHANGELOG.md`](CHANGELOG.md) and the
> [phase-0 backlog filter](https://github.com/MTCMarkFranco/lambda-rag/issues?q=is%3Aissue+label%3Aphase-0-credibility).

Phases 1–5 (canonical pattern, Canadian regulatory wedges, distribution,
governance + tooling, ecosystem) live as labelled GitHub issues. Near-term:

- 🖥️ Lightweight web UI (drag-drop document + ruleset → verdict + redlined .docx download)
- 🔌 Live Word task-pane add-in for in-place review (currently offline `.docx` markup only)
- 🌐 REST API surface in `LambdaRag.Api` exposing the same pipeline
- ✅ Positive-confirmation comments in markup mode (currently only Fail / Gap / Error are surfaced)

## License

MIT.

