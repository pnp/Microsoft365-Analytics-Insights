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
    public static class AgentCostDimensions
    {
        public const string Agent = "agent";
        public const string Environment = "environment";
        public const string Harness = "harness";
        public const string Feature = "feature";
        public const string Model = "model";
        public const string Tool = "tool";
        public const string KnowledgeSource = "knowledge";
        public const string Channel = "channel";

        public static readonly IReadOnlyList<string> All = new List<string>
        {
            Agent, Environment, Harness, Feature, Model, Tool, KnowledgeSource, Channel
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

        public static readonly IReadOnlyList<string> All = new List<string>
        {
            Meter, Service, MeterCategory, Resource, ResourceGroup, Subscription
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
        public string LlmModel { get; set; }

        /// <summary>
        /// The remaining billing dimensions. Present so every dimension the report can PIVOT by can also be
        /// FILTERED by - spotting that one tool or channel is driving the spend is only half an answer if you
        /// then cannot narrow the page to it.
        /// </summary>
        public string ToolInvoked { get; set; }
        public string KnowledgeSources { get; set; }
        public string ChannelId { get; set; }

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
        public bool CopilotStudioCreditsEnabled { get; set; }
        public bool AzureCostsEnabled { get; set; }

        /// <summary>True once any row exists, so the UI can tell "off" from "on but not yet run".</summary>
        public bool HasCopilotStudioCreditData { get; set; }
        public bool HasAzureCostData { get; set; }

        /// <summary>
        /// True when an import has completed without error at least once. Distinguishes "has not run yet"
        /// from "ran fine, this tenant simply has nothing to bill" - which otherwise look identical and would
        /// leave a tenant with no Copilot Studio agents staring at a "not imported yet" notice for ever.
        /// </summary>
        public bool CopilotStudioCreditsHasRunCleanly { get; set; }
        public bool AzureCostsHaveRunCleanly { get; set; }

        /// <summary>
        /// True once any per-user credit row exists. Separate from the per-agent flag because the per-user
        /// entitlement routes are newer and a tenant can legitimately have one without the other.
        /// </summary>
        public bool HasPerUserCreditData { get; set; }

        public DateTime? CopilotStudioCreditsLastImportUtc { get; set; }
        public DateTime? AzureCostsLastImportUtc { get; set; }

        /// <summary>Last recorded error per import, so a silent authorisation failure is visible on the page.</summary>
        public string CopilotStudioCreditsLastError { get; set; }
        public string AzureCostsLastError { get; set; }

        public DateTime? EarliestUsageDate { get; set; }
        public DateTime? LatestUsageDate { get; set; }

        /// <summary>Plain-English notes for the admin reading the page.</summary>
        public List<string> Messages { get; set; } = new List<string>();
    }

    /// <summary>Headline figures for the selected window.</summary>
    public class AgentCostSummary
    {
        public decimal BilledCredits { get; set; }
        public decimal NonBilledCredits { get; set; }
        public int DistinctAgents { get; set; }
        public int DistinctEnvironments { get; set; }
        public int DaysWithUsage { get; set; }

        /// <summary>
        /// The largest distinct-user count seen on any single slice. NOT a tenant-wide unique-user total:
        /// the API reports a count per slice, and the same person appears in many slices, so these cannot be
        /// added up. Presented as "busiest slice" rather than "users".
        /// </summary>
        public int? PeakDistinctUsersOnASlice { get; set; }

        /// <summary>Credits that could not be attributed to a known harness.</summary>
        public decimal UnclassifiedHarnessCredits { get; set; }

        public CopilotCapacitySnapshot Capacity { get; set; }

        /// <summary>Azure spend in the window, one entry per billing currency - never summed across them.</summary>
        public List<AzureCostByCurrency> AzureCost { get; set; } = new List<AzureCostByCurrency>();
    }

    public class AzureCostByCurrency
    {
        public string Currency { get; set; }
        public decimal Cost { get; set; }

        /// <summary>True when any contributing row was still a provisional (pre-invoice) estimate.</summary>
        public bool IncludesEstimates { get; set; }
    }

    /// <summary>The most recent tenant Copilot Credits entitlement snapshot.</summary>
    public class CopilotCapacitySnapshot
    {
        public DateTime SnapshotUtc { get; set; }
        public DateTime? ConsumptionAsOf { get; set; }
        public decimal? Entitled { get; set; }
        public decimal? Consumed { get; set; }
        public string ConsumptionType { get; set; }
        public decimal? Allocated { get; set; }
        public decimal? Available { get; set; }
        public decimal? PayAsYouGoConsumed { get; set; }
        public string Status { get; set; }
    }

    /// <summary>One day of the credit trend.</summary>
    public class AgentCostDailyPoint
    {
        public DateTime Date { get; set; }
        public decimal BilledCredits { get; set; }
        public decimal NonBilledCredits { get; set; }
    }

    /// <summary>One grouped row of a credit breakdown.</summary>
    public class AgentCostBreakdownRow
    {
        /// <summary>The grouping value, or null when the source reported none for this dimension.</summary>
        public string Key { get; set; }

        /// <summary>Human-readable label - the agent/environment name where one is known, else the id.</summary>
        public string Label { get; set; }

        public decimal BilledCredits { get; set; }
        public decimal NonBilledCredits { get; set; }

        /// <summary>How many distinct usage days this group was active on.</summary>
        public int ActiveDays { get; set; }

        /// <summary>Peak distinct-user count on any one slice in this group. See the summary note.</summary>
        public int? PeakDistinctUsers { get; set; }
    }

    /// <summary>One fully-granular credit row - the deepest view the source data supports.</summary>
    public class AgentCostDetailRow
    {
        public DateTime UsageDate { get; set; }
        public string EnvironmentId { get; set; }
        public string EnvironmentName { get; set; }
        public string AgentId { get; set; }
        public string AgentName { get; set; }
        public string Harness { get; set; }
        public string FeatureName { get; set; }
        public string ChannelId { get; set; }
        public string LlmModel { get; set; }
        public string ToolInvoked { get; set; }
        public string KnowledgeSources { get; set; }
        public decimal BilledCredits { get; set; }
        public decimal? NonBilledCredits { get; set; }
        public int? DistinctUsers { get; set; }
    }

    /// <summary>A page of granular rows plus the total available.</summary>
    public class AgentCostDetailPage
    {
        public List<AgentCostDetailRow> Rows { get; set; } = new List<AgentCostDetailRow>();
        public int TotalRows { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
    }

    /// <summary>One grouped row of an Azure cost breakdown.</summary>
    public class AzureCostBreakdownRow
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public string Currency { get; set; }
        public decimal Cost { get; set; }
        public decimal? Quantity { get; set; }
        public bool IncludesEstimates { get; set; }
    }

    /// <summary>One user's billed Copilot Studio credit consumption over the window.</summary>
    public class AgentCostUserRow
    {
        /// <summary>
        /// The identifier the licensing API reported. Deliberately not resolved to a display name: nothing
        /// verifies its format, so mapping it to a person would be a guess presented as a fact.
        /// </summary>
        public string UserId { get; set; }

        public decimal BilledCredits { get; set; }

        /// <summary>How many distinct usage days this user consumed credits on.</summary>
        public int ActiveDays { get; set; }
    }

    /// <summary>
    /// The distinct values present in the loaded data, so the UI can offer filters that are guaranteed to
    /// match something rather than a hard-coded list that may be empty on this tenant.
    /// </summary>
    public class AgentCostFilterOptions
    {
        public List<AgentCostFilterOption> Agents { get; set; } = new List<AgentCostFilterOption>();
        public List<AgentCostFilterOption> Environments { get; set; } = new List<AgentCostFilterOption>();
        public List<string> Harnesses { get; set; } = new List<string>();
        public List<string> Features { get; set; } = new List<string>();
        public List<string> Models { get; set; } = new List<string>();
        public List<string> Tools { get; set; } = new List<string>();
        public List<string> KnowledgeSources { get; set; } = new List<string>();
        public List<string> Channels { get; set; } = new List<string>();
    }

    public class AgentCostFilterOption
    {
        public string Id { get; set; }
        public string Label { get; set; }
    }
}
