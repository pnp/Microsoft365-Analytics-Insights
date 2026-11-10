using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

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

        /// <summary>
        /// Holders of this Copilot seat counted towards the reclaimable total (<c>CopilotAdoptionService.PopulateAssignedIdleBySku</c>);
        /// 0 for a licence type that is not a Copilot seat. Null only when withheld from a reader without See PII - see
        /// <see cref="AssignedIdleWithheld"/> - and never a substitute zero.
        /// </summary>
        [JsonProperty("assignedIdleUsers")]
        public int? AssignedIdleUsers { get; set; } = 0;

        /// <summary>
        /// True when <see cref="AssignedIdleUsers"/> is withheld from a reader without See PII (#844), as
        /// <c>LicenceActivitySku.Withheld</c> withholds a small licence's activity: <see cref="AssignedUsers"/> stays, but how
        /// many of those few people left their seat unused does not. <see cref="AssignedIdleWithheldReason"/> says why.
        /// </summary>
        [JsonProperty("assignedIdleWithheld")]
        public bool AssignedIdleWithheld { get; set; }

        /// <summary>
        /// Why <see cref="AssignedIdleUsers"/> is withheld: one of <see cref="SeatIdleWithheldReasons"/>, or null when it is
        /// shown. A stable key, not text - the portal writes the sentence.
        /// </summary>
        [JsonProperty("assignedIdleWithheldReason")]
        public string AssignedIdleWithheldReason { get; set; }

        /// <summary>
        /// On a row whose idle count is withheld, the fewest holders a Copilot seat needs for a reader without See PII to be
        /// given it; null otherwise.
        /// </summary>
        [JsonProperty("minimumHolders")]
        public int? MinimumHolders { get; set; }

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
                AssignedIdleWithheld = AssignedIdleWithheld,
                AssignedIdleWithheldReason = AssignedIdleWithheldReason,
                MinimumHolders = MinimumHolders,
                PurchasedUnitsRefreshedUtc = PurchasedUnitsRefreshedUtc,
                IsCopilotSeat = IsCopilotSeat,
                ClassificationReason = ClassificationReason,
                MatchedRule = MatchedRule,
            };
        }

        /// <summary>
        /// The licence types for a reader without See PII (#844): the idle count of a Copilot seat that some people, but
        /// fewer than <paramref name="minPeople"/>, hold is withheld, as Licence activity withholds a small licence's figures.
        /// Never modifies <paramref name="licences"/> or its rows; returns the same list when nothing is withheld.
        /// </summary>
        /// <remarks>
        /// <para>The Copilot seats' idle counts add up to the reclaimable headline, which stays on the page: a person holding
        /// two Copilot seats is counted under each but once in the total, so the rows overlap. Withholding a small seat's
        /// count alone would leave it as the headline less the counts shown, so the smallest other Copilot seat held by at
        /// least <paramref name="minPeople"/> people is withheld with it (<see cref="CopilotInvestment.SmallPopulations.WithholdOverlappingComplement"/>):
        /// the difference then covers at least that many people. Withholding the headline instead would take the page's
        /// main figure, and the funnel and bands that add up to it, from every reader for the sake of one table cell.</para>
        /// <para>When every Copilot seat is held by fewer people than that, each idle count is withheld and the headline is
        /// the whole seat population's own figure: a total over the reader's whole population, as every other headline is.</para>
        /// </remarks>
        internal static List<LicenceTypeClassification> WithoutSmallSeatIdleCounts(List<LicenceTypeClassification> licences, int minPeople)
        {
            if (licences == null) return null;

            var seats = licences.Where(l => l != null && l.IsCopilotSeat).ToList();
            var holders = seats.Select(l => Math.Max(0, l.AssignedUsers)).ToList();
            var small = holders.Select(h => CopilotInvestment.SmallPopulations.IsSmall(h, minPeople)).ToArray();
            if (!small.Any(s => s)) return licences;

            var withheld = (bool[])small.Clone();
            CopilotInvestment.SmallPopulations.WithholdOverlappingComplement(holders, withheld, minPeople);

            var reasons = new Dictionary<LicenceTypeClassification, string>();
            for (var i = 0; i < seats.Count; i++)
            {
                if (withheld[i]) reasons[seats[i]] = small[i] ? SeatIdleWithheldReasons.FewHolders : SeatIdleWithheldReasons.RevealsSmallerSeat;
            }

            return licences.Select(l =>
            {
                if (l == null || !reasons.TryGetValue(l, out var reason)) return l;
                var copy = l.Clone();
                copy.AssignedIdleUsers = null;
                copy.AssignedIdleWithheld = true;
                copy.AssignedIdleWithheldReason = reason;
                copy.MinimumHolders = minPeople;
                return copy;
            }).ToList();
        }
    }

    /// <summary>
    /// Why a Copilot seat's idle count is withheld from a reader without See PII. Part of the API contract: the portal maps
    /// each to a translated sentence.
    /// </summary>
    public static class SeatIdleWithheldReasons
    {
        /// <summary>Fewer people hold this seat than a reader without See PII may be given a figure about.</summary>
        public const string FewHolders = "fewHolders";

        /// <summary>Withheld with a smaller seat's: with the reclaimable total, this count would give that one back.</summary>
        public const string RevealsSmallerSeat = "revealsSmallerSeat";
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
