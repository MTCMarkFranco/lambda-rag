# Lambda-RAG golden test corpus

This directory is the **regression and demo corpus** for lambda-rag —
the canonical set of `(ruleset, document, expected-verdict)` triples we
point to whenever someone asks "but how do you *know* it's accurate?"

## Verticals shipped

| Topic map | Industry vertical | Source | Documents |
|---|---|---|---|
| `gov-architecture` | Public-sector cloud architecture | GC Cloud Guardrails v2.0 | 3 |
| `fsi` | Financial services third-party risk | OSFI Guideline B-10 | 2 |
| `contract` | Canadian commercial contracts | TBS SACC Manual + PIPEDA | 2 |

Per-vertical detail (sources, attribution, sanitisation, and what each
document tests) is in each subdirectory's `README.md`.

## Why these three (and not five)

Issue [#18](https://github.com/MTCMarkFranco/lambda-rag/issues/18)
specifies a target of 5 verticals × 5 documents = 25 triples. Per
direction on the issue thread — "even if you get three of them that's
fine. We don't need to get five — quality over quantity" — this initial
landing ships **3 verticals with depth** rather than 5 verticals with
shallow synthetic content.

The two deferred verticals — `permitting` and `business-review` — are
tracked as a follow-up to this issue. They will be added once we have
authoritative public-source rule documents that match the high bar
established by GC Cloud Guardrails / OSFI B-10 / SACC for the three
shipped today.

## Structure

```
tests/Goldens/corpus/
├── README.md                                    # this file
├── {topic-map}/
│   ├── README.md                                # vertical overview, attribution
│   ├── ruleset.json                             # rules derived from public sources
│   └── {doc-id}/
│       ├── source.md                            # synthetic candidate document
│       ├── rationale.md                         # what this doc tests
│       └── expected-verdict.json                # frozen golden snapshot
└── ...
```

## How the regression works

`tests/LambdaRag.IdempotencyTests/CorpusRegression.cs` iterates every
`{topic-map}/{doc-id}/` directory, runs the full review pipeline using
the topic map matching the directory name, and compares the produced
`ComplianceReport` to the checked-in `expected-verdict.json`. Any
drift fails the build.

The CI workflow `.github/workflows/corpus-regression.yml` runs this
test class on every push.

## Source format note (`.md` vs `.pdf`)

Issue #18's AC originally specified `source.pdf` for each document. We
chose Markdown for synthetic corpus documents because:

- Markdown is **diffable** in code review — a reviewer can see in a
  pull request exactly which sentence in a corpus document changed.
- Markdown is **byte-stable across operating systems** — no PDF
  generator variance to debug when goldens drift.
- The lambda-rag parser supports `.md` natively, so no information is
  lost.

The `.docx` and `.pdf` parser paths remain covered by the existing unit
tests and the bundled `samples/contracts/contract.md` walk-through. A
follow-up issue will add a small set of `.docx` corpus documents
specifically to exercise `expected-markup.docx` regression.

## Regenerating goldens

Goldens are intentionally **frozen**. They should only be regenerated
when a rule, source document, or projector change is intentional. To
regenerate:

```bash
# Delete the affected expected-verdict.json files
Remove-Item tests/Goldens/corpus/<topic-map>/<doc-id>/expected-verdict.json

# Run the corpus regression — it will bootstrap missing goldens and fail loudly
dotnet test tests/LambdaRag.IdempotencyTests --filter FullyQualifiedName~CorpusRegression

# Inspect the generated golden, commit if intentional, re-run to confirm green.
```
