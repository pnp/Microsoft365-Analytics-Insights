using Common.Entities.Copilot;
using Common.Entities.AgentCosts;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
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
        /// <summary>
        /// Stable key for product-authored empty bucket labels such as "(no manager)".
        /// <c>null</c> means <see cref="AdoptionSegmentRow.Segment"/> is tenant data and must be rendered verbatim.
        /// </summary>
        [JsonProperty("emptySegmentKey")]
        public string EmptySegmentKey { get; set; }

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

    /// <summary>
    /// Adoption for one email domain: one of the organisations that share this tenant.
    ///
    /// <para>Richer than a plain <see cref="AdoptionSegmentRow"/> because a domain is usually a whole
    /// company rather than a function within one, so the question being asked of it is different. The
    /// interesting comparison is not only "is this org using its seats" but "is it using them while
    /// also running unlicensed Copilot Chat, and does it have people who should be bought seats" - the
    /// three together say whether an acquired business needs training, more licences, or fewer.</para>
    /// </summary>
    public class AdoptionDomainRow : AdoptionSegmentRow
    {
        /// <summary>
        /// Copilot seats in this domain that look reclaimable, on the same confidence tiering the
        /// headline reclaim figure uses.
        /// </summary>
        [JsonProperty("reclaimableSeats")]
        public int ReclaimableSeats { get; set; }

        /// <summary>
        /// Audit interactions per licensed user, normalised to a month so the column does not change
        /// meaning with the reporting period. Report-sourced rows are excluded from the numerator for
        /// the same reason they are everywhere else: Microsoft prompt counts and audit interactions are
        /// different units.
        /// </summary>
        [JsonProperty("interactionsPerLicensedUser")]
        public double InteractionsPerLicensedUser { get; set; }

        /// <summary>People in this domain using Copilot Chat in the window with no seat assigned.</summary>
        [JsonProperty("unlicensedActiveUsers")]
        public int UnlicensedActiveUsers { get; set; }

        /// <summary>People in this domain the licence-opportunity ranking recommends buying a seat for.</summary>
        [JsonProperty("recommendedForLicence")]
        public int RecommendedForLicence { get; set; }

        /// <summary>
        /// Seat holders in this domain scored as prime Cowork candidates. Zero on a tenant whose Cowork
        /// readiness analysis did not run, which is why the panel reads
        /// <see cref="CopilotAdoptionSummary.CoworkReadinessAvailable"/> before showing the column.
        /// </summary>
        [JsonProperty("coworkPrimeCandidates")]
        public int CoworkPrimeCandidates { get; set; }

        /// <summary>
        /// True when the seats in this domain belong to external guests rather than to members of the
        /// tenant. Worth flagging because it changes what the row means: a guest domain is a partner
        /// being collaborated with, not a part of the business that can be sent on a training course.
        /// </summary>
        [JsonProperty("external")]
        public bool External { get; set; }
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

        /// <summary>Purchased-vs-assigned Copilot seat inventory. Purchased is null when Graph subscribedSkUs was unavailable.</summary>
        [JsonProperty("purchasedCopilotSeats")]
        public int? PurchasedCopilotSeats { get; set; }

        [JsonProperty("unassignedCopilotSeats")]
        public int? UnassignedCopilotSeats { get; set; }

        [JsonProperty("subscribedSkusAvailable")]
        public bool SubscribedSkusAvailable { get; set; }

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

        [JsonProperty("reclaimCaveatKey")]
        public string ReclaimCaveatKey { get; set; }

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
        /// The modelled Cowork estimate for the recommended cohort - the people ready for Cowork now, and
        /// the Cowork tab's headline. Cowork only: the time Cowork could give back on top of what these
        /// people's Copilot licences already save. Every figure inside is an assumption applied to
        /// observed volume - see <see cref="CoworkValueEstimate"/>.
        /// </summary>
        [JsonProperty("coworkValueEstimate")]
        public CoworkValueEstimate CoworkValueEstimate { get; set; } = new CoworkValueEstimate();

        /// <summary>
        /// The same model over EVERY scored Copilot seat holder: the ceiling if every seat holder used
        /// Cowork, rather than only the people ready for it today.
        /// </summary>
        /// <remarks>
        /// Published next to <see cref="CoworkValueEstimate"/> so the page can say both "where to start"
        /// and "how far it goes". Built from the same rows, options and task rate, so the recommended
        /// cohort can never model more time than the population it is drawn from. It overstates rather
        /// than understates: it projects the tenant's Cowork users' average onto people who are not ready
        /// for Cowork yet, and those people would likely run fewer tasks. It is every bit as modelled as
        /// its sibling and carries the same assumptions.
        /// </remarks>
        [JsonProperty("coworkFullRolloutEstimate")]
        public CoworkValueEstimate CoworkFullRolloutEstimate { get; set; } = new CoworkValueEstimate();

        #endregion

        #region Licence opportunity

        /// <summary>Users with no Copilot seat who nevertheless used Copilot Chat inside the window.</summary>
        [JsonProperty("unlicensedActiveUsers")]
        public int UnlicensedActiveUsers { get; set; }

        /// <summary>Unlicensed users scoring at or above the recommendation threshold.</summary>
        [JsonProperty("recommendedForLicence")]
        public int RecommendedForLicence { get; set; }

        /// <summary>
        /// The modelled licence estimate: the time Microsoft 365 Copilot could give back to every person
        /// recommended for a licence, and the Licence opportunities tab's headline. Every figure inside is
        /// an assumption applied to observed volume - see <see cref="LicenceValueEstimate"/>. Empty when
        /// nobody is recommended or the Microsoft 365 usage reports are unavailable, rather than a
        /// modelled zero.
        /// </summary>
        [JsonProperty("licenceOpportunityEstimate")]
        public LicenceValueEstimate LicenceOpportunityEstimate { get; set; } = new LicenceValueEstimate();

        /// <summary>
        /// The same model over the recommended candidates already using Copilot Chat without a licence -
        /// the ones the "Already using Copilot" filter shows - published beside
        /// <see cref="LicenceOpportunityEstimate"/> as the strongest part of the case: demand is observed
        /// rather than inferred. Some of it may already be realised through Copilot Chat.
        /// </summary>
        [JsonProperty("licenceChatUsersEstimate")]
        public LicenceValueEstimate LicenceChatUsersEstimate { get; set; } = new LicenceValueEstimate();

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

        /// <summary>
        /// Adoption by email domain - i.e. by the organisations that share this tenant.
        ///
        /// <para>A tenant assembled from acquisitions carries several verified domains, and they adopt
        /// Copilot very differently: the company that ran the rollout is not the one whose seats were
        /// handed out during a migration. Department cuts across all of them and hides exactly that
        /// difference, which is why this is a dimension in its own right rather than another
        /// <c>adoptionBy...</c> segment list.</para>
        ///
        /// <para>Worst adoption first, then largest, like every other breakdown here - the reading
        /// order of an enablement plan.</para>
        /// </summary>
        [JsonProperty("emailDomains")]
        public List<AdoptionDomainRow> EmailDomains { get; set; } = new List<AdoptionDomainRow>();

        /// <summary>
        /// The email domain this whole summary was narrowed to, or <c>null</c> for the whole tenant.
        /// </summary>
        /// <remarks>
        /// Echoed back rather than assumed from the request, so the page can state on screen which
        /// population every figure describes. A dashboard that is silently showing one subsidiary is
        /// the fastest way to get a licence decision wrong.
        /// </remarks>
        [JsonProperty("scopedEmailDomain")]
        public string ScopedEmailDomain { get; set; }

        /// <summary>
        /// Sections that stayed tenant-wide when <see cref="ScopedEmailDomain"/> is set, because they
        /// come from aggregate queries that carry no per-user identity to filter on.
        /// </summary>
        /// <remarks>
        /// Published rather than silently dropped or silently left unfiltered. Both of those are worse:
        /// dropping loses real information, and leaving a tenant-wide chart unlabelled next to a scoped
        /// one invites the reader to compare two different populations. Naming them lets the UI badge
        /// each one, which is the only honest option. Values are compile-time constants from
        /// <see cref="CopilotAdoptionUnscopedSections"/> - never anything derived from tenant data.
        /// </remarks>
        [JsonProperty("unscopedSections")]
        public List<string> UnscopedSections { get; set; } = new List<string>();

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

        [JsonProperty("warningDetails")]
        public List<CopilotAdoptionWarningDetail> WarningDetails { get; set; } = new List<CopilotAdoptionWarningDetail>();

        /// <summary>
        /// The warnings that were already present when scoring began - i.e. the ones about data
        /// sources, failed queries and capped result sets rather than about the population.
        /// </summary>
        /// <remarks>
        /// Snapshotted by <c>CopilotAdoptionService.FinaliseSummary</c> so a domain-scoped view can
        /// inherit exactly those and then raise its own population warnings for its own numbers.
        /// Without the split a scoped summary either loses "the audit import is behind" (which is still
        /// true of the subset) or repeats "N users were scored from the usage report" with the
        /// tenant-wide N next to the scoped one.
        ///
        /// Not serialised: it is a build-time detail of how a scoped summary is assembled, and the UI
        /// only ever renders <see cref="Warnings"/>.
        /// </remarks>
        [JsonIgnore]
        public List<string> SourceWarnings { get; set; } = new List<string>();

        [JsonIgnore]
        public List<CopilotAdoptionWarningDetail> SourceWarningDetails { get; set; } = new List<CopilotAdoptionWarningDetail>();

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

        [JsonProperty("warningDetails")]
        public List<CopilotAdoptionWarningDetail> WarningDetails { get; set; } = new List<CopilotAdoptionWarningDetail>();
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

        [JsonProperty("warningDetails")]
        public List<CopilotAdoptionWarningDetail> WarningDetails { get; set; } = new List<CopilotAdoptionWarningDetail>();
    }

    public class CopilotAdoptionWarningDetail
    {
        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("values")]
        public Dictionary<string, object> Values { get; set; } = new Dictionary<string, object>(StringComparer.Ordinal);

        public CopilotAdoptionWarningDetail Clone()
        {
            return new CopilotAdoptionWarningDetail
            {
                Key = Key,
                Values = new Dictionary<string, object>(Values ?? new Dictionary<string, object>(), StringComparer.Ordinal),
            };
        }
    }

    public static class CopilotAdoptionWarningKeys
    {
        public const string NoLicenceInformation = "noLicenceInformation";
        public const string NoCopilotLicences = "noCopilotLicences";
        public const string CopilotBackfillPending = "copilotBackfillPending";
        public const string CopilotUsageReportConcealed = "copilotUsageReportConcealed";
        public const string NoCopilotData = "noCopilotData";
        public const string AuditMissingUsingUsageReport = "auditMissingUsingUsageReport";
        public const string AgentInventoryCapped = "agentInventoryCapped";
        public const string UnlicensedUsageCapped = "unlicensedUsageCapped";
        public const string LicensedUsersSubset = "licensedUsersSubset";
        public const string LicenceOpportunitiesNoSources = "licenceOpportunitiesNoSources";
        public const string LicenceCandidatesAuditOnly = "licenceCandidatesAuditOnly";
        public const string CoworkReadinessNoSources = "coworkReadinessNoSources";
        public const string CoworkM365UsageMissing = "coworkM365UsageMissing";
        public const string CoworkUsageReportMissing = "coworkUsageReportMissing";
        public const string CoworkAuditMissing = "coworkAuditMissing";
        public const string UsageReportSourcedUsers = "usageReportSourcedUsers";
        public const string UsageReportWindowMismatch = "usageReportWindowMismatch";
        public const string CoworkEligibilityUnknown = "coworkEligibilityUnknown";
        public const string PurchasedSeatsUnknown = "purchasedSeatsUnknown";
        public const string SkuSeatMismatch = "skuSeatMismatch";
        public const string CoworkFluencyMissingAll = "coworkFluencyMissingAll";
        public const string CoworkFluencyPartial = "coworkFluencyPartial";
        public const string CouldNotLoad = "couldNotLoad";
        public const string ReclaimCaveat = "copilotAdoption.server.reclaimCaveat";
    }

    public static class CopilotAdoptionWarnings
    {
        private static readonly Dictionary<string, string> Templates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { CopilotAdoptionWarningKeys.NoLicenceInformation, "No licence information has been imported, so Copilot licences cannot be identified. Enable the user metadata import to use this tool." },
            { CopilotAdoptionWarningKeys.NoCopilotLicences, "No Microsoft 365 Copilot licences were found in this tenant. Adoption cannot be reported until at least one Copilot licence is assigned and the user import has run." },
            { CopilotAdoptionWarningKeys.CopilotBackfillPending, "Some Copilot interactions have not finished being upgraded to the new reporting format, so every Copilot figure below is currently too low. This repairs itself automatically on the next few import cycles - re-run this report once the importer has caught up. If it persists, check that the Office 365 activity importer web job is running." },
            { CopilotAdoptionWarningKeys.CopilotUsageReportConcealed, "This tenant has 'concealed user information' enabled, so Microsoft's per-user Copilot report returns hashed identities and cannot be used. Per-user figures below come from the Copilot audit log, which is unaffected by that setting." },
            { CopilotAdoptionWarningKeys.NoCopilotData, "Neither the Copilot audit import nor Microsoft's Copilot usage report has any data for this period, so every licensed user will appear as unused. Check the Health page before acting on these numbers." },
            { CopilotAdoptionWarningKeys.AuditMissingUsingUsageReport, "The Copilot audit import has no data for this period, so per-user engagement is derived from Microsoft's own usage report. That report covers Microsoft's aggregation window rather than the period selected here, and excludes unlicensed Copilot Chat use entirely." },
            { CopilotAdoptionWarningKeys.AgentInventoryCapped, "The agent inventory was capped at {maxAgents} agents, so the agent figures are a floor rather than a total." },
            { CopilotAdoptionWarningKeys.UnlicensedUsageCapped, "Unlicensed Copilot usage was capped at {maxUsers} users, so those figures are a floor rather than a total." },
            { CopilotAdoptionWarningKeys.LicensedUsersSubset, "This tenant holds {licensedUsers} Copilot licences, but only {scoredUsers} users could be analysed in one pass. Every rate and breakdown below describes those {scoredUsers} users, not the whole tenant - they are not tenant-wide figures and must not be quoted as such. Because the drill-down query is ordered by internal user id, the oldest user records are over-represented and the newest joiners or newly onboarded subsidiaries are excluded first; the subset is reproducible, but not representative." },
            { CopilotAdoptionWarningKeys.LicenceOpportunitiesNoSources, "Licence opportunities need either the Copilot audit import or the Microsoft 365 usage reports. Neither has data, so no candidates can be identified." },
            { CopilotAdoptionWarningKeys.LicenceCandidatesAuditOnly, "The Microsoft 365 usage reports are not available, so licence candidates are ranked only on unlicensed Copilot Chat use. Heavy Microsoft 365 users who have never tried Copilot will not appear." },
            { CopilotAdoptionWarningKeys.CoworkReadinessNoSources, "Cowork readiness needs the Cowork usage report, the Copilot audit import or the Microsoft 365 usage reports. None has data for this period, so no readiness assessment is possible." },
            { CopilotAdoptionWarningKeys.CoworkM365UsageMissing, "The Microsoft 365 usage reports are not available, so coordination load cannot be measured. Everyone will score zero on that axis and no one will be identified as a Cowork candidate. Enable the Microsoft 365 usage report import to use this tab." },
            { CopilotAdoptionWarningKeys.CoworkUsageReportMissing, "The first-party Cowork usage report is not available, so Cowork task counts, automation ratio and retention cannot be measured. Audit-derived Cowork interactions are retained only as a reconciliation signal." },
            { CopilotAdoptionWarningKeys.CoworkAuditMissing, "The Copilot audit import has no data for this period, so Cowork audit interactions cannot be reconciled against Microsoft's Cowork task report." },
            { CopilotAdoptionWarningKeys.UsageReportSourcedUsers, "{count} licensed user{userPlural} ({percentage}%) were scored from Microsoft's Copilot usage report because the audit import had no per-user signal for them. Their Microsoft prompt counts are not added to audit interaction totals, concentration, intensity or licensed/unlicensed interaction comparisons." },
            { CopilotAdoptionWarningKeys.UsageReportWindowMismatch, "Microsoft's pinned Copilot usage-report period is D{reportDays}, but this analysis window is D{analysisDays}. Report-sourced rows are kept in the adoption population so active people are not marked as never used, but a report-sourced row that would otherwise be a PROBABLE reclaim is excluded from reclaimable-seat totals rather than normalising prompt counts across unlike windows. Certain (disabled-account) seats are never held back this way, because a disabled account is not an inference from an absence of use. The band breakdown therefore counts more idle seats than the reclaim figure does; the difference is reported as \"held back for window mismatch\"." },
            { CopilotAdoptionWarningKeys.CoworkEligibilityUnknown, "Cowork adoption percentage is suppressed because Cowork eligibility is controlled by spending-policy scope and this import does not know that denominator. The deprecated Cowork agent entry is not used as an eligibility source." },
            { CopilotAdoptionWarningKeys.PurchasedSeatsUnknown, "Purchased and unassigned Copilot seats are unknown because Graph subscribedSkus/prepaidUnits has not been imported. Grant Organization.Read.All and rerun the user metadata import; the report deliberately does not show zero for unassigned seats when the purchase inventory is missing." },
            { CopilotAdoptionWarningKeys.SkuSeatMismatch, "Purchased and assigned Copilot seats disagree for {skuName}: Graph reports {purchased} purchased but {assigned} assigned, so unassigned seats are shown as Unknown rather than zero." },
            { CopilotAdoptionWarningKeys.CoworkFluencyMissingAll, "Cowork readiness was measured, but the licensed-user analysis it takes Copilot fluency from did not complete, so the tab could not be scored. This is NOT a missing usage report import - the Cowork signals imported fine. Check the Health page and re-run." },
            { CopilotAdoptionWarningKeys.CoworkFluencyPartial, "Cowork readiness: {withoutFluency} of {total} seat holders were scored without a Copilot fluency figure, because they fall outside the {maxLicensed}-row licensed-user analysis this tab joins against. Their fluency reads as 0 rather than as unknown, so they band lower than they should - most will show as \"build fluency first\". Treat the tier of those rows as unreliable; the rest of the tab is unaffected." },
            { CopilotAdoptionWarningKeys.CouldNotLoad, "Could not load {description}: {message}" },
        };

        public static IReadOnlyDictionary<string, string> EnglishTemplates => Templates;

        public static string RenderEnglish(string key, IDictionary<string, object> values = null)
        {
            string template;
            if (!Templates.TryGetValue(key, out template)) return key;
            var rendered = template;
            if (values == null) return rendered;
            foreach (var value in values)
            {
                rendered = rendered.Replace("{" + value.Key + "}", FormatEnglish(value.Value));
            }
            return rendered;
        }

        public static CopilotAdoptionWarningDetail Detail(string key, IDictionary<string, object> values = null)
        {
            return new CopilotAdoptionWarningDetail
            {
                Key = key,
                Values = values == null
                    ? new Dictionary<string, object>(StringComparer.Ordinal)
                    : new Dictionary<string, object>(values, StringComparer.Ordinal),
            };
        }

        public static void Add(List<string> warnings, List<CopilotAdoptionWarningDetail> details, string key, IDictionary<string, object> values = null)
        {
            warnings.Add(RenderEnglish(key, values));
            details.Add(Detail(key, values));
        }

        public static void Add(CopilotAdoptionSummary summary, string key, IDictionary<string, object> values = null)
        {
            Add(summary.Warnings, summary.WarningDetails, key, values);
        }

        private static string FormatEnglish(object value)
        {
            if (value == null) return string.Empty;
            if (value is double) return ((double)value).ToString("N1", CultureInfo.GetCultureInfo("en-GB"));
            if (value is float) return ((float)value).ToString("N1", CultureInfo.GetCultureInfo("en-GB"));
            if (value is decimal) return ((decimal)value).ToString("N1", CultureInfo.GetCultureInfo("en-GB"));
            if (value is int) return ((int)value).ToString("N0", CultureInfo.GetCultureInfo("en-GB"));
            if (value is long) return ((long)value).ToString("N0", CultureInfo.GetCultureInfo("en-GB"));
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }
}
