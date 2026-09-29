// ------------------------------
//   PARAMETERS
// ------------------------------

@description('Name of the Azure Developer CLI environment.')
@minLength(1)
@maxLength(64)
param environmentName string

@description('Azure region used for all resources in this module.')
param location string

@description('Optional dashboard username. Leave empty with dashboardPasswordHash to disable authentication.')
param dashboardUsername string = ''

@secure()
@description('Optional locally derived dashboard password hash. Leave empty with dashboardUsername to disable authentication.')
param dashboardPasswordHash string = ''

@description('Existing ingress domain bindings preserved before reprovisioning.')
param customDomains array = []

// ------------------------------
//   VARIABLES
// ------------------------------

@description('Deterministic suffix used to create unique resource names.')
var resourceToken = uniqueString(subscription().id, resourceGroup().id, location, environmentName)

@description('Lowercase workload and environment stem for names that prohibit hyphens.')
var compactEnvironmentName = toLower(replace(environmentName, '-', ''))

@description('CAF-style suffix for resource names that permit hyphens.')
var resourceNameSuffix = '${take(toLower(environmentName), 20)}-${toLower(location)}-${resourceToken}'

@description('Name of the user-assigned managed identity.')
var identityName = take('id-${resourceNameSuffix}', 128)

@description('Name of the Azure Container Registry.')
var registryName = 'cr${take(compactEnvironmentName, 20)}${toLower(location)}${resourceToken}'

@description('Name of the storage account used for persisted configuration.')
var storageAccountName = 'st${take(compactEnvironmentName, 7)}${resourceToken}'

@description('Name of the blob container used for persisted configuration.')
var blobContainerName = 'mockapi-config'

@description('Name of the virtual network used for private storage access.')
var virtualNetworkName = take('vnet-${resourceNameSuffix}', 64)

@description('Name of the storage private endpoint.')
var privateEndpointName = take('pep-blob-${resourceNameSuffix}', 80)

@description('Name of the private DNS virtual network link.')
var privateDnsLinkName = take('link-${resourceNameSuffix}', 80)

@description('Name of the Log Analytics workspace.')
var logAnalyticsName = take('log-${resourceNameSuffix}', 63)

@description('Name of the Azure Container Apps managed environment.')
var managedEnvironmentName = take('cae-${resourceNameSuffix}', 60)

@description('Name of the MockAPI container app.')
var containerAppName = 'ca-${take(toLower(environmentName), 7)}-${toLower(location)}-${resourceToken}'

@description('Whether Basic authentication protects the dashboard and management API.')
var dashboardAuthenticationEnabled = !empty(dashboardUsername) && !empty(dashboardPasswordHash)

@description('Name of the Container Apps secret containing the dashboard password hash.')
var dashboardPasswordSecretName = 'dashboard-password-hash'

@description('Subscription-scoped resource ID for the built-in AcrPull role.')
var acrPullRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7f951dda-4ed3-4680-a7ca-43fe172d538d'
)

@description('Subscription-scoped resource ID for the built-in Storage Blob Data Contributor role.')
var blobDataContributorRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
)


// ------------------------------
//   RESOURCES
// ------------------------------

// https://learn.microsoft.com/azure/templates/microsoft.managedidentity/userassignedidentities
resource deploymentIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
}

// https://learn.microsoft.com/azure/templates/microsoft.containerregistry/registries
resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

// https://learn.microsoft.com/azure/templates/microsoft.authorization/roleassignments
resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, deploymentIdentity.id, acrPullRoleDefinitionId)
  scope: containerRegistry
  properties: {
    principalId: deploymentIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: acrPullRoleDefinitionId
  }
}

// https://learn.microsoft.com/azure/templates/microsoft.storage/storageaccounts
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Disabled'
    supportsHttpsTrafficOnly: true
  }
}

// https://learn.microsoft.com/azure/templates/microsoft.storage/storageaccounts/blobservices
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

// https://learn.microsoft.com/azure/templates/microsoft.storage/storageaccounts/blobservices/containers
resource blobContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: blobContainerName
  properties: {
    publicAccess: 'None'
  }
}

// https://learn.microsoft.com/azure/templates/microsoft.authorization/roleassignments
resource blobDataContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(blobContainer.id, deploymentIdentity.id, blobDataContributorRoleDefinitionId)
  scope: blobContainer
  properties: {
    principalId: deploymentIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: blobDataContributorRoleDefinitionId
  }
}

