using Azure;
using Azure.Core;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using Azure.ResourceManager.PrivateDns;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

        // ARM reads should normally return in a few seconds. When they take longer than this
        // we emit a WARN so unexpectedly-slow ARM calls are visible in the install log.
        private const int SlowArmReadWarningSeconds = 20;

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
            var sw = Stopwatch.StartNew();
            try
            {
                var response = await Container.GetPrivateDnsZoneAsync(zoneName);
                dnsZone = response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Not found
            }
            WarnIfSlow(sw, $"reading private DNS zone '{zoneName}'");

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
            sw.Restart();
            try
            {
                var response = await dnsZone.GetVirtualNetworkLinkAsync(linkName);
                vnetLink = response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Not found
            }
            WarnIfSlow(sw, $"reading VNet link '{linkName}'");

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
            sw.Restart();
            try
            {
                var response = await peResource.GetPrivateDnsZoneGroupAsync(zoneGroupName);
                zoneGroup = response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Not found
            }
            WarnIfSlow(sw, $"reading DNS zone group '{zoneGroupName}'");

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
                // Reconcile: if the existing zone group references a different (wrong) private DNS zone
                // (e.g. an older installer pointed the PE at 'privatelink.redisenterprise.cache.azure.net'
                // instead of 'privatelink.redis.azure.net'), recreate it. Without this, the bad config is
                // sticky: A records never auto-register into the right zone and VNet-integrated clients
                // keep resolving the public IP.
                var configuredZoneIds = zoneGroup.Data.PrivateDnsZoneConfigs == null
                    ? new List<string>()
                    : zoneGroup.Data.PrivateDnsZoneConfigs
                        .Where(c => c?.PrivateDnsZoneId != null)
                        .Select(c => c.PrivateDnsZoneId.ToString())
                        .ToList();
                var pointsAtIntendedZone = configuredZoneIds
                    .Any(id => string.Equals(id, dnsZone.Id.ToString(), System.StringComparison.OrdinalIgnoreCase));

                if (!pointsAtIntendedZone)
                {
                    var existingSummary = configuredZoneIds.Count == 0
                        ? "<none>"
                        : string.Join(", ", configuredZoneIds);
                    _logger.LogWarning(
                        $"DNS zone group '{zoneGroupName}' on private endpoint '{peName}' references the wrong zone(s) [{existingSummary}] — expected '{dnsZone.Id}'. " +
                        "Recreating so A records auto-register into the correct zone.");
                    await zoneGroup.DeleteAsync(WaitUntil.Completed);

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
                    _logger.LogInformation($"Recreated DNS zone group '{zoneGroup.Data.Name}' pointing at '{dnsZone.Data.Name}'.");
                }
                else
                {
                    _logger.LogInformation($"Found existing DNS zone group '{zoneGroup.Data.Name}' on private endpoint '{peName}'.");
                }
            }

            return dnsZone;
        }

        private void WarnIfSlow(Stopwatch sw, string operation)
        {
            sw.Stop();
            if (sw.Elapsed.TotalSeconds >= SlowArmReadWarningSeconds)
            {
                _logger.LogWarning($"ARM operation '{operation}' took {(int)sw.Elapsed.TotalSeconds}s — slower than expected ({SlowArmReadWarningSeconds}s threshold). Could indicate ARM regional latency or throttling.");
            }
        }
    }
}
