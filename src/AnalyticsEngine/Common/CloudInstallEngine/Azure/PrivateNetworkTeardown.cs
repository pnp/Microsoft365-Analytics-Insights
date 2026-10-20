using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure
{
    public sealed class PrivateNetworkSnapshot
    {
        public PrivateNetworkArtifact Artifact { get; set; }
        public IDictionary<string, string> Tags { get; set; }
        public List<PrivateNetworkArtifact> Children { get; set; } = new List<PrivateNetworkArtifact>();
    }

    /// <summary>Read operations must distinguish 404 from failed/unauthorized reads.</summary>
    public interface IPrivateNetworkCleanupClient
    {
        Task<IReadOnlyList<PrivateNetworkSnapshot>> DiscoverEndpoints(CancellationToken ct);
        Task<PrivateNetworkSnapshot> Read(PrivateNetworkArtifact artifact, CancellationToken ct);
        Task ValidatePublicTarget(string targetId, CancellationToken ct);
        Task EnablePublicTarget(string targetId, CancellationToken ct);
        Task ValidateRecords(PrivateNetworkArtifact group, bool removed, CancellationToken ct);
        Task<bool> ZoneHasOtherRecords(string zoneId, CancellationToken ct);
        Task Delete(PrivateNetworkArtifact artifact, CancellationToken ct);
    }

    public sealed class PrivateNetworkTeardown
    {
        private readonly PrivateNetworkProvenance _provenance;
        private readonly IPrivateNetworkCleanupClient _client;
        private readonly ILogger _logger;

        public PrivateNetworkTeardown(PrivateNetworkProvenance provenance, IPrivateNetworkCleanupClient client, ILogger logger)
        {
            _provenance = provenance;
            _client = client;
            _logger = logger;
        }

        public async Task Run(bool vnetEnabled, CancellationToken ct)
        {
            // Public access + VNet is deliberately supported hybrid connectivity.
            if (vnetEnabled) return;
            var manifest = _provenance.Manifest;
            var candidates = await _client.DiscoverEndpoints(ct);
            var selected = new List<PrivateNetworkArtifact>();
            foreach (var endpoint in candidates)
            {
                Validate(endpoint);
                selected.Add(_provenance.Find(endpoint.Artifact.Id));
            }
            foreach (var entry in manifest.Artifacts.Where(a => !a.Removed &&
                (a.Kind == PrivateNetworkProvenance.Endpoint || a.Kind == PrivateNetworkProvenance.ZoneGroup ||
                 a.Kind == PrivateNetworkProvenance.Integration || a.Kind == PrivateNetworkProvenance.VirtualNetworkRule)).ToList())
            {
                var live = await _client.Read(entry, ct);
                if (live != null)
                {
                    Validate(live);
                    if (!selected.Contains(entry)) selected.Add(entry);
                }
                else if (entry.CreatedByInstaller && entry.CreationConfirmed)
                {
                    // Keep group record evidence until post-delete DNS verification, even on retry.
                    if (entry.Kind == PrivateNetworkProvenance.ZoneGroup)
                        await _client.ValidateRecords(entry, removed: true, ct: ct);
                    selected.Add(entry);
                }
            }

            var groups = selected.Where(a => a.Kind == PrivateNetworkProvenance.ZoneGroup).ToList();
            foreach (var endpoint in selected.Where(a => a.Kind == PrivateNetworkProvenance.Endpoint))
            {
                var live = await _client.Read(endpoint, ct);
                if (live == null) continue;
                foreach (var child in live.Children)
                {
                    var owned = _provenance.Find(child.Id);
                    if (owned == null || !owned.CreatedByInstaller || !owned.CreationConfirmed ||
                        !PrivateNetworkProvenance.Matches(owned, child))
                        throw PrivateNetworkProvenance.Blocked(child.Id, "the endpoint contains an adopted or unrecorded DNS zone group");
                    if (!groups.Contains(owned)) groups.Add(owned);
                }
            }
            foreach (var group in groups)
            {
                if (group.Records.Count == 0 || group.Records.Any(r => !r.CreatedExclusively))
                    throw PrivateNetworkProvenance.Blocked(group.Id, "DNS record creation was not exclusively confirmed");
                var live = await _client.Read(group, ct);
                await _client.ValidateRecords(group, removed: live == null, ct: ct);
            }

            var targets = selected.Where(a => a.Kind == PrivateNetworkProvenance.Endpoint ||
                                              a.Kind == PrivateNetworkProvenance.Integration ||
                                              a.Kind == PrivateNetworkProvenance.VirtualNetworkRule)
                .Select(a => a.TargetId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var target in targets) await _client.ValidatePublicTarget(target, ct);
            foreach (var link in manifest.Artifacts.Where(a => a.Kind == PrivateNetworkProvenance.Link &&
                a.CreatedByInstaller && !a.Removed))
            {
                var live = await _client.Read(link, ct);
                if (live != null) Validate(live);
            }

            // Removed groups retain the zone association across checkpointed partial transitions.
            var sharedDns = manifest.Artifacts.Any(a => a.Kind == PrivateNetworkProvenance.Link &&
                !a.CreatedByInstaller && !a.Removed &&
                a.ZoneIds.Any(z => manifest.Artifacts.Any(g => g.Kind == PrivateNetworkProvenance.ZoneGroup &&
                    g.CreatedByInstaller && g.CreationConfirmed &&
                    g.ZoneIds.Contains(z, StringComparer.OrdinalIgnoreCase))));

            // Nothing has been enabled/deleted until EVERY candidate and its dependencies passed.
            if (selected.Count == 0 && groups.Count == 0 &&
                !manifest.Artifacts.Any(a => a.CreatedByInstaller && !a.Removed && a.Kind == PrivateNetworkProvenance.Link))
            {
                if (manifest.AppliedTopology == "PublicTransitionPending" ||
                    sharedDns && manifest.AppliedTopology != "PublicSharedDnsReviewRequired")
                {
                    manifest.AppliedTopology = sharedDns ? "PublicSharedDnsReviewRequired" : "Public";
                    _provenance.Save();
                }
                return;
            }
            manifest.AppliedTopology = "PublicTransitionPending";
            _provenance.Save();
            _logger.LogWarning($"Private-to-public preview: {selected.Count(a => a.Kind == PrivateNetworkProvenance.Endpoint)} confirmed installer-owned endpoint(s), " +
                $"{groups.Count} owned DNS zone group(s). Shared/adopted DNS zones and links will not be deleted.");

            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                await _client.EnablePublicTarget(target, ct);
            }
            foreach (var group in groups)
            {
                await Remove(group, ct);
            }
            foreach (var entry in selected.Where(a => a.Kind != PrivateNetworkProvenance.ZoneGroup))
                await Remove(entry, ct);

            foreach (var link in manifest.Artifacts.Where(a => a.Kind == PrivateNetworkProvenance.Link &&
                a.CreatedByInstaller && !a.Removed).ToList())
            {
                var live = await _client.Read(link, ct);
                if (live != null)
                {
                    Validate(live);
                    if (await _client.ZoneHasOtherRecords(link.ZoneIds.Single(), ct))
                    {
                        sharedDns = true;
                        _logger.LogWarning($"Preserving shared DNS VNet link '{link.Id}': its zone still serves other records. " +
                            "The DNS owner must verify public-name fallback from the VNet; no shared records or zone are deleted.");
                        continue;
                    }
                }
                await Remove(link, ct);
            }
            manifest.AppliedTopology = sharedDns ? "PublicSharedDnsReviewRequired" : "Public";
            _provenance.Save();
            _logger.LogInformation("Confirmed owned private endpoints and their exclusive DNS records removed. " +
                "Operator networking inputs and shared DNS resources preserved. Verify public resolution/connectivity from the VNet.");
        }

        private void Validate(PrivateNetworkSnapshot snapshot)
        {
            var expected = _provenance.Find(snapshot.Artifact.Id);
            if (expected == null || !expected.CreatedByInstaller || !expected.CreationConfirmed || expected.Removed)
                throw PrivateNetworkProvenance.Blocked(snapshot.Artifact.Id, "legacy/adopted resource has no confirmed creator provenance");
            if (!PrivateNetworkProvenance.Matches(expected, snapshot.Artifact) ||
                (snapshot.Tags != null && (!snapshot.Tags.TryGetValue(PrivateNetworkProvenance.OwnerTag, out var token) ||
                 !string.Equals(token, _provenance.Manifest.DeploymentToken, StringComparison.Ordinal))))
                throw PrivateNetworkProvenance.Blocked(expected.Id, "live ownership marker or relationships differ from the saved manifest");
        }

        private async Task Remove(PrivateNetworkArtifact entry, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // Revalidate immediately before deletion. SDK adapters must also use conditional deletion
            // where the resource API supports it, and must propagate every error except genuine 404.
            var live = await _client.Read(entry, ct);
            if (live != null)
            {
                Validate(live);
                if (entry.Kind == PrivateNetworkProvenance.Endpoint && live.Children.Count != 0)
                    throw PrivateNetworkProvenance.Blocked(entry.Id, "DNS zone groups appeared or remain on the endpoint");
                if (entry.Kind == PrivateNetworkProvenance.ZoneGroup)
                    await _client.ValidateRecords(entry, removed: false, ct: ct);
                await _client.Delete(entry, ct);
                if (entry.Kind == PrivateNetworkProvenance.ZoneGroup)
                    await _client.ValidateRecords(entry, removed: true, ct: ct);
                _logger.LogInformation($"Removed installer-owned {entry.Kind} '{entry.Id}'.");
            }
            else if (entry.Kind == PrivateNetworkProvenance.ZoneGroup)
                await _client.ValidateRecords(entry, removed: true, ct: ct);
            entry.Removed = true;
            _provenance.Save();
        }
    }
}
