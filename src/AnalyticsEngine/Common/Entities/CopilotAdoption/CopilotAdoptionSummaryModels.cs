using Common.Entities.Copilot;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

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

        /// <summary>Microsoft-published resources for the person who executes this action.</summary>
        [JsonProperty("guidanceLinks")]
        public List<AdoptionGuidanceLink> GuidanceLinks { get; set; } = new List<AdoptionGuidanceLink>();
    }

    /// <summary>One Microsoft-published resource in the versioned adoption-guidance catalogue.</summary>
    public class AdoptionGuidanceLink
    {
        [JsonProperty("actionCode")]
        public string ActionCode { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonProperty("expectedTitle")]
        public string ExpectedTitle { get; set; }

        [JsonProperty("audience")]
        public string Audience { get; set; }

        [JsonProperty("publisher")]
        public string Publisher { get; set; }

        [JsonProperty("catalogueVersion")]
        public string CatalogueVersion { get; set; }
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
    /// One row of the accessed-resource-type breakdown: a raw <c>AccessedResources[].Type</c> value
    /// from Microsoft's Copilot audit log, how many references carried it, and what that value
    /// actually describes.
    ///
    /// The kind exists because the field is not a single taxonomy - see
    /// <see cref="CopilotResourceTypeKind"/>. Without it the chart puts a file kind, a citation and a
    /// web search on one axis and invites the reader to compare them.
    /// </summary>
    public class AdoptionResourceTypeRow
    {
        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("value")]
        public double Value { get; set; }

        /// <summary>
        /// What the label describes. <see cref="CopilotResourceTypeKind.Unclassified"/> for a value
        /// this version does not recognise, which is the expected outcome for anything Microsoft adds
        /// in future.
        /// </summary>
        [JsonProperty("kind")]
        public CopilotResourceTypeKind Kind { get; set; }
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

    /// <summary>
    /// One accountable organisational unit: by default a direct manager's span, but configurable to
    /// department, country, office or company for tenants whose Copilot spend is owned differently.
    /// </summary>
    public class AccountabilityRollupRow : AdoptionSegmentRow
    {
        [JsonProperty("reclaimableSeats")]
        public int ReclaimableSeats { get; set; }

        [JsonProperty("reclaimCertainSeats")]
        public int ReclaimCertainSeats { get; set; }

        [JsonProperty("reclaimProbableSeats")]
        public int ReclaimProbableSeats { get; set; }

        [JsonProperty("reclaimReviewSeats")]
        public int ReclaimReviewSeats { get; set; }

        [JsonProperty("reclaimExcludedUsers")]
        public int ReclaimExcludedUsers { get; set; }

        [JsonProperty("reclaimUsers")]
        public int ReclaimUsers { get; set; }

        [JsonProperty("reengageUsers")]
        public int ReengageUsers { get; set; }

        [JsonProperty("coachUsers")]
        public int CoachUsers { get; set; }

        [JsonProperty("broadenUsers")]
        public int BroadenUsers { get; set; }

        [JsonProperty("growUsers")]
        public int GrowUsers { get; set; }

        [JsonProperty("sustainUsers")]
        public int SustainUsers { get; set; }

        [JsonProperty("advocateUsers")]
        public int AdvocateUsers { get; set; }

        [JsonProperty("reviewUsers")]
        public int ReviewUsers { get; set; }

        [JsonProperty("excludedUsers")]
        public int ExcludedUsers { get; set; }

        /// <summary>
        /// Absolute number of seats with an actionable opportunity, used for sorting. Large teams with
        /// moderate gaps must outrank tiny teams with terrible percentages.
        /// </summary>
        [JsonProperty("opportunityUsers")]
        public int OpportunityUsers { get; set; }
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

        /// <summary>The first-party Cowork usage report has been imported.</summary>
        [JsonProperty("coworkUsageReportAvailable")]
        public bool CoworkUsageReportAvailable { get; set; }

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

        [JsonProperty("coworkUsageReportDate")]
        public DateTime? CoworkUsageReportDate { get; set; }

        [JsonProperty("coworkUsageReportPeriodDays")]
        public int CoworkUsageReportPeriodDays { get; set; }

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

        [JsonProperty("guidanceCatalogueVersion")]
        public string GuidanceCatalogueVersion { get; set; } = CopilotAdoptionGuidanceCatalogue.Version;

        [JsonProperty("guidanceLinks")]
        public List<AdoptionGuidanceLink> GuidanceLinks { get; set; } = CopilotAdoptionGuidanceCatalogue.All.ToList();

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

        /// <summary>Disabled accounts that still hold a Copilot seat. This is the zero-risk reclaim KPI.</summary>
        [JsonProperty("disabledLicensedUsers")]
        public int DisabledLicensedUsers { get; set; }

        [JsonProperty("reclaimCertainSeats")]
        public int ReclaimCertainSeats { get; set; }

        [JsonProperty("reclaimProbableSeats")]
        public int ReclaimProbableSeats { get; set; }

        [JsonProperty("reclaimReviewSeats")]
        public int ReclaimReviewSeats { get; set; }

        [JsonProperty("reclaimExcludedUsers")]
        public int ReclaimExcludedUsers { get; set; }

        [JsonProperty("expiredReclaimExclusions")]
        public int ExpiredReclaimExclusions { get; set; }

        [JsonProperty("tooNewToJudgeUsers")]
        public int TooNewToJudgeUsers { get; set; }

        [JsonProperty("reclaimCaveat")]
        public string ReclaimCaveat { get; set; }

        /// <summary>Mean engagement score across all licensed users, including the ones scoring zero.</summary>
        [JsonProperty("averageAdoptionScore")]
        public double AverageAdoptionScore { get; set; }

        /// <summary>Median engagement score - reported alongside the mean because a few Champions skew the mean upwards.</summary>
        [JsonProperty("medianAdoptionScore")]
        public double MedianAdoptionScore { get; set; }

        [JsonProperty("totalInteractions")]
        public long TotalInteractions { get; set; }

        /// <summary>
        /// Licensed users whose engagement row was scored from Microsoft's per-user usage report rather
        /// than from this product's Copilot audit import.
        /// </summary>
        /// <remarks>
        /// The fallback is deliberately preserved because it stops a partial or lagging audit import from
        /// putting active people on a reclaim list. It is nevertheless a different measurement source:
        /// Microsoft's report supplies prompt counts over its own D7/D28/D90/D180 window, so any
        /// interaction-volume aggregate must say how much of the scored population was not eligible for
        /// the audit-interaction totals.
        /// </remarks>
        [JsonProperty("usageReportSourcedUsers")]
        public int UsageReportSourcedUsers { get; set; }

        /// <summary>Share of scored licensed users whose engagement came from Microsoft's usage report.</summary>
        [JsonProperty("usageReportSourcedUserPct")]
        public double UsageReportSourcedUserPct { get; set; }

        /// <summary>
        /// True when report-sourced rows used a Microsoft report period that does not match the selected
        /// analysis window, so those rows are deliberately excluded from reclaimable-seat totals.
        /// </summary>
        [JsonProperty("usageReportWindowMismatch")]
        public bool UsageReportWindowMismatch { get; set; }

        /// <summary>
        /// Seats held back from <see cref="ReclaimableSeats"/> because they were scored from Microsoft's
        /// report over a window that does not match the one selected.
        /// </summary>
        /// <remarks>
        /// Published so the arithmetic still reconciles on screen. Without it
        /// <c>ReclaimableSeats != NeverUsedUsers + DormantUsers</c> whenever the windows disagree, and a
        /// reader who adds up the band breakdown finds a gap that nothing on the page accounts for.
        /// A figure that does not tie out is exactly the kind of thing that loses an argument in a
        /// licence negotiation, however defensible the reason behind it.
        /// </remarks>
        [JsonProperty("reclaimSeatsHeldBackForWindowMismatch")]
        public int ReclaimSeatsHeldBackForWindowMismatch { get; set; }

        /// <summary>
        /// Idle seats (never used or dormant) that the confidence tiers deliberately keep out of
        /// <see cref="ReclaimableSeats"/> because a human has to look at them first - dormant users,
        /// users still inside the grace period, users with no tenure or account-state evidence, and
        /// users an administrator has explicitly excluded.
        /// </summary>
        /// <remarks>
        /// Published alongside <see cref="ReclaimSeatsHeldBackForWindowMismatch"/> so the whole
        /// arithmetic ties out on screen:
        /// <c>NeverUsedUsers + DormantUsers + ReclaimSeatsFromActiveBands ==
        /// ReclaimableSeats + ReclaimSeatsHeldBackForWindowMismatch + ReclaimSeatsHeldBackForReview</c>.
        /// </remarks>
        [JsonProperty("reclaimSeatsHeldBackForReview")]
        public int ReclaimSeatsHeldBackForReview { get; set; }

        /// <summary>
        /// Reclaimable seats whose engagement band is better than dormant - in practice, disabled
        /// accounts that were still active right up to the day they were disabled.
        /// </summary>
        /// <remarks>
        /// They are the most certain reclaims there are, and they are <i>not</i> part of
        /// <c>NeverUsedUsers + DormantUsers</c>. Published so a reader adding up the band breakdown can
        /// account for the difference rather than finding an unexplained gap.
        /// </remarks>
        [JsonProperty("reclaimSeatsFromActiveBands")]
        public int ReclaimSeatsFromActiveBands { get; set; }

        #endregion

        #region Cowork

        /// <summary>Licensed users who used Microsoft 365 Copilot Cowork inside the window.</summary>
        [JsonProperty("coworkUsers")]
        public int CoworkUsers { get; set; }

        [JsonProperty("coworkAdoptionPct")]
        public double? CoworkAdoptionPct { get; set; }

        [JsonProperty("coworkEligibilityKnown")]
        public bool CoworkEligibilityKnown { get; set; }

        [JsonProperty("coworkEligibleUsers")]
        public int? CoworkEligibleUsers { get; set; }

        [JsonProperty("coworkAuditUsers")]
        public int CoworkAuditUsers { get; set; }

        [JsonProperty("coworkInteractions")]
        public long CoworkInteractions { get; set; }

        [JsonProperty("coworkReportUsers")]
        public int CoworkReportUsers { get; set; }

        [JsonProperty("coworkReportTotalTasks")]
        public int CoworkReportTotalTasks { get; set; }

        [JsonProperty("coworkReportScheduledTasks")]
        public int CoworkReportScheduledTasks { get; set; }

        [JsonProperty("coworkReportUserInitiatedTasks")]
        public int CoworkReportUserInitiatedTasks { get; set; }

        [JsonProperty("coworkAutomationRatioPct")]
        public double? CoworkAutomationRatioPct { get; set; }

        [JsonProperty("coworkTasksPerActiveUser")]
        public double? CoworkTasksPerActiveUser { get; set; }

        [JsonProperty("coworkReportRetainedUsers")]
        public int? CoworkReportRetainedUsers { get; set; }

        [JsonProperty("coworkReportRetentionPct")]
        public double? CoworkReportRetentionPct { get; set; }

        /// <summary>
        /// False when nothing in the data identifies Cowork at all - which on a tenant that has not
        /// been enabled for it is the correct and expected answer, and must not be shown as "0% adoption".
        /// </summary>
        [JsonProperty("coworkDetected")]
        public bool CoworkDetected { get; set; }

        #endregion

        #region Cowork readiness (the Cowork tab)

        /// <summary>
        /// True when the Cowork readiness analysis ran and produced rows. False on a tenant with no Copilot
        /// seats, or where the step failed - in which case the tab must say so rather than render an empty
        /// quadrant that reads as "nobody is a candidate".
        /// </summary>
        [JsonProperty("coworkReadinessAvailable")]
        public bool CoworkReadinessAvailable { get; set; }

        /// <summary>Seat holders scored for Cowork readiness.</summary>
        [JsonProperty("coworkScoredUsers")]
        public int CoworkScoredUsers { get; set; }

        /// <summary>Seat holders using Cowork on enough separate days to count as regular use.</summary>
        [JsonProperty("coworkEstablishedUsers")]
        public int CoworkEstablishedUsers { get; set; }

        /// <summary>Seat holders who have used Cowork but not yet regularly.</summary>
        [JsonProperty("coworkTriallingUsers")]
        public int CoworkTriallingUsers { get; set; }

        /// <summary>
        /// The headline number: fluent Copilot users carrying a heavy coordination load who are not yet
        /// using Cowork. This is the population a rollout should target first.
        /// </summary>
        [JsonProperty("coworkPrimeCandidates")]
        public int CoworkPrimeCandidates { get; set; }

        /// <summary>Users with the workload for Cowork but not yet the Copilot habit to delegate to it.</summary>
        [JsonProperty("coworkBuildFluencyFirst")]
        public int CoworkBuildFluencyFirst { get; set; }

        /// <summary>
        /// Everyone who should be in the Cowork spending policy: the prime candidates plus everyone already
        /// using Cowork. Deliberately includes current users - a policy scoped from candidates alone would
        /// revoke access from the people already proving the capability works.
        /// </summary>
        [JsonProperty("coworkRecommendedForPolicy")]
        public int CoworkRecommendedForPolicy { get; set; }

        /// <summary>Mean coordination load across scored seat holders.</summary>
        [JsonProperty("coworkAverageCoordinationLoad")]
        public double CoworkAverageCoordinationLoad { get; set; }

        /// <summary>Mean Copilot fluency across scored seat holders.</summary>
        [JsonProperty("coworkAverageFluency")]
        public double CoworkAverageFluency { get; set; }

        /// <summary>Every Cowork tier with its population, in report order.</summary>
        [JsonProperty("coworkTiers")]
        public List<CoworkTierSummary> CoworkTiers { get; set; } = new List<CoworkTierSummary>();

        /// <summary>Departments plotted on the readiness quadrant.</summary>
        [JsonProperty("coworkQuadrant")]
        public List<CoworkQuadrantPoint> CoworkQuadrant { get; set; } = new List<CoworkQuadrantPoint>();

        /// <summary>Departments ranked for rollout sequencing.</summary>
        [JsonProperty("coworkByDepartment")]
        public List<CoworkSegmentRow> CoworkByDepartment { get; set; } = new List<CoworkSegmentRow>();

        /// <summary>The tenant's shared Copilot Credit position, as rollout headroom.</summary>
        [JsonProperty("coworkCreditPosition")]
        public CoworkCreditPosition CoworkCreditPosition { get; set; } = new CoworkCreditPosition();

        /// <summary>
        /// The modelled time/cost estimate for the recommended cohort. Every figure inside is an
        /// assumption applied to observed volume - see <see cref="CoworkValueEstimate"/>.
        /// </summary>
        [JsonProperty("coworkValueEstimate")]
        public CoworkValueEstimate CoworkValueEstimate { get; set; } = new CoworkValueEstimate();

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

        /// <summary>Departments with the weakest habit formation, for the executive league table.</summary>
        [JsonProperty("habitByDepartment")]
        public List<AdoptionSegmentRow> HabitByDepartment { get; set; } = new List<AdoptionSegmentRow>();

        /// <summary>Adoption by country, for organisations that run enablement regionally.</summary>
        [JsonProperty("adoptionByCountry")]
        public List<AdoptionSegmentRow> AdoptionByCountry { get; set; } = new List<AdoptionSegmentRow>();

        /// <summary>The configured accountability dimension used for <see cref="AccountabilityRollup"/>.</summary>
        [JsonProperty("accountabilityDimension")]
        public string AccountabilityDimension { get; set; } = CopilotAdoptionAccountabilityDimensions.DirectManager;

        /// <summary>Human-readable label for the configured accountability dimension.</summary>
        [JsonProperty("accountabilityDimensionLabel")]
        public string AccountabilityDimensionLabel { get; set; } = "Direct manager";

        /// <summary>
        /// Adoption, reclaim and action counts by accountable organisational unit. Small groups are
        /// suppressed with the same minimum-seat threshold as the existing segment charts.
        /// </summary>
        [JsonProperty("accountabilityRollup")]
        public List<AccountabilityRollupRow> AccountabilityRollup { get; set; } = new List<AccountabilityRollupRow>();

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

        /// <summary>
        /// How Microsoft's audit log typed the resources Copilot referenced, with each value's own
        /// kind so a file type, a citation and a web search are not read as one taxonomy. See
        /// <see cref="AdoptionResourceTypeRow"/>.
        /// </summary>
        [JsonProperty("topResourceTypes")]
        public List<AdoptionResourceTypeRow> TopResourceTypes { get; set; } = new List<AdoptionResourceTypeRow>();

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
