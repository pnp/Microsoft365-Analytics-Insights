using Azure;
using Azure.Core;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using Azure.ResourceManager.PrivateDns;
using Azure.ResourceManager.PrivateDns.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Gets or creates a Private DNS Zone, links it to a VNet, and creates a DNS zone group on the private endpoint
    /// so that DNS records are automatically registered.
    /// </summary>
    public class PrivateDnsZoneInstallTask : InstallTaskInAzResourceGroup<PrivateDnsZoneResource>
    {
        public const string CONFIG_KEY_VNET_ID = "vnetId";
        public const string CONFIG_KEY_PE_NAME = "privateEndpointName";

        public PrivateDnsZoneInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "get/create private DNS zone";

        public override async Task<PrivateDnsZoneResource> ExecuteTaskReturnResult(object contextArg)
        {
            var zoneName = _config.GetNameConfigValue();
            var vnetId = _config.GetConfigValue(CONFIG_KEY_VNET_ID);
            var peName = _config.GetConfigValue(CONFIG_KEY_PE_NAME);

            // 1. Get or create the Private DNS Zone
            PrivateDnsZoneResource dnsZone = null;
            try
            {
                var response = await Container.GetPrivateDnsZoneAsync(zoneName);
                dnsZone = response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Not found
            }

            if (dnsZone == null)
            {
                _logger.LogInformation($"Creating private DNS zone '{zoneName}'...");
                var dnsZoneData = new PrivateDnsZoneData("global");
                EnsureTagsOnNew(dnsZoneData.Tags);
                var operation = await Container.GetPrivateDnsZones().CreateOrUpdateAsync(WaitUntil.Completed, zoneName, dnsZoneData);
                dnsZone = operation.Value;
                _logger.LogInformation($"Created private DNS zone '{dnsZone.Data.Name}'.");
            }
            else
            {
                _logger.LogInformation($"Found existing private DNS zone '{dnsZone.Data.Name}'.");
                await EnsureTagsOnExisting(dnsZone.Data.Tags, dnsZone.GetTagResource());
            }

            // 2. Get or create VNet link
            var linkName = $"{zoneName}-vnet-link";
            VirtualNetworkLinkResource vnetLink = null;
            try
            {
                var response = await dnsZone.GetVirtualNetworkLinkAsync(linkName);
                vnetLink = response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Not found
            }

            if (vnetLink == null)
            {
                _logger.LogInformation($"Creating VNet link '{linkName}' for DNS zone '{zoneName}'...");
                var linkData = new VirtualNetworkLinkData("global")
                {
                    VirtualNetworkId = new ResourceIdentifier(vnetId),
                    RegistrationEnabled = false,
                };
                EnsureTagsOnNew(linkData.Tags);
                var operation = await dnsZone.GetVirtualNetworkLinks().CreateOrUpdateAsync(WaitUntil.Completed, linkName, linkData);
                vnetLink = operation.Value;
                _logger.LogInformation($"Created VNet link '{vnetLink.Data.Name}'.");
            }
            else
            {
                _logger.LogInformation($"Found existing VNet link '{vnetLink.Data.Name}' for DNS zone '{zoneName}'.");
            }

            // 3. Get or create DNS zone group on the private endpoint so A records are auto-registered
            var peResource = Container.GetPrivateEndpoints().Get(peName).Value;
            var zoneGroupName = $"{peName}-zonegroup";
            PrivateDnsZoneGroupResource zoneGroup = null;
            try
            {
                var response = await peResource.GetPrivateDnsZoneGroupAsync(zoneGroupName);
                zoneGroup = response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Not found
            }

            if (zoneGroup == null)
            {
                _logger.LogInformation($"Creating DNS zone group '{zoneGroupName}' on private endpoint '{peName}'...");
                var zoneGroupData = new PrivateDnsZoneGroupData()
                {
                    Name = zoneGroupName,
                };
                zoneGroupData.PrivateDnsZoneConfigs.Add(new PrivateDnsZoneConfig()
                {
                    Name = zoneName.Replace(".", "-"),
                    PrivateDnsZoneId = dnsZone.Id,
                });
                var operation = await peResource.GetPrivateDnsZoneGroups().CreateOrUpdateAsync(WaitUntil.Completed, zoneGroupName, zoneGroupData);
                zoneGroup = operation.Value;
                _logger.LogInformation($"Created DNS zone group '{zoneGroup.Data.Name}'.");
            }
            else
            {
                _logger.LogInformation($"Found existing DNS zone group '{zoneGroup.Data.Name}' on private endpoint '{peName}'.");
            }

            return dnsZone;
        }
    }
}
