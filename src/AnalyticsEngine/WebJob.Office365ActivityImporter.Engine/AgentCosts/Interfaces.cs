using Common.Entities.Entities.AgentCosts;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>
    /// One page of Copilot Studio consumption rows, plus the token needed to ask for the next one.
    /// </summary>
    public class CopilotStudioCreditPage
    {
        public CopilotStudioCreditPage(IReadOnlyList<CopilotStudioCreditRow> rows, string continuationToken)
        {
            Rows = rows ?? new List<CopilotStudioCreditRow>();
            ContinuationToken = continuationToken;
        }

        public IReadOnlyList<CopilotStudioCreditRow> Rows { get; }

        /// <summary>
        /// Token for the next page, or null/empty when this was the last one. The licensing API pages with an
        /// opaque continuation token rather than OData <c>$skip</c>.
        /// </summary>
        public string ContinuationToken { get; }

        public bool HasMore => !string.IsNullOrEmpty(ContinuationToken);
    }

    /// <summary>
    /// A single consumption row exactly as the licensing API reports it, before it is turned into a
    /// <see cref="CopilotStudioCreditDaily"/>. Separate from the EF entity so parsing can be tested without a
    /// database, and so an API field this product does not yet use is not silently lost in the mapping.
    /// </summary>
    public class CopilotStudioCreditRow
    {
        public string EnvironmentId { get; set; }
        public string ResourceId { get; set; }
        public string ResourceName { get; set; }
        public decimal Consumed { get; set; }
        public decimal? NonBillableQuantity { get; set; }

        /// <summary>Distinct user COUNT. The API never returns user identities - see the entity docs.</summary>
        public int? Users { get; set; }

        public string ChannelId { get; set; }
        public string KnowledgeSources { get; set; }
        public string ToolInvoked { get; set; }
        public string LlmModel { get; set; }
        public string FeatureName { get; set; }

        /// <summary>The usage day the row belongs to, when the API reported one.</summary>
        public DateTime? AsOfDate { get; set; }

        public DateTime? LastRefreshedDate { get; set; }
    }

    /// <summary>Tenant-wide Copilot Credits entitlement figures.</summary>
    public class CopilotStudioCapacitySnapshot
    {
        public decimal? Entitled { get; set; }
        public decimal? Consumed { get; set; }
        public string ConsumptionType { get; set; }
        public DateTime? ConsumedLastUpdatedOn { get; set; }
        public decimal? Allocated { get; set; }
        public decimal? Available { get; set; }
        public decimal? PayAsYouGoConsumed { get; set; }
        public string Status { get; set; }
    }

    /// <summary>
    /// The result of one agent-cost import run: the log row that was written, plus whether the failure (if
    /// any) was an <b>authorisation</b> one.
    /// </summary>
    /// <remarks>
    /// The distinction drives the cadence gate. A transient failure should be retried on the next cycle, so
    /// the gate is deliberately not stamped for it. An authorisation failure is different in kind: no amount
    /// of retrying fixes a missing role assignment, and retrying every cycle would send a failing request to
    /// Microsoft every few minutes, indefinitely, while filling the import log with identical rows. Those runs
    /// stamp the gate so the retry happens at the normal interval instead.
    /// </remarks>
    public class AgentCostImportOutcome
    {
        public AgentCostImportOutcome(AgentCostImportLog log, bool isAuthorisationFailure = false)
        {
            Log = log;
            IsAuthorisationFailure = isAuthorisationFailure;
        }

        public AgentCostImportLog Log { get; }

        public bool IsAuthorisationFailure { get; }

        public bool Succeeded => Log != null && string.IsNullOrEmpty(Log.Error);

        /// <summary>
        /// Failed for a reason that retrying might fix - a timeout, a 500, an incomplete read.
        /// </summary>
        /// <remarks>
        /// The complement of <see cref="IsAuthorisationFailure"/> among failures, and the reason the two are
        /// tracked separately: a run made of several parts must only back off if <b>every</b> failing part
        /// was refused. If one part is permanently unauthorised and another merely timed out, treating the
        /// whole run as "refused" would suppress the retry the timeout deserves for a full interval.
        /// </remarks>
        public bool IsTransientFailure => !Succeeded && !IsAuthorisationFailure;
    }

    /// <summary>
    /// One user's billed Copilot Studio consumption for a day, exactly as the licensing API reports it.
    /// </summary>
    public class CopilotStudioUserCreditRow
    {
        /// <summary>
        /// The user identifier as returned. Microsoft documents it only as a string - it is deliberately not
        /// assumed to be a UPN or an Entra object id, and is never joined to the user table on that guess.
        /// </summary>
        /// <remarks>
        /// Measured against a live tenant it is a GUID (an Entra object id). Recorded as an observation
        /// rather than relied on: it is not a documented contract, and there is no Entra-object-id column on
        /// <c>dbo.users</c> to join it to in any case.
        /// </remarks>
        public string UserId { get; set; }

        public string EnvironmentId { get; set; }
        public decimal Consumed { get; set; }
        public string Unit { get; set; }
        public DateTime? AsOfDate { get; set; }

        /// <summary>
        /// Usage that was NOT charged, from <c>metadata.NonBillableQuantity</c>.
        /// </summary>
        /// <remarks>
        /// Parsed but not currently persisted - <c>copilot_studio_credit_user_daily</c> has no column for it,
        /// unlike the per-agent table which does. Observed on a live tenant: a user can have
        /// <c>consumed = 0</c> with <c>NonBillableQuantity = 1</c>, i.e. they used an agent but were not
        /// charged. The per-user panel is a COST report so showing zero is defensible, but it does mean reach
        /// and cost are not the same thing there. Persisting this needs an additive migration - see the
        /// milestone issue.
        /// </remarks>
        public decimal? NonBillableQuantity { get; set; }
    }

    /// <summary>One page of per-user consumption rows, plus the token for the next.</summary>
    public class CopilotStudioUserCreditPage
    {
        public CopilotStudioUserCreditPage(IReadOnlyList<CopilotStudioUserCreditRow> rows, string continuationToken)
        {
            Rows = rows ?? new List<CopilotStudioUserCreditRow>();
            ContinuationToken = continuationToken;
        }

        public IReadOnlyList<CopilotStudioUserCreditRow> Rows { get; }
        public string ContinuationToken { get; }
        public bool HasMore => !string.IsNullOrEmpty(ContinuationToken);
    }

    /// <summary>
    /// Reads billed Copilot Studio consumption from whatever the Power Platform licensing surface happens to
    /// be. An interface so the importer's windowing, paging and mapping logic can be tested against a fake
    /// without HTTP - and so the real adapter's undocumented-response-shape handling stays in one place.
    /// </summary>
    public interface ICopilotStudioCreditSource
    {
        /// <summary>
        /// One page of per-agent consumption for the inclusive date range. Pass a null or empty
        /// <paramref name="continuationToken"/> for the first page.
        /// </summary>
        Task<CopilotStudioCreditPage> GetConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken);

        /// <summary>
        /// One page of <b>per-user</b> consumption for the given day.
        /// </summary>
        /// <remarks>
        /// A separate method rather than an overload of the per-agent read, because it is a different
        /// endpoint with a different response envelope (<c>value[].users[]</c> rather than
        /// <c>value[].resources[]</c>) and a different documented model. Returns null when the tenant's API
        /// does not offer the route - these endpoints are new (July 2026), so an older or restricted tenant
        /// legitimately has nothing here and that must be distinguishable from "no consumption".
        /// </remarks>
        Task<CopilotStudioUserCreditPage> GetUserConsumptionPageAsync(DateTime fromDate, DateTime toDate, string continuationToken);

        /// <summary>The tenant's current entitlement/consumption totals, or null if unavailable.</summary>
        Task<CopilotStudioCapacitySnapshot> GetCapacityAsync();

        /// <summary>
        /// Environment id =&gt; display name. Consumption rows carry only the id. Implementations must return
        /// an empty map rather than throwing when the lookup is unavailable: an environment name is a nicety,
        /// and losing it must not cost the customer their billing data.
        /// </summary>
        Task<IReadOnlyDictionary<string, string>> GetEnvironmentNamesAsync();
    }

    /// <summary>A daily Azure cost row as returned by Cost Management, before mapping to the EF entity.</summary>
    public class AzureCostRow
    {
        public DateTime UsageDate { get; set; }
        public string SubscriptionId { get; set; }
        public string ResourceId { get; set; }
        public string ResourceGroup { get; set; }
        public string ServiceName { get; set; }
        public string MeterCategory { get; set; }
        public string MeterSubCategory { get; set; }
        public string MeterName { get; set; }
        public decimal Cost { get; set; }
        public string Currency { get; set; }
        public decimal? Quantity { get; set; }
    }

    /// <summary>
    /// Reads daily Azure spend. An interface for the same reason as
    /// <see cref="ICopilotStudioCreditSource"/>: the Cost Management response is a columnar
    /// (<c>columns[]</c> + <c>rows[][]</c>) shape whose column order is not guaranteed, and the importer
    /// should not have to know that to be testable.
    /// </summary>
    public interface IAzureCostSource
    {
        /// <summary>
        /// Daily costs for the inclusive date range at the given Cost Management scope. Implementations
        /// follow <c>nextLink</c> paging internally and return the whole window.
        /// </summary>
        Task<IReadOnlyList<AzureCostRow>> GetDailyCostsAsync(string scope, DateTime fromDate, DateTime toDate);
    }

    /// <summary>
    /// Persists imported agent-cost data. Both fact upserts are keyed on the usage date plus a hash of the
    /// row's identifying dimensions, so re-importing a trailing window (which both sources require, because
    /// both restate history) updates rows instead of duplicating them.
    /// </summary>
    public interface IAgentCostStore
    {
        /// <summary>Inserts or updates the given rows. Returns how many rows were written.</summary>
        Task<int> UpsertCopilotStudioCreditsAsync(IReadOnlyList<CopilotStudioCreditDaily> rows);

        /// <summary>Inserts or updates the given per-user rows. Returns how many rows were written.</summary>
        Task<int> UpsertCopilotStudioUserCreditsAsync(IReadOnlyList<CopilotStudioCreditUserDaily> rows);

        /// <summary>Inserts or updates the given rows. Returns how many rows were written.</summary>
        Task<int> UpsertAzureCostsAsync(IReadOnlyList<AzureCostDaily> rows);

        /// <summary>
        /// Replaces the stored Azure costs for one scope and window with exactly what was returned, removing
        /// anything the latest query no longer reports.
        /// </summary>
        /// <remarks>
        /// A Cost Management query result is a complete snapshot of its scope and window, not a delta. The
        /// meter filter and grouping are both configurable and expected to change, so an upsert-only write
        /// would leave superseded rows behind - narrowing a filter would keep the old wider rows, and
        /// changing the grouping would store the same money twice under different hashes.
        /// </remarks>
        Task<int> ReplaceAzureCostsAsync(IReadOnlyList<AzureCostDaily> rows, string scope, DateTime? from, DateTime? to);

        Task SaveCapacitySnapshotAsync(CopilotStudioCreditCapacity snapshot);

        Task SaveImportLogAsync(AgentCostImportLog log);
    }
}
