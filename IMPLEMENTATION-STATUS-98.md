# Implementation Status: Issue #98 Index-as-Source-of-Truth Runtime

Branch: feat/98-index-source-of-truth
Status: PARTIAL IMPLEMENTATION (foundation complete, runtime integration incomplete)

## Completed Work (Phases 1-2)

### ✅ Phase 1: Index Schema and Function Changes

**Completed:**
- Added 4 new fields to index schema (infra/search/rest/index.json):
  - rulesetName (Edm.String, filterable, facetable, sortable)
  - rulesetVersion (Edm.String, filterable, facetable, sortable)
  - approvedAtUtc (Edm.DateTimeOffset, filterable, sortable)
  - approvedBy (Edm.String, filterable)
  - NOTE: status and contentHash already existed in the schema

- Updated extraction schema (samples/authoring/rule-extraction.schema.json):
  - Added status, rulesetName, rulesetVersion, contentHash, approvedAtUtc, approvedBy to required fields
  - Added field definitions with validation patterns

- Modified ExtractRuleFunction (src/LambdaRag.Authoring.ExtractFunction/RuleExtractionService.cs):
  - Reads LambdaRag__Authoring__DefaultRulesetName and LambdaRag__Authoring__DefaultRulesetVersion env vars
  - Defaults: architecture-review @ 2026.05
  - Populates status="approved", approvedBy="system", approvedAtUtc=now
  - Computes contentHash as sha256(naturalLanguage:{nl}|lambda:{lambda}|predicate:{pred})

