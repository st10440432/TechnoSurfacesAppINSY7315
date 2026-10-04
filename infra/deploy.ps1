<#
.SYNOPSIS
Deploys infra/main.bicep to one environment, so the Azure resources can be rebuilt
from the repository rather than by hand in the portal.

.DESCRIPTION
Secrets are asked for at the prompt and passed in a temporary parameters file that
is deleted afterwards, so they never appear in the command history or in the
repository.

The SQL admin password must be the same for both environments: they share one SQL
server. Re-entering a different password changes it on the server and in both
connection secrets in the same deployment; restart both web apps afterwards so they
read the new secret.

.EXAMPLE
./infra/deploy.ps1 -Environment staging -ResourceGroup tsqa-rg -AlertEmail team@example.com -WhatIf
./infra/deploy.ps1 -Environment production -ResourceGroup tsqa-rg -AlertEmail team@example.com
#>
param(
    [Parameter(Mandatory)] [ValidateSet('staging', 'production')] [string] $Environment,
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [Parameter(Mandatory)] [string] $AlertEmail,
    [switch] $DemoAccounts,
    [switch] $NoBudget,
    [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'

function Read-Secret([string] $prompt) {
    $secure = Read-Host -Prompt $prompt -AsSecureString
    [System.Net.NetworkCredential]::new('', $secure).Password
}

$parameters = @{
    environment      = @{ value = $Environment }
    alertEmail       = @{ value = $AlertEmail }
    sqlAdminPassword = @{ value = (Read-Secret 'SQL admin password (the same one used before)') }
    demoAccounts     = @{ value = [bool] $DemoAccounts }
    createBudget     = @{ value = -not $NoBudget }
    budgetStartDate  = @{ value = (Get-Date -Day 1).ToString('yyyy-MM-dd') }
}

if ($DemoAccounts) {
    if ($Environment -eq 'production') { throw 'Demo accounts are never created in production.' }
    $parameters.demoPassword = @{ value = (Read-Secret 'Demo account password (12+ characters, upper, lower, digit)') }
}

if ($Environment -eq 'production') {
    $parameters.initialAdminEmail = @{ value = (Read-Host -Prompt 'First Managing Director email') }
    $parameters.initialAdminName = @{ value = (Read-Host -Prompt 'First Managing Director full name') }
    $parameters.initialAdminPassword = @{ value = (Read-Secret 'First Managing Director temporary password (12+ characters, upper, lower, digit)') }
}

$file = Join-Path ([System.IO.Path]::GetTempPath()) "tsqa-$Environment-$([guid]::NewGuid().ToString('N')).json"
try {
    @{
        '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
        contentVersion = '1.0.0.0'
        parameters     = $parameters
    } | ConvertTo-Json -Depth 5 | Set-Content -Path $file -Encoding utf8

    $template = Join-Path $PSScriptRoot 'main.bicep'
    $command = if ($WhatIf) { 'what-if' } else { 'create' }

    az deployment group $command `
        --resource-group $ResourceGroup `
        --name "tsqa-$Environment-$(Get-Date -Format 'yyyyMMdd-HHmm')" `
        --template-file $template `
        --parameters "@$file"

    if ($LASTEXITCODE -ne 0) { throw "az deployment group $command failed." }
}
finally {
    if (Test-Path $file) { Remove-Item $file -Force }
}