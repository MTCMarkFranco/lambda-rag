# Runtime determinism guardrails

> Issue [#74](https://github.com/MTCMarkFranco/lambda-rag/issues/74).
> Updated for issue [#98](https://github.com/MTCMarkFranco/lambda-rag/issues/98) (index-as-source-of-truth).
> Companion to [#72](https://github.com/MTCMarkFranco/lambda-rag/issues/72) (AI Search authoring pivot)
> and [#73](https://github.com/MTCMarkFranco/lambda-rag/issues/73) (self-validating rules).

## Why these guardrails exist

`lambda-rag` is positioned as a **deterministic, replayable, audit-grade**
rule-evaluation engine. The user-visible promise is:

> The same ruleset name + version + input document always produce the same
> `ComplianceReport` (verdict outcomes, remediation text), with the same
> `ruleSnapshotHash` in provenance.

Issue #98 makes **Azure AI Search the runtime source of truth** for rules.
The index is queried via `IRuleStore` (always with `status eq 'approved'`)
rather than from local JSON files.

## Hard boundary (updated for #98)

| Concern | Allowed at authoring time | Allowed at runtime |
|---------|--------------------------|-------------------|
| `Azure.Search.Documents` (authoring) | ✅ | ✅ via `IRuleStore` only |
| `IRuleStore.RetrieveAllAsync/RetrieveAsync` | n/a | ✅ Required |
| Direct `RuleSet` load from file | ✅ (tests/seed only) | 🚫 |
| `Azure.AI.OpenAI` (LLM authoring) | ✅ | 🚫 |
| Outbound HTTP (non-search) | ✅ | 🚫 |
| Frozen vector store | n/a | ✅ |
| Frozen per-rule thresholds | n/a | ✅ |

"Runtime" is defined as the execution path of
`LambdaRag.Evaluation.EvaluationService.EvaluateAsync(...)` and every
project that participates in producing its inputs at the moment the call
is made: `LambdaRag.Core`, `LambdaRag.Evaluation`, `LambdaRag.Selectors`,
`LambdaRag.Projection`, `LambdaRag.Parsing`, `LambdaRag.Markup`,
`LambdaRag.Persistence`.

## What the guardrails enforce

The tests live in `tests/LambdaRag.IdempotencyTests/RuntimeDeterminismGuardrails.cs`.

### 1. Package reference guardrail
`Runtime_project_does_not_reference_banned_package` — for each runtime
project, parses `obj/project.assets.json` and asserts the resolved NuGet
graph does **not** contain `Microsoft.SemanticKernel.Connectors.AzureAISearch`
or similar banned packages.

Note: `Azure.Search.Documents` is now permitted transitively in runtime
projects (via `LambdaRag.Indexing` → `AzureSearchRuleStore` → `IRuleStore`)
but **only through the `IRuleStore` abstraction**. Direct construction of
`SearchClient` outside of `AzureSearchRuleStore` is a guardrail violation.

### 2. Loaded assembly guardrail
`No_banned_assembly_is_loaded_after_evaluation` — runs a real corpus
evaluation, then enumerates `AppDomain.CurrentDomain.GetAssemblies()` and
asserts no forbidden assemblies have been loaded. Catches dynamic loads,
type-forwarders, and reflection-based instantiation that the static
package check would miss.

### 3. Replay byte-identity (corpus-wide)
`Corpus_document_produces_byte_identical_report_across_two_runs` — for
every `(vertical, doc)` pair under `tests/Goldens/corpus`, runs the full
review pipeline twice and asserts the SHA-256 of the canonical-JSON
report is identical. For index-backed rulesets, the `ruleSnapshotHash`
in provenance must also match across runs.

### 4. Snapshot pull determinism *(deferred)*
`Snapshot_pull_is_byte_identical_across_runs` — placeholder, currently
`[Fact(Skip = "Blocked on #72")]`.

## New guardrails added in #98

### 5. `status eq 'approved'` filter
Every call to `AzureSearchRuleStore` includes `status eq 'approved'` in
its `$filter`. Tested as a unit test on the query-building method
(see `AzureSearchRuleStoreFilterTests`).

### 6. Provenance stamping
Every `ComplianceReport` produced by the CLI MUST include a `Provenance`
block with `rulesetName`, `rulesetVersion`, `indexEndpoint`, and
`ruleSnapshotHash`. Missing provenance = broken audit chain.

## Do not remove these tests

If you need to make a change that breaks any of guardrails 1–4, the
correct response is **almost always to revert your change**, not to
disable the test. Acceptable reasons to modify a guardrail:

1. The runtime project list legitimately changes (new project added /
   merged). Update `RuntimeDeterminismGuardrails.RuntimeProjects` and
   document the change in this file.
2. A new banned package category appears. Add to `BannedPackages`.
3. Goldens are intentionally re-snapshotted because the rule artifacts
   themselves changed. Update the goldens; the byte-identity assertion
   itself stays.

Reference: issue #98 for the rationale behind the `IRuleStore` mandate.

## Why these guardrails exist

`lambda-rag` is positioned as a **deterministic, replayable, audit-grade**
rule-evaluation engine. The user-visible promise is:

> The same ruleset + the same input document always produce the same
> `ComplianceReport`, byte-for-byte, with no outbound network calls during
> evaluation.

Issue #72 introduces Azure AI Search as the **authoring** pipeline (rule
extraction, paraphrase generation, hybrid search, semantic ranker). That is
desirable — but it creates a real risk that, over time, an authoring-time
dependency leaks into the runtime evaluation path. If that ever happens,
the determinism contract is silently broken and our verdicts are no longer
auditable.

These guardrails are the load-bearing fence that prevents the leak.

## Hard boundary

| Concern                          | Allowed at authoring time | Allowed at runtime |
| -------------------------------- | ------------------------- | ------------------ |
| `Azure.Search.Documents`         | ✅                        | 🚫                 |
| `Azure.AI.OpenAI` (LLM authoring)| ✅                        | 🚫                 |
| Outbound HTTP                    | ✅                        | 🚫                 |
| Frozen vector store              | n/a                       | ✅                 |
| Frozen per-rule thresholds       | n/a                       | ✅                 |

"Runtime" is defined as the execution path of
`LambdaRag.Evaluation.EvaluationService.EvaluateAsync(...)` and every
project that participates in producing its inputs at the moment the call
is made: `LambdaRag.Core`, `LambdaRag.Evaluation`, `LambdaRag.Selectors`,
`LambdaRag.Projection`, `LambdaRag.Parsing`, `LambdaRag.Markup`,
`LambdaRag.Persistence`.

## What the guardrails enforce

The tests live in `tests/LambdaRag.IdempotencyTests/RuntimeDeterminismGuardrails.cs`.

### 1. Package reference guardrail
`Runtime_project_does_not_reference_banned_package` — for each runtime
project, parses `obj/project.assets.json` and asserts the resolved NuGet
graph does **not** contain any of the banned packages
(`Azure.Search.Documents`, `Microsoft.SemanticKernel.Connectors.AzureAISearch`).

This is a **transitive** check, because `project.assets.json` reflects
the full resolved graph after restore — not just direct
`<PackageReference>` entries.

### 2. Loaded assembly guardrail
`No_banned_assembly_is_loaded_after_evaluation` — runs a real corpus
evaluation, then enumerates `AppDomain.CurrentDomain.GetAssemblies()` and
asserts no banned assembly has been loaded. Catches dynamic loads,
type-forwarders, and reflection-based instantiation that the static
package check would miss.

### 3. Replay byte-identity (corpus-wide)
`Corpus_document_produces_byte_identical_report_across_two_runs` — for
every `(vertical, doc)` pair under `tests/Goldens/corpus`, runs the full
review pipeline twice and asserts the SHA-256 of the canonical-JSON
report is identical. Extends the existing single-document idempotency
check from `ReviewPipelineIdempotency` to the entire corpus.

### 4. Snapshot pull determinism *(deferred)*
`Snapshot_pull_is_byte_identical_across_runs` — placeholder, currently
`[Fact(Skip = "Blocked on #72")]`. Activated when #72 ships
`lambda-rag ruleset pull --version <hash>`. Asserts two pulls of the same
version produce byte-identical local artifacts.

## Do not remove these tests

If you need to make a change that breaks any of guardrails 1–3, the
correct response is **almost always to revert your change**, not to
disable the test. Acceptable reasons to modify a guardrail:

1. The runtime project list legitimately changes (new project added /
   merged). Update `RuntimeDeterminismGuardrails.RuntimeProjects` and
   document the change in this file.
2. A new banned package category appears (e.g. a future `Azure.AI.*`
   package that is also authoring-only). Add to `BannedPackages`.
3. Goldens are intentionally re-snapshotted because the rule artifacts
   themselves changed. Update the goldens; the byte-identity assertion
   itself stays.

If you find yourself wanting to add `Azure.Search.Documents` to a
runtime project's references — stop. That is the exact regression these
tests exist to prevent. Open a discussion on #74 / #72 first.
