using System;
using System.Collections.Generic;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// Private operator inventory, deliberately not a PrivateNetworkManifest or deletion plan.
    /// Nothing in this report can grant creator provenance, adoption or mutation authority.
    /// </summary>
    public sealed class PrivateNetworkReview
    {
        public bool ReadOnly => true;
        public bool DeletionAuthorized => false;
        public bool PublicDnsVerified => false;
        public string ResourceGroupId { get; set; }
        public DateTime ObservedAtUtc { get; set; } = DateTime.UtcNow;
        public string ScopeLimitation => "Inventory is limited to this resource group and recorded App Service/Key Vault " +
            "relationships. References from other resource groups/subscriptions, external DNS forwarders and data-plane " +
            "policy are not exhaustively discovered. This is not an atomic snapshot; use an exclusive change window " +
            "and separately verify external/shared dependencies.";
        public string RequiredAction => "Keep this inventory private. The network owner must separately verify exact " +
            "resource ownership and dependencies against original deployment/change records. Names, current relationships " +
            "and ordinary tags do not prove who created a resource. Do not copy report entries into the saved manifest. " +
            "Legacy artifacts require separately approved manual cleanup outside the installer; this report authorizes " +
            "no deletion, public-access change, firewall change or legacy adoption. Preserve shared DNS, VNets, subnets " +
            "and resource groups. Verify DNS and data-plane access from inside the former VNet after an approved change.";
        public List<PrivateNetworkReviewItem> Items { get; } = new List<PrivateNetworkReviewItem>();
    }

    public sealed class PrivateNetworkReviewItem
    {
        public string ResourceId { get; set; }
        public string ResourceType { get; set; }
        public string Observation { get; set; }
        public string SavedEvidence { get; set; }
        public List<string> RelationshipIds { get; set; } = new List<string>();
    }
}
