using Newtonsoft.Json;

namespace Common.Entities.CopilotAdoption
{
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

        /// <summary>
        /// How far back the agent inventory looks.
        ///
        /// Deliberately much shorter than <see cref="HistoryDays"/>. The inventory only needs enough
        /// history to reach past <see cref="AgentRetireInactiveDays"/> - an agent quiet for longer than
        /// that is already a retire candidate, and seeing exactly how much longer changes nothing. The
        /// only other date-sensitive rule is the <see cref="AgentNewDays"/> exemption, which looks
        /// forwards from first use, so an older agent being reported as "first seen at the window edge"
        /// cannot wrongly mark it New.
        ///
        /// The cost of getting this wrong is significant: the query aggregates <c>copilot_chats</c>
        /// joined to <c>audit_events</c>, and on a 200,000-user tenant a year of that history is
        /// several times the volume of the reporting window. 120 days keeps the retire verdict correct
        /// while reading roughly a third of what a full year would.
        /// </summary>
        [JsonProperty("agentHistoryDays")]
        public int AgentHistoryDays { get; set; } = 120;

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
}
