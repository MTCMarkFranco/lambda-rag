<#
.SYNOPSIS
    Builds and zip-deploys LambdaRag.Authoring.ExtractFunction to the Function
    App provisioned by `infra/search/main.bicep` (issue #79).

.DESCRIPTION
    Runs `dotnet publish` in Release mode, zips the publish output, and pushes
    the zip to the Function App via `az functionapp deployment source config-zip`.
    The Function App is configured for Flex Consumption with system-assigned
    managed identity, so no secrets travel through this script.

.PARAMETER ResourceGroup
    Resource group that hosts the Function App.

.PARAMETER FunctionAppName
    Name of the extract-rule Function App.

.EXAMPLE
    .\deploy-extract-function.ps1 -ResourceGroup rg-lambdarag-dev -FunctionAppName func-lambdarag-extract-dev
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [Parameter(Mandatory)] [string] $FunctionAppName
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoDir  = Resolve-Path (Join-Path $here '..\..\..')
$projDir  = Resolve-Path (Join-Path $repoDir 'src\LambdaRag.Authoring.ExtractFunction')
$proj     = Join-Path $projDir 'LambdaRag.Authoring.ExtractFunction.csproj'
$pubDir   = Join-Path $projDir 'bin\publish'
$zipPath  = Join-Path $projDir 'bin\extract-function.zip'

if (Test-Path $pubDir) { Remove-Item -Recurse -Force $pubDir }
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }

Write-Host "🛠️  dotnet publish -c Release -> $pubDir"
& dotnet publish $proj -c Release -o $pubDir --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "📦 Zipping publish output -> $zipPath"
Compress-Archive -Path (Join-Path $pubDir '*') -DestinationPath $zipPath -Force

Write-Host "🚀 Deploying zip to Function App '$FunctionAppName' (RG '$ResourceGroup')"
az functionapp deployment source config-zip `
    --resource-group $ResourceGroup `
    --name $FunctionAppName `
    --src $zipPath | Out-Host
if ($LASTEXITCODE -ne 0) { throw "zip deployment failed" }

Write-Host "✅ Function deployed."
$hostName = az functionapp show -g $ResourceGroup -n $FunctionAppName --query 'defaultHostName' -o tsv
Write-Host "   Endpoint: https://$hostName/api/extract-rule"
