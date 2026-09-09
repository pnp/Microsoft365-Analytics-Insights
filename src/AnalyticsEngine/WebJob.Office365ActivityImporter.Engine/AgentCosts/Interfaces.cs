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

        /// <summary>Inserts or updates the given rows. Returns how many rows were written.</summary>
        Task<int> UpsertAzureCostsAsync(IReadOnlyList<AzureCostDaily> rows);

        Task SaveCapacitySnapshotAsync(CopilotStudioCreditCapacity snapshot);

        Task SaveImportLogAsync(AgentCostImportLog log);
    }
}
