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

                EnsureTagsOnNew(vnetData.Tags);
                var operation = await Container.GetVirtualNetworks().CreateOrUpdateAsync(WaitUntil.Completed, name, vnetData);
                vnet = operation.Value;

                _logger.LogInformation($"Created VNet '{vnet.Data.Name}' with subnet '{subnetName}'.");
            }
            else
            {
                _logger.LogInformation($"Found existing VNet '{vnet.Data.Name}'. No changes made.");
                await EnsureTagsOnExisting(vnet.Data.Tags, vnet.GetTagResource());
            }

            return vnet;
        }
    }
}
