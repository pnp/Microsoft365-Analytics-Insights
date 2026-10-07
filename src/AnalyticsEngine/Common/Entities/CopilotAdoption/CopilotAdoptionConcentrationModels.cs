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

        // ----- Agent breadth and depth (#646) and builders (#647) -----------------------------------
        // Counted over the department's active Copilot users SEEN IN THE AUDIT LOG: active seat holders
        // scored from the audit import, plus every unlicensed user. A seat holder scored from Microsoft's
        // usage report is left out of both sides, because that report carries no agent identity - counting
        // them would read as "active and never used an agent". Agents are those
        // CopilotAgentFigureScope includes; see CopilotAdoptionSummary.AgentFiguresScope.

        /// <summary>The department's active Copilot users seen in the audit log - the denominator below.</summary>
        [JsonProperty("agentActiveUsers")]
        public int? AgentActiveUsers { get; set; }

        /// <summary>Of <see cref="AgentActiveUsers"/>, how many used at least one agent.</summary>
        [JsonProperty("agentUsers")]
        public int? AgentUsers { get; set; }

        /// <summary>Breadth: <see cref="AgentUsers"/> as a share of <see cref="AgentActiveUsers"/>, 0-100.</summary>
        [JsonProperty("agentUserPct")]
        public double? AgentUserPct { get; set; }

        /// <summary>Distinct agents those people used in the period.</summary>
        [JsonProperty("distinctAgents")]
        public int? DistinctAgents { get; set; }

        /// <summary>Depth: <see cref="DistinctAgents"/> per 100 of <see cref="AgentActiveUsers"/>.</summary>
        [JsonProperty("agentsPer100ActiveUsers")]
        public double? AgentsPer100ActiveUsers { get; set; }

        /// <summary>Their interactions with those agents in the period.</summary>
        [JsonProperty("agentInteractions")]
        public long? AgentInteractions { get; set; }

        /// <summary>Depth: <see cref="AgentInteractions"/> per agent in <see cref="DistinctAgents"/>.</summary>
        [JsonProperty("interactionsPerActiveAgent")]
        public double? InteractionsPerActiveAgent { get; set; }

        /// <summary>
        /// People in this department who created, published or shared a Copilot Studio agent in the period
        /// (#647). Null when no Copilot Studio authoring events have been imported, so a missing feed is
        /// not read as "nobody here builds agents".
        /// </summary>
        [JsonProperty("agentBuilders")]
        public int? AgentBuilders { get; set; }
    }
}
