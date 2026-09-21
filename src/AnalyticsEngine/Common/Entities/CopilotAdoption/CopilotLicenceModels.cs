using Newtonsoft.Json;
using System;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>A row of <c>dbo.license_types</c> plus how many users hold it.</summary>
    public class LicenceTypeRow
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public string SkuPartNumber { get; set; }

        public int AssignedUsers { get; set; }

        public int? PurchasedUnits { get; set; }

        public DateTime? PurchasedUnitsRefreshedUtc { get; set; }
    }

    /// <summary>A licence type and whether the tool counted it as a Microsoft 365 Copilot seat.</summary>
    public class LicenceTypeClassification
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("skuPartNumber")]
        public string SkuPartNumber { get; set; }

        [JsonProperty("assignedUsers")]
        public int AssignedUsers { get; set; }

        /// <summary>Purchased seats from Graph subscribedSkus.prepaidUnits, or null when tenant SKU inventory is unavailable.</summary>
        [JsonProperty("purchasedUnits")]
        public int? PurchasedUnits { get; set; }

        [JsonProperty("unassignedUnits")]
        public int? UnassignedUnits { get; set; }

        [JsonProperty("assignedIdleUsers")]
        public int AssignedIdleUsers { get; set; }

        [JsonProperty("purchasedUnitsRefreshedUtc")]
        public DateTime? PurchasedUnitsRefreshedUtc { get; set; }

        [JsonProperty("isCopilotSeat")]
        public bool IsCopilotSeat { get; set; }

        /// <summary>
        /// A copy, so a narrowed view can recompute the per-SKU assigned and idle counts without
        /// writing them back into the cached tenant-wide analysis every other caller is reading.
        /// </summary>
        public LicenceTypeClassification Clone()
        {
            return new LicenceTypeClassification
            {
                Id = Id,
                Name = Name,
                SkuPartNumber = SkuPartNumber,
                AssignedUsers = AssignedUsers,
                PurchasedUnits = PurchasedUnits,
                UnassignedUnits = UnassignedUnits,
                AssignedIdleUsers = AssignedIdleUsers,
                PurchasedUnitsRefreshedUtc = PurchasedUnitsRefreshedUtc,
                IsCopilotSeat = IsCopilotSeat,
            };
        }
    }
}
