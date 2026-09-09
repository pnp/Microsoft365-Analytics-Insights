using System.Threading.Tasks;

namespace Common.Entities.AgentCosts
{
    /// <summary>
    /// Read side of the agent-cost reporting data.
    ///
    /// An interface so the report page's shaping rules - windowing, grouping, the
    /// "peak users, never summed" treatment - can be tested against in-memory data, and so the SQL
    /// implementation can be swapped without touching the controller.
    /// </summary>
    public interface IAgentCostReportStore
    {
        Task<AgentCostAvailability> GetAvailabilityAsync(bool copilotStudioCreditsEnabled, bool azureCostsEnabled);

        Task<AgentCostSummary> GetSummaryAsync(AgentCostQuery query);

        Task<System.Collections.Generic.List<AgentCostDailyPoint>> GetDailyTrendAsync(AgentCostQuery query);

        /// <summary>
        /// Credits grouped by one of the <see cref="AgentCostDimensions"/> values, biggest spender first.
        /// </summary>
        Task<System.Collections.Generic.List<AgentCostBreakdownRow>> GetBreakdownAsync(AgentCostQuery query, string dimension, int top);

        /// <summary>The fully-granular rows, paged. This is the deepest view the source supports.</summary>
        Task<AgentCostDetailPage> GetDetailAsync(AgentCostQuery query);

        Task<System.Collections.Generic.List<AzureCostBreakdownRow>> GetAzureBreakdownAsync(AgentCostQuery query, string dimension, int top);

        Task<AgentCostFilterOptions> GetFilterOptionsAsync(AgentCostQuery query);
    }
}
