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

> 🖼️ **One picture:**
> [`docs/diagrams/authoring-vs-runtime.md`](docs/diagrams/authoring-vs-runtime.md)
> is the canonical authoring-vs-runtime architecture diagram. Use it
> in slides, papers, and onboarding.

> 📜 **One page of prose:**
> [`docs/manifesto.md`](docs/manifesto.md) — *Rule Projection: Deterministic
> Reasoning over Documents.* The pattern, the bet, and the honest limits.
> Read this before deciding whether lambda-rag fits your problem.

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
| `arb-psa.v1` | Architecture Review Board — Project Solution Architecture review (12 dims) |

List them at any time:

```pwsh
dotnet run --project src/LambdaRag.Cli -- topic-map list
```

## 🚀 Try it on the bundled sample

```pwsh
dotnet build
dotnet test    # 266 unit + 35 idempotency / golden-master proofs

# Review the bundled sample contract → JSON report
dotnet run --project src/LambdaRag.Cli -- review `
  --document samples/contracts/contoso-sample-contract.docx `
  --ruleset  rulesets/contracts/contoso-demo-ruleset.json `
  --out      out/sample `
  --mode     report

# Same review → redlined Word document with tracked changes
# (Markup mode requires a .docx source — uses the bundled sample contract)
dotnet run --project src/LambdaRag.Cli -- review `
  --document samples/contracts/contoso-sample-contract.docx `
  --ruleset  rulesets/contracts/contoso-demo-ruleset.json `
  --out      out/sample `
  --mode     markup

# Add positive-confirmation ✓ comments for Pass verdicts (full coverage proof)
dotnet run --project src/LambdaRag.Cli -- review `
  --document samples/contracts/contoso-sample-contract.docx `
  --ruleset  rulesets/contracts/contoso-demo-ruleset.json `
  --out      out/sample `
  --mode     markup `
  --annotate-pass

# Both at once, with --rewrite to emit tracked-change replacements
# (ComplianceEditor renders a concrete rewrite for each Fail verdict
#  and the markup stage applies it as a w:del / w:ins pair anchored
#  to the offending clause)
dotnet run --project src/LambdaRag.Cli -- review `
  --document samples/contracts/contoso-sample-contract.docx `
  --ruleset  rulesets/contracts/contoso-demo-ruleset.json `
  --out      out/sample `
  --mode     both `
  --rewrite
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

The `--domain` flag tells the extractor which topic ontology to use
when mapping policy sections to canonical topic IDs. Available domains:

| Domain | Description |
|--------|-------------|
| `contract` | Commercial contract review (default) |
| `architecture-review` | Cloud architecture / ASD review |
| `fsi` | Financial services (Basel, AML, KYC, capital adequacy) |
| `oil-gas` | Upstream / downstream (HSE, well integrity, environmental) |
| `business-review` | MOUs, SOWs, business cases, vendor reviews |
| `gov-architecture` | Government cloud architecture review |
| `permitting` | Government permit / planning application review |

```pwsh
# 1. Drop your policy files into a folder
mkdir policies\acme-corp
# copy ACME-Procurement-Policy.pdf, ACME-DataProtection.docx, etc. into it

# 2. Run the deterministic extractor
dotnet run --project src/LambdaRag.Cli -- extract-rules `
  --policy-dir policies/acme-corp `
  --domain     contract `
  --id         rs_acme_procurement `
  --out        rulesets/contracts/acme-procurement.json `
  --prefix     ACME `
  --min-chars  200
```

Output: `rulesets/contracts/acme-procurement.json` — every rule includes:
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
  --out    rulesets/contracts/clause-7-rule.json
```

### Option C — Hand-write a ruleset

Look at `rulesets/contracts/ruleset.json`. The schema is small and
documented in `docs/`. Anything you can express as a typed predicate
over a projected document graph can be a rule.

### Then test it

```pwsh
# Sanity-check coverage of your ruleset against a target document
dotnet run --project src/LambdaRag.Cli -- coverage `
  --document my-customer-doc.docx `
  --ruleset  rulesets/contracts/acme-procurement.json `
  --out      out/acme/coverage.json

# Run the full review
dotnet run --project src/LambdaRag.Cli -- review `
  --document my-customer-doc.docx `
  --ruleset  rulesets/contracts/acme-procurement.json `
  --out      out/acme `
  --mode     both
