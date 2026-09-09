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

        /// <summary>
        /// The biggest per-user credit consumers in the window, highest first.
        /// </summary>
        /// <remarks>
        /// Reads <c>copilot_studio_credit_user_daily</c>, which comes from Microsoft's per-user entitlement
        /// routes - a different endpoint from the per-agent table, not a breakdown of it. The two totals are
        /// therefore not guaranteed to reconcile exactly and must not be presented as if they were.
        /// </remarks>
        Task<System.Collections.Generic.List<AgentCostUserRow>> GetTopUsersAsync(AgentCostQuery query, int top);
    }
}
