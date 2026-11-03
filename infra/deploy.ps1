<#
.SYNOPSIS
Deploys infra/main.bicep to one environment, so the Azure resources can be rebuilt
from the repository rather than by hand in the portal.

.DESCRIPTION
Secrets are asked for at the prompt and passed in a temporary parameters file that
is deleted afterwards, so they never appear in the command history or in the
repository.

The web apps connect to Azure SQL as a user-assigned managed identity, which is the
server's Microsoft Entra admin, so the connection string holds no password. Without
-EntraOnly the SQL admin password is still asked for because the server resource
requires one; it must be the same for both environments, which share one server.
With -EntraOnly it is not asked for, because the server refuses it.

Moving an existing deployment to the managed identity:
  1. Deploy staging, then production, without -EntraOnly.
  2. Restart both web apps so they read the new connection secret, and check that
     /health reports Healthy on both.
  3. Deploy staging again with -EntraOnly, which stops SQL logins, the admin
     password included, working on the server. The setting belongs to the server,
     so this covers production too.

Once -EntraOnly has been used, pass it on every later deployment of either
environment. Without it the deployment fails with AadOnlyAuthenticationIsEnabled.
To allow SQL logins again, first run
  az sql server ad-only-auth disable --resource-group <group> --name <server>
and then deploy without -EntraOnly.

Deploying into a new subscription while the old apps still exist needs a new
-AppPrefix, because App Service names are unique across Azure. A subscription
billed in rand takes -BudgetAmount 1000.

-PlanSku S1 puts the shared plan on the Standard tier and gives production a
staging slot. Both environments share the plan, so pass the same -PlanSku to
each, or the second deployment moves the plan back. To use the slot, set the
variable SLOT_NAME=staging on the GitHub production environment. To go back to
B1, first remove SLOT_NAME, then delete the slot with
  az webapp deployment slot delete --resource-group <group> --name <app> --slot staging
and then deploy both environments with -PlanSku B1.

.EXAMPLE
./infra/deploy.ps1 -Environment staging -ResourceGroup tsqa-rg -AlertEmail team@example.com -WhatIf
./infra/deploy.ps1 -Environment production -ResourceGroup tsqa-rg -AlertEmail team@example.com
./infra/deploy.ps1 -Environment production -ResourceGroup tsqa-rg -AlertEmail team@example.com -EntraOnly
./infra/deploy.ps1 -Environment staging -ResourceGroup tsqa-rg -AlertEmail owner@example.com -AppPrefix technosurfaces-quotes -BudgetAmount 1000
./infra/deploy.ps1 -Environment production -ResourceGroup tsqa-rg -AlertEmail owner@example.com -AppPrefix technosurfaces-quotes -PlanSku S1 -EntraOnly
#>
param(
    [Parameter(Mandatory)] [ValidateSet('staging', 'production')] [string] $Environment,
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [Parameter(Mandatory)] [string] $AlertEmail,
    [string] $AppPrefix = 'tsqa-app',
    [int] $BudgetAmount = 54,
    [ValidateSet('B1', 'S1')] [string] $PlanSku = 'B1',
    [switch] $DemoAccounts,
    [switch] $NoBudget,
    [switch] $EntraOnly,
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
    appPrefix        = @{ value = $AppPrefix }
    budgetAmount     = @{ value = $BudgetAmount }
    planSku          = @{ value = $PlanSku }
    demoAccounts     = @{ value = [bool] $DemoAccounts }
    createBudget     = @{ value = -not $NoBudget }
    entraOnlyAuthentication = @{ value = [bool] $EntraOnly }
    budgetStartDate  = @{ value = (Get-Date -Day 1).ToString('yyyy-MM-dd') }
}

if (-not $EntraOnly) {
    $parameters.sqlAdminPassword = @{ value = (Read-Secret 'SQL admin password (the same one used before)') }
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