```

### Adding a brand-new industry topic map

If the ontology you need isn't in the table above, copy
`src/LambdaRag.Projection/TopicMaps/contract.v1.json` to
`my-industry.v1.json`, add your headings/aliases per topic, rebuild,
and pass `--topic-map my-industry.v1` to the extractor.

### Numeric thresholds with `text_features` (projector v1.4.0+)

Every projected section now carries a `text_features` block with
generic numeric facts extracted from the section's prose:

| Field | What it captures | Example match |
|-------|------------------|---------------|
| `day_counts` / `day_count_min` / `day_count_max` | day quantities | `45 days`, `120-day cure`, `90 calendar days` |
| `month_counts` / `_min` / `_max` | month quantities | `12 months`, `36-month term` |
| `year_counts` / `_min` / `_max` | year quantities | `5 years`, `2-year warranty` |
| `percent_values` / `percent_min` / `percent_max` | percentages | `1.5%`, `30 percent` |
| `dollar_amounts` / `dollar_min` / `dollar_max` | dollar values | `$5,000,000`, `$1.5M`, `USD 10,000,000`, `CAD$ 2.5 million` |

Rule lambdas reference these fields directly — no per-domain code:

```json
{
  "predicate": "input1.topics.Contains(\"insurance\") && input1.text_features.dollar_amounts.Count > 0",
  "lambda":    "input1.text_features.dollar_max >= 5000000"
}
```

This is a *generic* extractor: it works on **any** domain (vendor
bonds, ESG recycled-content thresholds, permit response windows,
pipeline pressure-test durations…). The same rule shape is used for
contracts, public-sector permitting, oil-and-gas, FSI policies, and
governance frameworks.

## CLI cheat sheet

```
lambda-rag review        --document <path> --ruleset <path> --out <dir> [--mode report|markup|both] [--overlay <path>] [--annotate-pass] [--rewrite] [--doc-kind <id>] [--topic-map <id-or-path>]
lambda-rag extract-rules --policy-dir <dir> --domain <name> --id <ruleset-id> --out <path>
lambda-rag author        --chunk <path> --domain <name> --prefix <id-prefix> --out <path>
lambda-rag coverage      --document <path> --ruleset <path> --out <path>
lambda-rag project       --document <path> --out <path>
lambda-rag parse         --document <path> --out <path>
lambda-rag index         --ruleset <path> [--out <path>]              # authoring-side only — see wrong-path-search-index.md
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
     --ruleset rulesets/contracts/acme.json `
     --overlay rulesets/contracts/acme.overlay.json `
     --rule    ACME-PAY-003 `
     --reason  "superseded by 2026-Q2 side-letter clause 4.2" `
     --by      legal@acme.com
   ```

2. **Annotate a rule** — reviewer commentary that does *not* change the verdict

   ```pwsh
   lambda-rag rules annotate `
     --ruleset rulesets/contracts/acme.json `
     --overlay rulesets/contracts/acme.overlay.json `
     --rule    ACME-LIAB-001 `
     --note    "see clause 7.2 in MSA — capped at fees paid in prior 12 months" `
     --by      legal@acme.com
   ```

Then run a review with the overlay applied:

```pwsh
lambda-rag review `
  --document customer-doc.docx `
  --ruleset  rulesets/contracts/acme.json `
  --overlay  rulesets/contracts/acme.overlay.json `
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
  LambdaRag.Core/                       Domain, hashing, selectors, abstractions
  LambdaRag.Parsing/                    PDF/DOCX/MD parsers → ParsedDocument
  LambdaRag.Projection/                 ParsedDocument → ProjectedDocument + topic maps
  LambdaRag.Selectors/                  JSONPath-subset matcher
  LambdaRag.Evaluation/                 Microsoft RulesEngine wrapper, verdict aggregator
  LambdaRag.Markup/                     OpenXml tracked-changes annotator + IClauseRewriter
  LambdaRag.Authoring/                  MAF agents: extract rules + ComplianceEditor (rewrites)
  LambdaRag.Authoring.ExtractFunction/  Azure Function — Web API custom skill for indexer-driven extraction
  LambdaRag.Indexing/                   Authoring-side Azure Search adapters (rule semantic / signature indexes)
  LambdaRag.Persistence/                SQLite stores: rules, projections, evaluations
  LambdaRag.Api/                        ASP.NET Core minimal API — POST /review
  LambdaRag.Cli/                        `lambda-rag` command-line tool
tests/
  LambdaRag.UnitTests/             266 unit tests
  LambdaRag.IdempotencyTests/      35 run-twice + golden-master byte-equality proofs
  Goldens/corpus/                  5-vertical regression corpus (frozen expected-verdict.json)
rulesets/                          generated and hand-authored rulesets, organized by domain
  arb/                               ARB rulesets (arb-ruleset.json, arb-ruleset-with-examples.json)
  cloud/                             cloud architecture rulesets (cloud_architecture.json, …)
  contracts/                         contract review rulesets (contoso-demo-ruleset.json, loi-25-ruleset.json, …)
samples/                           sample input documents for review (no rulesets here)
  architecture/                      sample_asd.docx, Cloud-service-Architecture.docx
  contracts/                         contoso-sample-contract.docx, contract.md
policies/                          source policy documents (input to extract-rules / author)
  arb/                               Architecture Review Board markdown policies
  cloud/                             Azure cloud policy documents
docs/                              ARCHITECTURE.md, DETERMINISM.md, PIPELINE.md, SELECTORS.md, manifesto.md, diagrams/, findings/, regulatory/
wrong-path-search-index.md         Postmortem: why the runtime never reads rules from Azure Search
```

