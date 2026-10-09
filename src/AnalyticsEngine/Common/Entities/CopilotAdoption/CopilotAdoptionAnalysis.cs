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

        /// <summary>
        /// True when the licence-opportunity query returned
        /// <see cref="CopilotAdoptionOptions.MaxOpportunityCandidates"/> rows, so candidates beyond the cap
        /// were never scored.
        /// </summary>
        /// <remarks>
        /// Held here rather than derived from <see cref="Opportunities"/> because the cap is applied
        /// tenant-wide: a view narrowed to one email domain holds fewer rows than the cap, yet that
        /// domain's weaker candidates may still have been cut. The licence estimate reads it to say its
        /// figure may be a floor.
        /// </remarks>
        public bool OpportunitiesCapped { get; set; }

        /// <summary>Every Copilot agent seen in the history window, with its health verdict.</summary>
        public List<AgentUsageRow> Agents { get; set; } = new List<AgentUsageRow>();

        /// <summary>
        /// The people who used Copilot agents in the period, heaviest first, up to
        /// <see cref="CopilotAdoptionOptions.MaxAgentUsersScored"/>.
        /// </summary>
        /// <remarks>
        /// Held as rows, unlike the tenant-level agent inventory, so a filtered view narrows them like any
        /// other per-person list and lists its own heaviest users rather than the tenant's. The summary
        /// names only the first few (<see cref="CopilotAdoptionSummary.TopAgentUsers"/>), and only to a
        /// reader with the See PII permission.
        /// </remarks>
        public List<AgentUserRow> AgentUsers { get; set; } = new List<AgentUserRow>();

        /// <summary>
        /// True when the agent-user query stopped at <see cref="CopilotAdoptionOptions.MaxAgentUsersScored"/>
        /// - in the tenant analysis and in every slice of it, which inherits the cap: a slice's heaviest
        /// users may rank below the tenant-wide cut.
        /// </summary>
        public bool AgentUsersCapped { get; set; }

        /// <summary>
        /// Every (agent, person) pair in the reporting period, from <see cref="CopilotAdoptionSql.AgentReachSql"/>
        /// - what the agent breadth and depth figures are computed from (#646). Null when the query did not
        /// run or failed, or when it returned more than <see cref="CopilotAdoptionSql.MaxAgentReachRows"/>.
        /// </summary>
        /// <remarks>
        /// Shared, not copied, by a filtered view: each figure only counts the people the view's own
        /// licensed and unlicensed rows hold, so the slice narrows it without a second list.
        /// </remarks>
        internal List<AgentReachRow> AgentReachRows { get; set; }

        /// <summary>
        /// The same pairs over the agent inventory's own period, which the reach columns on each
        /// <see cref="AgentUsageRow"/> come from (#647). The same list as <see cref="AgentReachRows"/>
        /// unless the analysis covers past dates, when the inventory still describes the latest period.
        /// Null in a filtered view: reach is part of the tenant-wide inventory and is computed once.
        /// </summary>
        internal List<AgentReachRow> AgentInventoryReachRows { get; set; }

        /// <summary>Department names by <c>user_departments.id</c>, for <see cref="AgentInventoryReachRows"/>.</summary>
        internal Dictionary<int, string> AgentReachDepartments { get; set; }

        /// <summary>
        /// Each stored agent's origin key (<see cref="Copilot.CopilotAgentOriginKeys"/>) by <c>copilot_agents.id</c>,
        /// resolved with <see cref="Copilot.CopilotAgentClassifier.ResolveStoredOrigin"/> - what decides which of
        /// <see cref="AgentReachRows"/> count (<see cref="CopilotAgentFigureScope"/>). Null when it could not be
        /// read, in which case breadth and depth are not measured rather than counted over no agents.
        /// </summary>
        internal Dictionary<int, string> AgentOrigins { get; set; }

        /// <summary>
        /// The people who created, published or shared a Copilot Studio agent in the period (#647). Held so a
        /// filtered view can narrow them; only ever counted.
        /// </summary>
        internal List<AgentBuilderRow> AgentBuilders { get; set; } = new List<AgentBuilderRow>();

        /// <summary>
        /// True when <see cref="AgentBuilders"/> is a measurement: Copilot Studio authoring events have been
        /// imported and the builders query succeeded. False means the builder figures are unknown, not zero.
        /// </summary>
        internal bool AgentBuildersAssessed { get; set; }

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
        /// True only when the unlicensed-usage query completed. Without it an empty
        /// <see cref="UnlicensedUsers"/> cannot be told apart from a query that failed or never ran (no
        /// audit import) - and the manager-modelling figures read a manager's ABSENCE from that list as
        /// "did not use Copilot", which is only true when the list is really there.
        /// </summary>
        internal bool UnlicensedUsageAssessed { get; set; }

        /// <summary>
        /// Every manager of a scored licensed user with their own Copilot status, resolved against the
        /// whole tenant's rows (#641). Built by the finalise pass of the tenant analysis; a narrowed
        /// analysis inherits it, so a report inside the slice whose manager is outside it is still resolved.
        /// </summary>
        internal CopilotAdoptionManagerDirectory ManagerDirectory { get; set; }

        /// <summary>
        /// Observed Copilot actions per licensed user, already collapsed by SQL to one row per user for
        /// the modelled realised-value estimate. Internal only; never exposed as a per-person hours figure.
        /// </summary>
        internal List<SeatHolderTimeSavedUserRow> SeatHolderTimeSavedRows { get; set; } = new List<SeatHolderTimeSavedUserRow>();

        /// <summary>
        /// True only when the realised seat-holder time query completed. An empty row set is then a real
        /// zero-activity result; false means the estimate was not assessed and must be withheld.
        /// </summary>
        internal bool SeatHolderTimeSavedAssessed { get; set; }

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
        /// True when this analysis is a slice of one whose Cowork assessment ran, so an empty
        /// <see cref="CoworkSignals"/> means "nobody in this slice", not "the assessment did not run".
        /// </summary>
        /// <remarks>
        /// Without it a filter such as "User type is Guest" - Cowork is assessed for members only - would
        /// empty the signals and the Cowork tab would tell an admin to check their imports, when the truth
        /// is that the people they selected have no Cowork readiness to show.
        /// </remarks>
        public bool CoworkAssessedForWholePopulation { get; set; }

        /// <summary>
        /// True when this analysis is a slice of one whose Cowork assessment ran but stopped at
        /// <see cref="CopilotAdoptionOptions.MaxCoworkUsersScored"/>, so an empty slice means "not
        /// assessed" - neither a measured zero nor a missing import - and is explained as such.
        /// </summary>
        public bool CoworkAssessmentCapped { get; set; }

        /// <summary>
        /// The Copilot seat holders the licensed-user query did not return because it stopped at
        /// <see cref="CopilotAdoptionOptions.MaxLicensedUsersScored"/>, or <c>null</c> when it did not stop
        /// there.
        /// </summary>
        /// <remarks>
        /// User ids only - a few hundred kilobytes even at 200,000 seats - taken from the licence
        /// assignments, which are never capped. A filtered view counts the ones it would include and says
        /// so. Without it, a filter made up of the newest user records - which the cap cuts first - reads as
        /// nobody, beside a tenant-wide warning that cannot say how much of that one view is missing.
        /// </remarks>
        public IReadOnlyList<int> LicensedUsersNotAnalysed { get; set; }

        /// <summary>
        /// True when the licensed-user query stopped at <see cref="CopilotAdoptionOptions.MaxLicensedUsersScored"/>
        /// - in the tenant analysis, and in every slice of it, which inherits the cap.
        /// </summary>
        /// <remarks>
        /// The one fact a slice needs to tell "nobody in this view was analysed" from "the analysis did not
        /// run": the Cowork tab takes each person's Copilot fluency from the licensed-user rows, and a view
        /// made up entirely of seat holders past the cap has Cowork signals but no licensed-user row at all.
        /// </remarks>
        public bool LicensedUsersCapped { get; set; }

        /// <summary>
        /// The queries that produced this analysis, keyed by a short name, for the SQL popover the rest
        /// of the admin site uses. Showing the working is part of the point: these numbers get quoted
        /// in licence negotiations, so an admin has to be able to verify them independently.
        /// </summary>
        public Dictionary<string, string> Sql { get; set; } = new Dictionary<string, string>();
    }
}
