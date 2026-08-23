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
        [JsonProperty("windowDays")]
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
        [JsonProperty("historyDays")]
        public int HistoryDays { get; set; } = 365;

        /// <summary>
        /// Working days per calendar week, used to convert the window into the number of days a fully
        /// engaged user could realistically be active. Scoring against calendar days would cap even a
        /// daily user at ~71%, which is not a number anyone should have to explain to a CFO.
        /// </summary>
        [JsonProperty("workingDaysPerWeek")]
        public double WorkingDaysPerWeek { get; set; } = 5;

        /// <summary>
        /// Share of available working days a user must be active on to score full marks for frequency.
        /// 0.6 = active on ~3 days in a typical 5-day week, which is a genuine habit rather than
        /// perfection.
        /// </summary>
        [JsonProperty("frequencyTargetRatio")]
        public double FrequencyTargetRatio { get; set; } = 0.6;

        /// <summary>Interactions per active day that score full marks for depth of use.</summary>
        [JsonProperty("depthTargetInteractionsPerActiveDay")]
        public double DepthTargetInteractionsPerActiveDay { get; set; } = 5;

        /// <summary>Distinct Copilot surfaces (Teams, Word, Outlook, Chat...) that score full marks for breadth.</summary>
        [JsonProperty("breadthTargetApps")]
        public double BreadthTargetApps { get; set; } = 3;

        /// <summary>Weight of the frequency component in the engagement score. The three weights should sum to 1.</summary>
        [JsonProperty("frequencyWeight")]
        public double FrequencyWeight { get; set; } = 0.5;

        /// <summary>Weight of the depth component in the engagement score.</summary>
        [JsonProperty("depthWeight")]
        public double DepthWeight { get; set; } = 0.3;

        /// <summary>Weight of the breadth component in the engagement score.</summary>
        [JsonProperty("breadthWeight")]
        public double BreadthWeight { get; set; } = 0.2;

        /// <summary>Score at or above which a user is a <see cref="AdoptionBand.Champion"/>.</summary>
        [JsonProperty("championScore")]
        public double ChampionScore { get; set; } = 75;

        /// <summary>Score at or above which a user is <see cref="AdoptionBand.Established"/> (the "habit formed" line).</summary>
        [JsonProperty("establishedScore")]
        public double EstablishedScore { get; set; } = 50;

        /// <summary>Score at or above which a user is <see cref="AdoptionBand.Developing"/>.</summary>
        [JsonProperty("developingScore")]
        public double DevelopingScore { get; set; } = 25;

        #region Habit-formation buckets

        /// <summary>
        /// Window length that the active-day habit buckets below are expressed in. Active days are
        /// normalised to this length before bucketing (a user active on 40 days of a 90-day window
        /// counts as 40 x 28/90 = 12.4 days a month), so the buckets mean the same thing whichever
        /// reporting period is selected. 28 days matches Microsoft's own usage-report month.
        /// </summary>
        [JsonProperty("habitBucketNormalisationDays")]
        public int HabitBucketNormalisationDays { get; set; } = 28;

        /// <summary>Normalised active days per month at or above which use is "Moderate" (below this it is "Infrequent").</summary>
        [JsonProperty("habitModerateMinDays")]
        public double HabitModerateMinDays { get; set; } = 6;

        /// <summary>Normalised active days per month at or above which use is "Frequent".</summary>
        [JsonProperty("habitFrequentMinDays")]
        public double HabitFrequentMinDays { get; set; } = 11;

        /// <summary>Normalised active days per month at or above which use is "Daily" - i.e. essentially every working day.</summary>
        [JsonProperty("habitDailyMinDays")]
        public double HabitDailyMinDays { get; set; } = 20;

        #endregion

        #region Agent inventory tuning

        /// <summary>
        /// How recently an agent must have been used to count as current. Below this it is "Keep";
        /// beyond it the agent starts attracting review.
        /// </summary>
        [JsonProperty("agentReviewInactiveDays")]
        public int AgentReviewInactiveDays { get; set; } = 30;

        /// <summary>Days of inactivity after which an agent is proposed for retirement.</summary>
        [JsonProperty("agentRetireInactiveDays")]
        public int AgentRetireInactiveDays { get; set; } = 90;

        /// <summary>
        /// An agent first seen within this many days is reported as New and exempted from review. A
        /// brand-new agent with three users is not failing, it has not started - and retiring it on
        /// that evidence is how an agent programme gets strangled in its first month.
        /// </summary>
        [JsonProperty("agentNewDays")]
        public int AgentNewDays { get; set; } = 30;

        /// <summary>
        /// Distinct users an agent needs before its usage is treated as adoption rather than as its
        /// author testing it. Matches the "minimum 3 users" convention used in Microsoft's own agent
        /// reporting.
        /// </summary>
        [JsonProperty("agentMinUsers")]
        public int AgentMinUsers { get; set; } = 3;

        /// <summary>How many agents the inventory query returns.</summary>
        [JsonProperty("maxAgents")]
        public int MaxAgents { get; set; } = 500;

        /// <summary>How many unlicensed Copilot users are pulled in to describe that population.</summary>
        [JsonProperty("maxUnlicensedUsersScored")]
        public int MaxUnlicensedUsersScored { get; set; } = 50000;

        #endregion

        #region Licence-opportunity tuning

        /// <summary>
        /// Weight of "already using Copilot Chat without a seat" in the licence-opportunity score.
        /// The heaviest weight by design: it is the only signal that is evidence of demand for Copilot
        /// specifically, rather than an inference from general Microsoft 365 activity.
        /// </summary>
        [JsonProperty("opportunityUnlicensedCopilotWeight")]
        public double OpportunityUnlicensedCopilotWeight { get; set; } = 35;

        /// <summary>Weight of Teams collaboration volume (messages, meetings, calls).</summary>
        [JsonProperty("opportunityCollaborationWeight")]
        public double OpportunityCollaborationWeight { get; set; } = 25;

        /// <summary>Weight of email volume (sent + read).</summary>
        [JsonProperty("opportunityEmailWeight")]
        public double OpportunityEmailWeight { get; set; } = 20;

        /// <summary>Weight of document work (SharePoint / OneDrive files viewed or edited).</summary>
        [JsonProperty("opportunityDocumentWeight")]
        public double OpportunityDocumentWeight { get; set; } = 20;

        /// <summary>Unlicensed Copilot interactions in the window that score full marks for that component.</summary>
        [JsonProperty("opportunityCopilotTarget")]
        public double OpportunityCopilotTarget { get; set; } = 20;

        /// <summary>Teams messages + meetings on the latest daily snapshot that score full marks.</summary>
        [JsonProperty("opportunityCollaborationTarget")]
        public double OpportunityCollaborationTarget { get; set; } = 60;

        /// <summary>Emails sent + read on the latest daily snapshot that score full marks.</summary>
        [JsonProperty("opportunityEmailTarget")]
        public double OpportunityEmailTarget { get; set; } = 80;

        /// <summary>Files viewed or edited on the latest daily snapshot that score full marks.</summary>
        [JsonProperty("opportunityDocumentTarget")]
        public double OpportunityDocumentTarget { get; set; } = 40;

        /// <summary>
        /// Opportunity score at or above which an unlicensed user is counted as a recommended licence
        /// candidate in the headline figures.
        /// </summary>
        [JsonProperty("opportunityRecommendScore")]
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
        [JsonProperty("maxLicensedUsersScored")]
        public int MaxLicensedUsersScored { get; set; } = 50000;

        /// <summary>How many unlicensed candidates the database ranks and returns for the opportunity list.</summary>
        [JsonProperty("maxOpportunityCandidates")]
        public int MaxOpportunityCandidates { get; set; } = 5000;

        /// <summary>
        /// Microsoft publishes the usage reports a couple of days in arrears and keeps back-filling a
        /// report date for a short while after it appears, so snapshots newer than this are ignored.
        /// Mirrors the Reports area's <c>UsageReportLagDays</c>.
        /// </summary>
        [JsonProperty("usageReportLagDays")]
        public int UsageReportLagDays { get; set; } = 3;

        /// <summary>Number of segments (departments, countries...) returned in the breakdown charts.</summary>
        [JsonProperty("topSegments")]
        public int TopSegments { get; set; } = 10;

        /// <summary>
        /// Minimum seats a segment needs before it appears in the "adoption by department" chart. A
        /// department with two seats and one active user is a 50% data point that means nothing, and
        /// putting it in front of an executive invites the wrong decision.
        /// </summary>
        [JsonProperty("minSeatsPerSegment")]
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

        /// <summary>
        /// The action as a short, stable code (<c>reclaim</c>, <c>reengage</c>, <c>coach</c>,
        /// <c>broaden</c>, <c>grow</c>, <c>sustain</c>, <c>advocate</c>).
        ///
        /// Split out from <see cref="RecommendedAction"/> so the UI can show a two-word tag per row and
        /// state the full explanation once, rather than repeating the identical paragraph down every
        /// row of a band. The prose stays in the CSV, where a department lead reads one row at a time
        /// and the repetition costs nothing.
        /// </summary>
        [JsonProperty("recommendedActionCode")]
        public string RecommendedActionCode { get; set; }

        /// <summary>Short display label for <see cref="RecommendedActionCode"/>, e.g. "Re-engage".</summary>
        [JsonProperty("recommendedActionLabel")]
        public string RecommendedActionLabel { get; set; }
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

    #region Agents

    /// <summary>What to do about an agent, worst first. Numeric values are stable for the UI.</summary>
    public enum AgentHealth
    {
        /// <summary>Dormant long enough that it is almost certainly abandoned.</summary>
        Retire = 0,

        /// <summary>Either going quiet, or being used by too few people to call it adopted.</summary>
        Review = 1,

        /// <summary>Too recently introduced to judge - deliberately exempt from review.</summary>
        New = 2,

        /// <summary>Current and genuinely adopted.</summary>
        Keep = 3,
    }

    /// <summary>Raw per-agent usage straight from the audit log.</summary>
    public class AgentUsageQueryRow
    {
        public int AgentId { get; set; }
        public string Name { get; set; }
        public string AgentKey { get; set; }
        public bool IsCustomAgent { get; set; }
        public long Interactions { get; set; }
        public int Users { get; set; }
        public int LicensedUsers { get; set; }
        public int ActiveDays { get; set; }
        public int AppsUsed { get; set; }
        public DateTime? FirstUsedUtc { get; set; }
        public DateTime? LastUsedUtc { get; set; }
    }

    /// <summary>
    /// One Copilot agent with the figures an inventory review needs, plus the verdict on it.
    ///
    /// Agents are counted across the whole tenant, licensed and unlicensed: an agent's worth to the
    /// organisation does not depend on the licence status of the people using it. The licensed share
    /// is carried separately so the two populations can still be told apart.
    /// </summary>
    public class AgentUsageRow
    {
        [JsonProperty("agentId")]
        public int AgentId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>The agent's identifier from the audit payload, e.g. a first-party Copilot agent id.</summary>
        [JsonProperty("agentKey")]
        public string AgentKey { get; set; }

        /// <summary>True for a customer-built agent, false for one Microsoft ships.</summary>
        [JsonProperty("isCustomAgent")]
        public bool IsCustomAgent { get; set; }

        [JsonProperty("interactions")]
        public long Interactions { get; set; }

        [JsonProperty("users")]
        public int Users { get; set; }

        [JsonProperty("licensedUsers")]
        public int LicensedUsers { get; set; }

        [JsonProperty("activeDays")]
        public int ActiveDays { get; set; }

        /// <summary>
        /// Distinct Copilot surfaces the agent was invoked from - its versatility. An agent that only
        /// ever runs in one host is doing a narrower job than its interaction count suggests.
        /// </summary>
        [JsonProperty("appsUsed")]
        public int AppsUsed { get; set; }

        [JsonProperty("interactionsPerUser")]
        public double InteractionsPerUser { get; set; }

        [JsonProperty("firstUsedUtc")]
        public DateTime? FirstUsedUtc { get; set; }

        [JsonProperty("lastUsedUtc")]
        public DateTime? LastUsedUtc { get; set; }

        [JsonProperty("daysSinceLastUse")]
        public int? DaysSinceLastUse { get; set; }

        [JsonProperty("health")]
        public AgentHealth Health { get; set; }

        [JsonProperty("healthName")]
        public string HealthName { get; set; }

        /// <summary>Why this agent got this verdict, in plain English.</summary>
        [JsonProperty("healthReason")]
        public string HealthReason { get; set; }
    }

    /// <summary>The agent estate at a glance.</summary>
    public class AgentEstateSummary
    {
        /// <summary>Agents used at least once inside the reporting period.</summary>
        [JsonProperty("activeAgents")]
        public int ActiveAgents { get; set; }

        /// <summary>Agents seen in the longer history window, whether or not they were used in the period.</summary>
        [JsonProperty("knownAgents")]
        public int KnownAgents { get; set; }

        [JsonProperty("customAgents")]
        public int CustomAgents { get; set; }

        /// <summary>Distinct people who used any agent in the period.</summary>
        [JsonProperty("agentUsers")]
        public int AgentUsers { get; set; }

        [JsonProperty("licensedAgentUsers")]
        public int LicensedAgentUsers { get; set; }

        [JsonProperty("agentInteractions")]
        public long AgentInteractions { get; set; }

        [JsonProperty("interactionsPerAgentUser")]
        public double InteractionsPerAgentUser { get; set; }

        /// <summary>The agent the most people use - the one whose retirement would be felt.</summary>
        [JsonProperty("mostPopularAgent")]
        public string MostPopularAgent { get; set; }

        /// <summary>The agent used across the most Copilot surfaces.</summary>
        [JsonProperty("mostVersatileAgent")]
        public string MostVersatileAgent { get; set; }

        /// <summary>Counts by <see cref="AgentHealth"/>, so the size of an inventory clean-up is visible.</summary>
        [JsonProperty("healthBreakdown")]
        public List<AdoptionCategory> HealthBreakdown { get; set; } = new List<AdoptionCategory>();

        /// <summary>Agent interactions by department.</summary>
        [JsonProperty("usageByDepartment")]
        public List<AdoptionCategory> UsageByDepartment { get; set; } = new List<AdoptionCategory>();

        /// <summary>Interactions per agent, for the inventory treemap.</summary>
        [JsonProperty("usageByAgent")]
        public List<AdoptionCategory> UsageByAgent { get; set; } = new List<AdoptionCategory>();

        /// <summary>
        /// The agents themselves. Returned inline rather than behind a paged endpoint because the
        /// inventory is capped at a few hundred rows - an agent estate is nothing like the size of a
        /// user population, and a second round trip would buy nothing.
        /// </summary>
        [JsonProperty("agents")]
        public List<AgentUsageRow> Agents { get; set; } = new List<AgentUsageRow>();
    }

    #endregion

    #region Unlicensed population

    /// <summary>Raw per-user usage for someone with no Copilot seat.</summary>
    public class UnlicensedUsageQueryRow
    {
        public int UserId { get; set; }
        public string Department { get; set; }
        public long Interactions { get; set; }
        public int ActiveDays { get; set; }
        public int AppsUsed { get; set; }
        public int AgentsUsed { get; set; }
        public DateTime? LastInteractionUtc { get; set; }
    }

    /// <summary>
    /// Unlicensed Copilot Chat treated as a population in its own right, not merely as a pool of
    /// licence candidates.
    ///
    /// Worth reporting separately because it is the one Copilot population Microsoft's own tooling
    /// cannot see at all, and because its shape answers a different question: not "who should get a
    /// seat" but "how much Copilot is this organisation already doing without paying for it".
    /// </summary>
    public class UnlicensedPopulationSummary
    {
        [JsonProperty("activeUsers")]
        public int ActiveUsers { get; set; }

        [JsonProperty("interactions")]
        public long Interactions { get; set; }

        /// <summary>Mean interactions per active unlicensed user, normalised to a month.</summary>
        [JsonProperty("interactionsPerUserPerMonth")]
        public double InteractionsPerUserPerMonth { get; set; }

        [JsonProperty("agentUsers")]
        public int AgentUsers { get; set; }

        /// <summary>The same habit buckets the licensed population uses, so the two are comparable.</summary>
        [JsonProperty("habitBuckets")]
        public List<AdoptionHabitBucket> HabitBuckets { get; set; } = new List<AdoptionHabitBucket>();

        [JsonProperty("usageByApp")]
        public List<AdoptionCategory> UsageByApp { get; set; } = new List<AdoptionCategory>();

        [JsonProperty("usageByDepartment")]
        public List<AdoptionCategory> UsageByDepartment { get; set; } = new List<AdoptionCategory>();

        /// <summary>True when the row cap was hit, so the figures are a floor rather than a total.</summary>
        [JsonProperty("truncated")]
        public bool Truncated { get; set; }
    }

    #endregion

    #region Concentration and combined leaderboard

    /// <summary>
    /// One slice of the usage distribution: how much of all Copilot activity a given cohort of users
    /// accounts for.
    ///
    /// Copilot usage is almost always a power law, and the difference between "40% adoption spread
    /// evenly" and "40% adoption where a tenth of them do most of it" is the difference between a
    /// programme that is working and one propped up by a handful of enthusiasts. An adoption
    /// percentage cannot distinguish those two; this can.
    /// </summary>
    public class AdoptionConcentrationBand
    {
        /// <summary>Cohort name, e.g. "Top 10%".</summary>
        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("users")]
        public int Users { get; set; }

        [JsonProperty("interactions")]
        public long Interactions { get; set; }

        /// <summary>This cohort's share of all interactions by active licensed users.</summary>
        [JsonProperty("sharePct")]
        public double SharePct { get; set; }

        [JsonProperty("interactionsPerUser")]
        public double InteractionsPerUser { get; set; }
    }

    /// <summary>
    /// Licensed and unlicensed Copilot use for one department, side by side.
    ///
    /// The comparison is the point: a department with idle seats <i>and</i> heavy unlicensed Chat use
    /// is not an adoption problem, it is a seat-allocation problem, and no single-population view
    /// makes that visible.
    /// </summary>
    public class AdoptionCombinedSegmentRow
    {
        [JsonProperty("segment")]
        public string Segment { get; set; }

        [JsonProperty("licensedUsers")]
        public int LicensedUsers { get; set; }

        [JsonProperty("licensedActiveUsers")]
        public int LicensedActiveUsers { get; set; }

        /// <summary>Interactions per licensed seat, normalised to a month - including idle seats.</summary>
        [JsonProperty("interactionsPerLicensedUser")]
        public double InteractionsPerLicensedUser { get; set; }

        /// <summary>Share of licensed users who used at least one agent.</summary>
        [JsonProperty("licensedAgentUserPct")]
        public double LicensedAgentUserPct { get; set; }

        [JsonProperty("unlicensedActiveUsers")]
        public int UnlicensedActiveUsers { get; set; }

        /// <summary>Interactions per active unlicensed user, normalised to a month.</summary>
        [JsonProperty("interactionsPerUnlicensedUser")]
        public double InteractionsPerUnlicensedUser { get; set; }

        [JsonProperty("unlicensedAgentUserPct")]
        public double UnlicensedAgentUserPct { get; set; }
    }

    #endregion

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