> 🧭 **Anti-pattern note.** A previous direction wired the runtime to fetch rules from an Azure AI Search
> index. It is rolled back on `main`. The reasoning, signals we missed, and rules for next time live in
> [`wrong-path-search-index.md`](wrong-path-search-index.md). The `LambdaRag.Indexing` project remains for
> *authoring-side* discovery only — no runtime evaluation reads from a search index.

## Roadmap

> **Phase 0 (credibility close-out) — ✅ complete.** Contoso gap analysis,
> reviewed.docx golden-master idempotency, defensible accuracy framing,
> [`what-lambda-rag-is-not.md`](docs/what-lambda-rag-is-not.md), and a
> [Roslyn-scripting contingency](docs/dependencies/rules-engine-risk.md)
> for the RulesEngine dependency are all shipped. See
> [`CHANGELOG.md`](CHANGELOG.md) and the
> [phase-0 backlog filter](https://github.com/MTCMarkFranco/lambda-rag/issues?q=is%3Aissue+label%3Aphase-0-credibility).

> **P1.8 (golden test corpus) — ✅ shipped (5 verticals).** A
> public-source-grounded regression corpus lives under
> [`tests/Goldens/corpus/`](tests/Goldens/corpus/) with five verticals:
> *gov-architecture* (Government of Canada Cloud Guardrails v2.0),
> *fsi* (OSFI Guideline B-10), *contract* (TBS SACC + PIPEDA),
> *permitting* (Ontario Building Code O.Reg.332/12 + IASR/AODA O.Reg.191/11
> + Impact Assessment Act S.C.2019 c.28 + Constitution Act 1982 s.35), and
> *oil-gas* (CER Onshore Pipeline Regulations SOR/99-294 + Methane Regulations
> SOR/2018-66 + AER Directive 071 + s.35). 11 candidate documents, 25 rules,
> frozen `expected-verdict.json` snapshots, and a
> [`corpus-regression`](.github/workflows/corpus-regression.yml)
> GitHub Actions job that fails the build on any drift.

> **P1 pattern-definition batch — ✅ shipped (5 docs).** The canonical
> documentation set for the rule-projection pattern is now in repo:
> the [manifesto](docs/manifesto.md) ([P1.1 #11](https://github.com/MTCMarkFranco/lambda-rag/issues/11)),
> the [authoring-vs-runtime diagram](docs/diagrams/authoring-vs-runtime.md) ([P1.6 #16](https://github.com/MTCMarkFranco/lambda-rag/issues/16)),
> and three regulatory clause-by-clause mappings:
> [OSFI E-23](docs/regulatory/osfi-e23-mapping.md) ([P1.2 #12](https://github.com/MTCMarkFranco/lambda-rag/issues/12)),
> [TBS Directive on ADM](docs/regulatory/tbs-adm-mapping.md) ([P1.5 #15](https://github.com/MTCMarkFranco/lambda-rag/issues/15)), and
> [Bill C-27 / AIDA](docs/regulatory/bill-c27-aida-mapping.md) ([P1.3 #13](https://github.com/MTCMarkFranco/lambda-rag/issues/13))
> with ~80 candidate rules sketched and worked JSON examples for each.

Phases 1–5 (canonical pattern, Canadian regulatory wedges, distribution,
governance + tooling, ecosystem) live as labelled GitHub issues. Near-term:

- 🖥️ Lightweight web UI (drag-drop document + ruleset → verdict + redlined .docx download)
- 🔌 Live Word task-pane add-in for in-place review (currently offline `.docx` markup only)
- 🌐 REST API surface in `LambdaRag.Api` exposing the same pipeline
- ✅ Positive-confirmation comments in markup mode (currently only Fail / Gap / Error are surfaced)

## License

MIT.

