@allowed([
  'staging'
  'production'
])
param environment string

param location string = resourceGroup().location

@description('Start of the two App Service names, which must be unique across Azure. A new subscription needs a new prefix while the old apps still exist.')
param appPrefix string = 'tsqa-app'

@description('Full App Service name, unique across Azure. Leave it out for <appPrefix>-<environment>. The production slot is reached at <appName>-staging.azurewebsites.net.')
param appName string = '${appPrefix}-${environment}'

@description('App Service plan tier, shared by both environments, so pass the same value to both. S1 (Standard) adds a staging slot to production, so a release is swapped in and can be swapped back. A plan with a slot cannot move to B1 until the slot is deleted.')
@allowed([
  'B1'
  'S1'
])
param planSku string = 'B1'

param sqlAdminLogin string = 'tsadmin'

@description('SQL admin password. Required on the first deployment, which creates the server. Not used when entraOnlyAuthentication is true.')
@secure()
param sqlAdminPassword string = ''

@description('Linux App Service runtime. Verified against az webapp list-runtimes --os-type linux --runtime dotnet.')
param linuxRuntime string = 'DOTNETCORE|10.0'

@description('Seed the demo Managing Director and estimator accounts in this environment.')
param demoAccounts bool = false

@description('Password for the demo accounts. Stored in Key Vault as seed-demo-password. At least 12 characters with an upper-case letter, a lower-case letter and a digit.')
@secure()
param demoPassword string = ''

@description('Production only. Email address of the first Managing Director account, created once at the first start of an empty production database. Leave empty for staging.')
param initialAdminEmail string = ''

@description('Production only. Full name of the first Managing Director account.')
param initialAdminName string = ''

@description('Production only. Temporary password for the first Managing Director account; it must be changed at first sign-in. At least 12 characters with an upper-case letter, a lower-case letter and a digit.')
@secure()
param initialAdminPassword string = ''

@description('Address that receives the availability alert and the budget alert.')
param alertEmail string

@description('Refuse SQL logins on the server, so only Microsoft Entra identities can connect. Turn on once both web apps report Healthy on the managed identity connection (staging and production share the server). Once on, every later deployment of either environment must also set it.')
param entraOnlyAuthentication bool = false

@description('Create the monthly budget. Set to false if the subscription does not support budgets.')
param createBudget bool = true

@description('Monthly budget in the subscription billing currency. 54 USD is the client ceiling of R1 000 at the R18.50 per USD rate used in Task 1 section 10; a subscription billed in rand takes 1000.')
param budgetAmount int = 54

@description('First day of the current month, yyyy-MM-01. A budget cannot start in the past.')
param budgetStartDate string = '2026-10-01'

var isProd = environment == 'production'
var useSlot = isProd && planSku == 'S1'
var slotName = 'staging'
var unique = uniqueString(resourceGroup().id)

var planName = 'tsqa-plan'
var sqlServerName = 'tsqa-sql-${unique}'
var databaseName = 'tsqa-db-${environment}'
var kvName = 'tsqa-kv-${unique}'
var lawName = 'tsqa-log'
var aiName = 'tsqa-ai-${environment}'
var sqlIdentityName = 'tsqa-sql-identity'

var sqlHostname = '${sqlServerName}${az.environment().suffixes.sqlServerHostname}'
var appHostname = '${appName}.azurewebsites.net'
var healthUrl = 'https://${appHostname}/health'

// Production has no demo accounts, so its first Managing Director account is
// created from these three secrets (IdentitySeeder.SeedInitialManagingDirectorAsync).
var seedInitialAdmin = isProd && !empty(initialAdminEmail)
var initialAdminSettings = seedInitialAdmin ? [
  {
    name: 'Seed__InitialAdminEmail'
    value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=seed-initial-admin-email)'
  }
  {
    name: 'Seed__InitialAdminName'
    value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=seed-initial-admin-name)'
  }
  {
    name: 'Seed__InitialAdminPassword'
    value: '@Microsoft.KeyVault(VaultName=${kvName};SecretName=seed-initial-admin-password)'
  }
] : []

// -----------------------------------------------------------------------------
// Network
// -----------------------------------------------------------------------------

resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: 'tsqa-vnet'
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.20.0.0/23'
      ]
    }
    subnets: [
      {
        name: 'sql'
        properties: {
          addressPrefix: '10.20.0.0/24'
        }
      }
      {
        name: 'web'
        properties: {
          addressPrefix: '10.20.1.0/24'
          delegations: [
            {
              name: 'web'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
        }
      }
    ]
  }
}

var sqlSubnetId = resourceId(
  'Microsoft.Network/virtualNetworks/subnets',
  vnet.name,
  'sql'
)

var webSubnetId = resourceId(
  'Microsoft.Network/virtualNetworks/subnets',
  vnet.name,
  'web'
)

// -----------------------------------------------------------------------------
// Private DNS for Azure SQL
// -----------------------------------------------------------------------------

resource dnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink${az.environment().suffixes.sqlServerHostname}'
  location: 'global'
}

resource dnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: dnsZone
  name: 'tsqa-link'
  location: 'global'
  properties: {
    virtualNetwork: {
      id: vnet.id
    }
    registrationEnabled: false
  }
}

// -----------------------------------------------------------------------------
// Azure SQL
// -----------------------------------------------------------------------------

resource sql 'Microsoft.Sql/servers@2022-05-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    version: '12.0'
    publicNetworkAccess: 'Disabled'
    minimalTlsVersion: '1.2'
    // While Entra-only authentication is on, the server refuses a write that
    // carries the admin login and password (AadOnlyAuthenticationIsEnabled), so
    // both are left out once it is on. The login stays on the server, unusable.
    administratorLogin: entraOnlyAuthentication ? null : sqlAdminLogin
    administratorLoginPassword: entraOnlyAuthentication ? null : sqlAdminPassword
  }
}

// -----------------------------------------------------------------------------
// The identity the app connects to Azure SQL as (Task 1 7.3.3)
// -----------------------------------------------------------------------------

// One user-assigned identity, carried by both web apps and set as the server's
// Microsoft Entra admin. The app signs in to SQL with a token for this identity,
// so the connection string holds no password. It is user-assigned rather than each
// app's own identity because the two environments share one server, which takes
// one Entra admin, and because making it the admin needs no Microsoft Graph
// permission or T-SQL run against a server that has no public endpoint.
resource sqlIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: sqlIdentityName
  location: location
}

resource sqlEntraAdmin 'Microsoft.Sql/servers/administrators@2022-05-01-preview' = {
  parent: sql
  name: 'ActiveDirectory'
  properties: {
    administratorType: 'ActiveDirectory'
    login: sqlIdentity.name
    sid: sqlIdentity.properties.principalId
    tenantId: subscription().tenantId
  }
}

// Off until both apps are confirmed on the managed identity connection. Turning it
// on stops the SQL admin password working on the server at all.
resource sqlEntraOnly 'Microsoft.Sql/servers/azureADOnlyAuthentications@2022-05-01-preview' = {
  parent: sql
  name: 'Default'
  properties: {
    azureADOnlyAuthentication: entraOnlyAuthentication
  }
  dependsOn: [
    sqlEntraAdmin
  ]
}

resource db 'Microsoft.Sql/servers/databases@2023-05-01-preview' = {
  parent: sql
  name: databaseName
  location: location
  sku: {
    name: 'Basic'
    tier: 'Basic'
    capacity: 5
  }
}

resource pe 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  name: 'tsqa-sql-pe'
  location: location
  properties: {
    subnet: {
      id: sqlSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'tsqa-sql-conn'
        properties: {
          privateLinkServiceId: sql.id
          groupIds: [
            'sqlServer'
          ]
        }
      }
    ]
  }
}

resource peDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = {
  parent: pe
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'sql'
        properties: {
          privateDnsZoneId: dnsZone.id
        }
      }
    ]
  }
}

// -----------------------------------------------------------------------------
// Key Vault
// -----------------------------------------------------------------------------

resource kv 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: kvName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    tenantId: subscription().tenantId
    accessPolicies: []
  }
}

// The connection string the app reads through its managed identity. Written
// through Azure Resource Manager, so whoever deploys needs no Key Vault data
// permission. It holds no password: the app authenticates to SQL as the
// user-assigned identity above, named by its client id (Task 1 7.3.3).
resource connectionSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: kv
  name: 'app-connection-${environment}'
  properties: {
    value: 'Server=tcp:${sqlHostname},1433;Initial Catalog=${databaseName};Authentication=Active Directory Managed Identity;User Id=${sqlIdentity.properties.clientId};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
  }
}

