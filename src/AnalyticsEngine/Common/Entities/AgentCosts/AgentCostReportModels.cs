using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Common.Entities.AgentCosts
{
    /// <summary>
    /// The dimensions a Copilot Studio credit report can be grouped by.
    ///
    /// A closed set rather than a free-text column name, because the value arrives from a query string and
    /// is used to choose a grouping expression - accepting arbitrary text there is how a reporting filter
    /// turns into an injection point.
    /// </summary>
    /// <remarks>
    /// <para><b>Deliberately smaller than the columns the database can hold.</b> A live capture of the
    /// Power Platform per-agent credit response showed its <c>metadata</c> carrying exactly
    /// <c>ResourceName</c>, <c>NonBillableQuantity</c> and <c>Users</c> - nothing else. The LLM model, the
    /// tool invoked, the knowledge sources and the channel appear in no Microsoft reference and in no
    /// observed payload, so a pivot on any of them could only ever answer "Not reported". They were removed
    /// rather than left on the page: a picker that cannot produce an answer is worse than an absent one,
    /// because it reads as "this tenant did none of that".</para>
    ///
    /// <para><c>Feature</c> and <c>Harness</c> are kept because Microsoft's REST reference does document a
    /// <c>Feature</c> key (harness is classified from it), even though the captured response omitted it on a
    /// zero-consumption agent. They are gated at runtime by
    /// <see cref="AgentCostAvailability.CreditDimensionsWithData"/> instead, so they disappear when there is
    /// nothing behind them and reappear if Microsoft ever sends them.</para>
    /// </remarks>
    public static class AgentCostDimensions
    {
        public const string Agent = "agent";
        public const string Environment = "environment";
        public const string Harness = "harness";
        public const string Feature = "feature";

        public static readonly IReadOnlyList<string> All = new List<string>
        {
            Agent, Environment, Harness, Feature
        };

        public static bool IsValid(string dimension)
        {
            if (string.IsNullOrWhiteSpace(dimension)) return false;
            foreach (var d in All)
            {
                if (string.Equals(d, dimension, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    /// <summary>Dimensions an Azure cost report can be grouped by.</summary>
    public static class AzureCostDimensions
    {
        public const string Meter = "meter";
        public const string Service = "service";
        public const string MeterCategory = "category";
        public const string Resource = "resource";
        public const string ResourceGroup = "resourcegroup";
        public const string Subscription = "subscription";

        /// <summary>
        /// The value of the tag the query was grouped by. Only populated when a <c>TagKey:</c> grouping is
        /// configured, and - like every other dimension here - only offered by the UI once there is data
        /// behind it.
        /// </summary>
        public const string Tag = "tag";

        public static readonly IReadOnlyList<string> All = new List<string>
        {
            Meter, Service, MeterCategory, Resource, ResourceGroup, Subscription, Tag
        };

        public static bool IsValid(string dimension)
        {
            if (string.IsNullOrWhiteSpace(dimension)) return false;
            foreach (var d in All)
            {
                if (string.Equals(d, dimension, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    /// <summary>Inclusive date window plus the optional dimension filters a report is narrowed by.</summary>
    public class AgentCostQuery
    {
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }

        public string AgentId { get; set; }
        public string EnvironmentId { get; set; }
        public string Harness { get; set; }
        public string FeatureName { get; set; }

        /// <summary>Free-text match against the agent name. Null or empty means no name filter.</summary>
        public string Search { get; set; }

        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
        public string Sort { get; set; } = "credits";
        public string Direction { get; set; } = "desc";
    }

    /// <summary>Whether each agent-cost import is switched on, and how it last got on.</summary>
    public class AgentCostAvailability
    {
        [JsonProperty("copilotStudioCreditsEnabled")]
        public bool CopilotStudioCreditsEnabled { get; set; }

        [JsonProperty("azureCostsEnabled")]
        public bool AzureCostsEnabled { get; set; }

        /// <summary>True once any row exists, so the UI can tell "off" from "on but not yet run".</summary>
        [JsonProperty("hasCopilotStudioCreditData")]
        public bool HasCopilotStudioCreditData { get; set; }

        [JsonProperty("hasAzureCostData")]
        public bool HasAzureCostData { get; set; }

        /// <summary>
        /// True when an import has completed without error at least once. Distinguishes "has not run yet"
        /// from "ran fine, this tenant simply has nothing to bill" - which otherwise look identical and would
        /// leave a tenant with no Copilot Studio agents staring at a "not imported yet" notice for ever.
        /// </summary>
        [JsonProperty("copilotStudioCreditsHasRunCleanly")]
        public bool CopilotStudioCreditsHasRunCleanly { get; set; }

        [JsonProperty("azureCostsHaveRunCleanly")]
        public bool AzureCostsHaveRunCleanly { get; set; }

        /// <summary>
        /// True once any per-user credit row exists. Separate from the per-agent flag because the per-user
        /// entitlement routes are newer and a tenant can legitimately have one without the other.
        /// </summary>
        [JsonProperty("hasPerUserCreditData")]
        public bool HasPerUserCreditData { get; set; }

        [JsonProperty("copilotStudioCreditsLastImportUtc")]
        public DateTime? CopilotStudioCreditsLastImportUtc { get; set; }

        [JsonProperty("azureCostsLastImportUtc")]
        public DateTime? AzureCostsLastImportUtc { get; set; }

        /// <summary>Last recorded error per import, so a silent authorisation failure is visible on the page.</summary>
        [JsonProperty("copilotStudioCreditsLastError")]
        public string CopilotStudioCreditsLastError { get; set; }

        [JsonProperty("azureCostsLastError")]
        public string AzureCostsLastError { get; set; }

        /// <summary>
        /// The per-user and capacity reads are separate runs with their own failure modes - the per-user
        /// route can be refused while the per-agent one succeeds - so they carry their own status rather
        /// than hiding behind the per-agent result.
        /// </summary>
        [JsonProperty("perUserCreditsLastImportUtc")]
        public DateTime? PerUserCreditsLastImportUtc { get; set; }

        [JsonProperty("perUserCreditsLastError")]
        public string PerUserCreditsLastError { get; set; }

        [JsonProperty("capacityLastError")]
        public string CapacityLastError { get; set; }

        [JsonProperty("earliestUsageDate")]
        public DateTime? EarliestUsageDate { get; set; }

        [JsonProperty("latestUsageDate")]
        public DateTime? LatestUsageDate { get; set; }

        /// <summary>
        /// The <see cref="AzureCostDimensions"/> values that actually have data, so the UI never offers a
        /// pivot that can only answer "Not reported".
        /// </summary>
        /// <remarks>
        /// Needed because Cost Management allows only two group-by clauses per query, so whichever
        /// dimensions are not grouped are simply never populated. Which two those are is configurable, so
        /// the answer cannot be hard-coded - it has to be read from the data.
        /// </remarks>
        [JsonProperty("azureDimensionsWithData")]
        public List<string> AzureDimensionsWithData { get; set; } = new List<string>();

        /// <summary>
        /// The <see cref="AgentCostDimensions"/> values that actually have data, so the UI never offers a
        /// credit pivot that can only answer "Not reported".
        /// </summary>
        /// <remarks>
        /// The exact counterpart of <see cref="AzureDimensionsWithData"/>, and needed for the same reason.
        /// Microsoft populates the per-agent credit <c>metadata</c> sparsely - a live capture carried only
        /// the agent name, a non-billable quantity and a user count - and which keys arrive is not something
        /// this product controls or can predict. Reading it from the stored data is the only honest answer,
        /// and it means a dimension reappears by itself if Microsoft starts sending it.
        /// </remarks>
        [JsonProperty("creditDimensionsWithData")]
        public List<string> CreditDimensionsWithData { get; set; } = new List<string>();

        /// <summary>Plain-English notes for the admin reading the page.</summary>
        [JsonProperty("messages")]
        public List<string> Messages { get; set; } = new List<string>();
    }

    /// <summary>Headline figures for the selected window.</summary>
    public class AgentCostSummary
    {
        [JsonProperty("billedCredits")]
        public decimal BilledCredits { get; set; }

        [JsonProperty("nonBilledCredits")]
        public decimal NonBilledCredits { get; set; }

        [JsonProperty("distinctAgents")]
        public int DistinctAgents { get; set; }

        [JsonProperty("distinctEnvironments")]
        public int DistinctEnvironments { get; set; }

        [JsonProperty("daysWithUsage")]
        public int DaysWithUsage { get; set; }

        /// <summary>
        /// The largest distinct-user count seen on any single slice. NOT a tenant-wide unique-user total:
        /// the API reports a count per slice, and the same person appears in many slices, so these cannot be
        /// added up. Presented as "busiest slice" rather than "users".
        /// </summary>
        [JsonProperty("peakDistinctUsersOnASlice")]
        public int? PeakDistinctUsersOnASlice { get; set; }

        /// <summary>Credits that could not be attributed to a known harness.</summary>
        [JsonProperty("unclassifiedHarnessCredits")]
        public decimal UnclassifiedHarnessCredits { get; set; }

        [JsonProperty("capacity")]
        public CopilotCapacitySnapshot Capacity { get; set; }

        /// <summary>Azure spend in the window, one entry per billing currency - never summed across them.</summary>
        [JsonProperty("azureCost")]
        public List<AzureCostByCurrency> AzureCost { get; set; } = new List<AzureCostByCurrency>();
    }

    public class AzureCostByCurrency
    {
        [JsonProperty("currency")]
        public string Currency { get; set; }

        [JsonProperty("cost")]
        public decimal Cost { get; set; }

        /// <summary>
        /// Metered quantity behind <see cref="Cost"/>, or null when it would be meaningless.
        /// </summary>
        /// <remarks>
        /// Populated ONLY when a single meter/tag combination contributes, because quantities of different
        /// meters are different units - credits, GB, hours, operations - and adding them gives a number that
        /// means nothing. Measured on a real subscription the naive sum came to "5,342,761 metered units",
        /// which is worse than showing nothing at all.
        ///
        /// When it IS populated - an import narrowed to the Copilot Credits meter - it is the credit count,
        /// and that matters: the headline credit figures on this page come from the Copilot Studio credit
        /// import (the <c>MCSMessages</c> entitlement), and Copilot Cowork does not draw on that entitlement
        /// at all. A Cowork tenant therefore has real Azure spend while those figures are legitimately zero.
        ///
        /// For an unfiltered subscription the per-tag or per-meter breakdown is where the credit count lives,
        /// scoped to one key and therefore to one unit.
        /// </remarks>
        [JsonProperty("quantity")]
        public decimal? Quantity { get; set; }

        /// <summary>True when any contributing row was still a provisional (pre-invoice) estimate.</summary>
        [JsonProperty("includesEstimates")]
        public bool IncludesEstimates { get; set; }
    }

    /// <summary>The most recent tenant Copilot Credits entitlement snapshot.</summary>
    public class CopilotCapacitySnapshot
    {
        [JsonProperty("snapshotUtc")]
        public DateTime SnapshotUtc { get; set; }

        [JsonProperty("consumptionAsOf")]
        public DateTime? ConsumptionAsOf { get; set; }

        [JsonProperty("entitled")]
        public decimal? Entitled { get; set; }

        [JsonProperty("consumed")]
        public decimal? Consumed { get; set; }

        [JsonProperty("consumptionType")]
        public string ConsumptionType { get; set; }

        [JsonProperty("allocated")]
        public decimal? Allocated { get; set; }

        [JsonProperty("available")]
        public decimal? Available { get; set; }

        [JsonProperty("payAsYouGoConsumed")]
        public decimal? PayAsYouGoConsumed { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }
    }

    /// <summary>One day of the credit trend.</summary>
    public class AgentCostDailyPoint
    {
        [JsonProperty("date")]
        public DateTime Date { get; set; }

        [JsonProperty("billedCredits")]
        public decimal BilledCredits { get; set; }

        [JsonProperty("nonBilledCredits")]
        public decimal NonBilledCredits { get; set; }
    }

    /// <summary>One grouped row of a credit breakdown.</summary>
    public class AgentCostBreakdownRow
    {
        /// <summary>The grouping value, or null when the source reported none for this dimension.</summary>
        [JsonProperty("key")]
        public string Key { get; set; }

        /// <summary>Human-readable label - the agent/environment name where one is known, else the id.</summary>
        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("billedCredits")]
        public decimal BilledCredits { get; set; }

        [JsonProperty("nonBilledCredits")]
        public decimal NonBilledCredits { get; set; }

        /// <summary>How many distinct usage days this group was active on.</summary>
        [JsonProperty("activeDays")]
        public int ActiveDays { get; set; }

        /// <summary>Peak distinct-user count on any one slice in this group. See the summary note.</summary>
        [JsonProperty("peakDistinctUsers")]
        public int? PeakDistinctUsers { get; set; }
    }

    /// <summary>One fully-granular credit row - the deepest view the source data supports.</summary>
    /// <remarks>
    /// The LLM model, tool invoked, knowledge sources and channel are deliberately absent. A live capture of
    /// the per-agent credit response carried only <c>ResourceName</c>, <c>NonBillableQuantity</c> and
    /// <c>Users</c> in its metadata; those four fields appear in no Microsoft reference and in no observed
    /// payload, so every row would have shown them blank.
    /// </remarks>
    public class AgentCostDetailRow
    {
        [JsonProperty("usageDate")]
        public DateTime UsageDate { get; set; }

        [JsonProperty("environmentId")]
        public string EnvironmentId { get; set; }

        [JsonProperty("environmentName")]
        public string EnvironmentName { get; set; }

        [JsonProperty("agentId")]
        public string AgentId { get; set; }

        [JsonProperty("agentName")]
        public string AgentName { get; set; }

        [JsonProperty("harness")]
        public string Harness { get; set; }

        [JsonProperty("featureName")]
        public string FeatureName { get; set; }

        [JsonProperty("billedCredits")]
        public decimal BilledCredits { get; set; }

        [JsonProperty("nonBilledCredits")]
        public decimal? NonBilledCredits { get; set; }

        [JsonProperty("distinctUsers")]
        public int? DistinctUsers { get; set; }
    }

    /// <summary>A page of granular rows plus the total available.</summary>
    public class AgentCostDetailPage
    {
        [JsonProperty("rows")]
        public List<AgentCostDetailRow> Rows { get; set; } = new List<AgentCostDetailRow>();

        [JsonProperty("totalRows")]
        public int TotalRows { get; set; }

        [JsonProperty("page")]
        public int Page { get; set; }

        [JsonProperty("pageSize")]
        public int PageSize { get; set; }
    }

    /// <summary>One grouped row of an Azure cost breakdown.</summary>
    public class AzureCostBreakdownRow
    {
        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("currency")]
        public string Currency { get; set; }

        [JsonProperty("cost")]
        public decimal Cost { get; set; }

        [JsonProperty("quantity")]
        public decimal? Quantity { get; set; }

        [JsonProperty("includesEstimates")]
        public bool IncludesEstimates { get; set; }
    }

    /// <summary>One user's billed Copilot Studio credit consumption over the window.</summary>
    public class AgentCostUserRow
    {
        /// <summary>
        /// The resolved <c>dbo.users</c> key, or null when the identifier could not be matched to a person.
        /// </summary>
        [JsonProperty("userId")]
        public int? UserId { get; set; }

        /// <summary>
        /// The identifier the licensing API reported - an Entra object id. Always present, so an
        /// unresolved user can still be identified and chased up.
        /// </summary>
        [JsonProperty("entraObjectId")]
        public string EntraObjectId { get; set; }

        /// <summary>
        /// The person's user principal name, or null when they are not in <c>dbo.users</c>. Null is shown
        /// as an explicitly unresolved user rather than as a blank name.
        /// </summary>
        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }

        [JsonProperty("billedCredits")]
        public decimal BilledCredits { get; set; }

        /// <summary>How many distinct usage days this user consumed credits on.</summary>
        [JsonProperty("activeDays")]
        public int ActiveDays { get; set; }
    }

    /// <summary>
    /// The distinct values present in the loaded data, so the UI can offer filters that are guaranteed to
    /// match something rather than a hard-coded list that may be empty on this tenant.
    /// </summary>
    public class AgentCostFilterOptions
    {
        [JsonProperty("agents")]
        public List<AgentCostFilterOption> Agents { get; set; } = new List<AgentCostFilterOption>();

        [JsonProperty("environments")]
        public List<AgentCostFilterOption> Environments { get; set; } = new List<AgentCostFilterOption>();

        [JsonProperty("harnesses")]
        public List<string> Harnesses { get; set; } = new List<string>();

        [JsonProperty("features")]
        public List<string> Features { get; set; } = new List<string>();
    }

    public class AgentCostFilterOption
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("label")]
        public string Label { get; set; }
    }
}
