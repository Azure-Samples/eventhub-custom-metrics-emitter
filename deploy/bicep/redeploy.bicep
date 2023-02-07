

@description('container app environment name from param.json file')
param AcaEnvName string

@description('container app name from param.json file')
param ContainerAppName string


@description('Name of the Emitter Container App')
param EmitterImage string

@description('Name of the Emitter Registry')
param registryLoginServer string = 'ghcr.io'

@description('managed identity name from param.json file')
param managedIdentityName string 

param location string = resourceGroup().location

@description('Event Hub Namespace - provided in the param.json file')
param EventHubNamespace string

@description('Event Hub - provided in the param.json file')
param EventHubName string

// consider to also pass in param file (or we should take all consumer groups)
param ConsumerGroup string = '$Default'


@description('Storage Account Name - provided in the param.json file')
param CheckpointAccountName string

@description('Storage Container Name - provided in the param.json file')
param CheckpointContainerName string

@description('Custom Metric Interval - provided in the param.json file')
param CustomMetricInterval string




resource mngIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2022-01-31-preview' existing = {
  name: managedIdentityName 
}

// and this is the code to use for the existing container app environment

resource containerAppEnvironment 'Microsoft.App/managedEnvironments@2022-06-01-preview' existing = {
    name: AcaEnvName   
}


resource ContainerApp 'Microsoft.App/containerApps@2022-06-01-preview' = {
  name: ContainerAppName 
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${ mngIdentity.id}':{}
    }
  }
  properties: {
    managedEnvironmentId: containerAppEnvironment.id
    
    configuration: {
    
    }
  template: {
    containers: [
      {
        name: 'emitter' 
        image: '${registryLoginServer}/${EmitterImage}' 
        resources: {
          cpu: json('0.25')
          memory: '0.5Gi'
        } 
        env: [
          {
            name: 'TenantId'
            value: subscription().tenantId
          }
          {
            name: 'SubscriptionId'
            value: subscription().subscriptionId
          }            
          {
            name: 'ResourceGroup'
            value: resourceGroup().name
          }            
          {
            name: 'Region'
            value: location
          }            
          {
            name: 'EventHubNamespace'
            value: EventHubNamespace
          }            
          {
            name: 'EventHubName'
            value: EventHubName
          }            
          {
            name: 'ConsumerGroup'
            value: ConsumerGroup
          }            
          {
            name: 'CheckpointAccountName'
            value: CheckpointAccountName
          }            
          {
            name: 'CheckpointContainerName'
            value: CheckpointContainerName
          }            
          {
            name: 'CustomMetricInterval'
            value: CustomMetricInterval
          } 
          {
            name: 'ManagedIdentityClientId'
            value: mngIdentity.properties.clientId
          }

        ]        
      }
    ]
    scale: {
      minReplicas: 1
      maxReplicas: 1
    }
  }
}

}
