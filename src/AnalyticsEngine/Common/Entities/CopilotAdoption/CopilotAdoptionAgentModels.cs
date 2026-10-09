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

        /// <summary>
        /// Distinct people who used this agent inside the reporting period - the denominator of
        /// <see cref="HomeDepartmentSharePct"/>. Not the same as <see cref="Users"/>, which counts the
        /// inventory's longer history window. Null when the reach query did not run or failed (#647).
        /// </summary>
        [JsonProperty("windowUsers")]
        public int? WindowUsers { get; set; }

        /// <summary>
        /// How many departments had at least one person using this agent inside the reporting period
        /// (#647). People with no department are not a department, so they are not counted here, but they
        /// are in <see cref="WindowUsers"/>. Null when the reach query did not run or failed.
        /// </summary>
        [JsonProperty("departments")]
        public int? Departments { get; set; }

        /// <summary>
        /// The department with the most of this agent's users in the period - the team it most likely
        /// came from. Left null, for privacy, when that department contributed fewer than
        /// <see cref="CopilotAdoptionOptions.MinSeatsPerSegment"/> of them, and when nobody who used it has
        /// a department. Tenant data: shown as stored, never translated.
        /// </summary>
        [JsonProperty("homeDepartment")]
        public string HomeDepartment { get; set; }

        /// <summary>
        /// The share of <see cref="WindowUsers"/> who are in the home department, 0-100. Published even when
        /// the department's name is withheld: a share names nobody, and it is what says whether the agent
        /// has spread beyond the team that made it.
        /// </summary>
        [JsonProperty("homeDepartmentSharePct")]
        public double? HomeDepartmentSharePct { get; set; }
    }

    /// <summary>
    /// One (agent, person) pair from the reporting period, with the person's department id - the one grain
    /// the agent breadth, depth and reach figures are all derived from (#646, #647).
    /// </summary>
    /// <remarks>
    /// Held as rows rather than aggregated in SQL so the agent-inclusion rule
    /// (<see cref="CopilotAgentFigureScope"/>) is applied in exactly one place in C#, and so a filtered view
    /// can narrow it person by person like every other per-person list. Internal: never serialised.
    /// </remarks>
    public class AgentReachRow
    {
        /// <summary><c>copilot_agents.id</c>, the same key as <see cref="AgentUsageRow.AgentId"/>.</summary>
        public int AgentId { get; set; }

        public int UserId { get; set; }

        /// <summary><c>users.department_id</c>; null for someone with no department.</summary>
        public int? DepartmentId { get; set; }

        /// <summary>This person's interactions with this agent in the period.</summary>
        public long Interactions { get; set; }
    }

    /// <summary>One <c>user_departments</c> row, to name the departments in <see cref="AgentReachRow"/>.</summary>
    public class DepartmentNameRow
    {
        public int Id { get; set; }

        public string Name { get; set; }
    }

    /// <summary>
    /// One <c>copilot_agents</c> row as stored, for <see cref="CopilotAgentClassifier.ResolveStoredOrigin"/> - so
    /// the agents in <see cref="AgentReachRow"/> get an origin whether or not the agent inventory holds them.
    /// </summary>
    public class AgentOriginRow
    {
        public int Id { get; set; }

        public string AgentKey { get; set; }

        /// <summary>The importer's stored flag: only a 1 is evidence; see <see cref="CopilotAgentClassifier.ResolveStoredOrigin"/>.</summary>
        public bool? IsCustomAgent { get; set; }
    }

    /// <summary>
    /// Someone who created, published or shared a Copilot Studio agent in the reporting period (#647).
    /// </summary>
    /// <remarks>
    /// Internal: the analysis keeps these rows only so a filtered view can narrow them like any other
    /// per-person list. Only counts ever leave the service - nothing here names a builder on screen, in an
    /// export or in telemetry.
    /// </remarks>
    public class AgentBuilderRow
    {
        public int UserId { get; set; }

        public string UserPrincipalName { get; set; }

        public string Mail { get; set; }

        public string Department { get; set; }

        /// <summary>Derived after the query, like every other per-person row; see <see cref="CopilotAdoptionEmailDomain"/>.</summary>
        public string EmailDomain { get; set; }
    }

    /// <summary>
    /// Which agents the breadth, depth and reach figures count (#646, #647) - decided in this ONE place.
    /// </summary>
    /// <remarks>
    /// <para>Customer-built agents only: the 2026 Work Trend Index counts a firm's own agents (#638), and agent
    /// origin is classified again since #639 (<see cref="CopilotAgentClassifier"/>). Microsoft's agents are left
    /// out by design. An agent of unknown origin is left out too - the classifier never guesses - and the
    /// figures say how many were, so they read as a floor rather than a total.</para>
    /// <para>The scope travels with the figures as a stable key (<see cref="CopilotAdoptionSummary.AgentFiguresScope"/>)
    /// so the portal and the workbook can say which agents a figure counts, in the reader's language, rather
    /// than leaving it to be guessed.</para>
    /// </remarks>
    public static class CopilotAgentFigureScope
    {
        /// <summary>Every agent, Microsoft's and the tenant's own. Not the current scope; kept as a known key.</summary>
        public const string AllAgents = "allAgents";

        /// <summary>The agents the tenant built itself.</summary>
        public const string CustomerBuiltAgents = "customerBuiltAgents";

        /// <summary>The scope the figures are currently computed over.</summary>
        public const string Current = CustomerBuiltAgents;

        /// <summary>
        /// Whether an agent counts towards breadth, depth and reach, from its origin key
        /// (<see cref="CopilotAgentOriginKeys"/>): <see cref="AgentUsageRow.Origin"/> for an inventory row, or
        /// <see cref="CopilotAgentClassifier.ResolveStoredOrigin"/> for a stored agent.
        /// </summary>
        public static bool Includes(string origin)
        {
            return string.Equals(origin, CopilotAgentOriginKeys.CustomerBuilt, StringComparison.Ordinal);
        }
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
        [SnapshotFactsBreakdown(typeof(AgentHealth))]
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