- Updated skillset projections (infra/search/rest/skillset.json and skillset-md.json):
  - Added mappings for all 6 new fields
  - Binary path: /document/layoutMarkdown/*/chunks/*/extractedRule/<field>
  - MD path: /document/chunks/*/extractedRule/<field>

**NOT deployed:**
- Index schema changes not pushed to srch-lambdarag-dev
- Function code not published to func-lambdarag-extract-dev
- App settings (LambdaRag__Authoring__DefaultRulesetName/Version) not configured

### ✅ Phase 2: IRuleStore Abstraction

**Completed:**
- Created IRuleStore interface (src/LambdaRag.Core/Abstractions/IRuleStore.cs):
  - GetAvailableVersionsAsync(rulesetName) → distinct versions via facets
  - RetrieveAsync(RuleQuery) → hybrid BM25+vector retrieval with topK
  - RetrieveAllAsync(rulesetName, rulesetVersion) → full ruleset

- Implemented AzureSearchRuleStore (src/LambdaRag.Indexing/AzureSearch/AzureSearchRuleStore.cs):
  - Uses DefaultAzureCredential
  - Always filters: status eq 'approved' and rulesetName eq '<name>' and rulesetVersion eq '<ver>'
  - Hybrid query: BM25 over naturalLanguage/concepts/predicate + vector over conceptsVector
  - Computes snapshot hash: sha256 over sorted [{ruleId, contentHash}] JSON

- Implemented InMemoryRuleStore (src/LambdaRag.Core/InMemoryRuleStore.cs):
  - Fixture-backed, deterministic retrieval
  - Simple BM25-like token overlap + cosine similarity scoring
  - Same snapshot hash algorithm as Azure implementation

- Added supporting types:
  - RuleQuery(RulesetName, RulesetVersion, QueryText, QueryVector, TopK)
  - RuleQueryResult(Rules, Metadata)
  - RulesetMetadata(RulesetName, RulesetVersion, IndexEndpoint, SnapshotHash)

**Solution builds successfully.**

## Remaining Work (Phases 3-11)

### Phase 3: Embedding Helper + Runtime Wiring (INCOMPLETE)

**Remaining:**
- Add query embedding helper that wraps existing IRuleEmbedder for runtime use
- Wire IRuleStore into DI container (Program.cs BuildServices):
  - Read LambdaRag:Search:Endpoint and :IndexName from config
  - Bind IRuleStore to AzureSearchRuleStore by default
  - Fall back to error if config missing
- Refactor EvaluationService to consume IRuleStore instead of RuleSet:
  - Replace RuleSet parameter with IRuleStore
  - For each clause chunk, call store.RetrieveAsync(query) with embedded query text
  - Update all call sites in CLI
- **Decision needed:** How to handle existing file-based overlays (RuleOverlay) when rules come from index?

### Phase 4: CLI Ergonomics (NOT STARTED)

**Remaining:**
- Remove --ruleset flag from ReviewAsync and related commands
- Add --ruleset-name and --ruleset-version flags
- Create lambdarag.config.json schema and loader:
  `json
  {
    "defaults": {
      "rulesetName": "architecture-review",
      "rulesetVersion": "2026.05"
    }
  }
  `
- Resolution order:
  1. Explicit --ruleset-version flag
  2. Config file defaults.rulesetVersion
  3. If neither: call IRuleStore.GetAvailableVersionsAsync, print list, exit 2
- Update PrintHelp() with new syntax

### Phase 5: Output Traceability (NOT STARTED)

**Remaining:**
- Augment ComplianceReport with provenance block:
  `json
  "provenance": {
    "rulesetName": "...",
    "rulesetVersion": "...",
    "indexEndpoint": "...",
    "ruleSnapshotHash": "<sha256>",
    "runAtUtc": "...",
    "documentSha256": "<hash>"
  }
  `
- Compute documentSha256 from input document bytes
- Redline docx: prepend comment or doc property with provenance JSON

### Phase 6: Seed Migration + Remove File-Based Ruleset (NOT STARTED)

**Remaining:**
- Create infra/search/scripts/seed-ruleset-from-json.ps1:
  - Takes *.ruleset.json path, rulesetName, rulesetVersion
  - POSTs each rule via POST /docs/index with @search.action: mergeOrUpload
  - Sets status=approved, approvedBy=seed, approvedAtUtc=now
  - Computes contentHash same as Function
- Run seed script against samples/contracts/contoso-demo-ruleset.json → rulesetName=architecture-review, version=2026.05-seed
- Delete all *.ruleset.json files from samples/contracts/
- Delete RuleSet class, RuleSetIO.Load, and file-based loader
- Delete fixture files in tests that load file-based rulesets

### Phase 7: Tests (NOT STARTED)

**Remaining:**
- Update unit tests to bind InMemoryRuleStore
- Update idempotency tests to use InMemoryRuleStore fixture
- Update architecture tests:
  - Remove "no SearchClient in runtime" assertion
  - Add assertion: code producing Verdict MUST receive rules via IRuleStore
  - Add assertion: AzureSearchRuleStore queries MUST include status eq 'approved' filter
- Run dotnet test and fix failures

### Phase 8: Curation UI SPA (NOT STARTED)

**Remaining:**
- Adapt rules-iq/src/ui-adapter/ (from Clawpilot repo) to lambda-rag:
  - Three editable columns: ruleId (RO), lambda (RO), status (editable dropdown: approved/disabled)
  - Readonly columns: severity, rulesetName, rulesetVersion
  - Filter bar: rulesetName dropdown, rulesetVersion dropdown (populated from facets)
  - Save: POST /indexes/lambda-rag-rules/docs/index with @search.action: merge
  - MSAL: clientId/tenantId from config.js, scope: https://search.azure.com/.default
- Build SPA to dist/
- Deploy to lambdaragauthdev  container:
  - az storage blob service-properties update --account-name lambdaragauthdev --static-website --index-document index.html
  - az storage blob upload-batch -d '' -s dist --account-name lambdaragauthdev --auth-mode login --overwrite
- Create src/LambdaRag.Ui/README.md with:
  - App registration steps (SPA, redirect URI, Search API permission)
  - config.js.template with placeholders
  - .gitignore config.js

### Phase 9: Docs and Guardrail Rewrite (NOT STARTED)

**Remaining:**
- Update docs/ARCHITECTURE.md (or wherever Phase C is documented):
  - Replace "no SearchClient in runtime" with new guardrails
  - Document status eq 'approved' filter requirement
  - Document provenance stamping requirement
  - Reference issue #98 for rationale
- Update README.md:
  - Replace --ruleset examples with --ruleset-name + --ruleset-version
  - Add lambdarag.config.json example
  - Document indexer routing for PDF/DOCX/MD
  - Add "Curation UI" section
  - Add "Versioning" section (how to bump rulesetVersion)
- Update infra/search/README.md with new fields and seed script

### Phase 10: End-to-End Verification (NOT STARTED)

**Remaining:**
- dotnet build -nologo (currently passing)
- dotnet test -nologo (needs test updates from Phase 7)
- Create lambdarag.config.json in repo root with:
  `json
  { "defaults": { "rulesetName": "architecture-review", "rulesetVersion": "2026.05-seed" } }
  `
- Run: dotnet run --project src/LambdaRag.Cli -- review --document samples/contracts/contoso-sample-contract.docx --out out/sample --mode both
- Verify:
  - Run completes
  - out/sample/report.json has provenance block
  - Redlined docx exists and opens
  - At least one verdict was produced
- Determinism check: delete out/sample/, rerun, diff report.json (should be identical except runAtUtc)

### Phase 11: Ship (NOT STARTED)

**Remaining:**
- Commit remaining phases with Co-authored-by trailer
- Push feat/98-index-source-of-truth
- Open PR:
  - Title: feat(#98): index-as-source-of-truth runtime
  - Body: summary of all phases, breaking CLI changes, validation results
  - End with "Closes #98"
- Squash-merge with gh pr merge <#> --squash --delete-branch
- Verify gh issue view 98 --json state -q .state returns CLOSED

## Infrastructure Deployment Steps (Required Before E2E Test)

1. **Deploy index schema:**
   `powershell
   cd C:\Projects\lambda-rag\infra\search\scripts
   .\deploy-search-assets.ps1 
     -SearchServiceName "srch-lambdarag-dev" 
     -SubscriptionId "ME-MngEnvMCAP490549-marfra-1" 
     -ResourceGroup "rg-lambdarag-dev"
   `

2. **Configure Function app settings:**
   `powershell
   az functionapp config appsettings set 
     --name func-lambdarag-extract-dev 
     --resource-group rg-lambdarag-dev 
     --settings LambdaRag__Authoring__DefaultRulesetName=architecture-review LambdaRag__Authoring__DefaultRulesetVersion=2026.05
   `

3. **Deploy Function:**
   `powershell
   cd C:\Projects\lambda-rag\infra\search\scripts
   .\deploy-extract-function.ps1 
     -FunctionAppName "func-lambdarag-extract-dev" 
     -ResourceGroup "rg-lambdarag-dev"
   `
   OR manually:
   `powershell
   cd C:\Projects\lambda-rag
   func azure functionapp publish func-lambdarag-extract-dev --csharp
   `

4. **Reset and run indexers:**
   `powershell
   az search indexer reset --name lambda-rag-rules-indexer --service-name srch-lambdarag-dev --resource-group rg-lambdarag-dev
   az search indexer run --name lambda-rag-rules-indexer --service-name srch-lambdarag-dev --resource-group rg-lambdarag-dev
   az search indexer reset --name lambda-rag-rules-indexer-md --service-name srch-lambdarag-dev --resource-group rg-lambdarag-dev
   az search indexer run --name lambda-rag-rules-indexer-md --service-name srch-lambdarag-dev --resource-group rg-lambdarag-dev
   `

5. **Verify indexed documents:**
   `powershell
   az search index show-document-count --index-name lambda-rag-rules --service-name srch-lambdarag-dev --resource-group rg-lambdarag-dev
   `

## Known Issues and Decisions

1. **Blob metadata for rulesetName/rulesetVersion:**
   - The Function reads from blob metadata keys ulesetName and ulesetVersion
   - The skillset doesn't currently expose these (metadata_storage_name is available, but not custom metadata)
   - Current implementation: falls back to env defaults for ALL documents
   - **Decision:** Document this as known limitation in README; revisit if per-blob versioning becomes critical

2. **Overlay compatibility:**
   - Current RuleOverlay (status, notes, disabled list) assumes file-based rulesets
   - With index-backed rules, overlay logic needs rethinking:
     - Option A: Overlays become query-time filters (e.g., exclude disabled ruleIds from retrieval)
     - Option B: Overlays write directly to index (merge status=disabled)
   - **Decision:** Phase 6 removes overlays from runtime path; UI is the curation tool

3. **Selector mapping:**
   - Extracted rules don't specify a selector; the extraction prompt doesn't model this
   - Current implementation: hardcoded PathSelector("$.clauses[*]") in MapToRule
   - **Decision:** Acceptable for v1; real selectors require extraction prompt redesign

4. **RuleSet fingerprint:**
   - RuleSet.Fingerprint() doesn't exist in index-backed world (no single immutable RuleSet object)
   - Replaced by RulesetMetadata.SnapshotHash (computed per-query over retrieved rules)
   - **Decision:** Tests and coverage tools must adapt to per-query snapshot hashes

## Summary

**What's done:**
- Index schema extended ✅
- Function extracts new fields ✅
- Skillsets project new fields ✅
- IRuleStore abstraction defined ✅
- Azure Search implementation complete ✅
- In-memory test implementation complete ✅
- Solution builds ✅

**What's missing:**
- Runtime wiring (CLI, EvaluationService integration)
- Embedding query helper
- Config loader + flag changes
- Provenance stamping
- Seed script + file-based ruleset removal
- Test updates
- Curation UI
- Documentation updates
- E2E verification
- Infrastructure deployment

**Estimated remaining effort:** 16-24 hours (assumes no unexpected blockers)

## Next Steps

1. Deploy infra (index schema + Function) to enable integration testing
2. Wire IRuleStore into CLI (Phase 3)
3. Implement CLI flag changes + config loader (Phase 4)
4. Seed existing ruleset into index (Phase 6 partial)
5. E2E smoke test
6. Tests, docs, UI (Phases 7-9)
7. Full E2E validation (Phase 10)
8. Ship (Phase 11)

---

Generated: 2026-05-12T10:26:34Z
Commits: 3 (fb217cc, a2b3bf0, a5b3a7c)
Branch: feat/98-index-source-of-truth
Author: Copilot (autonomous implementation)
