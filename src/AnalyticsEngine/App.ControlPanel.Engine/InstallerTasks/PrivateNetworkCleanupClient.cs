using Azure;
using Azure.Core;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Automation;
using Azure.ResourceManager.Automation.Models;
using Azure.ResourceManager.CognitiveServices;
using Azure.ResourceManager.CognitiveServices.Models;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.KeyVault.Models;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.PrivateDns;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.ServiceBus;
using Azure.ResourceManager.ServiceBus.Models;
using Azure.ResourceManager.Sql;
using Azure.ResourceManager.Sql.Models;
using Azure.ResourceManager.Storage;
using Azure.ResourceManager.Storage.Models;
using CloudInstallEngine.Azure;
using CloudInstallEngine.Azure.InstallTasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>ARM adapter. No resource-group, subnet, VNet or whole-zone deletes.</summary>
    public sealed class PrivateNetworkCleanupClient : IPrivateNetworkCleanupClient
    {
        private readonly ResourceGroupResource _group;
        private readonly PrivateNetworkManifest _manifest;
        private readonly HashSet<string> _targets;
        private readonly HashSet<string> _endpointNames;
        private readonly string _previousVnetId;

        public PrivateNetworkCleanupClient(ResourceGroupResource group, PrivateNetworkManifest manifest,
            IEnumerable<string> targets, IEnumerable<string> endpointNames, string previousVnetId = null)
        {
            _group = group;
            _manifest = manifest;
            _targets = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
            _endpointNames = new HashSet<string>(endpointNames, StringComparer.OrdinalIgnoreCase);
            _previousVnetId = previousVnetId;
        }

        /// <summary>
        /// GET-only resource-group inventory, including unrelated/shared and retired Redis networking.
        /// It is intentionally separate from fail-fast teardown discovery and never updates the journal.
        /// </summary>
        public async Task<PrivateNetworkReview> Review(CancellationToken ct)
        {
            var report = new PrivateNetworkReview { ResourceGroupId = _group.Id.ToString() };
            var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string id, string observation, IEnumerable<string> relationships = null)
            {
                observed.Add(id);
                var saved = _manifest.Artifacts.SingleOrDefault(a => PrivateNetworkProvenance.Same(a.Id, id));
                report.Items.Add(new PrivateNetworkReviewItem
                {
                    ResourceId = id,
                    ResourceType = new ResourceIdentifier(id).ResourceType.ToString(),
                    Observation = observation,
                    SavedEvidence = saved == null ? "No saved creator evidence; separate owner review required." :
                        saved.Removed ? "Previously recorded removed; live artifact must not be adopted." :
                        saved.CreatedByInstaller && saved.CreationConfirmed ?
                            "Confirmed creation is recorded; this inventory is not deletion approval or ownership revalidation." :
                            "Adopted or unconfirmed creation; separate owner evidence required.",
                    RelationshipIds = relationships?.Where(r => r != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                        ?? new List<string>()
                });
            }

            foreach (var endpoint in _group.GetPrivateEndpoints().GetAll(ct))
            {
                var connections = endpoint.Data.PrivateLinkServiceConnections
                    .Concat(endpoint.Data.ManualPrivateLinkServiceConnections).ToList();
                var targets = connections.Select(c => c.PrivateLinkServiceId?.ToString()).ToList();
                var related = targets.Any(t => _targets.Contains(t)) || _endpointNames.Contains(endpoint.Data.Name) ||
                    _manifest.Artifacts.Any(a => PrivateNetworkProvenance.Same(a.Id, endpoint.Id.ToString()));
                Add(endpoint.Id.ToString(),
                    (related ? "Configured/recorded candidate. " : "Resource-group inventory only; deployment ownership unknown. ") +
                    "Connection states: " + string.Join(", ", connections.Select(c => c.PrivateLinkServiceConnectionState?.Status.ToString() ?? "Unknown")) +
                    ". Subresources: " + string.Join(", ", connections.SelectMany(c => c.GroupIds)) + ".",
                    targets.Concat(new[] { endpoint.Data.Subnet?.Id?.ToString() }));
                foreach (var group in endpoint.GetPrivateDnsZoneGroups().GetAll(ct))
                    Add(group.Id.ToString(), "Endpoint DNS zone group; review every linked zone and its shared users.",
                        group.Data.PrivateDnsZoneConfigs.Select(c => c.PrivateDnsZoneId?.ToString()).Concat(new[] { endpoint.Id.ToString() }));
            }
            foreach (var zone in _group.GetPrivateDnsZones().GetAll(cancellationToken: ct))
            {
                if (!zone.Data.Name.StartsWith("privatelink.", StringComparison.OrdinalIgnoreCase) &&
                    !_manifest.Artifacts.Any(a => a.ZoneIds.Contains(zone.Id.ToString(), StringComparer.OrdinalIgnoreCase) ||
                        PrivateNetworkProvenance.Same(a.Id, zone.Id.ToString()))) continue;
                Add(zone.Id.ToString(), "Private DNS zone retained; never delete the whole zone through this workflow.");
                foreach (var record in zone.GetRecords(cancellationToken: ct))
                    Add(record.Id.ToString(),
                        "DNS record in a potentially shared zone; current presence/name is not exclusive creator evidence.",
                        new[] { zone.Id.ToString() });
                foreach (var link in zone.GetVirtualNetworkLinks().GetAll(cancellationToken: ct))
                    Add(link.Id.ToString(), "VNet link in a potentially shared zone; review resolution impact before any separately approved change.",
                        new[] { zone.Id.ToString(), link.Data.VirtualNetworkId?.ToString() });
            }
            foreach (var target in _targets.Where(t => new ResourceIdentifier(t).ResourceType.ToString()
                .Equals("Microsoft.Web/sites", StringComparison.OrdinalIgnoreCase)))
            {
                var integration = await Read(new PrivateNetworkArtifact { Id = target, Kind = PrivateNetworkProvenance.Integration }, ct);
                if (integration?.Artifact.SubnetId != null)
                    Add(target, "App Service outbound VNet integration; current association does not prove creator ownership.",
                        new[] { integration.Artifact.SubnetId });
            }
            foreach (var target in _targets.Where(t => new ResourceIdentifier(t).ResourceType.ToString()
                .Equals("Microsoft.KeyVault/vaults", StringComparison.OrdinalIgnoreCase)))
            {
                KeyVaultResource vault;
                try { vault = (await _group.GetKeyVaultAsync(new ResourceIdentifier(target).Name, ct)).Value; }
                catch (RequestFailedException ex) when (PrivateNetworkProvenance.IsMissing(ex)) { continue; }
                var rules = vault.Data.Properties.NetworkRuleSet;
                Add(target, "Key Vault public access: " + vault.Data.Properties.PublicNetworkAccess +
                    "; firewall default: " + rules?.DefaultAction + ". IP rules and policy are not changed.");
                if (rules != null)
                    foreach (var rule in rules.VirtualNetworkRules)
                        Add(target + "/virtualNetworkRules/" + Uri.EscapeDataString(rule.Id.ToString()),
                            "Key Vault subnet rule; current association does not prove installer ownership.",
                            new[] { target, rule.Id.ToString() });
            }
            foreach (var artifact in _manifest.Artifacts.Where(a => !a.Removed && !observed.Contains(a.Id) &&
                (a.Kind == PrivateNetworkProvenance.Integration || a.Kind == PrivateNetworkProvenance.VirtualNetworkRule)))
            {
                var live = await Read(artifact, ct);
                if (live != null && !observed.Contains(artifact.Id))
                    Add(artifact.Id, "Recorded integration/subnet rule; owner must review exact relationship before cleanup.",
                        new[] { live.Artifact.TargetId, live.Artifact.SubnetId });
            }
            foreach (var resource in _group.GetGenericResources(filter: LeftoverRedisResourceWarning.ResourceTypeFilter, cancellationToken: ct))
                Add(resource.Id.ToString(), "Retired Redis provider: not reconfigured or deleted by this installer. " +
                    "Resource-group presence does not prove this deployment owns it; separately approved manual review is required.");
            foreach (var artifact in _manifest.Artifacts.Where(a => !a.Removed && !observed.Contains(a.Id)))
                Add(artifact.Id, "Recorded artifact was not observed in this resource-group inventory; " +
                    "do not treat absence as deletion approval or reset the saved journal.",
                    new[] { artifact.TargetId, artifact.SubnetId }.Concat(artifact.ZoneIds));
            ct.ThrowIfCancellationRequested();
            return report;
        }

        public async Task<IReadOnlyList<PrivateNetworkSnapshot>> DiscoverEndpoints(CancellationToken ct)
        {
            var result = new List<PrivateNetworkSnapshot>();
            foreach (var pe in _group.GetPrivateEndpoints().GetAll(ct))
            {
                var connections = pe.Data.PrivateLinkServiceConnections.Concat(pe.Data.ManualPrivateLinkServiceConnections);
                if (connections.Any(c => _targets.Contains(c.PrivateLinkServiceId?.ToString())) ||
                    _endpointNames.Contains(pe.Data.Name) ||
                    _manifest.Artifacts.Any(a => PrivateNetworkProvenance.Same(a.Id, pe.Id.ToString()) && !a.Removed))
                    result.Add(await ReadEndpoint(pe, ct));
            }
            foreach (var target in _targets.Where(t => new ResourceIdentifier(t).ResourceType.ToString()
                .Equals("Microsoft.Web/sites", StringComparison.OrdinalIgnoreCase)))
            {
                var integration = await Read(new PrivateNetworkArtifact
                {
                    Id = target, Kind = PrivateNetworkProvenance.Integration
                }, ct);
                if (integration?.Artifact.SubnetId != null) result.Add(integration);
            }
            // A legacy endpoint may already have been removed while its DNS still hijacks the name.
            // A record-name match is a reason to BLOCK, never permission to delete.
            foreach (var zone in _group.GetPrivateDnsZones().GetAll(cancellationToken: ct))
            {
                var targetNames = new HashSet<string>(_targets.Where(t => SupportsZone(
                    new ResourceIdentifier(t).ResourceType.ToString(), zone.Data.Name))
                    .Select(t => new ResourceIdentifier(t).Name), StringComparer.OrdinalIgnoreCase);
                if (targetNames.Count == 0) continue;
                foreach (var record in zone.GetRecords(cancellationToken: ct))
                {
                    if ((record.Id.ResourceType.ToString().EndsWith("/A", StringComparison.OrdinalIgnoreCase) ||
                         record.Id.ResourceType.ToString().EndsWith("/AAAA", StringComparison.OrdinalIgnoreCase)) &&
                        targetNames.Contains(record.Name.Split('.')[0]) &&
                        !_manifest.Artifacts.Any(a => a.Kind == PrivateNetworkProvenance.ZoneGroup &&
                            a.CreatedByInstaller && a.CreationConfirmed && !a.Removed &&
                            a.Records.Any(r => r.CreatedExclusively &&
                                PrivateNetworkProvenance.Same(r.ZoneId, zone.Id.ToString()) &&
                                PrivateNetworkProvenance.Same(r.Name, record.Name))))
                        throw PrivateNetworkProvenance.Blocked(record.Id.ToString(),
                            "a relevant private DNS record has no exclusive creator provenance (the endpoint may already be missing)");
                }
                if (!string.IsNullOrWhiteSpace(_previousVnetId))
                {
                    foreach (var link in zone.GetVirtualNetworkLinks().GetAll(cancellationToken: ct))
                    {
                        if (!PrivateNetworkProvenance.Same(link.Data.VirtualNetworkId?.ToString(), _previousVnetId)) continue;
                        if (_manifest.Artifacts.Any(a => a.Kind == PrivateNetworkProvenance.Link &&
                            PrivateNetworkProvenance.Same(a.Id, link.Id.ToString()) && !a.Removed)) continue;
                        throw PrivateNetworkProvenance.Blocked(link.Id.ToString(),
                            "a legacy VNet-linked private DNS zone is present without a saved created-versus-adopted inventory");
                    }
                }
            }
            return result;
        }

        private static bool SupportsZone(string resourceType, string zone)
        {
            switch (resourceType.ToLowerInvariant())
            {
                case "microsoft.sql/servers": return PrivateNetworkProvenance.Same(zone, "privatelink.database.windows.net");
                case "microsoft.storage/storageaccounts":
                    return PrivateNetworkProvenance.Same(zone, "privatelink.blob.core.windows.net") ||
                           PrivateNetworkProvenance.Same(zone, "privatelink.table.core.windows.net");
                case "microsoft.keyvault/vaults": return PrivateNetworkProvenance.Same(zone, "privatelink.vaultcore.azure.net");
                case "microsoft.servicebus/namespaces": return PrivateNetworkProvenance.Same(zone, "privatelink.servicebus.windows.net");
                case "microsoft.web/sites": return PrivateNetworkProvenance.Same(zone, "privatelink.azurewebsites.net");
                case "microsoft.cognitiveservices/accounts":
                    return PrivateNetworkProvenance.Same(zone, "privatelink.cognitiveservices.azure.com") ||
                           PrivateNetworkProvenance.Same(zone, "privatelink.openai.azure.com");
                case "microsoft.automation/automationaccounts": return PrivateNetworkProvenance.Same(zone, "privatelink.azure-automation.net");
                default: return false;
            }
        }

        public async Task<PrivateNetworkSnapshot> Read(PrivateNetworkArtifact artifact, CancellationToken ct)
        {
            Scope(artifact.Id);
            try
            {
                var id = new ResourceIdentifier(artifact.Id);
                if (artifact.Kind == PrivateNetworkProvenance.Endpoint)
                    return await ReadEndpoint((await _group.GetPrivateEndpointAsync(id.Name, cancellationToken: ct)).Value, ct);
                if (artifact.Kind == PrivateNetworkProvenance.ZoneGroup)
                {
                    var pe = (await _group.GetPrivateEndpointAsync(id.Parent.Name, cancellationToken: ct)).Value;
                    var group = (await pe.GetPrivateDnsZoneGroups().GetAsync(id.Name, ct)).Value;
                    return new PrivateNetworkSnapshot { Artifact = GroupArtifact(group, pe.Id.ToString()) };
                }
                if (artifact.Kind == PrivateNetworkProvenance.Link)
                {
                    var zone = (await _group.GetPrivateDnsZoneAsync(id.Parent.Name, ct)).Value;
                    var link = (await zone.GetVirtualNetworkLinks().GetAsync(id.Name, ct)).Value;
                    return new PrivateNetworkSnapshot
                    {
                        Artifact = new PrivateNetworkArtifact
                        {
                            Id = link.Id.ToString(), Kind = PrivateNetworkProvenance.Link,
                            TargetId = link.Data.VirtualNetworkId?.ToString(),
                            ZoneIds = new List<string> { zone.Id.ToString() }
                        },
                        Tags = link.Data.Tags
                    };
                }
                if (artifact.Kind == PrivateNetworkProvenance.Integration)
                {
                    var site = (await _group.GetWebSiteAsync(id.Name, ct)).Value;
                    return new PrivateNetworkSnapshot
                    {
                        Artifact = new PrivateNetworkArtifact
                        {
                            Id = site.Id.ToString(), Kind = artifact.Kind, TargetId = site.Id.ToString(),
                            SubnetId = site.Data.VirtualNetworkSubnetId?.ToString()
                        }
                    };
                }
                if (artifact.Kind == PrivateNetworkProvenance.VirtualNetworkRule)
                {
                    Scope(artifact.TargetId);
                    var vault = (await _group.GetKeyVaultAsync(new ResourceIdentifier(artifact.TargetId).Name, ct)).Value;
                    if (vault.Data.Properties.NetworkRuleSet?.VirtualNetworkRules
                        .Any(r => PrivateNetworkProvenance.Same(r.Id.ToString(), artifact.SubnetId)) != true) return null;
                    return new PrivateNetworkSnapshot
                    {
                        Artifact = new PrivateNetworkArtifact
                        {
                            Id = artifact.Id, Kind = artifact.Kind, TargetId = vault.Id.ToString(), SubnetId = artifact.SubnetId
                        }
                    };
                }
                throw PrivateNetworkProvenance.Blocked(artifact.Id, "unsupported manifest artifact kind");
            }
            catch (RequestFailedException ex) when (PrivateNetworkProvenance.IsMissing(ex)) { return null; }
        }

        private Task<PrivateNetworkSnapshot> ReadEndpoint(PrivateEndpointResource pe, CancellationToken ct)
        {
            var connections = pe.Data.PrivateLinkServiceConnections.Concat(pe.Data.ManualPrivateLinkServiceConnections).ToList();
            if (connections.Count != 1 || connections[0].GroupIds.Count != 1)
                throw PrivateNetworkProvenance.Blocked(pe.Id.ToString(), "ambiguous endpoint target/group relationships");
            var result = new PrivateNetworkSnapshot
            {
                Artifact = new PrivateNetworkArtifact
                {
                    Id = pe.Id.ToString(), Kind = PrivateNetworkProvenance.Endpoint,
                    TargetId = connections[0].PrivateLinkServiceId?.ToString(),
                    GroupId = connections[0].GroupIds[0], SubnetId = pe.Data.Subnet?.Id?.ToString()
                },
                Tags = pe.Data.Tags
            };
            foreach (var group in pe.GetPrivateDnsZoneGroups().GetAll(ct))
                result.Children.Add(GroupArtifact(group, pe.Id.ToString()));
            return Task.FromResult(result);
        }

        private static PrivateNetworkArtifact GroupArtifact(PrivateDnsZoneGroupResource group, string endpointId) =>
            new PrivateNetworkArtifact
            {
                Id = group.Id.ToString(), Kind = PrivateNetworkProvenance.ZoneGroup, TargetId = endpointId,
                ZoneIds = group.Data.PrivateDnsZoneConfigs.Select(c => c.PrivateDnsZoneId.ToString()).ToList(),
                Records = PrivateDnsZoneInstallTask.ReadRecords(group.Data.PrivateDnsZoneConfigs)
            };

        public async Task ValidateRecords(PrivateNetworkArtifact group, bool removed, CancellationToken ct)
        {
            if (!removed)
            {
                // Identical addresses do not prove exclusivity: another endpoint's zone group can
                // now depend on the same record set, even if its contents have not changed.
                foreach (var endpoint in _group.GetPrivateEndpoints().GetAll(ct))
                foreach (var other in endpoint.GetPrivateDnsZoneGroups().GetAll(ct))
                {
                    if (PrivateNetworkProvenance.Same(other.Id.ToString(), group.Id)) continue;
                    var configs = other.Data.PrivateDnsZoneConfigs.Where(c =>
                        group.ZoneIds.Contains(c.PrivateDnsZoneId?.ToString(), StringComparer.OrdinalIgnoreCase)).ToList();
                    if (configs.Count == 0) continue;
                    var records = PrivateDnsZoneInstallTask.ReadRecords(configs);
                    if (records.Count == 0 || records.Any(r => group.Records.Any(expected =>
                        PrivateNetworkProvenance.Same(r.ZoneId, expected.ZoneId) &&
                        PrivateNetworkProvenance.Same(r.Name, expected.Name) &&
                        PrivateNetworkProvenance.Same(r.Type, expected.Type))))
                        throw PrivateNetworkProvenance.Blocked(other.Id.ToString(),
                            "another endpoint DNS zone group shares an owned record set or its record inventory is not yet available");
                }
            }
            foreach (var expected in group.Records)
            {
                Scope(expected.ZoneId);
                if (!expected.CreatedExclusively ||
                    (!PrivateNetworkProvenance.Same(expected.Type, "A") && !PrivateNetworkProvenance.Same(expected.Type, "AAAA")))
                    throw PrivateNetworkProvenance.Blocked(group.Id, "DNS record type/ownership cannot be safely verified");
                var zone = await GetZone(expected.ZoneId, ct);
                if (zone == null)
                {
                    if (removed) continue;
                    throw PrivateNetworkProvenance.Blocked(expected.ZoneId, "the expected DNS zone is missing");
                }
                var found = false;
                foreach (var record in zone.GetRecords(cancellationToken: ct))
                {
                    if (!PrivateNetworkProvenance.Same(record.Name, expected.Name) ||
                        !record.Id.ResourceType.ToString().EndsWith("/" + expected.Type, StringComparison.OrdinalIgnoreCase)) continue;
                    found = true;
                    var addresses = record.ARecords.Select(a => a.IPv4Address.ToString())
                        .Concat(record.AaaaRecords.Select(a => a.IPv6Address.ToString()));
                    if (removed || !new HashSet<string>(expected.Addresses, StringComparer.OrdinalIgnoreCase).SetEquals(addresses))
                        throw PrivateNetworkProvenance.Blocked(record.Id.ToString(),
                            removed ? "DNS records remain after zone-group deletion; manual DNS review is required" :
                                      "DNS records were changed/shared after creation");
                }
                if (!found && !removed)
                    throw PrivateNetworkProvenance.Blocked(group.Id, "an expected exclusive DNS record is missing");
            }
        }

        public async Task<bool> ZoneHasOtherRecords(string zoneId, CancellationToken ct)
        {
            var zone = await GetZone(zoneId, ct);
            if (zone == null) return false;
            foreach (var record in zone.GetRecords(cancellationToken: ct))
                if (!record.Id.ResourceType.ToString().EndsWith("/SOA", StringComparison.OrdinalIgnoreCase))
                    return true;
            foreach (var pe in _group.GetPrivateEndpoints().GetAll(ct))
                foreach (var group in pe.GetPrivateDnsZoneGroups().GetAll(ct))
                    if (group.Data.PrivateDnsZoneConfigs.Any(c =>
                        PrivateNetworkProvenance.Same(c.PrivateDnsZoneId?.ToString(), zoneId)))
                        return true;
            return false;
        }

        private async Task<PrivateDnsZoneResource> GetZone(string zoneId, CancellationToken ct)
        {
            Scope(zoneId);
            try { return (await _group.GetPrivateDnsZoneAsync(new ResourceIdentifier(zoneId).Name, ct)).Value; }
            catch (RequestFailedException ex) when (PrivateNetworkProvenance.IsMissing(ex)) { return null; }
        }

        public async Task Delete(PrivateNetworkArtifact artifact, CancellationToken ct)
        {
            Scope(artifact.Id);
            var recorded = _manifest.Artifacts.SingleOrDefault(a => PrivateNetworkProvenance.Same(a.Id, artifact.Id));
            if (recorded == null || !recorded.CreatedByInstaller || !recorded.CreationConfirmed ||
                recorded.Removed || !PrivateNetworkProvenance.Matches(recorded, artifact))
                throw PrivateNetworkProvenance.Blocked(artifact.Id, "the SDK adapter has no confirmed deletion authority");
            var id = new ResourceIdentifier(artifact.Id);
            try
            {
                if (artifact.Kind == PrivateNetworkProvenance.Endpoint)
                {
                    var pe = (await _group.GetPrivateEndpointAsync(id.Name, cancellationToken: ct)).Value;
                    var live = await ReadEndpoint(pe, ct);
                    if (!PrivateNetworkProvenance.Matches(artifact, live.Artifact) || live.Children.Count != 0 ||
                        !pe.Data.Tags.TryGetValue(PrivateNetworkProvenance.OwnerTag, out var owner) ||
                        owner != _manifest.DeploymentToken)
                        throw PrivateNetworkProvenance.Blocked(artifact.Id, "endpoint ownership, relationships or DNS dependencies changed during cleanup");
                    await pe.DeleteAsync(WaitUntil.Completed, ct);
                }
                else if (artifact.Kind == PrivateNetworkProvenance.ZoneGroup)
                {
                    var pe = (await _group.GetPrivateEndpointAsync(id.Parent.Name, cancellationToken: ct)).Value;
                    var group = (await pe.GetPrivateDnsZoneGroups().GetAsync(id.Name, ct)).Value;
                    if (!PrivateNetworkProvenance.Matches(artifact, GroupArtifact(group, pe.Id.ToString())))
                        throw PrivateNetworkProvenance.Blocked(artifact.Id, "zone-group relationships or records changed during cleanup");
                    await ValidateRecords(artifact, false, ct);
                    await group.DeleteAsync(WaitUntil.Completed, ct);
                }
                else if (artifact.Kind == PrivateNetworkProvenance.Link)
                {
                    var zone = await GetZone(artifact.ZoneIds.Single(), ct);
                    if (zone == null) return;
                    var link = (await zone.GetVirtualNetworkLinks().GetAsync(id.Name, ct)).Value;
                    if (!PrivateNetworkProvenance.Same(link.Data.VirtualNetworkId?.ToString(), artifact.TargetId) ||
                        !link.Data.Tags.TryGetValue(PrivateNetworkProvenance.OwnerTag, out var token) ||
                        token != _manifest.DeploymentToken || await ZoneHasOtherRecords(zone.Id.ToString(), ct))
                        throw PrivateNetworkProvenance.Blocked(artifact.Id, "link ownership or zone usage changed during cleanup");
                    await link.DeleteAsync(WaitUntil.Completed, link.Data.ETag, ct);
                }
                else if (artifact.Kind == PrivateNetworkProvenance.Integration)
                {
                    var site = (await _group.GetWebSiteAsync(id.Name, ct)).Value;
                    if (site.Data.VirtualNetworkSubnetId != null)
                    {
                        if (!PrivateNetworkProvenance.Same(site.Data.VirtualNetworkSubnetId.ToString(), artifact.SubnetId))
                            throw PrivateNetworkProvenance.Blocked(artifact.Id, "the App Service integration subnet changed during cleanup");
                        var connection = site.GetSiteNetworkConfig();
                        await connection.DeleteAsync(WaitUntil.Completed, ct);
                    }
                    await site.UpdateAsync(new SitePatchInfo
                    {
                        SiteConfig = new SiteConfigProperties { IsVnetRouteAllEnabled = artifact.PreviousRouteAllEnabled }
                    }, ct);
                    if ((await site.GetAsync(ct)).Value.Data.VirtualNetworkSubnetId != null ||
                        ((await site.GetWebSiteConfig().GetAsync(ct)).Value.Data.IsVnetRouteAllEnabled == true) != artifact.PreviousRouteAllEnabled)
                        throw PrivateNetworkProvenance.Blocked(artifact.Id, "Azure did not confirm App Service integration reset");
                }
                else if (artifact.Kind == PrivateNetworkProvenance.VirtualNetworkRule)
                {
                    Scope(artifact.TargetId);
                    var vault = (await _group.GetKeyVaultAsync(new ResourceIdentifier(artifact.TargetId).Name, ct)).Value;
                    var rules = vault.Data.Properties.NetworkRuleSet;
                    if (rules == null) return;
                    var ownedRules = rules.VirtualNetworkRules.Where(r =>
                        PrivateNetworkProvenance.Same(r.Id.ToString(), artifact.SubnetId)).ToList();
                    if (ownedRules.Count == 0) return;
                    foreach (var rule in ownedRules)
                        rules.VirtualNetworkRules.Remove(rule);
                    await vault.UpdateAsync(new KeyVaultPatch { Properties = new KeyVaultPatchProperties { NetworkRuleSet = rules } }, ct);
                    if ((await vault.GetAsync(ct)).Value.Data.Properties.NetworkRuleSet?.VirtualNetworkRules.Any(r =>
                        PrivateNetworkProvenance.Same(r.Id.ToString(), artifact.SubnetId)) == true)
                        throw PrivateNetworkProvenance.Blocked(artifact.Id, "Azure did not confirm removal of the owned Key Vault subnet rule");
                }
                else throw PrivateNetworkProvenance.Blocked(artifact.Id, "unsupported delete kind");
            }
            catch (RequestFailedException ex) when (PrivateNetworkProvenance.IsMissing(ex))
            {
                if (await Read(artifact, ct) != null) throw;
            }
        }

        public Task ValidatePublicTarget(string targetId, CancellationToken ct) => PublicTarget(targetId, false, ct);
        public Task EnablePublicTarget(string targetId, CancellationToken ct) => PublicTarget(targetId, true, ct);

        private async Task PublicTarget(string targetId, bool apply, CancellationToken ct)
        {
            Scope(targetId);
            if (!_manifest.Artifacts.Any(a => a.CreatedByInstaller && a.CreationConfirmed && !a.Removed &&
                (a.Kind == PrivateNetworkProvenance.Endpoint || a.Kind == PrivateNetworkProvenance.Integration ||
                 a.Kind == PrivateNetworkProvenance.VirtualNetworkRule) && PrivateNetworkProvenance.Same(a.TargetId, targetId)))
                throw PrivateNetworkProvenance.Blocked(targetId, "public access has no confirmed owned-network transition authority");
            var id = new ResourceIdentifier(targetId);
            var readSucceeded = false;
            try
            {
                switch (id.ResourceType.ToString().ToLowerInvariant())
                {
                    case "microsoft.sql/servers":
                        var sql = (await _group.GetSqlServerAsync(id.Name, cancellationToken: ct)).Value;
                        readSucceeded = true;
                        if (apply) await sql.UpdateAsync(WaitUntil.Completed, new SqlServerPatch { PublicNetworkAccess = ServerNetworkAccessFlag.Enabled }, ct);
                        break;
                    case "microsoft.storage/storageaccounts":
                        var storage = (await _group.GetStorageAccountAsync(id.Name, cancellationToken: ct)).Value;
                        readSucceeded = true;
                        if (storage.Data.NetworkRuleSet?.DefaultAction == StorageNetworkDefaultAction.Deny)
                            throw PrivateNetworkProvenance.Blocked(targetId, "Storage has an operator-managed Deny firewall; approve public firewall access manually first");
                        if (apply) await storage.UpdateAsync(new StorageAccountPatch { PublicNetworkAccess = StoragePublicNetworkAccess.Enabled }, ct);
                        break;
                    case "microsoft.keyvault/vaults":
                        var vault = (await _group.GetKeyVaultAsync(id.Name, ct)).Value;
                        readSucceeded = true;
                        if (apply) await vault.UpdateAsync(new KeyVaultPatch { Properties = new KeyVaultPatchProperties { PublicNetworkAccess = "Enabled" } }, ct);
                        break;
                    case "microsoft.servicebus/namespaces":
                        var bus = (await _group.GetServiceBusNamespaceAsync(id.Name, ct)).Value;
                        readSucceeded = true;
                        if (apply)
                        {
                            bus.Data.PublicNetworkAccess = ServiceBusPublicNetworkAccess.Enabled;
                            await _group.GetServiceBusNamespaces().CreateOrUpdateAsync(WaitUntil.Completed, id.Name, bus.Data, ct);
                        }
                        break;
                    case "microsoft.web/sites":
                        var site = (await _group.GetWebSiteAsync(id.Name, ct)).Value;
                        readSucceeded = true;
                        if (apply) await site.UpdateAsync(new SitePatchInfo { PublicNetworkAccess = "Enabled" }, ct);
                        break;
                    case "microsoft.cognitiveservices/accounts":
                        var ai = (await _group.GetCognitiveServicesAccountAsync(id.Name, ct)).Value;
                        readSucceeded = true;
                        if (apply)
                        {
                            ai.Data.Properties.PublicNetworkAccess = ServiceAccountPublicNetworkAccess.Enabled;
                            await ai.UpdateAsync(WaitUntil.Completed, ai.Data, ct);
                        }
                        break;
                    case "microsoft.automation/automationaccounts":
                        var automation = (await _group.GetAutomationAccountAsync(id.Name, ct)).Value;
                        readSucceeded = true;
                        if (apply) await automation.UpdateAsync(new AutomationAccountPatch { IsPublicNetworkAccessAllowed = true }, ct);
                        break;
                    default:
                        throw PrivateNetworkProvenance.Blocked(targetId, "unsupported public-network target type");
                }
                if (apply) await VerifyPublicTarget(id, ct);
            }
            catch (RequestFailedException ex) when (!readSucceeded && PrivateNetworkProvenance.IsMissing(ex))
            {
                // A deleted target has no public access setting to change; the ordinary install task
                // will recreate configured resources publicly after owned stale networking is gone.
            }
        }

        private async Task VerifyPublicTarget(ResourceIdentifier id, CancellationToken ct)
        {
            bool enabled;
            switch (id.ResourceType.ToString().ToLowerInvariant())
            {
                case "microsoft.sql/servers":
                    enabled = (await _group.GetSqlServerAsync(id.Name, cancellationToken: ct)).Value.Data.PublicNetworkAccess == ServerNetworkAccessFlag.Enabled;
                    break;
                case "microsoft.storage/storageaccounts":
                    enabled = (await _group.GetStorageAccountAsync(id.Name, cancellationToken: ct)).Value.Data.PublicNetworkAccess == StoragePublicNetworkAccess.Enabled;
                    break;
                case "microsoft.keyvault/vaults":
                    enabled = PrivateNetworkProvenance.Same((await _group.GetKeyVaultAsync(id.Name, ct)).Value.Data.Properties.PublicNetworkAccess, "Enabled");
                    break;
                case "microsoft.servicebus/namespaces":
                    enabled = (await _group.GetServiceBusNamespaceAsync(id.Name, ct)).Value.Data.PublicNetworkAccess == ServiceBusPublicNetworkAccess.Enabled;
                    break;
                case "microsoft.web/sites":
                    enabled = PrivateNetworkProvenance.Same((await _group.GetWebSiteAsync(id.Name, ct)).Value.Data.PublicNetworkAccess, "Enabled");
                    break;
                case "microsoft.cognitiveservices/accounts":
                    enabled = (await _group.GetCognitiveServicesAccountAsync(id.Name, ct)).Value.Data.Properties.PublicNetworkAccess == ServiceAccountPublicNetworkAccess.Enabled;
                    break;
                case "microsoft.automation/automationaccounts":
                    enabled = (await _group.GetAutomationAccountAsync(id.Name, ct)).Value.Data.IsPublicNetworkAccessAllowed == true;
                    break;
                default: enabled = false; break;
            }
            if (!enabled) throw PrivateNetworkProvenance.Blocked(id.ToString(),
                "Azure did not confirm public network access enabled; private networking has been retained");
        }

        private void Scope(string id)
        {
            if (id == null || !id.StartsWith(_group.Id + "/providers/", StringComparison.OrdinalIgnoreCase))
                throw PrivateNetworkProvenance.Blocked(id, "resource is outside the manifest's resource group");
        }
    }
}
