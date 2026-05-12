# Lambda-RAG Rule Curation UI

A lightweight, static single-page application for curating rules in the `lambda-rag-rules` Azure AI Search index.

## What it does

- **Filter** by `rulesetName` and `rulesetVersion` (populated from Search facets)
- **View** rules: ID, natural language description, lambda expression, severity, ruleset metadata
- **Edit** the `status` field per rule (`approved` → `disabled` or vice-versa)
- **Save** changes by calling the Azure Search merge API with delegated MSAL tokens

## Prerequisites

1. An Azure App Registration with:
   - Type: **Single-Page Application (SPA)**
   - Redirect URI: `https://<storage-account>.z13.web.core.windows.net/` (or your custom domain)
   - API Permission: **Azure Cognitive Search** → `user_impersonation` (delegated)
   - The identity running the browser must have **Search Index Data Contributor** on `srch-lambdarag-dev`

2. Azure Blob Storage with **static website** hosting enabled on `lambdaragauthdev`

## Deploying

### Step 1 — Create App Registration

```bash
# Register the SPA (do this once)
az ad app create \
  --display-name "lambda-rag-curation-ui" \
  --spa-redirect-uris "https://lambdaragauthdev.z13.web.core.windows.net/" \
  --sign-in-audience AzureADMyOrg
```

Note the **Application (client) ID** and **Directory (tenant) ID** from the Azure Portal.

### Step 2 — Configure `config.js`

Copy `dist/config.js.template` to `dist/config.js` and fill in your values:

```js
window.lambdaRagConfig = {
  clientId: "<APP-REGISTRATION-CLIENT-ID>",
  tenantId: "<TENANT-ID>",
  searchEndpoint: "https://srch-lambdarag-dev.search.windows.net",
  indexName: "lambda-rag-rules"
};
```

⚠️ `config.js` is in `.gitignore` — do NOT commit it. It contains your tenant/client IDs.

### Step 3 — Enable static website hosting

```powershell
az storage blob service-properties update `
  --account-name lambdaragauthdev `
  --static-website `
  --index-document index.html `
  --auth-mode login
```

Get the static site endpoint:
```powershell
az storage account show `
  --name lambdaragauthdev `
  --query "primaryEndpoints.web" -o tsv
```

### Step 4 — Upload

```powershell
az storage blob upload-batch `
  --destination '$web' `
  --source dist `
  --account-name lambdaragauthdev `
  --auth-mode login `
  --overwrite
```

After upload, navigate to the static endpoint URL in your browser.

## RBAC

The signed-in user needs **Search Index Data Contributor** on `srch-lambdarag-dev`:

```bash
az role assignment create \
  --role "Search Index Data Contributor" \
  --assignee <user-upn> \
  --scope /subscriptions/ME-MngEnvMCAP490549-marfra-1/resourceGroups/rg-lambdarag-dev/providers/Microsoft.Search/searchServices/srch-lambdarag-dev
```

## Known limitations

- `config.js` must be served from the same origin as `index.html`. Populate it at deploy time (e.g., from a Key Vault reference or a pipeline variable).
- CORS on the Azure Search service must allow the static website's origin. Add the `$web` hostname under **CORS** → **Allowed Origins** in the Search service portal blade.

## Architecture

```
Browser
  └─ MSAL → Entra ID (delegated token for https://search.azure.com)
  └─ Fetch → Azure AI Search REST API (lambda-rag-rules index)
              filter: status eq 'approved' and rulesetName eq '...' and rulesetVersion eq '...'
              merge:  { "@search.action": "merge", id, status, approvedAtUtc, approvedBy }
```

No backend required. Direct RBAC per issue #98.
