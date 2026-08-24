using Newtonsoft.Json;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// One slice of the usage distribution: how much of all Copilot activity a given cohort of users
    /// accounts for.
    ///
    /// Copilot usage is almost always a power law, and the difference between "40% adoption spread
    /// evenly" and "40% adoption where a tenth of them do most of it" is the difference between a
    /// programme that is working and one propped up by a handful of enthusiasts. An adoption
    /// percentage cannot distinguish those two; this can.
    /// </summary>
    public class AdoptionConcentrationBand
    {
        /// <summary>Cohort name, e.g. "Top 10%".</summary>
        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("users")]
        public int Users { get; set; }

        [JsonProperty("interactions")]
        public long Interactions { get; set; }

        /// <summary>This cohort's share of all interactions by active licensed users.</summary>
        [JsonProperty("sharePct")]
        public double SharePct { get; set; }

        [JsonProperty("interactionsPerUser")]
        public double InteractionsPerUser { get; set; }
    }

    /// <summary>
    /// Licensed and unlicensed Copilot use for one department, side by side.
    ///
    /// The comparison is the point: a department with idle seats <i>and</i> heavy unlicensed Chat use
    /// is not an adoption problem, it is a seat-allocation problem, and no single-population view
    /// makes that visible.
    /// </summary>
    public class AdoptionCombinedSegmentRow
    {
        [JsonProperty("segment")]
        public string Segment { get; set; }

        [JsonProperty("licensedUsers")]
        public int LicensedUsers { get; set; }

        [JsonProperty("licensedActiveUsers")]
        public int LicensedActiveUsers { get; set; }

        /// <summary>Interactions per licensed seat, normalised to a month - including idle seats.</summary>
        [JsonProperty("interactionsPerLicensedUser")]
        public double InteractionsPerLicensedUser { get; set; }

        /// <summary>Share of licensed users who used at least one agent.</summary>
        [JsonProperty("licensedAgentUserPct")]
        public double LicensedAgentUserPct { get; set; }

        [JsonProperty("unlicensedActiveUsers")]
        public int UnlicensedActiveUsers { get; set; }

        /// <summary>Interactions per active unlicensed user, normalised to a month.</summary>
        [JsonProperty("interactionsPerUnlicensedUser")]
        public double InteractionsPerUnlicensedUser { get; set; }

        [JsonProperty("unlicensedAgentUserPct")]
        public double UnlicensedAgentUserPct { get; set; }
    }
}