// https://learn.microsoft.com/azure/templates/microsoft.network/virtualnetworks
resource virtualNetwork 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: virtualNetworkName
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.0.0.0/16'
      ]
    }
    subnets: [
      {
        name: 'snet-container-apps'
        properties: {
          addressPrefix: '10.0.0.0/23'
          delegations: [
            {
              name: 'Microsoft.App.environments'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'snet-private-endpoints'
        properties: {
          addressPrefix: '10.0.2.0/24'
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

resource containerAppsSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: virtualNetwork
  name: 'snet-container-apps'
}

resource privateEndpointsSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: virtualNetwork
  name: 'snet-private-endpoints'
}

// Azure Storage private endpoints require this exact DNS zone name.
resource blobPrivateDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: 'privatelink.blob.${environment().suffixes.storage}'
  location: 'global'
}

resource privateDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: blobPrivateDnsZone
  name: privateDnsLinkName
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: virtualNetwork.id
    }
  }
}

// https://learn.microsoft.com/azure/templates/microsoft.network/privateendpoints
resource storagePrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: privateEndpointName
  location: location
  properties: {
    privateLinkServiceConnections: [
      {
        name: 'blob'
        properties: {
          groupIds: [
            'blob'
          ]
          privateLinkServiceId: storageAccount.id
        }
      }
    ]
    subnet: {
      id: privateEndpointsSubnet.id
    }
  }
}

resource storagePrivateDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: storagePrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'blob'
        properties: {
          privateDnsZoneId: blobPrivateDnsZone.id
        }
      }
    ]
  }
}

// https://learn.microsoft.com/azure/templates/microsoft.operationalinsights/workspaces
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    retentionInDays: 30
    sku: {
      name: 'PerGB2018'
    }
  }
}

// https://learn.microsoft.com/azure/templates/microsoft.app/managedenvironments
resource managedEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: managedEnvironmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    vnetConfiguration: {
      infrastructureSubnetId: containerAppsSubnet.id
      internal: false
    }
  }
}

// https://learn.microsoft.com/azure/templates/microsoft.app/containerapps
resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  tags: {
    'azd-service-name': 'mockapi'
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${deploymentIdentity.id}': {}
    }
  }
  properties: {
    environmentId: managedEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      secrets: dashboardAuthenticationEnabled ? [
        {
          name: dashboardPasswordSecretName
          value: dashboardPasswordHash
        }
      ] : []
      ingress: {
        allowInsecure: false
        customDomains: customDomains
        corsPolicy: {
          allowedHeaders: [
            '*'
          ]
          allowedMethods: [
            '*'
          ]
          allowedOrigins: [
            '*'
          ]
        }
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          identity: deploymentIdentity.id
          server: containerRegistry.properties.loginServer
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'mockapi'
          image: 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
          env: [
            {
              name: 'MockApi__ConfigurationBlobUri'
              value: uri(storageAccount.properties.primaryEndpoints.blob, '${blobContainer.name}/mockapi.json')
            }
            {
              name: 'MockApi__ManagedIdentityClientId'
              value: deploymentIdentity.properties.clientId
            }
            {
              name: 'MockApi__LogAnalyticsWorkspaceUri'
              value: '${environment().portal}/#resource${logAnalytics.id}/overview'
            }
            {
              name: 'MockApi__EnableManagementApi'
              value: 'true'
            }
            {
              name: 'MockApi__EnableDashboard'
              value: 'true'
            }
            {
              name: 'MockApi__EnableOpenApi'
              value: 'false'
            }
            {
              name: 'MockApi__EnableSwaggerUi'
              value: 'false'
            }
            ...dashboardAuthenticationEnabled ? [
              {
                name: 'MockApi__DashboardUsername'
                value: dashboardUsername
              }
              {
                name: 'MockApi__DashboardPasswordHash'
                secretRef: dashboardPasswordSecretName
              }
            ] : []
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 5
              periodSeconds: 10
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 2
              periodSeconds: 5
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
        rules: [
          {
            name: 'http-requests'
            http: {
              metadata: {
                concurrentRequests: '10'
              }
            }
          }
        ]
      }
    }
  }
  dependsOn: [
    acrPullRole
    blobDataContributorRole
    storagePrivateDnsZoneGroup
  ]
}


// ------------------------------
//   OUTPUTS
// ------------------------------

output containerRegistryEndpoint string = containerRegistry.properties.loginServer
output serviceUri string = 'https://${containerApp.properties.configuration.ingress.fqdn}'
