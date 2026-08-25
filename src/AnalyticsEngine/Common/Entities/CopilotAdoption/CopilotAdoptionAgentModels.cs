using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>What to do about an agent, worst first. Numeric values are stable for the UI.</summary>
    public enum AgentHealth
    {
        /// <summary>Dormant long enough that it is almost certainly abandoned.</summary>
        Retire = 0,

        /// <summary>Either going quiet, or being used by too few people to call it adopted.</summary>
        Review = 1,

        /// <summary>Too recently introduced to judge - deliberately exempt from review.</summary>
        New = 2,

        /// <summary>Current and genuinely adopted.</summary>
        Keep = 3,
    }

    /// <summary>Raw per-agent usage straight from the audit log.</summary>
    public class AgentUsageQueryRow
    {
        public int AgentId { get; set; }
        public string Name { get; set; }
        public string AgentKey { get; set; }
        public bool IsCustomAgent { get; set; }

        /// <summary>Interactions across the whole inventory history window.</summary>
        public long Interactions { get; set; }

        /// <summary>Interactions inside the selected reporting period only.</summary>
        public long WindowInteractions { get; set; }

        public int Users { get; set; }
        public int LicensedUsers { get; set; }
        public int ActiveDays { get; set; }
        public int AppsUsed { get; set; }
        public DateTime? FirstUsedUtc { get; set; }
        public DateTime? LastUsedUtc { get; set; }
    }

    /// <summary>
    /// One Copilot agent with the figures an inventory review needs, plus the verdict on it.
    ///
    /// Agents are counted across the whole tenant, licensed and unlicensed: an agent's worth to the
    /// organisation does not depend on the licence status of the people using it. The licensed share
    /// is carried separately so the two populations can still be told apart.
    /// </summary>
    public class AgentUsageRow
    {
        [JsonProperty("agentId")]
        public int AgentId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>The agent's identifier from the audit payload, e.g. a first-party Copilot agent id.</summary>
        [JsonProperty("agentKey")]
        public string AgentKey { get; set; }

        /// <summary>True for a customer-built agent, false for one Microsoft ships.</summary>
        [JsonProperty("isCustomAgent")]
        public bool IsCustomAgent { get; set; }

        [JsonProperty("interactions")]
        public long Interactions { get; set; }

        /// <summary>
        /// Interactions inside the selected reporting period, as opposed to across the whole inventory
        /// history. Carried separately because the two answer different questions, and dividing one by
        /// the other's denominator inflates the result by the ratio of the two windows.
        /// </summary>
        [JsonProperty("windowInteractions")]
        public long WindowInteractions { get; set; }

        [JsonProperty("users")]
        public int Users { get; set; }

        [JsonProperty("licensedUsers")]
        public int LicensedUsers { get; set; }

        [JsonProperty("activeDays")]
        public int ActiveDays { get; set; }

        /// <summary>
        /// Distinct Copilot surfaces the agent was invoked from - its versatility. An agent that only
        /// ever runs in one host is doing a narrower job than its interaction count suggests.
        /// </summary>
        [JsonProperty("appsUsed")]
        public int AppsUsed { get; set; }

        [JsonProperty("interactionsPerUser")]
        public double InteractionsPerUser { get; set; }

        [JsonProperty("firstUsedUtc")]
        public DateTime? FirstUsedUtc { get; set; }

        [JsonProperty("lastUsedUtc")]
        public DateTime? LastUsedUtc { get; set; }

        [JsonProperty("daysSinceLastUse")]
        public int? DaysSinceLastUse { get; set; }

        [JsonProperty("health")]
        public AgentHealth Health { get; set; }

        [JsonProperty("healthName")]
        public string HealthName { get; set; }

        /// <summary>Why this agent got this verdict, in plain English.</summary>
        [JsonProperty("healthReason")]
        public string HealthReason { get; set; }
    }

    /// <summary>The agent estate at a glance.</summary>
    public class AgentEstateSummary
    {
        /// <summary>
        /// How many days of history the inventory actually read, so the UI and the workbook can state
        /// it rather than recomputing the rule. Shorter than the analysis history window by design -
        /// see <see cref="CopilotAdoptionOptions.AgentHistoryDays"/>.
        /// </summary>
        [JsonProperty("historyDays")]
        public int HistoryDays { get; set; }

        /// <summary>Agents used at least once inside the reporting period.</summary>
        [JsonProperty("activeAgents")]
        public int ActiveAgents { get; set; }

        /// <summary>Agents seen in the longer history window, whether or not they were used in the period.</summary>
        [JsonProperty("knownAgents")]
        public int KnownAgents { get; set; }

        [JsonProperty("customAgents")]
        public int CustomAgents { get; set; }

        /// <summary>Distinct people who used any agent in the period.</summary>
        [JsonProperty("agentUsers")]
        public int AgentUsers { get; set; }

        [JsonProperty("licensedAgentUsers")]
        public int LicensedAgentUsers { get; set; }

        [JsonProperty("agentInteractions")]
        public long AgentInteractions { get; set; }

        [JsonProperty("interactionsPerAgentUser")]
        public double InteractionsPerAgentUser { get; set; }

        /// <summary>The agent the most people use - the one whose retirement would be felt.</summary>
        [JsonProperty("mostPopularAgent")]
        public string MostPopularAgent { get; set; }

        /// <summary>The agent used across the most Copilot surfaces.</summary>
        [JsonProperty("mostVersatileAgent")]
        public string MostVersatileAgent { get; set; }

        /// <summary>Counts by <see cref="AgentHealth"/>, so the size of an inventory clean-up is visible.</summary>
        [JsonProperty("healthBreakdown")]
        public List<AdoptionCategory> HealthBreakdown { get; set; } = new List<AdoptionCategory>();

        /// <summary>Agent interactions by department.</summary>
        [JsonProperty("usageByDepartment")]
        public List<AdoptionCategory> UsageByDepartment { get; set; } = new List<AdoptionCategory>();

        /// <summary>Interactions per agent, for the inventory treemap.</summary>
        [JsonProperty("usageByAgent")]
        public List<AdoptionCategory> UsageByAgent { get; set; } = new List<AdoptionCategory>();

        /// <summary>
        /// The agents themselves. Returned inline rather than behind a paged endpoint because the
        /// inventory is capped at a few hundred rows - an agent estate is nothing like the size of a
        /// user population, and a second round trip would buy nothing.
        /// </summary>
        [JsonProperty("agents")]
        public List<AgentUsageRow> Agents { get; set; } = new List<AgentUsageRow>();
    }
}