resource demoPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = if (demoAccounts) {
  parent: kv
  name: 'seed-demo-password'
  properties: {
    value: demoPassword
  }
}

resource initialAdminEmailSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = if (seedInitialAdmin) {
  parent: kv
  name: 'seed-initial-admin-email'
  properties: {
    value: initialAdminEmail
  }
}

resource initialAdminNameSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = if (seedInitialAdmin) {
  parent: kv
  name: 'seed-initial-admin-name'
  properties: {
    value: initialAdminName
  }
}

resource initialAdminPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = if (seedInitialAdmin) {
  parent: kv
  name: 'seed-initial-admin-password'
  properties: {
    value: initialAdminPassword
  }
}

// -----------------------------------------------------------------------------
// Monitoring
// -----------------------------------------------------------------------------

resource law 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: lawName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource ai 'Microsoft.Insights/components@2020-02-02' = {
  name: aiName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: law.id
  }
}

// -----------------------------------------------------------------------------
// App Service
// -----------------------------------------------------------------------------

// The production app and its slot run the same configuration, so a swap moves
// the code and nothing else.
var siteConfig = {
  linuxFxVersion: linuxRuntime
  minTlsVersion: '1.2'
  alwaysOn: true
  appSettings: concat([
    {
      name: 'ASPNETCORE_ENVIRONMENT'
      value: isProd ? 'Production' : 'Staging'
    }
    {
      name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
      value: ai.properties.ConnectionString
    }
    {
      // The App Service agent records each request and its SQL calls, with no
      // SDK in the app. Production had it switched on in the portal only.
      name: 'ApplicationInsightsAgent_EXTENSION_VERSION'
      value: '~3'
    }
    {
      name: 'XDT_MicrosoftApplicationInsights_Mode'
      value: 'recommended'
    }
    {
      name: 'ConnectionStrings__DefaultConnection'
      value: '@Microsoft.KeyVault(SecretUri=${kv.properties.vaultUri}secrets/app-connection-${environment})'
    }
    {
      // Gives the app up to ten minutes to start, which covers the first
      // start on an empty database: migrations plus loading the catalogue.
      name: 'WEBSITES_CONTAINER_START_TIME_LIMIT'
      value: '600'
    }
    {
      // The database has no public endpoint, so the app applies migrations
      // through the private endpoint when it starts.
      name: 'Database__MigrateOnStartup'
      value: 'true'
    }
    {
      // Lets the sign-in rate limiter see the real client address behind the
      // App Service front end.
      name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
      value: 'true'
    }
    {
      name: 'Seed__DemoAccounts'
      value: demoAccounts ? 'true' : 'false'
    }
    {
      name: 'Seed__DevelopmentPassword'
      value: demoAccounts ? '@Microsoft.KeyVault(SecretUri=${kv.properties.vaultUri}secrets/seed-demo-password)' : ''
    }
  ], initialAdminSettings)
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  kind: 'linux'
  sku: {
    name: planSku
    tier: planSku == 'S1' ? 'Standard' : 'Basic'
  }
  properties: {
    reserved: true
  }
}

resource web 'Microsoft.Web/sites@2023-01-01' = {
  name: appName
  location: location
  kind: 'app,linux'
  // The app reads both secrets when it starts, and connects to SQL as the
  // user-assigned identity, so they must exist first.
  dependsOn: [
    sqlEntraAdmin
    connectionSecret
    demoPasswordSecret
    initialAdminEmailSecret
    initialAdminNameSecret
    initialAdminPasswordSecret
  ]
  // System-assigned for the Key Vault references; user-assigned for Azure SQL.
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${sqlIdentity.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    // The app keeps nothing on the server between requests, so the ARRAffinity
    // cookies only added the two ZAP low findings (risk R17).
    clientAffinityEnabled: false
    virtualNetworkSubnetId: webSubnetId
    siteConfig: siteConfig
  }
}

// Key Vault Secrets User
resource kvReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(kv.id, web.id, 'kv-secrets-user')
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '4633458b-17de-408a-b874-0445c86b69e6'
    )
    principalId: web.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// Production staging slot (Standard tier only)
// -----------------------------------------------------------------------------

