using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Common.Entities.CopilotAdoption
{
    #region Tuning

    /// <summary>
    /// Every threshold and weight the adoption maths uses, in one overridable place.
    ///
    /// These are judgement calls, not facts, and this tool is used to justify licence spend and to
    /// pick who gets training - so they must be inspectable, adjustable and unit-testable rather than
    /// buried as magic numbers inside a query. The API returns the values it used alongside the
    /// numbers, so a figure in a board pack can always be traced back to the rule that produced it.
    /// </summary>
    public class CopilotAdoptionOptions
    {
        /// <summary>Length of the reporting window in days. 28 matches Microsoft's own D28 usage reports.</summary>
        public int WindowDays { get; set; } = 28;

        /// <summary>
        /// How far back to look for <i>any</i> prior Copilot activity, used to tell "never touched it"
        /// apart from "used to use it and stopped" - two populations that need completely different
        /// interventions (onboarding vs re-engagement, or reclaiming the seat).
        ///
        /// Bounded rather than unlimited because it decides how much of <c>copilot_chats</c> the query
        /// reads: on a large tenant an unbounded lifetime scan of the audit history is the difference
        /// between a report that returns and one that times out.
        /// </summary>
        public int HistoryDays { get; set; } = 365;

        /// <summary>
        /// Working days per calendar week, used to convert the window into the number of days a fully
        /// engaged user could realistically be active. Scoring against calendar days would cap even a
        /// daily user at ~71%, which is not a number anyone should have to explain to a CFO.
        /// </summary>
        public double WorkingDaysPerWeek { get; set; } = 5;

        /// <summary>
        /// Share of available working days a user must be active on to score full marks for frequency.
        /// 0.6 = active on ~3 days in a typical 5-day week, which is a genuine habit rather than
        /// perfection.
        /// </summary>
        public double FrequencyTargetRatio { get; set; } = 0.6;

        /// <summary>Interactions per active day that score full marks for depth of use.</summary>
        public double DepthTargetInteractionsPerActiveDay { get; set; } = 5;

        /// <summary>Distinct Copilot surfaces (Teams, Word, Outlook, Chat...) that score full marks for breadth.</summary>
        public double BreadthTargetApps { get; set; } = 3;

        /// <summary>Weight of the frequency component in the engagement score. The three weights should sum to 1.</summary>
        public double FrequencyWeight { get; set; } = 0.5;

        /// <summary>Weight of the depth component in the engagement score.</summary>
        public double DepthWeight { get; set; } = 0.3;

        /// <summary>Weight of the breadth component in the engagement score.</summary>
        public double BreadthWeight { get; set; } = 0.2;

        /// <summary>Score at or above which a user is a <see cref="AdoptionBand.Champion"/>.</summary>
        public double ChampionScore { get; set; } = 75;

        /// <summary>Score at or above which a user is <see cref="AdoptionBand.Established"/> (the "habit formed" line).</summary>
        public double EstablishedScore { get; set; } = 50;

        /// <summary>Score at or above which a user is <see cref="AdoptionBand.Developing"/>.</summary>
        public double DevelopingScore { get; set; } = 25;

        #region Licence-opportunity tuning

        /// <summary>
        /// Weight of "already using Copilot Chat without a seat" in the licence-opportunity score.
        /// The heaviest weight by design: it is the only signal that is evidence of demand for Copilot
        /// specifically, rather than an inference from general Microsoft 365 activity.
        /// </summary>
        public double OpportunityUnlicensedCopilotWeight { get; set; } = 35;

        /// <summary>Weight of Teams collaboration volume (messages, meetings, calls).</summary>
        public double OpportunityCollaborationWeight { get; set; } = 25;

        /// <summary>Weight of email volume (sent + read).</summary>
        public double OpportunityEmailWeight { get; set; } = 20;

        /// <summary>Weight of document work (SharePoint / OneDrive files viewed or edited).</summary>
        public double OpportunityDocumentWeight { get; set; } = 20;

        /// <summary>Unlicensed Copilot interactions in the window that score full marks for that component.</summary>
        public double OpportunityCopilotTarget { get; set; } = 20;

        /// <summary>Teams messages + meetings on the latest daily snapshot that score full marks.</summary>
        public double OpportunityCollaborationTarget { get; set; } = 60;

        /// <summary>Emails sent + read on the latest daily snapshot that score full marks.</summary>
        public double OpportunityEmailTarget { get; set; } = 80;

        /// <summary>Files viewed or edited on the latest daily snapshot that score full marks.</summary>
        public double OpportunityDocumentTarget { get; set; } = 40;

        /// <summary>
        /// Opportunity score at or above which an unlicensed user is counted as a recommended licence
        /// candidate in the headline figures.
        /// </summary>
        public double OpportunityRecommendScore { get; set; } = 50;

        #endregion

        /// <summary>
        /// Hard ceiling on how many licensed users are pulled into memory to be scored. The scored set
        /// is held in C# (not SQL) so that the scoring rules have exactly one implementation, which is
        /// unit-testable and shared with any future scheduled report - see
        /// <see cref="CopilotAdoptionScoring"/>. Copilot seats are purchased individually, so even a
        /// very large customer is far below this; if it is ever hit the result carries an explicit
        /// warning rather than silently truncating a licence-spend report.
        /// </summary>
        public int MaxLicensedUsersScored { get; set; } = 50000;

        /// <summary>How many unlicensed candidates the database ranks and returns for the opportunity list.</summary>
        public int MaxOpportunityCandidates { get; set; } = 5000;

        /// <summary>
        /// Microsoft publishes the usage reports a couple of days in arrears and keeps back-filling a
        /// report date for a short while after it appears, so snapshots newer than this are ignored.
        /// Mirrors the Reports area's <c>UsageReportLagDays</c>.
        /// </summary>
        public int UsageReportLagDays { get; set; } = 3;

        /// <summary>Number of segments (departments, countries...) returned in the breakdown charts.</summary>
        public int TopSegments { get; set; } = 10;

        /// <summary>
        /// Minimum seats a segment needs before it appears in the "adoption by department" chart. A
        /// department with two seats and one active user is a 50% data point that means nothing, and
        /// putting it in front of an executive invites the wrong decision.
        /// </summary>
        public int MinSeatsPerSegment { get; set; } = 5;

        public static CopilotAdoptionOptions Default => new CopilotAdoptionOptions();
    }

    #endregion

    #region Licence classification

    /// <summary>A row of <c>dbo.license_types</c> plus how many users hold it.</summary>
    public class LicenceTypeRow
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public string SkuPartNumber { get; set; }

        public int AssignedUsers { get; set; }
    }

    /// <summary>A licence type and whether the tool counted it as a Microsoft 365 Copilot seat.</summary>
    public class LicenceTypeClassification
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("skuPartNumber")]
        public string SkuPartNumber { get; set; }

        [JsonProperty("assignedUsers")]
        public int AssignedUsers { get; set; }

        [JsonProperty("isCopilotSeat")]
        public bool IsCopilotSeat { get; set; }
    }

    #endregion

    #region Adoption bands

    /// <summary>
    /// How embedded Copilot is in one licensed user's working week. Ordered from worst to best so a
    /// distribution reads left to right as a maturity curve.
    ///
    /// The split between <see cref="NeverUsed"/> and <see cref="Dormant"/> is the one that pays for
    /// itself: a user who never started needs onboarding, a user who tried Copilot and stopped needs
    /// either a conversation or their seat reclaiming, and averaging the two together hides both.
    /// </summary>
    public enum AdoptionBand
    {
        /// <summary>Holds a seat and has no recorded Copilot activity at all within the history window.</summary>
        NeverUsed = 0,

        /// <summary>Used Copilot before the reporting window but not once inside it.</summary>
        Dormant = 1,

        /// <summary>Some activity in the window, but well below a working habit.</summary>
        Trialling = 2,

        /// <summary>Using Copilot regularly enough to be building a habit.</summary>
        Developing = 3,

        /// <summary>Copilot is part of the working week.</summary>
        Established = 4,

        /// <summary>Deep, frequent, multi-app use - the people to recruit as internal advocates.</summary>
        Champion = 5,
    }

    #endregion

    #region Per-user rows

    /// <summary>
    /// One licensed user's raw, un-scored signals, exactly as the database returns them. Kept separate
    /// from the scored row so the scoring rules can be unit-tested against hand-written inputs with no
    /// database involved.
    /// </summary>
    public class LicensedUserUsageRow
    {
        public int UserId { get; set; }

        public string UserPrincipalName { get; set; }

        public string Mail { get; set; }

        public string Department { get; set; }

        public string JobTitle { get; set; }

        public string Country { get; set; }

        public string OfficeLocation { get; set; }

        public string CompanyName { get; set; }

        public string ManagerUserPrincipalName { get; set; }

        public bool? AccountEnabled { get; set; }

        /// <summary>Copilot seat SKUs held, comma separated (a user can hold more than one).</summary>
        public string SeatLicences { get; set; }

        #region Audit-log derived (all users, including Copilot Chat with no seat)

        /// <summary>Copilot interactions inside the reporting window.</summary>
        public long Interactions { get; set; }

        /// <summary>Distinct calendar days inside the window with at least one interaction.</summary>
        public int ActiveDays { get; set; }

        /// <summary>Distinct Copilot surfaces (app hosts) used inside the window.</summary>
        public int AppsUsed { get; set; }

        /// <summary>Interactions inside the window attributed to Microsoft 365 Copilot Cowork.</summary>
        public long CoworkInteractions { get; set; }

        /// <summary>Distinct Copilot agents used inside the window.</summary>
        public int AgentsUsed { get; set; }

        /// <summary>Earliest interaction within the history window (not necessarily all time).</summary>
        public DateTime? FirstInteractionUtc { get; set; }

        /// <summary>Most recent interaction within the history window.</summary>
        public DateTime? LastInteractionUtc { get; set; }

        /// <summary>Interactions between the start of the history window and the start of the reporting window.</summary>
        public long PriorInteractions { get; set; }

        #endregion

        #region Microsoft Graph Copilot usage report (licensed users only)

        /// <summary>Prompts submitted across all Copilot apps, per Microsoft's own report.</summary>
        public int? ReportPrompts { get; set; }

        /// <summary>Days active in Microsoft's report window.</summary>
        public int? ReportActiveDays { get; set; }

        /// <summary>
        /// How many Copilot apps Microsoft's report shows activity in during the reporting window,
        /// counted from its per-app last-activity dates. Only used when the audit import is
        /// unavailable, where it is the sole breadth-of-use signal.
        /// </summary>
        public int? ReportAppsUsed { get; set; }

        /// <summary>Last activity date Microsoft reports for this user, across any Copilot app.</summary>
        public DateTime? ReportLastActivityUtc { get; set; }

        /// <summary>Last date Microsoft reports the user used a Copilot agent.</summary>
        public DateTime? ReportAgentLastActivityUtc { get; set; }

        #endregion
    }

    /// <summary>A licensed user with the adoption maths applied. This is what the UI lists and the CSV exports.</summary>
    public class LicensedUserAdoptionRow
    {
        [JsonProperty("userId")]
        public int UserId { get; set; }

        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }

        [JsonProperty("mail")]
        public string Mail { get; set; }

        [JsonProperty("department")]
        public string Department { get; set; }

        [JsonProperty("jobTitle")]
        public string JobTitle { get; set; }

        [JsonProperty("country")]
        public string Country { get; set; }

        [JsonProperty("officeLocation")]
        public string OfficeLocation { get; set; }

        [JsonProperty("companyName")]
        public string CompanyName { get; set; }

        [JsonProperty("manager")]
        public string ManagerUserPrincipalName { get; set; }

        [JsonProperty("accountEnabled")]
        public bool? AccountEnabled { get; set; }

        [JsonProperty("seatLicences")]
        public string SeatLicences { get; set; }

        [JsonProperty("interactions")]
        public long Interactions { get; set; }

        [JsonProperty("activeDays")]
        public int ActiveDays { get; set; }

        [JsonProperty("expectedActiveDays")]
        public double ExpectedActiveDays { get; set; }

        [JsonProperty("appsUsed")]
        public int AppsUsed { get; set; }

        [JsonProperty("agentsUsed")]
        public int AgentsUsed { get; set; }

        [JsonProperty("coworkInteractions")]
        public long CoworkInteractions { get; set; }

        [JsonProperty("usedCowork")]
        public bool UsedCowork { get; set; }

        [JsonProperty("firstInteractionUtc")]
        public DateTime? FirstInteractionUtc { get; set; }

        [JsonProperty("lastInteractionUtc")]
        public DateTime? LastInteractionUtc { get; set; }

        [JsonProperty("daysSinceLastUse")]
        public int? DaysSinceLastUse { get; set; }

        [JsonProperty("reportPrompts")]
        public int? ReportPrompts { get; set; }

        [JsonProperty("reportActiveDays")]
        public int? ReportActiveDays { get; set; }

        [JsonProperty("reportLastActivityUtc")]
        public DateTime? ReportLastActivityUtc { get; set; }

        /// <summary>Overall engagement, 0-100. See <see cref="CopilotAdoptionScoring"/> for the formula.</summary>
        [JsonProperty("adoptionScore")]
        public double AdoptionScore { get; set; }

        /// <summary>Frequency component, 0-100 (how many of the available working days were used).</summary>
        [JsonProperty("frequencyScore")]
        public double FrequencyScore { get; set; }

        /// <summary>Depth component, 0-100 (interactions per active day).</summary>
        [JsonProperty("depthScore")]
        public double DepthScore { get; set; }

        /// <summary>Breadth component, 0-100 (how many Copilot surfaces were used).</summary>
        [JsonProperty("breadthScore")]
        public double BreadthScore { get; set; }

        [JsonProperty("band")]
        public AdoptionBand Band { get; set; }

        /// <summary>Human-readable band, so the CSV and the UI never disagree about the wording.</summary>
        [JsonProperty("bandName")]
        public string BandName { get; set; }

        /// <summary>
        /// Which signal the score was built from: <c>audit</c> (our own Copilot audit-log import, which
        /// matches the requested window exactly) or <c>usageReport</c> (Microsoft's per-user report,
        /// used when the audit import is unavailable - its window is Microsoft's, not ours).
        /// Exported so a number can never be quoted without knowing where it came from.
        /// </summary>
        [JsonProperty("signalSource")]
        public string SignalSource { get; set; }

        /// <summary>
        /// The single recommended next step for this user, in plain English. This is what makes the
        /// export actionable rather than merely informative.
        /// </summary>
        [JsonProperty("recommendedAction")]
        public string RecommendedAction { get; set; }
    }

    /// <summary>
    /// One unlicensed user's raw signals: their Microsoft 365 activity, plus any Copilot Chat use they
    /// already have without a seat.
    /// </summary>
    public class UnlicensedUserSignalRow
    {
        public int UserId { get; set; }

        public string UserPrincipalName { get; set; }

        public string Mail { get; set; }

        public string Department { get; set; }

        public string JobTitle { get; set; }

        public string Country { get; set; }

        public string OfficeLocation { get; set; }

        public string CompanyName { get; set; }

        public string ManagerUserPrincipalName { get; set; }

        /// <summary>Copilot interactions in the window with no Copilot seat assigned (i.e. Copilot Chat).</summary>
        public long UnlicensedCopilotInteractions { get; set; }

        public int UnlicensedCopilotActiveDays { get; set; }

        public DateTime? LastCopilotInteractionUtc { get; set; }

        /// <summary>Teams chat + channel messages on the latest settled usage-report snapshot.</summary>
        public long TeamsMessages { get; set; }

        /// <summary>Teams meetings attended on the latest settled usage-report snapshot.</summary>
        public long TeamsMeetings { get; set; }

        public long EmailsSent { get; set; }

        public long EmailsRead { get; set; }

        /// <summary>SharePoint + OneDrive files viewed or edited on the latest settled snapshot.</summary>
        public long FilesViewedOrEdited { get; set; }

        /// <summary>Most recent activity date across the Microsoft 365 usage reports.</summary>
        public DateTime? LastM365ActivityUtc { get; set; }
    }

    /// <summary>An unlicensed user ranked as a candidate for a Copilot seat.</summary>
    public class LicenceOpportunityRow
    {
        [JsonProperty("userId")]
        public int UserId { get; set; }

        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }

        [JsonProperty("mail")]
        public string Mail { get; set; }

        [JsonProperty("department")]
        public string Department { get; set; }

        [JsonProperty("jobTitle")]
        public string JobTitle { get; set; }

        [JsonProperty("country")]
        public string Country { get; set; }

        [JsonProperty("officeLocation")]
        public string OfficeLocation { get; set; }

        [JsonProperty("companyName")]
        public string CompanyName { get; set; }

        [JsonProperty("manager")]
        public string ManagerUserPrincipalName { get; set; }

        [JsonProperty("unlicensedCopilotInteractions")]
        public long UnlicensedCopilotInteractions { get; set; }

        [JsonProperty("unlicensedCopilotActiveDays")]
        public int UnlicensedCopilotActiveDays { get; set; }

        [JsonProperty("lastCopilotInteractionUtc")]
        public DateTime? LastCopilotInteractionUtc { get; set; }

        [JsonProperty("teamsMessages")]
        public long TeamsMessages { get; set; }

        [JsonProperty("teamsMeetings")]
        public long TeamsMeetings { get; set; }

        [JsonProperty("emailsSent")]
        public long EmailsSent { get; set; }

        [JsonProperty("emailsRead")]
        public long EmailsRead { get; set; }

        [JsonProperty("filesViewedOrEdited")]
        public long FilesViewedOrEdited { get; set; }

        [JsonProperty("lastM365ActivityUtc")]
        public DateTime? LastM365ActivityUtc { get; set; }

        /// <summary>0-100. Higher means a stronger business case for giving this person a seat.</summary>
        [JsonProperty("opportunityScore")]
        public double OpportunityScore { get; set; }

        [JsonProperty("copilotDemandScore")]
        public double CopilotDemandScore { get; set; }

        [JsonProperty("collaborationScore")]
        public double CollaborationScore { get; set; }

        [JsonProperty("emailScore")]
        public double EmailScore { get; set; }

        [JsonProperty("documentScore")]
        public double DocumentScore { get; set; }

        /// <summary>True when the score clears <see cref="CopilotAdoptionOptions.OpportunityRecommendScore"/>.</summary>
        [JsonProperty("recommended")]
        public bool Recommended { get; set; }

        /// <summary>Plain-English justification, safe to paste into a licence request.</summary>
        [JsonProperty("rationale")]
        public string Rationale { get; set; }
    }

    #endregion

    #region Charts and summary

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

        /// <summary>The snapshot date the Microsoft 365 workload figures came from.</summary>
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

        /// <summary>Users holding at least one Microsoft 365 Copilot seat.</summary>
        [JsonProperty("licensedUsers")]
        public int LicensedUsers { get; set; }

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

        /// <summary>Weekly active licensed users, so a trend is visible rather than a single snapshot.</summary>
        [JsonProperty("weeklyTrend")]
        public List<AdoptionSeries> WeeklyTrend { get; set; } = new List<AdoptionSeries>();

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

    #endregion
}
