using System.Collections.Generic;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// The complete, scored output of one adoption analysis: the executive summary, every scored
    /// licensed user and every ranked licence candidate.
    ///
    /// Produced in a single pass and then sliced in memory, so paging, filtering, sorting and CSV
    /// export never re-run the heavy queries - and, more importantly, so the list an admin exports is
    /// guaranteed to be the same data the summary on screen was calculated from. A CSV that quietly
    /// disagrees with the chart above it is worse than no CSV.
    /// </summary>
    public class CopilotAdoptionAnalysis
    {
        public CopilotAdoptionSummary Summary { get; set; } = new CopilotAdoptionSummary();

        public List<LicensedUserAdoptionRow> LicensedUsers { get; set; } = new List<LicensedUserAdoptionRow>();

        public List<LicenceOpportunityRow> Opportunities { get; set; } = new List<LicenceOpportunityRow>();

        /// <summary>Every Copilot agent seen in the history window, with its health verdict.</summary>
        public List<AgentUsageRow> Agents { get; set; } = new List<AgentUsageRow>();

        /// <summary>
        /// Every Copilot seat holder scored for Cowork readiness: who already uses it, and who carries the
        /// coordination load that Cowork is built to absorb.
        ///
        /// Held alongside the licensed users rather than merged into them because the two answer different
        /// questions and are filtered, sorted and exported independently - and because this list is absent
        /// on a tenant whose usage-report import has never run, where the licensed-user list is still fine.
        /// </summary>
        public List<CoworkReadinessRow> CoworkReadiness { get; set; } = new List<CoworkReadinessRow>();

        /// <summary>Raw usage for every unlicensed person who used Copilot in the window.</summary>
        public List<UnlicensedUsageQueryRow> UnlicensedUsers { get; set; } = new List<UnlicensedUsageQueryRow>();

        /// <summary>
        /// Raw Cowork signals as the database returned them, before scoring.
        ///
        /// Held between the query step and scoring because the Copilot engagement score they are scored
        /// against is produced by a <i>different</i> step running concurrently - so the join can only
        /// happen once every step has completed. Same raw-then-finalise shape as
        /// <see cref="UnlicensedUsers"/>.
        /// </summary>
        public List<CoworkReadinessSignalRow> CoworkSignals { get; set; } = new List<CoworkReadinessSignalRow>();

        /// <summary>
        /// The queries that produced this analysis, keyed by a short name, for the SQL popover the rest
        /// of the admin site uses. Showing the working is part of the point: these numbers get quoted
        /// in licence negotiations, so an admin has to be able to verify them independently.
        /// </summary>
        public Dictionary<string, string> Sql { get; set; } = new Dictionary<string, string>();
    }
}
