using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>Raw per-user usage for someone with no Copilot seat.</summary>
    public class UnlicensedUsageQueryRow
    {
        public int UserId { get; set; }
        public string Department { get; set; }
        public long Interactions { get; set; }
        public int ActiveDays { get; set; }
        public int AppsUsed { get; set; }
        public int AgentsUsed { get; set; }
        public DateTime? LastInteractionUtc { get; set; }
    }

    /// <summary>
    /// Unlicensed Copilot Chat treated as a population in its own right, not merely as a pool of
    /// licence candidates.
    ///
    /// Worth reporting separately because it is the one Copilot population Microsoft's own tooling
    /// cannot see at all, and because its shape answers a different question: not "who should get a
    /// seat" but "how much Copilot is this organisation already doing without paying for it".
    /// </summary>
    public class UnlicensedPopulationSummary
    {
        [JsonProperty("activeUsers")]
        public int ActiveUsers { get; set; }

        [JsonProperty("interactions")]
        public long Interactions { get; set; }

        /// <summary>Mean interactions per active unlicensed user, normalised to a month.</summary>
        [JsonProperty("interactionsPerUserPerMonth")]
        public double InteractionsPerUserPerMonth { get; set; }

        [JsonProperty("agentUsers")]
        public int AgentUsers { get; set; }

        /// <summary>The same habit buckets the licensed population uses, so the two are comparable.</summary>
        [JsonProperty("habitBuckets")]
        public List<AdoptionHabitBucket> HabitBuckets { get; set; } = new List<AdoptionHabitBucket>();

        [JsonProperty("usageByApp")]
        public List<AdoptionCategory> UsageByApp { get; set; } = new List<AdoptionCategory>();

        [JsonProperty("usageByDepartment")]
        public List<AdoptionCategory> UsageByDepartment { get; set; } = new List<AdoptionCategory>();

        /// <summary>True when the row cap was hit, so the figures are a floor rather than a total.</summary>
        [JsonProperty("truncated")]
        public bool Truncated { get; set; }
    }
}
