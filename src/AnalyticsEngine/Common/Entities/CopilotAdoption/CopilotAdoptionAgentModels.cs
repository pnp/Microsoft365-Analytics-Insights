using Common.Entities.Copilot;
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

        /// <summary>
        /// True when the importer stored <c>copilot_agents.is_custom_agent = 1</c>; a stored 0 and NULL both read
        /// as false. Only a hint for <see cref="CopilotAgentClassifier.ResolveStoredOrigin"/>, which decides the
        /// origin from <see cref="AgentKey"/> first: builds before #639 wrote the flag inconsistently.
        /// </summary>
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

        /// <summary>
        /// Who made the agent, as a stable key: <c>customerBuilt</c>, <c>microsoft</c> or <c>unknown</c> (see
        /// <see cref="CopilotAgentOriginKeys"/>). Derived when the inventory is read, from the agent's id and the
        /// flag the importer stored (<see cref="CopilotAgentClassifier.ResolveStoredOrigin"/>), so agents imported
        /// before #639 are labelled correctly without a backfill. The portal maps the key to its own text.
        /// </summary>
        [JsonProperty("origin")]
        public string Origin { get; set; } = CopilotAgentOriginKeys.Unknown;

        /// <summary>
        /// True when the agent is customer-built - the question to ask in code. Not serialised: the API carries
        /// <see cref="Origin"/>, and <see cref="IsCustomAgent"/> for older clients.
        /// </summary>
        [JsonIgnore]
        public bool IsCustomerBuilt => Origin == CopilotAgentOriginKeys.CustomerBuilt;

        /// <summary>
        /// True for a customer-built agent, false for one Microsoft ships AND for one whose origin is unknown.
        /// Kept so clients that read it keep working; new code should read <see cref="Origin"/>, which can tell
        /// "Microsoft's" from "not known".
        /// </summary>
        [JsonProperty("isCustomAgent")]
        public bool IsCustomAgent => IsCustomerBuilt;

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

        /// <summary>
        /// Agents in the inventory that are customer-built (<see cref="AgentUsageRow.Origin"/> is
        /// <c>customerBuilt</c>). Agents of unknown origin are not counted here; see
        /// <see cref="UnknownOriginAgents"/>.
        /// </summary>
        [JsonProperty("customAgents")]
        public int CustomAgents { get; set; }

        /// <summary>
        /// Agents in the inventory whose origin could not be established from the audit log, so they are
        /// counted neither as customer-built nor as Microsoft's. The size of the classification gap (#639).
        /// </summary>
        [JsonProperty("unknownOriginAgents")]
        public int UnknownOriginAgents { get; set; }

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

        /// <summary>
        /// Which agents <see cref="Growth"/> counts: one of the <see cref="AgentGrowthScopes"/> keys. A key
        /// rather than a sentence, so the portal and the workbook can each name the scope in their own words.
        /// </summary>
        [JsonProperty("growthScope")]
        public string GrowthScope { get; set; } = CopilotAdoptionAgentGrowth.Scope;

        /// <summary>
        /// The first Copilot interaction the audit log holds, or null when it holds none. A window that
        /// starts before it is not measured, so this is what explains a blank year-ago window.
        /// </summary>
        [JsonProperty("growthAuditHistoryStartUtc")]
        public DateTime? GrowthAuditHistoryStartUtc { get; set; }

        /// <summary>
        /// Agent use over <see cref="CopilotAdoptionAgentGrowth.WindowCount"/> consecutive, closed
        /// 28-day windows, most recent first, ending at the last settled day (#645). Window 0 against
        /// window 13 is the year-on-year comparison. Empty when the series was not computed.
        /// </summary>
        /// <remarks>
        /// Tenant-wide like the rest of the estate, and as of now even for a historical reporting period,
        /// like the inventory. Recomputed from the raw audit rows on every run: nothing is stored (#605).
        /// </remarks>
        [JsonProperty("growth")]
        public List<AgentGrowthWindow> Growth { get; set; } = new List<AgentGrowthWindow>();
    }

    /// <summary>
    /// One closed 28-day window of the agent growth series (#645). Every figure is null when the window
    /// was not measured - never zero, so "we could not measure this" and "nobody used an agent" stay
    /// distinguishable, which matters most in the year-ago window a growth ratio divides by.
    /// </summary>
    public class AgentGrowthWindow
    {
        /// <summary>0 for the most recent closed window, 13 for the same 28 days a year earlier.</summary>
        [JsonProperty("windowsAgo")]
        public int WindowsAgo { get; set; }

        /// <summary>The window's first day (UTC, inclusive).</summary>
        [JsonProperty("fromUtc")]
        public DateTime FromUtc { get; set; }

        /// <summary>The window's last day (UTC, inclusive).</summary>
        [JsonProperty("toUtc")]
        public DateTime ToUtc { get; set; }

        /// <summary>
        /// Agents with at least one day of user-initiated use in the window: the Work Trend Index's
        /// definition of an active agent, less its autonomous-run half, which is
        /// <see cref="CopilotStudioBilledAgents"/>.
        /// </summary>
        [JsonProperty("activeAgents")]
        public int? ActiveAgents { get; set; }

        /// <summary>Distinct people who used at least one of those agents in the window.</summary>
        [JsonProperty("agentUsers")]
        public int? AgentUsers { get; set; }

        /// <summary>Copilot audit-log interactions with those agents in the window.</summary>
        [JsonProperty("agentInteractions")]
        public long? AgentInteractions { get; set; }

        /// <summary>Interactions divided by agent users, to one decimal place. Null when nobody used an agent.</summary>
        [JsonProperty("interactionsPerAgentUser")]
        public double? InteractionsPerAgentUser { get; set; }

        /// <summary>
        /// Evidence of autonomous runs, kept apart from the user-initiated figures above and never added to
        /// them: Copilot Studio agents with billed consumption in the window, from the Power Platform
        /// billing import (<c>copilot_studio_credit_daily</c>). Billing covers conversations as well as
        /// autonomous runs, so this is evidence that agents ran, not a count of autonomous runs. Null when
        /// that import holds no rows for the window.
        /// </summary>
        [JsonProperty("copilotStudioBilledAgents")]
        public int? CopilotStudioBilledAgents { get; set; }
    }

    /// <summary>
    /// One person's use of Copilot agents in the reporting period, for the Agents tab's list of the
    /// heaviest agent users - and whether they hold a Microsoft 365 Copilot seat, because heavy agent
    /// use without one is a licence conversation and heavy use with one is an advocate.
    /// </summary>
    /// <remarks>
    /// Names a person, so it is only ever sent to a reader with the See PII permission: see
    /// <see cref="CopilotAdoptionSummary.WithoutIndividualData"/>. Every agent counts, Cowork included, as
    /// it does in the estate's own agent-user figures.
    /// </remarks>
    public class AgentUserRow
    {
        [JsonProperty("userId")]
        public int UserId { get; set; }

        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }

        [JsonProperty("mail")]
        public string Mail { get; set; }

        /// <summary>The organisation this person belongs to. Derived after the query; see <see cref="CopilotAdoptionEmailDomain"/>.</summary>
        [JsonProperty("emailDomain")]
        public string EmailDomain { get; set; }

        [JsonProperty("department")]
        public string Department { get; set; }

        [JsonProperty("jobTitle")]
        public string JobTitle { get; set; }

        /// <summary>Agent interactions in the reporting period, across every agent.</summary>
        [JsonProperty("interactions")]
        public long Interactions { get; set; }

        /// <summary>Distinct agents used in the period.</summary>
        [JsonProperty("agentsUsed")]
        public int AgentsUsed { get; set; }

        /// <summary>Distinct days with at least one agent interaction.</summary>
        [JsonProperty("activeDays")]
        public int ActiveDays { get; set; }

        [JsonProperty("lastUsedUtc")]
        public DateTime? LastUsedUtc { get; set; }

        /// <summary>The agent this person used most, or the unnamed-agent placeholder when it has no name.</summary>
        [JsonProperty("topAgentName")]
        public string TopAgentName { get; set; }

        [JsonProperty("topAgentInteractions")]
        public long TopAgentInteractions { get; set; }

        /// <summary>True when the person holds a Microsoft 365 Copilot seat today.</summary>
        [JsonProperty("holdsCopilotSeat")]
        public bool HoldsCopilotSeat { get; set; }
    }
}