// A release is deployed here, checked on /health and then swapped into
// production, so the live site never serves a version that is still starting.
// After a swap the slot holds the previous release, and swapping back is the
// rollback. It uses the production database and the same settings as production.
resource slot 'Microsoft.Web/sites/slots@2023-01-01' = if (useSlot) {
  parent: web
  name: slotName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${sqlIdentity.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    virtualNetworkSubnetId: webSubnetId
    siteConfig: siteConfig
  }
}

// The slot has its own system-assigned identity, so it needs its own access to
// the Key Vault references.
resource slotKvReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (useSlot) {
  name: guid(kv.id, web.id, slotName, 'kv-secrets-user')
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '4633458b-17de-408a-b874-0445c86b69e6'
    )
    principalId: slot!.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// Application Insights Standard availability test
// -----------------------------------------------------------------------------

resource webtest 'Microsoft.Insights/webtests@2022-06-15' = {
  name: 'tsqa-avail-${environment}'
  location: location
  kind: 'standard'
  tags: {
    'hidden-link:${ai.id}': 'Resource'
  }
  properties: {
    Description: 'Availability test for ${appName}'
    Enabled: true
    Frequency: 900
    Kind: 'standard'
    Locations: [
      {
        Id: 'emea-nl-ams-azr'
      }
    ]
    Name: 'tsqa-avail-${environment}'
    Request: {
      // A redirect from /health means it is behind the sign-in page, which is a
      // failure, not something to follow to a page that returns 200.
      FollowRedirects: false
      HttpVerb: 'GET'
      ParseDependentRequests: false
      RequestUrl: healthUrl
    }
    RetryEnabled: true
    SyntheticMonitorId: 'tsqa-avail-${environment}'
    Timeout: 60
    ValidationRules: {
      ExpectedHttpStatusCode: 200
      SSLCheck: true
      SSLCertRemainingLifetimeCheck: 7
    }
  }
}

// -----------------------------------------------------------------------------
// Availability metric alert
// -----------------------------------------------------------------------------

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'tsqa-alerts'
  location: 'global'
  properties: {
    groupShortName: 'tsqa'
    enabled: true
    emailReceivers: [
      {
        name: 'team'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

resource availAlert 'Microsoft.Insights/metricalerts@2018-03-01' = {
  name: 'tsqa-avail-alert-${environment}'
  location: 'global'
  properties: {
    description: 'Availability of /health dropped below 90 percent'
    severity: 1
    enabled: true
    scopes: [
      ai.id
    ]
    evaluationFrequency: 'PT30M'
    windowSize: 'PT30M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'Availability'
          criterionType: 'StaticThresholdCriterion'
          metricName: 'availabilityResults/availabilityPercentage'
          operator: 'LessThan'
          threshold: 90
          timeAggregation: 'Average'
        }
      ]
    }
    actions: [
      {
        actionGroupId: actionGroup.id
      }
    ]
  }
}

// -----------------------------------------------------------------------------
// Budget alert: warns when the month is forecast to pass 90 percent of the
// client's ceiling, and again when it passes the ceiling. The running cost is
// about 82 percent of it (October 2026), so an 80 percent alert fired every month.
// -----------------------------------------------------------------------------

resource budget 'Microsoft.Consumption/budgets@2023-05-01' = if (createBudget) {
  name: 'tsqa-monthly-budget'
  properties: {
    category: 'Cost'
    amount: budgetAmount
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: budgetStartDate
    }
    notifications: {
      forecastOver90Percent: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 90
        thresholdType: 'Forecasted'
        contactEmails: [
          alertEmail
        ]
      }
      actualOver100Percent: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        thresholdType: 'Actual'
        contactEmails: [
          alertEmail
        ]
      }
    }
  }
}

// -----------------------------------------------------------------------------
// Outputs
// -----------------------------------------------------------------------------

output appName string = appName
output appHostname string = appHostname
output healthUrl string = healthUrl
output slotName string = useSlot ? slotName : ''
output keyVaultName string = kvName
output sqlServerName string = sqlServerName
output databaseName string = databaseName
output sqlHostname string = sqlHostname
output applicationInsightsName string = aiName
output sqlIdentityName string = sqlIdentity.name
output sqlIdentityClientId string = sqlIdentity.properties.clientId