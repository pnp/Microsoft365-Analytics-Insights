using CloudInstallEngine.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CloudInstallEngine.Azure
{
    /// <summary>
    /// Installer configuration, not an inventory of everything bearing the deployment's user tags.
    /// An intent is saved before creation; an unconfirmed intent never authorizes deletion.
    /// </summary>
    public sealed class PrivateNetworkManifest
    {
        public string DeploymentToken { get; set; } = Guid.NewGuid().ToString("N");
        public string ResourceGroupId { get; set; }
        public string AppliedTopology { get; set; } = "Unknown";
        public List<PrivateNetworkArtifact> Artifacts { get; set; } = new List<PrivateNetworkArtifact>();
    }

    public sealed class PrivateNetworkArtifact
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string TargetId { get; set; }
        public string GroupId { get; set; }
        public string SubnetId { get; set; }
        public List<string> ZoneIds { get; set; } = new List<string>();
        public List<PrivateNetworkRecord> Records { get; set; } = new List<PrivateNetworkRecord>();
        public bool CreatedByInstaller { get; set; }
        public bool CreationConfirmed { get; set; }
        public bool Removed { get; set; }
        public bool PreviousRouteAllEnabled { get; set; }
    }

    public sealed class PrivateNetworkRecord
    {
        public string ZoneId { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public List<string> Addresses { get; set; } = new List<string>();
        public bool CreatedExclusively { get; set; }
    }

    public sealed class PrivateNetworkProvenance
    {
        public const string OwnerTag = "M365Analytics-NetworkOwner";
        public const string Endpoint = "Endpoint";
        public const string Zone = "Zone";
        public const string Link = "Link";
        public const string ZoneGroup = "ZoneGroup";
        public const string Integration = "Integration";
        public const string VirtualNetworkRule = "VirtualNetworkRule";
        private readonly Action _save;

        public PrivateNetworkProvenance(PrivateNetworkManifest manifest, string resourceGroupId, Action save)
        {
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _save = save ?? throw new ArgumentNullException(nameof(save));
            if (string.IsNullOrWhiteSpace(manifest.DeploymentToken))
                throw new InstallException("The networking manifest has no ownership token. Restore the original installer configuration; do not adopt resources by name.");
            if (manifest.ResourceGroupId != null && !Same(manifest.ResourceGroupId, resourceGroupId))
                throw new InstallException("The networking manifest belongs to a different resource group. Use its original configuration.");
            manifest.ResourceGroupId = resourceGroupId;
        }

        public PrivateNetworkManifest Manifest { get; }
        public void Save() => _save();
        public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        public static bool IsMissing(global::Azure.RequestFailedException ex) => ex.Status == 404 &&
            (Same(ex.ErrorCode, "ResourceNotFound") || Same(ex.ErrorCode, "ResourceGroupNotFound") ||
             Same(ex.ErrorCode, "ParentResourceNotFound") || Same(ex.ErrorCode, "NotFound"));
        public PrivateNetworkArtifact Find(string id) => Manifest.Artifacts.SingleOrDefault(a => Same(a.Id, id));
        public bool IsOwned(string id) => Find(id) is PrivateNetworkArtifact a &&
            a.CreatedByInstaller && a.CreationConfirmed && !a.Removed;

        public void BeginCreate(PrivateNetworkArtifact artifact)
        {
            var previous = Find(artifact.Id);
            if (previous != null && !previous.Removed)
                throw Blocked(artifact.Id, "a previous creation or adopted resource already exists in the manifest");
            if (previous != null) Manifest.Artifacts.Remove(previous);
            artifact.CreatedByInstaller = true;
            artifact.CreationConfirmed = false;
            Manifest.Artifacts.Add(artifact);
            Manifest.AppliedTopology = "Pending";
            Save();
        }

        public void ConfirmCreate(string id)
        {
            var artifact = Find(id) ?? throw Blocked(id, "no durable creation intent");
            artifact.CreationConfirmed = true;
            Save();
        }

        public void ObserveMissing(string id)
        {
            var artifact = Find(id);
            if (artifact == null || artifact.Removed) return;
            artifact.Removed = true;
            Save();
        }

        public void ObserveMissingResourceGroup()
        {
            if (Manifest.Artifacts.All(a => a.Removed) && Manifest.AppliedTopology == "Unknown") return;
            foreach (var artifact in Manifest.Artifacts) artifact.Removed = true;
            Manifest.AppliedTopology = "Public";
            Save();
        }

        public void ObserveExisting(PrivateNetworkArtifact artifact, IDictionary<string, string> tags = null)
        {
            var previous = Find(artifact.Id);
            if (previous != null && !previous.Removed && previous.CreatedByInstaller)
            {
                // No tags exist on zone groups/integration. An interrupted, unconfirmed PUT for those
                // resources cannot be distinguished from an external creator and must not be adopted.
                if (!previous.CreationConfirmed || !Matches(previous, artifact) ||
                    (tags != null && (!tags.TryGetValue(OwnerTag, out var token) ||
                                     !string.Equals(token, Manifest.DeploymentToken, StringComparison.Ordinal))))
                    throw Blocked(artifact.Id, "creator provenance or resource relationships no longer match");
                return;
            }
            if (previous != null) Manifest.Artifacts.Remove(previous);
            artifact.CreatedByInstaller = false;
            artifact.CreationConfirmed = true;
            Manifest.Artifacts.Add(artifact);
            Save();
        }

        public void StampNew(IDictionary<string, string> tags)
        {
            tags[OwnerTag] = Manifest.DeploymentToken;
        }

        public static bool Matches(PrivateNetworkArtifact expected, PrivateNetworkArtifact actual) =>
            Same(expected.Id, actual.Id) && Same(expected.Kind, actual.Kind) &&
            Same(expected.TargetId, actual.TargetId) && Same(expected.GroupId, actual.GroupId) &&
            (Same(expected.SubnetId, actual.SubnetId) || expected.Kind == Integration && actual.SubnetId == null) &&
            new HashSet<string>(expected.ZoneIds, StringComparer.OrdinalIgnoreCase).SetEquals(actual.ZoneIds) &&
            (expected.Kind != ZoneGroup || SameRecords(expected.Records, actual.Records));

        private static bool SameRecords(List<PrivateNetworkRecord> expected, List<PrivateNetworkRecord> actual) =>
            expected.Count == actual.Count && expected.All(e => actual.Any(a =>
                Same(e.ZoneId, a.ZoneId) && Same(e.Name, a.Name) && Same(e.Type, a.Type) &&
                new HashSet<string>(e.Addresses, StringComparer.OrdinalIgnoreCase).SetEquals(a.Addresses)));

        public static InstallException Blocked(string id, string reason) => new InstallException(
            $"Private-to-public networking transition BLOCKED for '{id}': {reason}. " +
            "Nothing without confirmed creator provenance can be deleted automatically. " +
            "Restore the original saved installer manifest, or have the network owner review and manually remove the exact resource and its DNS dependencies outside this installer, then retry. " +
            "Use AnalyticsInstaller.exe --review-networking <saved-config.json> <new-private-report.json> for a GET-only inventory; the report is not deletion approval. " +
            "This build does not provide automatic legacy adoption. " +
            "Names and ordinary deployment tags are not ownership evidence.");
    }
}
