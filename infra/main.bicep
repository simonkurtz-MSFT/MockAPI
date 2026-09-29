targetScope = 'subscription'


// ------------------------------
//   PARAMETERS
// ------------------------------

@description('Name of the Azure Developer CLI environment.')
@minLength(1)
@maxLength(64)
param environmentName string

@description('Azure region used for the resource group and its resources.')
param location string

@description('Name of the resource group created for the environment.')
param resourceGroupName string = 'rg-${environmentName}-${location}'

@description('Optional dashboard username. Leave empty with dashboardPasswordHash to disable authentication.')
param dashboardUsername string = ''

@secure()
@description('Optional locally derived dashboard password hash. Leave empty with dashboardUsername to disable authentication.')
param dashboardPasswordHash string = ''

@description('Existing ingress domain bindings preserved by the developer CLI before reprovisioning. New managed certificates are bound after deployment and DNS validation.')
param customDomainsJson string = '[]'

// ------------------------------
//   RESOURCES
// ------------------------------

// https://learn.microsoft.com/azure/templates/microsoft.resources/resourcegroups
resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-11-01' = {
  name: resourceGroupName
  location: location
  tags: {
    'azd-env-name': environmentName
  }
}

// Local module containing the environment resources.
module resources 'resources.bicep' = {
  name: take('mockapi-${environmentName}', 64)
  scope: resourceGroup
  params: {
    environmentName: environmentName
    location: location
    dashboardUsername: dashboardUsername
    dashboardPasswordHash: dashboardPasswordHash
    customDomains: json(customDomainsJson)
  }
}


// ------------------------------
//   OUTPUTS
// ------------------------------

output RESOURCE_GROUP_ID string = resourceGroup.id
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = resources.outputs.containerRegistryEndpoint
output SERVICE_MOCKAPI_URI string = resources.outputs.serviceUri
