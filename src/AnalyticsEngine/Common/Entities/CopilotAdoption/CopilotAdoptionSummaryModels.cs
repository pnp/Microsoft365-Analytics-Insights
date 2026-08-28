using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// One point of a weekly series. Serialises to the same JSON shape as the Reports area's
    /// <c>ReportTimePoint</c> so the SPA's existing chart components render this with no new code.
    /// A null value means "unknown" (e.g. the week's usage report never arrived) and is drawn as a
    /// gap rather than a misleading drop to zero.
    /// </summary>
    public class AdoptionTimePoint
    {
        [JsonProperty("weekStart")]
        public DateTime WeekStart { get; set; }

        [JsonProperty("value")]
        public double? Value { get; set; }
    }

    /// <summary>
    /// The average shape of engagement for a group of users: how much of their score comes from
    /// frequency, from depth, and from breadth.
    ///
    /// Reported as a profile rather than a single number because two populations can average the
    /// same score with completely different shapes - frequent-but-shallow and deep-but-narrow need
    /// opposite programmes - and the overall score cannot tell them apart.
    /// </summary>
    public class AdoptionScoreProfile
    {
        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("users")]
        public int Users { get; set; }

        [JsonProperty("frequencyScore")]
        public double FrequencyScore { get; set; }

        [JsonProperty("depthScore")]
        public double DepthScore { get; set; }

        [JsonProperty("breadthScore")]
        public double BreadthScore { get; set; }
    }

    /// <summary>
    /// One recommended action and how many licensed users need it.
    ///
    /// Exists so the licensed-user list can stop repeating an identical paragraph on every row of a
    /// band. The explanation is stated once, with a count next to it, which is both less noise and
    /// more information: "coach 76 people" is a plan, "coach this person" x 76 is a wall of text.
    /// </summary>
    public class AdoptionActionSummary
    {
        /// <summary>Stable code: reclaim, reengage, coach, broaden, grow, sustain, advocate.</summary>
        [JsonProperty("code")]
        public string Code { get; set; }

        /// <summary>Short display label, e.g. "Re-engage".</summary>
        [JsonProperty("label")]
        public string Label { get; set; }

        /// <summary>Why these users are in this group and what the action involves.</summary>
        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("users")]
        public int Users { get; set; }

        [JsonProperty("sharePct")]
        public double SharePct { get; set; }
    }

    /// <summary>
    /// One habit-formation bucket: how many licensed users are active on that many days a month, and
    /// what share of the licensed population that is.
    ///
    /// Separate from the engagement bands on purpose. A band folds frequency, depth and breadth into
    /// one judgement; this is the single raw question "how many days a month do they actually open
    /// it?", which is the number a sceptical reader trusts because there is no weighting in it.
    /// </summary>
    public class AdoptionHabitBucket
    {
        /// <summary>Bucket name: Infrequent, Moderate, Frequent or Daily.</summary>
        [JsonProperty("label")]
        public string Label { get; set; }

        /// <summary>Plain-English range, e.g. "6-10 active days a month".</summary>
        [JsonProperty("rangeLabel")]
        public string RangeLabel { get; set; }

        [JsonProperty("users")]
        public int Users { get; set; }

        /// <summary>Share of active licensed users in this bucket. Percentages across the buckets sum to 100.</summary>
        [JsonProperty("sharePct")]
        public double SharePct { get; set; }
    }

    /// <summary>
    /// A department plotted on the two axes that actually separate "used it" from "relies on it":
    /// how many days a month its users are active (frequency), and how many interactions they run on
    /// each of those days (intensity).
    ///
    /// A single adoption percentage cannot tell a department of daily-but-shallow users apart from a
    /// department of occasional-but-deep ones, and those two need opposite interventions.
    /// </summary>
    public class AdoptionIntensityPoint
    {
        [JsonProperty("segment")]
        public string Segment { get; set; }

        /// <summary>Licensed seats in the department - the bubble size, so a large slow department outranks a tiny one.</summary>
        [JsonProperty("licensedUsers")]
        public int LicensedUsers { get; set; }

        [JsonProperty("activeUsers")]
        public int ActiveUsers { get; set; }

        /// <summary>Mean active days per active user, normalised to a month.</summary>
        [JsonProperty("activeDaysPerUser")]
        public double ActiveDaysPerUser { get; set; }

        /// <summary>Mean interactions per active day.</summary>
        [JsonProperty("actionsPerActiveDay")]
        public double ActionsPerActiveDay { get; set; }

        /// <summary>
        /// Mean engagement score across this segment's <i>active</i> users only, matching the two axes.
        ///
        /// Deliberately not the whole-department average that the "adoption by department" table shows:
        /// every other dimension of this plot describes the people who actually use Copilot, so colouring
        /// the bubble by an average that includes never-used seats would make the chart contradict its
        /// own caption - and would double-count a problem the reclaim figures already report.
        /// </summary>
        [JsonProperty("activeUserAverageScore")]
        public double ActiveUserAverageScore { get; set; }
    }

    /// <summary>A named line in a weekly chart. Same JSON shape as the Reports area's <c>ReportSeries</c>.</summary>
    public class AdoptionSeries
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("points")]
        public List<AdoptionTimePoint> Points { get; set; } = new List<AdoptionTimePoint>();
    }

    /// <summary>One bar of a categorical chart. Same JSON shape as the Reports area's <c>ReportCategory</c>.</summary>
    public class AdoptionCategory
    {
        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("value")]
        public double Value { get; set; }
    }

    /// <summary>
    /// Adoption for one slice of the organisation (a department, country, office...). Carries the raw
    /// counts as well as the percentage: "40% adopted" means something very different across 5 seats
    /// and across 500, and an executive summary that shows only the percentage invites the wrong call.
    /// </summary>
    public class AdoptionSegmentRow
    {
        [JsonProperty("segment")]
        public string Segment { get; set; }

        [JsonProperty("licensedUsers")]
        public int LicensedUsers { get; set; }

        [JsonProperty("activeUsers")]
        public int ActiveUsers { get; set; }

        [JsonProperty("habitualUsers")]
        public int HabitualUsers { get; set; }

        [JsonProperty("neverUsedUsers")]
        public int NeverUsedUsers { get; set; }

        [JsonProperty("adoptionRatePct")]
        public double AdoptionRatePct { get; set; }

        [JsonProperty("averageAdoptionScore")]
        public double AverageAdoptionScore { get; set; }
    }

    /// <summary>Which underlying imports actually supplied data, so no headline number is silently wrong.</summary>
    public class AdoptionDataSources
    {
        /// <summary>The Copilot audit-log import (<c>copilot_chats</c>) is enabled and has data.</summary>
        [JsonProperty("auditAvailable")]
        public bool AuditAvailable { get; set; }

        /// <summary>Microsoft's own per-user Copilot usage report has been imported.</summary>
        [JsonProperty("copilotUsageReportAvailable")]
        public bool CopilotUsageReportAvailable { get; set; }

        /// <summary>The Microsoft 365 workload usage reports (Teams/Outlook/SharePoint/OneDrive) have data.</summary>
        [JsonProperty("m365UsageReportsAvailable")]
        public bool M365UsageReportsAvailable { get; set; }

        /// <summary>User metadata (departments, managers, licences) has been imported.</summary>
        [JsonProperty("userMetadataAvailable")]
        public bool UserMetadataAvailable { get; set; }

        /// <summary>The report snapshot date the per-user Copilot figures came from.</summary>
        [JsonProperty("copilotUsageReportDate")]
        public DateTime? CopilotUsageReportDate { get; set; }

        /// <summary>
        /// Which Graph report period (7 / 28 / 90 / 180) the snapshot above was read from; 0 when the
        /// imported rows predate the period being recorded. Pinned because the usage-report table holds one
        /// row per (date, user, period), so a date-only snapshot duplicates every user.
        /// </summary>
        [JsonProperty("copilotUsageReportPeriodDays")]
        public int CopilotUsageReportPeriodDays { get; set; }

        /// <summary>
        /// The last daily Microsoft 365 usage report available. It bounds the period the workload
        /// figures are averaged over; it is not the only day they are read from.
        /// </summary>
        [JsonProperty("m365UsageReportDate")]
        public DateTime? M365UsageReportDate { get; set; }

        /// <summary>
        /// True when the tenant has "concealed user information" switched on, so Microsoft's per-user
        /// report is unusable. Audit-derived figures are unaffected, which is worth saying out loud
        /// because the two sources otherwise appear to contradict each other.
        /// </summary>
        [JsonProperty("copilotUsageReportObfuscated")]
        public bool CopilotUsageReportObfuscated { get; set; }
    }

    /// <summary>The executive view: headline numbers, the adoption funnel and the breakdown charts.</summary>
    public class CopilotAdoptionSummary
    {
        [JsonProperty("generatedUtc")]
        public DateTime GeneratedUtc { get; set; }

        [JsonProperty("windowDays")]
        public int WindowDays { get; set; }

        [JsonProperty("fromUtc")]
        public DateTime FromUtc { get; set; }

        [JsonProperty("toUtc")]
        public DateTime ToUtc { get; set; }

        [JsonProperty("dataSources")]
        public AdoptionDataSources DataSources { get; set; } = new AdoptionDataSources();

        /// <summary>Which licence types were counted as Copilot seats, so the population is auditable.</summary>
        [JsonProperty("seatLicenceTypes")]
        public List<LicenceTypeClassification> SeatLicenceTypes { get; set; } = new List<LicenceTypeClassification>();

        #region Headline figures

        /// <summary>
        /// Users holding at least one Microsoft 365 Copilot seat. The true seat count, never capped.
        /// </summary>
        [JsonProperty("licensedUsers")]
        public int LicensedUsers { get; set; }

        /// <summary>
        /// Licensed users this analysis actually scored, and the denominator of every rate below.
        ///
        /// Normally identical to <see cref="LicensedUsers"/>. It is lower only when the detail query
        /// hit <see cref="CopilotAdoptionOptions.MaxLicensedUsersScored"/>, and in that case every
        /// percentage here describes the scored subset rather than the whole tenant.
        ///
        /// Kept as its own field rather than dividing by the seat count, because doing the latter is
        /// arithmetically wrong the moment the cap bites: a 200,000-seat tenant scored 50,000 users
        /// deep can never report an adoption rate above 25%, however healthy adoption actually is, and
        /// the funnel would open with a 75% drop that is pure measurement artefact.
        /// </summary>
        [JsonProperty("scoredUsers")]
        public int ScoredUsers { get; set; }

        /// <summary>Licensed users with at least one Copilot interaction inside the window.</summary>
        [JsonProperty("activeUsers")]
        public int ActiveUsers { get; set; }

        /// <summary>Licensed users with no recorded Copilot activity at all in the history window.</summary>
        [JsonProperty("neverUsedUsers")]
        public int NeverUsedUsers { get; set; }

        /// <summary>Licensed users who used Copilot before the window but not inside it.</summary>
        [JsonProperty("dormantUsers")]
        public int DormantUsers { get; set; }

        /// <summary>Active licensed users as a percentage of all licensed users.</summary>
        [JsonProperty("adoptionRatePct")]
        public double AdoptionRatePct { get; set; }

        /// <summary>Licensed users whose engagement is Established or Champion - i.e. Copilot is a habit.</summary>
        [JsonProperty("habitualUsers")]
        public int HabitualUsers { get; set; }

        [JsonProperty("habitRatePct")]
        public double HabitRatePct { get; set; }

        /// <summary>
        /// Seats held by users who did nothing in the window: the directly actionable number. These are
        /// candidates for reassignment to the people on the opportunity list, or for a targeted
        /// enablement push before the next renewal.
        /// </summary>
        [JsonProperty("reclaimableSeats")]
        public int ReclaimableSeats { get; set; }

        /// <summary>Mean engagement score across all licensed users, including the ones scoring zero.</summary>
        [JsonProperty("averageAdoptionScore")]
        public double AverageAdoptionScore { get; set; }

        /// <summary>Median engagement score - reported alongside the mean because a few Champions skew the mean upwards.</summary>
        [JsonProperty("medianAdoptionScore")]
        public double MedianAdoptionScore { get; set; }

        [JsonProperty("totalInteractions")]
        public long TotalInteractions { get; set; }

        #endregion

        #region Cowork

        /// <summary>Licensed users who used Microsoft 365 Copilot Cowork inside the window.</summary>
        [JsonProperty("coworkUsers")]
        public int CoworkUsers { get; set; }

        [JsonProperty("coworkAdoptionPct")]
        public double CoworkAdoptionPct { get; set; }

        [JsonProperty("coworkInteractions")]
        public long CoworkInteractions { get; set; }

        /// <summary>
        /// False when nothing in the data identifies Cowork at all - which on a tenant that has not
        /// been enabled for it is the correct and expected answer, and must not be shown as "0% adoption".
        /// </summary>
        [JsonProperty("coworkDetected")]
        public bool CoworkDetected { get; set; }

        #endregion

        #region Licence opportunity

        /// <summary>Users with no Copilot seat who nevertheless used Copilot Chat inside the window.</summary>
        [JsonProperty("unlicensedActiveUsers")]
        public int UnlicensedActiveUsers { get; set; }

        /// <summary>Unlicensed users scoring at or above the recommendation threshold.</summary>
        [JsonProperty("recommendedForLicence")]
        public int RecommendedForLicence { get; set; }

        #endregion

        #region Charts

        /// <summary>Licensed -&gt; ever used -&gt; active -&gt; habitual -&gt; champion. The story in one chart.</summary>
        [JsonProperty("funnel")]
        public List<AdoptionCategory> Funnel { get; set; } = new List<AdoptionCategory>();

        /// <summary>How many licensed users fall in each <see cref="AdoptionBand"/>.</summary>
        [JsonProperty("bandBreakdown")]
        public List<AdoptionCategory> BandBreakdown { get; set; } = new List<AdoptionCategory>();

        /// <summary>
        /// Active licensed users bucketed by how many days a month they actually use Copilot. The
        /// unweighted counterpart to the engagement bands - see <see cref="AdoptionHabitBucket"/>.
        /// </summary>
        [JsonProperty("habitBuckets")]
        public List<AdoptionHabitBucket> HabitBuckets { get; set; } = new List<AdoptionHabitBucket>();

        /// <summary>Departments plotted as frequency vs intensity, so shallow-daily and deep-occasional are distinguishable.</summary>
        [JsonProperty("intensityByDepartment")]
        public List<AdoptionIntensityPoint> IntensityByDepartment { get; set; } = new List<AdoptionIntensityPoint>();

        /// <summary>
        /// How many licensed users need each recommended action. Turns a column that repeats the same
        /// sentence hundreds of times into the thing an admin actually wants: the size of each job.
        /// </summary>
        [JsonProperty("actionPlan")]
        public List<AdoptionActionSummary> ActionPlan { get; set; } = new List<AdoptionActionSummary>();

        /// <summary>Adoption by department, worst first - i.e. where enablement effort should go.</summary>
        [JsonProperty("adoptionByDepartment")]
        public List<AdoptionSegmentRow> AdoptionByDepartment { get; set; } = new List<AdoptionSegmentRow>();

        /// <summary>Adoption by country, for organisations that run enablement regionally.</summary>
        [JsonProperty("adoptionByCountry")]
        public List<AdoptionSegmentRow> AdoptionByCountry { get; set; } = new List<AdoptionSegmentRow>();

        /// <summary>Where Copilot is actually being used (Teams, Word, Outlook, Copilot Chat...).</summary>
        [JsonProperty("usageByApp")]
        public List<AdoptionCategory> UsageByApp { get; set; } = new List<AdoptionCategory>();

        /// <summary>Departments with the most unlicensed users who would benefit from a seat.</summary>
        [JsonProperty("opportunityByDepartment")]
        public List<AdoptionCategory> OpportunityByDepartment { get; set; } = new List<AdoptionCategory>();

        /// <summary>
        /// How concentrated Copilot usage is across the people who use it. See
        /// <see cref="AdoptionConcentrationBand"/> - this is what tells a broad programme apart from
        /// one carried by a handful of enthusiasts.
        /// </summary>
        [JsonProperty("concentration")]
        public List<AdoptionConcentrationBand> Concentration { get; set; } = new List<AdoptionConcentrationBand>();

        /// <summary>Licensed and unlicensed Copilot use per department, side by side.</summary>
        [JsonProperty("combinedByDepartment")]
        public List<AdoptionCombinedSegmentRow> CombinedByDepartment { get; set; } = new List<AdoptionCombinedSegmentRow>();

        /// <summary>The kinds of tenant content Copilot grounded its answers in.</summary>
        [JsonProperty("topResourceTypes")]
        public List<AdoptionCategory> TopResourceTypes { get; set; } = new List<AdoptionCategory>();

        /// <summary>
        /// The shape of engagement for the average active user and for the Champions, so the gap
        /// between "typical here" and "best here" is visible. See <see cref="AdoptionScoreProfile"/>.
        /// </summary>
        [JsonProperty("scoreProfiles")]
        public List<AdoptionScoreProfile> ScoreProfiles { get; set; } = new List<AdoptionScoreProfile>();

        /// <summary>The agent estate: what exists, who uses it, and what should be retired.</summary>
        [JsonProperty("agents")]
        public AgentEstateSummary Agents { get; set; } = new AgentEstateSummary();

        /// <summary>Unlicensed Copilot Chat as a population in its own right.</summary>
        [JsonProperty("unlicensed")]
        public UnlicensedPopulationSummary Unlicensed { get; set; } = new UnlicensedPopulationSummary();

        /// <summary>Weekly active licensed users, so a trend is visible rather than a single snapshot.</summary>
        [JsonProperty("weeklyTrend")]
        public List<AdoptionSeries> WeeklyTrend { get; set; } = new List<AdoptionSeries>();

        /// <summary>
        /// Weekly interaction volume, licensed against unlicensed. Kept apart from
        /// <see cref="WeeklyTrend"/> because headcounts and volumes share no sensible axis.
        /// </summary>
        [JsonProperty("weeklyVolumeTrend")]
        public List<AdoptionSeries> WeeklyVolumeTrend { get; set; } = new List<AdoptionSeries>();

        #endregion

        /// <summary>The tuning actually used, echoed back so every figure can be traced to its rule.</summary>
        [JsonProperty("options")]
        public CopilotAdoptionOptions Options { get; set; }

        /// <summary>
        /// Anything that makes a number less trustworthy than it looks - a missing import, a capped
        /// result set, a query that failed. Surfaced prominently rather than logged, because this
        /// report is used to make spending decisions.
        /// </summary>
        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>
        /// True when a query the headline figures are DERIVED FROM failed, so the adoption numbers below
        /// describe an incomplete population and must not be read as fact.
        /// </summary>
        /// <remarks>
        /// This is not the same as <see cref="Warnings"/>. A warning means "one chart is missing"; this
        /// means "the licensed-user population itself could not be loaded, so every rate, funnel stage and
        /// segment on this page was computed from whatever did load".
        ///
        /// It exists because the failure was previously indistinguishable from a real result. When the
        /// licensed-user queries time out the analysis still completes: the seat count degrades to zero and
        /// the user list to empty, and the page renders a normal-looking dashboard reporting almost no
        /// adoption. On a tenant large enough for those queries to time out at the median, an admin could
        /// reasonably act on that and start reclaiming licences that are in fact being used.
        /// </remarks>
        [JsonProperty("figuresIncomplete")]
        public bool FiguresIncomplete { get; set; }

        /// <summary>
        /// Which specific datasets could not be loaded, for the message shown in place of the figures.
        /// Compile-time descriptions only - never anything derived from tenant data.
        /// </summary>
        [JsonProperty("incompleteReasons")]
        public List<string> IncompleteReasons { get; set; } = new List<string>();

        /// <summary>
        /// Records that a dataset the headline figures depend on could not be loaded.
        /// </summary>
        public void MarkFiguresIncomplete(string dataset)
        {
            FiguresIncomplete = true;

            if (!string.IsNullOrWhiteSpace(dataset) && !IncompleteReasons.Contains(dataset))
            {
                IncompleteReasons.Add(dataset);
            }
        }

        /// <summary>
        /// How long the analysis took, per step. Emitted to App Insights so a tenant whose report is
        /// quietly degrading (queries timing out into warnings rather than errors) is visible to an
        /// operator instead of only to whoever happens to open the page.
        ///
        /// Not rendered on screen; it is telemetry that travels with the analysis so the controller can
        /// report it without re-running anything.
        /// </summary>
        [JsonProperty("diagnostics")]
        public CopilotAdoptionDiagnostics Diagnostics { get; set; } = new CopilotAdoptionDiagnostics();
    }

    /// <summary>Which parts of the adoption tool this deployment can actually show.</summary>
    public class CopilotAdoptionAvailability
    {
        /// <summary>False when neither Copilot data source is imported - the tool then has nothing to say.</summary>
        [JsonProperty("available")]
        public bool Available { get; set; }

        [JsonProperty("copilotAuditImportEnabled")]
        public bool CopilotAuditImportEnabled { get; set; }

        [JsonProperty("copilotUsageReportImportEnabled")]
        public bool CopilotUsageReportImportEnabled { get; set; }

        [JsonProperty("userMetadataImportEnabled")]
        public bool UserMetadataImportEnabled { get; set; }

        [JsonProperty("m365UsageReportImportEnabled")]
        public bool M365UsageReportImportEnabled { get; set; }

        /// <summary>Explains, in plain English, anything that is switched off and what it costs the report.</summary>
        [JsonProperty("messages")]
        public List<string> Messages { get; set; } = new List<string>();
    }

    /// <summary>A page of scored licensed users, plus the totals for the filtered set.</summary>
    public class LicensedUserPage
    {
        [JsonProperty("total")]
        public int Total { get; set; }

        [JsonProperty("skip")]
        public int Skip { get; set; }

        [JsonProperty("take")]
        public int Take { get; set; }

        [JsonProperty("rows")]
        public List<LicensedUserAdoptionRow> Rows { get; set; } = new List<LicensedUserAdoptionRow>();

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>A page of ranked licence-opportunity candidates.</summary>
    public class LicenceOpportunityPage
    {
        [JsonProperty("total")]
        public int Total { get; set; }

        [JsonProperty("skip")]
        public int Skip { get; set; }

        [JsonProperty("take")]
        public int Take { get; set; }

        [JsonProperty("rows")]
        public List<LicenceOpportunityRow> Rows { get; set; } = new List<LicenceOpportunityRow>();

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
