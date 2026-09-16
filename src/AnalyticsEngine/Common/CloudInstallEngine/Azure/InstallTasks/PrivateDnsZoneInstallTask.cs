using Azure;
using Azure.Core;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using Azure.ResourceManager.PrivateDns;
using Microsoft.Extensions.Logging;
using System;
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
            sw.Restart();
            var existingLinks = dnsZone.GetVirtualNetworkLinks().GetAll()
                .Select(link => new VNetLinkInfo(
                    link.Data.Name,
                    link.Data.VirtualNetworkId?.ToString()))
                .ToList();
            var existingLink = FindVNetLinkForVirtualNetwork(existingLinks, vnetId);
            WarnIfSlow(sw, $"listing VNet links for DNS zone '{zoneName}'");

            if (existingLink != null)
            {
                if (string.Equals(existingLink.Name, linkName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation($"Found existing VNet link '{existingLink.Name}' for DNS zone '{zoneName}'.");
                }
                else
                {
                    _logger.LogInformation($"Found existing VNet link '{existingLink.Name}' for DNS zone '{zoneName}' (name differs from the installer default - created outside the installer; reusing).");
                }
            }

            if (existingLink == null)
            {
                _logger.LogInformation($"Creating VNet link '{linkName}' for DNS zone '{zoneName}'...");
                var linkData = new VirtualNetworkLinkData("global")
                {
                    VirtualNetworkId = new ResourceIdentifier(vnetId),
                    RegistrationEnabled = false,
                };
                EnsureTagsOnNew(linkData.Tags);
                try
                {
                    var operation = await dnsZone.GetVirtualNetworkLinks().CreateOrUpdateAsync(WaitUntil.Completed, linkName, linkData);
                    _logger.LogInformation($"Created VNet link '{operation.Value.Data.Name}'.");
                }
                catch (RequestFailedException ex) when (IsVNetLinkAlreadyPresentConflict(ex))
                {
                    _logger.LogInformation($"Azure reported that DNS zone '{zoneName}' already has a VNet link for the configured virtual network; reusing the existing link.");
                }
            }

            // 3. Get or create DNS zone group on the private endpoint so A records are auto-registered
            var peResource = Container.GetPrivateEndpoints().Get(peName).Value;
            var zoneGroupName = $"{peName}-zonegroup";
            PrivateDnsZoneGroupResource zoneGroup = null;
            sw.Restart();
            var zoneGroups = peResource.GetPrivateDnsZoneGroups().GetAll().ToList();
            zoneGroup = zoneGroups.FirstOrDefault();
            var zoneGroupDecision = DecideZoneGroupAction(
                zoneGroups.Select(group => new ZoneGroupInfo(
                    group.Data.Name,
                    GetConfiguredZoneIds(group.Data.PrivateDnsZoneConfigs))),
                dnsZone.Id.ToString());
            WarnIfSlow(sw, $"listing DNS zone groups on private endpoint '{peName}'");

            if (zoneGroupDecision.Action == ZoneGroupAction.Create)
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
                try
                {
                    var operation = await peResource.GetPrivateDnsZoneGroups().CreateOrUpdateAsync(WaitUntil.Completed, zoneGroupName, zoneGroupData);
                    zoneGroup = operation.Value;
                    _logger.LogInformation($"Created DNS zone group '{zoneGroup.Data.Name}'.");
                }
                catch (RequestFailedException ex) when (IsZoneGroupAlreadyPresentConflict(ex))
                {
                    _logger.LogInformation($"Azure reported that private endpoint '{peName}' already has a DNS zone group; reusing the existing group.");
                }
            }
            else if (zoneGroupDecision.Action == ZoneGroupAction.Recreate)
            {
                // Reconcile: if the existing zone group references a different (wrong) private DNS zone
                // (e.g. an older installer pointed the PE at 'privatelink.redisenterprise.cache.azure.net'
                // instead of 'privatelink.redis.azure.net'), recreate it. Without this, the bad config is
                // sticky: A records never auto-register into the right zone and VNet-integrated clients
                // keep resolving the public IP.
                var existingSummary = zoneGroupDecision.ExistingGroup.PrivateDnsZoneIds.Count == 0
                    ? "<none>"
                    : string.Join(", ", zoneGroupDecision.ExistingGroup.PrivateDnsZoneIds);
                _logger.LogWarning(
                    $"DNS zone group '{zoneGroupDecision.ExistingGroup.Name}' on private endpoint '{peName}' references the wrong zone(s) [{existingSummary}] — expected '{dnsZone.Id}'. " +
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
                try
                {
                    var operation = await peResource.GetPrivateDnsZoneGroups().CreateOrUpdateAsync(WaitUntil.Completed, zoneGroupName, zoneGroupData);
                    zoneGroup = operation.Value;
                    _logger.LogInformation($"Recreated DNS zone group '{zoneGroup.Data.Name}' pointing at '{dnsZone.Data.Name}'.");
                }
                catch (RequestFailedException ex) when (IsZoneGroupAlreadyPresentConflict(ex))
                {
                    _logger.LogInformation($"Azure reported that private endpoint '{peName}' already has a DNS zone group after reconcile; reusing the existing group.");
                }
            }
            else if (string.Equals(zoneGroupDecision.ExistingGroup.Name, zoneGroupName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation($"Found existing DNS zone group '{zoneGroupDecision.ExistingGroup.Name}' on private endpoint '{peName}'.");
            }
            else
            {
                _logger.LogInformation($"Found existing DNS zone group '{zoneGroupDecision.ExistingGroup.Name}' on private endpoint '{peName}' (name differs from the installer default - created outside the installer; reusing).");
            }

            return dnsZone;
        }

        internal static VNetLinkInfo FindVNetLinkForVirtualNetwork(IEnumerable<VNetLinkInfo> links, string vnetId)
        {
            if (links == null || string.IsNullOrEmpty(vnetId))
            {
                return null;
            }

            return links.FirstOrDefault(link =>
                !string.IsNullOrEmpty(link?.VirtualNetworkId) &&
                string.Equals(link.VirtualNetworkId, vnetId, StringComparison.OrdinalIgnoreCase));
        }

        internal static ZoneGroupDecision DecideZoneGroupAction(IEnumerable<ZoneGroupInfo> zoneGroups, string intendedZoneId)
        {
            var existing = zoneGroups?.FirstOrDefault();
            if (existing == null)
            {
                return new ZoneGroupDecision(ZoneGroupAction.Create, null);
            }

            var pointsAtIntendedZone = existing.PrivateDnsZoneIds
                .Any(id => string.Equals(id, intendedZoneId, StringComparison.OrdinalIgnoreCase));

            return new ZoneGroupDecision(
                pointsAtIntendedZone ? ZoneGroupAction.Reuse : ZoneGroupAction.Recreate,
                existing);
        }

        internal static bool IsVNetLinkAlreadyPresentConflict(RequestFailedException ex)
        {
            return string.Equals(ex?.ErrorCode, "Conflict", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsZoneGroupAlreadyPresentConflict(RequestFailedException ex)
        {
            return string.Equals(ex?.ErrorCode, "MoreThanOnePrivateDnsZoneGroupPerPrivateEndpointNotAllowed", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> GetConfiguredZoneIds(IEnumerable<PrivateDnsZoneConfig> zoneConfigs)
        {
            return zoneConfigs == null
                ? new List<string>()
                : zoneConfigs
                    .Where(c => c?.PrivateDnsZoneId != null)
                    .Select(c => c.PrivateDnsZoneId.ToString())
                    .ToList();
        }

        private void WarnIfSlow(Stopwatch sw, string operation)
        {
            sw.Stop();
            if (sw.Elapsed.TotalSeconds >= SlowArmReadWarningSeconds)
            {
                _logger.LogWarning($"ARM operation '{operation}' took {(int)sw.Elapsed.TotalSeconds}s — slower than expected ({SlowArmReadWarningSeconds}s threshold). Could indicate ARM regional latency or throttling.");
            }
        }

        internal class VNetLinkInfo
        {
            public VNetLinkInfo(string name, string virtualNetworkId)
            {
                Name = name;
                VirtualNetworkId = virtualNetworkId;
            }

            public string Name { get; }

            public string VirtualNetworkId { get; }
        }

        internal class ZoneGroupInfo
        {
            public ZoneGroupInfo(string name, IEnumerable<string> privateDnsZoneIds)
            {
                Name = name;
                PrivateDnsZoneIds = privateDnsZoneIds?.ToList() ?? new List<string>();
            }

            public string Name { get; }

            public List<string> PrivateDnsZoneIds { get; }
        }

        internal enum ZoneGroupAction
        {
            Create,
            Reuse,
            Recreate,
        }

        internal class ZoneGroupDecision
        {
            public ZoneGroupDecision(ZoneGroupAction action, ZoneGroupInfo existingGroup)
            {
                Action = action;
                ExistingGroup = existingGroup;
            }

            public ZoneGroupAction Action { get; }

            public ZoneGroupInfo ExistingGroup { get; }
        }
    }
}
