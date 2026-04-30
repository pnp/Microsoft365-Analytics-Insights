using Azure;
using Azure.Core;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Gets or creates a Virtual Network with a default subnet.
    /// </summary>
    public class VNetInstallTask : InstallTaskInAzResourceGroup<VirtualNetworkResource>
    {
        public const string CONFIG_KEY_ADDRESS_PREFIX = "addressPrefix";
        public const string CONFIG_KEY_SUBNET_NAME = "subnetName";
        public const string CONFIG_KEY_SUBNET_ADDRESS_PREFIX = "subnetAddressPrefix";
        public const string CONFIG_KEY_APP_INTEGRATION_SUBNET_NAME = "appIntegrationSubnetName";
        public const string CONFIG_KEY_APP_INTEGRATION_SUBNET_ADDRESS_PREFIX = "appIntegrationSubnetAddressPrefix";

        public VNetInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "get/create VNet";

        public override async Task<VirtualNetworkResource> ExecuteTaskReturnResult(object contextArg)
        {
            var name = _config.GetNameConfigValue();
            var addressPrefix = _config.GetConfigValue(CONFIG_KEY_ADDRESS_PREFIX);
            var subnetName = _config.GetConfigValue(CONFIG_KEY_SUBNET_NAME);
            var subnetAddressPrefix = _config.GetConfigValue(CONFIG_KEY_SUBNET_ADDRESS_PREFIX);
            var appIntegrationSubnetName = _config.GetConfigValue(CONFIG_KEY_APP_INTEGRATION_SUBNET_NAME);
            var appIntegrationSubnetAddressPrefix = _config.GetConfigValue(CONFIG_KEY_APP_INTEGRATION_SUBNET_ADDRESS_PREFIX);

            VirtualNetworkResource vnet = null;
            try
            {
                var response = await Container.GetVirtualNetworkAsync(name);
                vnet = response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Not found
            }

            if (vnet == null)
            {
                _logger.LogInformation($"Creating new VNet '{name}' with address space '{addressPrefix}'...");

                var vnetData = new VirtualNetworkData()
                {
                    Location = AzureLocation,
                };
                vnetData.AddressPrefixes.Add(addressPrefix);
                vnetData.Subnets.Add(new SubnetData()
                {
                    Name = subnetName,
                    AddressPrefix = subnetAddressPrefix,
                });

                // Dedicated subnet for App Service VNet integration
                if (!string.IsNullOrWhiteSpace(appIntegrationSubnetName))
                {
                    var integrationSubnet = new SubnetData()
                    {
                        Name = appIntegrationSubnetName,
                        AddressPrefix = appIntegrationSubnetAddressPrefix,
                    };
                    // App Service integration requires delegation
                    integrationSubnet.Delegations.Add(new ServiceDelegation()
                    {
                        Name = "Microsoft.Web.serverFarms",
                        ServiceName = "Microsoft.Web/serverFarms"
                    });
                    vnetData.Subnets.Add(integrationSubnet);
                }

                EnsureTagsOnNew(vnetData.Tags);
                var operation = await Container.GetVirtualNetworks().CreateOrUpdateAsync(WaitUntil.Completed, name, vnetData);
                vnet = operation.Value;

                _logger.LogInformation($"Created VNet '{vnet.Data.Name}' with subnet '{subnetName}'.");
            }
            else
            {
                _logger.LogInformation($"Found existing VNet '{vnet.Data.Name}'.");

                // Ensure the App Service integration subnet exists
                if (!string.IsNullOrWhiteSpace(appIntegrationSubnetName))
                {
                    var existingSubnets = vnet.GetSubnets();
                    SubnetResource integrationSubnet = null;
                    try
                    {
                        integrationSubnet = (await existingSubnets.GetAsync(appIntegrationSubnetName)).Value;
                    }
                    catch (RequestFailedException ex) when (ex.Status == 404)
                    {
                        // Not found
                    }

                    if (integrationSubnet == null)
                    {
                        _logger.LogInformation($"Creating App Service integration subnet '{appIntegrationSubnetName}' in VNet '{vnet.Data.Name}'...");
                        var subnetData = new SubnetData()
                        {
                            AddressPrefix = appIntegrationSubnetAddressPrefix,
                        };
                        subnetData.Delegations.Add(new ServiceDelegation()
                        {
                            Name = "Microsoft.Web.serverFarms",
                            ServiceName = "Microsoft.Web/serverFarms"
                        });
                        try
                        {
                            await existingSubnets.CreateOrUpdateAsync(WaitUntil.Completed, appIntegrationSubnetName, subnetData);
                            _logger.LogInformation($"Created integration subnet '{appIntegrationSubnetName}'.");
                        }
                        catch (RequestFailedException ex) when (ex.Status == 400 && ex.ErrorCode == "NetcfgSubnetRangesOverlap")
                        {
                            _logger.LogWarning($"Cannot create integration subnet '{appIntegrationSubnetName}' with prefix '{appIntegrationSubnetAddressPrefix}' — address range overlaps with an existing subnet. " +
                                $"Please update the App Service integration subnet address prefix in the networking configuration to a non-overlapping range within the VNet address space.");
                        }
                    }
                    else
                    {
                        _logger.LogInformation($"Integration subnet '{appIntegrationSubnetName}' already exists.");
                    }
                }

                await EnsureTagsOnExisting(vnet.Data.Tags, vnet.GetTagResource());
            }

            return vnet;
        }
    }
}
