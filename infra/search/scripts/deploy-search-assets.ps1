<#
.SYNOPSIS
    Deploys the lambda-rag AI Search authoring assets (index, skillset,
    datasource, indexer) to a search service that has already been provisioned
    by `infra/search/main.bicep`.

.DESCRIPTION
    Performs token substitution on the JSON files under ./rest, then PUTs each
    one through the Azure AI Search REST API using the user's
    DefaultAzureCredential bearer token (the search service has
    `disableLocalAuth=true`).

    The skillset uses a Web API custom skill that points at the extract-rule
    Azure Function (issue #79). The Function holds the system prompt + schema
    and calls Foundry with its own managed identity. This script must therefore
    receive the Function URI + a function key via parameters.

    HARD BOUNDARY: this is AUTHORING-TIME tooling. The runtime evaluation path
    must never call the search service — Phase C guardrails enforce that.

.PARAMETER SearchServiceName
    Name of the AI Search service (e.g. "srch-lambdarag-dev").

.PARAMETER SubscriptionId
    Azure subscription that holds the storage account.

.PARAMETER ResourceGroup
    Resource group that holds the storage account.

.PARAMETER StorageAccount
    Storage account name with the "policies" container.

.PARAMETER OpenAiEndpoint
    Azure OpenAI / Foundry endpoint, e.g. https://rg-openai-hub.services.ai.azure.com

.PARAMETER EmbeddingDeployment
    Embedding deployment name used by the embedding skill + index vectorizer
    (e.g. "text-embedding-3-large").

.PARAMETER FunctionAppName
    Name of the extract-rule Function App (e.g. "func-lambdarag-extract-dev").
    Used to resolve the default hostname for the WebApiSkill URI.

.PARAMETER FunctionUri
    Optional explicit URI to the extract-rule endpoint. When omitted, derived
    from FunctionAppName as https://<app>.azurewebsites.net/api/extract-rule.

.PARAMETER AuthResourceId
    AAD audience the AI Search service should request a token for when calling
    the function. Typically `api://<function-app-registration-clientId>`.
    Easy Auth (App Service Authentication v2) on the Function App validates
    this audience and (per allowedApplications) restricts callers to the
    search service's system-assigned MI. No shared keys.

.PARAMETER ApiVersion
    Search REST API version (default 2024-11-01-preview).

.EXAMPLE
    .\deploy-search-assets.ps1 `
        -SearchServiceName srch-lambdarag-dev `
        -SubscriptionId $env:AZURE_SUBSCRIPTION_ID `
        -ResourceGroup rg-lambdarag-dev `
        -StorageAccount lambdaragauthdev `
        -OpenAiEndpoint https://rg-openai-hub.services.ai.azure.com `
        -EmbeddingDeployment text-embedding-3-large `
        -FunctionAppName func-lambdarag-extract-dev `
        -AuthResourceId api://c8878e3f-c9c6-47c3-beb4-b005bbcd7d9a
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SearchServiceName,
    [Parameter(Mandatory)] [string] $SubscriptionId,
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [Parameter(Mandatory)] [string] $StorageAccount,
    [Parameter(Mandatory)] [string] $OpenAiEndpoint,
    [Parameter(Mandatory)] [string] $EmbeddingDeployment,
    [Parameter(Mandatory)] [string] $FunctionAppName,
    [Parameter(Mandatory)] [string] $AuthResourceId,
    [string] $FunctionUri,
    [string] $ApiVersion = '2024-11-01-preview'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$restDir = Resolve-Path (Join-Path $here '..\rest')

# 1. Acquire a bearer token for the search REST API (RBAC-only mode).
Write-Host '🔐 Acquiring bearer token for https://search.azure.com ...'
$token = (az account get-access-token --resource 'https://search.azure.com' --query accessToken -o tsv)
if (-not $token) { throw 'Failed to acquire bearer token. Run `az login` first.' }
$headers = @{
    Authorization  = "Bearer $token"
    'Content-Type' = 'application/json'
}

$searchUri = "https://$SearchServiceName.search.windows.net"

# 2. Resolve Function URI + key if not supplied.
if (-not $FunctionUri) {
    Write-Host "🔎 Resolving default hostname for Function App '$FunctionAppName' ..."
    $hostName = (az functionapp show -g $ResourceGroup -n $FunctionAppName --query 'defaultHostName' -o tsv)
    if (-not $hostName) { throw "Could not resolve defaultHostName for $FunctionAppName" }
    $FunctionUri = "https://$hostName/api/extract-rule"
}
Write-Host "🔗 Extract-rule endpoint: $FunctionUri"
Write-Host "🛡  Auth resource (AAD audience): $AuthResourceId"

function Expand-Tokens {
    param([string] $Path)
    $body = Get-Content -Raw -Encoding UTF8 $Path
    $body = $body.Replace('{{AZURE_OPENAI_ENDPOINT}}', $OpenAiEndpoint)
    $body = $body.Replace('{{EMBEDDING_DEPLOYMENT}}',  $EmbeddingDeployment)
    $body = $body.Replace('{{SUBSCRIPTION_ID}}',       $SubscriptionId)
    $body = $body.Replace('{{RESOURCE_GROUP}}',        $ResourceGroup)
    $body = $body.Replace('{{STORAGE_ACCOUNT}}',       $StorageAccount)
    $body = $body.Replace('{{FUNCTION_URI}}',          $FunctionUri)
    $body = $body.Replace('{{AUTH_RESOURCE_ID}}',      $AuthResourceId)
    return $body
}

function Put-Asset {
    param([string] $Kind, [string] $Name, [string] $Body)
    $url = "$searchUri/$Kind('$Name')?api-version=$ApiVersion"
    Write-Host "📦 PUT $Kind/$Name"
    Invoke-RestMethod -Method Put -Uri $url -Headers $headers -Body $Body | Out-Null
}

$indexBody      = Expand-Tokens (Join-Path $restDir 'index.json')
$dsBody         = Expand-Tokens (Join-Path $restDir 'datasource.json')
$skillsetBody   = Expand-Tokens (Join-Path $restDir 'skillset.json')
$indexerBody    = Expand-Tokens (Join-Path $restDir 'indexer.json')
$skillsetMdBody = Expand-Tokens (Join-Path $restDir 'skillset-md.json')
$indexerMdBody  = Expand-Tokens (Join-Path $restDir 'indexer-md.json')

# Order matters: index → datasource → skillset → skillset-md → indexer → indexer-md.
Put-Asset -Kind 'indexes'      -Name 'lambda-rag-rules'              -Body $indexBody
Put-Asset -Kind 'datasources'  -Name 'lambda-rag-policies-ds'        -Body $dsBody
Put-Asset -Kind 'skillsets'    -Name 'lambda-rag-rules-skillset'     -Body $skillsetBody
Put-Asset -Kind 'skillsets'    -Name 'lambda-rag-rules-skillset-md'  -Body $skillsetMdBody
Put-Asset -Kind 'indexers'     -Name 'lambda-rag-rules-indexer'      -Body $indexerBody
Put-Asset -Kind 'indexers'     -Name 'lambda-rag-rules-indexer-md'   -Body $indexerMdBody

Write-Host '✅ All authoring assets deployed.'
Write-Host "   Index endpoint: $searchUri/indexes('lambda-rag-rules')?api-version=$ApiVersion"
