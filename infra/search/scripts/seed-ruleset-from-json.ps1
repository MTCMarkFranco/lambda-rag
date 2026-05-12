<#
.SYNOPSIS
    Seeds rules from a *.ruleset.json file into the Azure AI Search index.
.DESCRIPTION
    Reads a ruleset JSON file, computes contentHash for each rule, and POSTs
    them to the lambda-rag-rules index via REST API using DefaultAzureCredential.
.PARAMETER RulesetPath
    Path to the *.ruleset.json file.
.PARAMETER RulesetName
    Ruleset name tag (e.g. "architecture-review").
.PARAMETER RulesetVersion
    Ruleset version tag (e.g. "2026.05-seed").
.PARAMETER SearchEndpoint
    Azure AI Search endpoint URL (default: https://srch-lambdarag-dev.search.windows.net).
.PARAMETER IndexName
    Index name (default: lambda-rag-rules).
.EXAMPLE
    .\seed-ruleset-from-json.ps1 `
        -RulesetPath ..\..\samples\contracts\contoso-demo-ruleset.json `
        -RulesetName "architecture-review" `
        -RulesetVersion "2026.05-seed"
#>
param(
    [Parameter(Mandatory)][string]$RulesetPath,
    [Parameter(Mandatory)][string]$RulesetName,
    [Parameter(Mandatory)][string]$RulesetVersion,
    [string]$SearchEndpoint = "https://srch-lambdarag-dev.search.windows.net",
    [string]$IndexName = "lambda-rag-rules",
    [string]$ApiVersion = "2024-11-01-preview"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Get access token using DefaultAzureCredential (via az CLI token)
$token = (az account get-access-token --resource "https://search.azure.com" --query accessToken -o tsv)
if (-not $token) { throw "Failed to get access token. Run 'az login' first." }

$headers = @{
    "Authorization" = "Bearer $token"
    "Content-Type"  = "application/json"
}

# Read the ruleset JSON
$ruleset = Get-Content -Raw -Path $RulesetPath | ConvertFrom-Json
$rules = $ruleset.rules
if (-not $rules) { throw "No 'rules' array found in the ruleset JSON." }

# contentHash helper: SHA256 over "naturalLanguage:<nl>|lambda:<l>|predicate:<p>"
function Get-ContentHash([string]$naturalLanguage, [string]$lambda, [string]$predicate) {
    $input = "naturalLanguage:$naturalLanguage|lambda:$lambda|predicate:$predicate"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($input)
    $hash = [System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
    return ([System.BitConverter]::ToString($hash) -replace "-","").ToLowerInvariant()
}

$approvedAt = [datetime]::UtcNow.ToString("o")
$documents = @()

foreach ($rule in $rules) {
    $naturalLanguage = if ($rule.naturalLanguage) { $rule.naturalLanguage } else { "" }
    $lambda = if ($rule.lambda) { $rule.lambda } else { "true" }
    $predicate = if ($rule.predicate) { $rule.predicate } else { "true" }
    $contentHash = Get-ContentHash -naturalLanguage $naturalLanguage -lambda $lambda -predicate $predicate
    
    # The index key is 'id'; for seeded docs use "<rulesetName>_<rulesetVersion>_<ruleId>"
    # so seeds from different versions don't collide with indexer-ingested docs.
    $docId = "$($RulesetName)_$($RulesetVersion)_$($rule.id)" -replace "[^a-zA-Z0-9_\-]", "_"
    $doc = [ordered]@{
        "@search.action" = "mergeOrUpload"
        "id"             = $docId
        "ruleId"         = $rule.id
        "naturalLanguage"= $naturalLanguage
        "lambda"         = $lambda
        "predicate"      = $predicate
        "severity"       = if ($rule.severity) { $rule.severity } else { "Violation" }
        "status"         = "approved"
        "rulesetName"    = $RulesetName
        "rulesetVersion" = $RulesetVersion
        "contentHash"    = $contentHash
        "approvedAtUtc"  = $approvedAt
        "approvedBy"     = "seed"
    }
    $documents += $doc
}

# POST in batches of 100
$batchSize = 100
for ($i = 0; $i -lt $documents.Count; $i += $batchSize) {
    $batch = $documents[$i..([Math]::Min($i + $batchSize - 1, $documents.Count - 1))]
    $body = @{ value = $batch } | ConvertTo-Json -Depth 10
    
    $url = "$SearchEndpoint/indexes/$IndexName/docs/index?api-version=$ApiVersion"
    $response = Invoke-RestMethod -Uri $url -Method POST -Headers $headers -Body $body
    Write-Host "Indexed batch $([Math]::Floor($i/$batchSize) + 1): $($batch.Count) docs"
}

Write-Host "Seeded $($documents.Count) rules from '$RulesetPath' as rulesetName='$RulesetName' rulesetVersion='$RulesetVersion'"
