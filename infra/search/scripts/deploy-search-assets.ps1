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

.PARAMETER ChatDeployment
    Chat deployment name used by the GenAI prompt skill (e.g. "gpt-4o-mini").

.PARAMETER EmbeddingDeployment
    Embedding deployment name used by the embedding skill + index vectorizer
    (e.g. "text-embedding-3-large").

.PARAMETER ApiVersion
    Search REST API version (default 2024-11-01-preview to get the GenAI
    prompt skill + integrated vectorization).

.EXAMPLE
    .\deploy-search-assets.ps1 `
        -SearchServiceName srch-lambdarag-dev `
        -SubscriptionId $env:AZURE_SUBSCRIPTION_ID `
        -ResourceGroup rg-lambdarag-dev `
        -StorageAccount lambdaragauthdev `
        -OpenAiEndpoint https://rg-openai-hub.services.ai.azure.com `
        -ChatDeployment gpt-4o-mini `
        -EmbeddingDeployment text-embedding-3-large
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SearchServiceName,
    [Parameter(Mandatory)] [string] $SubscriptionId,
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [Parameter(Mandatory)] [string] $StorageAccount,
    [Parameter(Mandatory)] [string] $OpenAiEndpoint,
    [Parameter(Mandatory)] [string] $ChatDeployment,
    [Parameter(Mandatory)] [string] $EmbeddingDeployment,
    [string] $ApiVersion = '2024-11-01-preview'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoDir = Resolve-Path (Join-Path $here '..\..\..')
$restDir = Resolve-Path (Join-Path $here '..\rest')
$promptPath = Join-Path $repoDir 'samples\authoring\rule-extraction.system-prompt.md'

if (-not (Test-Path $promptPath)) {
    throw "Missing system prompt at $promptPath"
}

# 1. Acquire a bearer token for the search REST API (RBAC-only mode).
Write-Host '🔐 Acquiring bearer token for https://search.azure.com ...'
$token = (az account get-access-token --resource 'https://search.azure.com' --query accessToken -o tsv)
if (-not $token) { throw 'Failed to acquire bearer token. Run `az login` first.' }
$headers = @{
    Authorization  = "Bearer $token"
    'Content-Type' = 'application/json'
}

$searchUri = "https://$SearchServiceName.search.windows.net"

# 2. Read + substitute placeholders.
$systemPromptRaw = Get-Content -Raw -Encoding UTF8 $promptPath
# JSON-escape the prompt: it'll be placed inside a "content" string.
$systemPromptJson = ($systemPromptRaw | ConvertTo-Json -Compress -Depth 1).Trim('"')

function Expand-Tokens {
    param([string] $Path)
    $body = Get-Content -Raw -Encoding UTF8 $Path
    $body = $body.Replace('{{AZURE_OPENAI_ENDPOINT}}', $OpenAiEndpoint)
    $body = $body.Replace('{{CHAT_DEPLOYMENT}}',       $ChatDeployment)
    $body = $body.Replace('{{EMBEDDING_DEPLOYMENT}}',  $EmbeddingDeployment)
    $body = $body.Replace('{{SUBSCRIPTION_ID}}',       $SubscriptionId)
    $body = $body.Replace('{{RESOURCE_GROUP}}',        $ResourceGroup)
    $body = $body.Replace('{{STORAGE_ACCOUNT}}',       $StorageAccount)
    $body = $body.Replace('{{SYSTEM_PROMPT}}',         $systemPromptJson)
    return $body
}

function Put-Asset {
    param([string] $Kind, [string] $Name, [string] $Body)
    $url = "$searchUri/$Kind('$Name')?api-version=$ApiVersion"
    Write-Host "📦 PUT $Kind/$Name"
    Invoke-RestMethod -Method Put -Uri $url -Headers $headers -Body $Body | Out-Null
}

$indexBody     = Expand-Tokens (Join-Path $restDir 'index.json')
$dsBody        = Expand-Tokens (Join-Path $restDir 'datasource.json')
$skillsetBody  = Expand-Tokens (Join-Path $restDir 'skillset.json')
$indexerBody   = Expand-Tokens (Join-Path $restDir 'indexer.json')

# Order matters: index → datasource → skillset → indexer.
Put-Asset -Kind 'indexes'      -Name 'lambda-rag-rules'           -Body $indexBody
Put-Asset -Kind 'datasources'  -Name 'lambda-rag-policies-ds'     -Body $dsBody
Put-Asset -Kind 'skillsets'    -Name 'lambda-rag-rules-skillset'  -Body $skillsetBody
Put-Asset -Kind 'indexers'     -Name 'lambda-rag-rules-indexer'   -Body $indexerBody

Write-Host '✅ All authoring assets deployed.'
Write-Host "   Index endpoint: $searchUri/indexes('lambda-rag-rules')?api-version=$ApiVersion"
