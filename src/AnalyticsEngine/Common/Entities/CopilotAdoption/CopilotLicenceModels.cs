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
        /// Why <see cref="IsCopilotSeat"/> has its value: one of <see cref="CopilotSeatClassificationReasons"/>.
        /// A stable key, not text - the portal writes the sentence.
        /// </summary>
        [JsonProperty("classificationReason")]
        public string ClassificationReason { get; set; }

        /// <summary>The prefix, excluded word or name rule that matched, or null when no rule did.</summary>
        [JsonProperty("matchedRule")]
        public string MatchedRule { get; set; }

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
                ClassificationReason = ClassificationReason,
                MatchedRule = MatchedRule,
            };
        }
    }

    /// <summary>The Copilot seat decision for one licence type, and the rule that made it.</summary>
    public sealed class CopilotSeatClassification
    {
        public CopilotSeatClassification(bool isCopilotSeat, string reason, string matchedRule)
        {
            IsCopilotSeat = isCopilotSeat;
            Reason = reason;
            MatchedRule = matchedRule;
        }

        public bool IsCopilotSeat { get; }

        /// <summary>One of <see cref="CopilotSeatClassificationReasons"/>.</summary>
        public string Reason { get; }

        public string MatchedRule { get; }
    }

    /// <summary>
    /// Stable reason keys for a Copilot seat classification. Part of the API contract: the portal maps each
    /// to a translated sentence, so renaming one turns that explanation into the raw key on screen.
    /// </summary>
    public static class CopilotSeatClassificationReasons
    {
        /// <summary>The SKU part number starts with a Copilot-branded product that is not a Microsoft 365 Copilot seat.</summary>
        public const string ExcludedSkuPrefix = "excludedSkuPrefix";

        /// <summary>The SKU part number starts with a known Microsoft 365 Copilot seat (or a suite that carries one).</summary>
        public const string SeatSkuPrefix = "seatSkuPrefix";

        /// <summary>No SKU rule applied, but the product name reads as Microsoft 365 Copilot.</summary>
        public const string SeatDisplayName = "seatDisplayName";

        /// <summary>The product name mentions Copilot together with another Copilot-branded product's word.</summary>
        public const string ExcludedNameWord = "excludedNameWord";

        /// <summary>The product name mentions Copilot but not the Microsoft 365 family.</summary>
        public const string CopilotNameWithoutQualifier = "copilotNameWithoutQualifier";

        /// <summary>Nothing about the SKU or its name suggests Microsoft 365 Copilot.</summary>
        public const string NoRuleMatched = "noRuleMatched";

        /// <summary>The report's explicit seat selection counted a licence the rules would not.</summary>
        public const string ReportOverrideIncluded = "reportOverrideIncluded";

        /// <summary>The report's explicit seat selection left out a licence the rules would count.</summary>
        public const string ReportOverrideExcluded = "reportOverrideExcluded";
    }
}
