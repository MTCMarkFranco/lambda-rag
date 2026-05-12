# lambda-rag — AI Search authoring stack (issue [#72](https://github.com/MTCMarkFranco/lambda-rag/issues/72))

This directory provisions the **authoring-time** cloud surface for
lambda-rag. The runtime evaluation path is **never** allowed to call any of
these resources — Phase C guardrails (PR #75) enforce that boundary in CI.

```
infra/search/
├── main.bicep                      # service + storage + RBAC
├── modules/
│   ├── search-service.bicep        # Microsoft.Search/searchServices
│   ├── storage.bicep               # source-policy blob container
│   ├── rbac.bicep                  # RBAC at the local RG scope
│   └── rbac-openai.bicep           # cross-RG role on the Foundry account
├── rest/
│   ├── index.json                  # ExtractedRule + vector field schema
│   ├── datasource.json             # blob datasource
│   ├── skillset.json               # Layout → Split → extractRule → Embedding  (PDF/DOCX path)
│   ├── skillset-md.json            # Split → extractRule → Embedding            (Markdown path)
│   ├── indexer.json                # links datasource → skillset → index        (PDF/DOCX)
│   └── indexer-md.json             # links datasource → skillset-md → index     (Markdown)
├── scripts/
│   └── deploy-search-assets.ps1    # PUTs the REST objects via az+REST
└── README.md
```

## Architecture

### Dual-indexer pattern (issue #84)

Drop **PDF, DOCX, or Markdown** files into the `policies` blob container — no local preprocessing needed.

| File type | Indexer | Skillset | Notes |
|---|---|---|---|
| `.pdf`, `.docx` | `lambda-rag-rules-indexer` | Layout → Split → extractRule → embedConcepts | DI Layout converts binary to Markdown first |
| `.md` | `lambda-rag-rules-indexer-md` | Split → extractRule → embedConcepts | Already text; skips DI Layout entirely |

Both indexers project into the same `lambda-rag-rules` index via `indexProjections.selectors`.

### Binary path (PDF / DOCX)

```
PDF/DOCX in blob ─► Document Layout skill  (markdown + heading hierarchy)
                 ─► Text Split skill        (8000 char chunks / 400 overlap)
                 ─► extract-rule Function   (returns validated ExtractedRule)
                 ─► Embedding skill         (concepts → 3072-d vector)
                 ─► Index projection        (one-document-per-rule → lambda-rag-rules)
```

### Markdown path

```
.md in blob ─► Text Split skill   (8000 char chunks / 400 overlap, on /document/content)
            ─► extract-rule Function
            ─► Embedding skill
            ─► Index projection   (same lambda-rag-rules index)
```

The CLI consumes the index two ways:

| Verb | Operation | Why filter-based pull |
|------|-----------|-----------------------|
| `lambda-rag author --source <pdf>` | Drops PDFs in the blob container, kicks the indexer, polls completion. | Lets reviewers re-trigger an indexer run without local LLM creds. |
| `lambda-rag ruleset pull --domain <d> --version <v>` | `$filter=domain eq 'd' and version eq 'v' and status eq 'approved'`, paged + ordered, hashed to a frozen snapshot file. | Replay-safe: the runtime only ever loads the on-disk snapshot, never hits the index. |

## Deployment

### 1. Provision Azure resources

```pwsh
az login
az account set --subscription <sub>

az group create -n rg-lambdarag-dev -l eastus

az deployment group create `
  --resource-group rg-lambdarag-dev `
  --template-file infra/search/main.bicep `
  --parameters `
      workload=lambdarag `
      environment=dev `
      openAiAccountResourceId=/subscriptions/<sub>/resourceGroups/rg-openai-hub/providers/Microsoft.CognitiveServices/accounts/<acct> `
      authorObjectIds='["<your-entra-oid>"]'
```

### 2. Deploy the search-side assets

```pwsh
./infra/search/scripts/deploy-search-assets.ps1 `
    -SearchServiceName srch-lambdarag-dev `
    -SubscriptionId    <sub> `
    -ResourceGroup     rg-lambdarag-dev `
    -StorageAccount    lambdaragauthdev `
    -OpenAiEndpoint    https://rg-openai-hub.services.ai.azure.com `
    -ChatDeployment    gpt-4o-mini `
    -EmbeddingDeployment text-embedding-3-large
```

The script reads `samples/authoring/rule-extraction.system-prompt.md` and
embeds it inside the GenAI Prompt skill — the same prompt validated at
20/20 schema-pass in the spike (`docs/findings/spike-72-comparison.md`).

## Index features in use

- **Hybrid retrieval (Phase B)** — `concepts` is a searchable BM25 field
  *and* `conceptsVector` is a 3072-d HNSW vector. The Phase B
  self-validation gate uses RRF + the `default-semantic` configuration.
- **Filtering / faceting** — `domain`, `version`, `status`, `severity`,
  `applicability`, `metadata/category`, `metadata/mandatory`. Powers
  `ruleset pull` and the future review UI.
- **Scoring profile `boost-mandatory`** — boosts mandatory rules when
  reviewers query for guidance.
- **Integrated vectorization** — index-level `vectorizer` lets queries
  pass plain text via `vectorQueries[].kind=text` without the client
  embedding upfront.

## Re-authoring an existing ruleset

```pwsh
# 1. Upload the source PDFs.
az storage blob upload-batch `
   -d policies -s ./policies/arb/source-pdfs `
   --account-name lambdaragauthdev --auth-mode login

# 2. Run the indexer.
az search indexer run --service-name srch-lambdarag-dev --name lambda-rag-rules-indexer

# 3. Once approved, pull the snapshot for the runtime.
lambda-rag ruleset pull `
    --search-service srch-lambdarag-dev `
    --domain architecture-review `
    --version v1 `
    --out samples/contracts/arb-ruleset.json
```

The runtime then loads `samples/contracts/arb-ruleset.json` exactly as it
does today — byte-identical, content-hashed, replay-safe.
