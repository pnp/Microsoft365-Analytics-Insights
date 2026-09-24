using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// One Copilot seat holder's raw Cowork-readiness signals, exactly as the database returns them.
    ///
    /// Kept separate from the scored row for the same reason
    /// <see cref="LicensedUserUsageRow"/> is: the scoring rules can then be unit-tested against
    /// hand-written inputs with no database involved.
    /// </summary>
    public class CoworkReadinessSignalRow
    {
        public int UserId { get; set; }

        public string UserPrincipalName { get; set; }

        public string Mail { get; set; }

        /// <summary>
        /// The organisation this person belongs to, derived once when the rows are loaded rather than
        /// re-parsed from the UPN on every filtered request. See <see cref="CopilotAdoptionEmailDomain"/>.
        /// </summary>
        public string EmailDomain { get; set; }

        public string Department { get; set; }

        public string JobTitle { get; set; }

        public string Country { get; set; }

        public string OfficeLocation { get; set; }

        public string CompanyName { get; set; }

        public string ManagerUserPrincipalName { get; set; }

        public bool? AccountEnabled { get; set; }

        #region Cowork usage (evidence, from the Copilot audit log)

        /// <summary>Cowork interactions inside the reporting window.</summary>
        public long CoworkInteractions { get; set; }

        /// <summary>
        /// Distinct calendar days inside the window with at least one Cowork interaction.
        ///
        /// This, not <see cref="CoworkInteractions"/>, is what decides whether use is regular: a single
        /// afternoon of experimentation can produce a large interaction count and is not adoption.
        /// </summary>
        public int CoworkActiveDays { get; set; }

        /// <summary>Most recent Cowork interaction inside the window.</summary>
        public DateTime? LastCoworkInteractionUtc { get; set; }

        public int? CoworkReportTotalTasks { get; set; }
        public int? CoworkReportScheduledTasks { get; set; }
        public int? CoworkReportUserInitiatedTasks { get; set; }
        public int? CoworkReportActiveDays { get; set; }
        public DateTime? CoworkReportLastActivityDate { get; set; }
        public bool? CoworkReportRetainedUser { get; set; }

        #endregion

        #region Microsoft 365 coordination load (per active day)

        /// <summary>Teams chat + channel messages on a typical active day.</summary>
        public long TeamsMessages { get; set; }

        /// <summary>Teams meetings attended + organised on a typical active day.</summary>
        public long TeamsMeetings { get; set; }

        public long EmailsSent { get; set; }

        public long EmailsRead { get; set; }

        /// <summary>SharePoint + OneDrive files viewed or edited on a typical active day.</summary>
        public long FilesViewedOrEdited { get; set; }

        /// <summary>Most recent activity date across the Microsoft 365 usage reports.</summary>
        public DateTime? LastM365ActivityUtc { get; set; }

        #endregion

        #region The work Cowork could take on (per active day, UNROUNDED)

        /// <summary>
        /// Meetings organised on a typical active day - Teams' <c>meetings_organized_count</c> - unrounded.
        ///
        /// <para>The coordination-load figures above are rounded to whole numbers per active day, which is
        /// harmless for a 0-100 score and wrong for a volume: someone who organises two meetings a week
        /// averages 0.4 a day, and rounding models them as organising none. These five feed the Cowork
        /// estimate (<see cref="CoworkActivities"/>) and stay floats from the query to the sum.</para>
        /// </summary>
        public double MeetingsOrganisedPerActiveDay { get; set; }

        /// <summary>Meetings attended on a typical active day - Teams' <c>meetings_attended_count</c> - unrounded.</summary>
        public double MeetingsAttendedPerActiveDay { get; set; }

        /// <summary>
        /// Teams chat plus channel messages on a typical active day, unrounded. Channel messages
        /// (<c>team_chat_count</c>) already include the posts and replies Microsoft also reports
        /// separately, so those are not added a second time.
        /// </summary>
        public double ChatAndChannelMessagesPerActiveDay { get; set; }

        /// <summary>Emails sent on a typical active day - Outlook's <c>email_send_count</c> - unrounded.</summary>
        public double EmailsSentPerActiveDay { get; set; }

        /// <summary>SharePoint plus OneDrive files viewed or edited on a typical active day, unrounded.</summary>
        public double FilesPerActiveDay { get; set; }

        #endregion

        /// <summary>
        /// The user's Copilot engagement score, carried across from the licensed-user analysis rather than
        /// recomputed here.
        ///
        /// Deliberately populated in memory from the already-scored
        /// <see cref="LicensedUserAdoptionRow"/> set instead of being re-derived in this query. Two
        /// independent implementations of the engagement score would eventually disagree, and a page that
        /// showed a user as Established on one tab and not fluent enough on another would be indefensible.
        /// </summary>
        public double AdoptionScore { get; set; }

        /// <summary>Distinct Copilot agents used inside the window, carried across with the score.</summary>
        public int AgentsUsed { get; set; }

        /// <summary>Whether the user was active in Copilot at all inside the window.</summary>
        public bool CopilotActive { get; set; }

        /// <summary>
        /// Total Copilot Credits billed to this user in the window, where the Copilot Studio credit import
        /// has per-user rows for them.
        ///
        /// <b>Null means "not attributable", never zero.</b> And this is <i>all</i> Copilot Credit
        /// consumption, not Cowork's share: Microsoft meters Cowork against the shared Copilot Credits
        /// pool with no per-row workload discriminator, so a Cowork-only figure cannot be produced. See
        /// <see cref="Common.Entities.Entities.AgentCosts.CopilotStudioHarnessClassifier"/>.
        /// </summary>
        public decimal? TotalCopilotCredits { get; set; }
    }

    /// <summary>
    /// A Copilot seat holder with the Cowork readiness maths applied. This is what the UI lists and the
    /// CSV exports.
    /// </summary>
    public class CoworkReadinessRow
    {
        [JsonProperty("userId")]
        public int UserId { get; set; }

        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }

        [JsonProperty("mail")]
        public string Mail { get; set; }

        /// <summary>The organisation this person belongs to. See <see cref="CopilotAdoptionEmailDomain"/>.</summary>
        [JsonProperty("emailDomain")]
        public string EmailDomain { get; set; }

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

        #region Cowork usage today

        [JsonProperty("coworkInteractions")]
        public long CoworkInteractions { get; set; }

        [JsonProperty("coworkActiveDays")]
        public int CoworkActiveDays { get; set; }

        [JsonProperty("lastCoworkInteractionUtc")]
        public DateTime? LastCoworkInteractionUtc { get; set; }

        [JsonProperty("coworkReportTotalTasks")]
        public int? CoworkReportTotalTasks { get; set; }

        [JsonProperty("coworkReportScheduledTasks")]
        public int? CoworkReportScheduledTasks { get; set; }

        [JsonProperty("coworkReportUserInitiatedTasks")]
        public int? CoworkReportUserInitiatedTasks { get; set; }

        [JsonProperty("coworkReportActiveDays")]
        public int? CoworkReportActiveDays { get; set; }

        [JsonProperty("coworkReportLastActivityDate")]
        public DateTime? CoworkReportLastActivityDate { get; set; }

        [JsonProperty("coworkReportRetainedUser")]
        public bool? CoworkReportRetainedUser { get; set; }

        [JsonProperty("coworkAutomationRatioPct")]
        public double? CoworkAutomationRatioPct { get; set; }

        [JsonProperty("coworkCreditsPerTask")]
        public decimal? CoworkCreditsPerTask { get; set; }

        [JsonProperty("usedCowork")]
        public bool UsedCowork { get; set; }

        /// <summary>True when Cowork use clears <see cref="CopilotAdoptionOptions.CoworkRegularMinActiveDays"/>.</summary>
        [JsonProperty("regularCoworkUser")]
        public bool RegularCoworkUser { get; set; }

        #endregion

        #region The two axes

        /// <summary>
        /// Coordination load, 0-100: how much delegable, multi-step coordination work this person carries.
        /// The x-axis of the quadrant.
        /// </summary>
        [JsonProperty("coordinationLoadScore")]
        public double CoordinationLoadScore { get; set; }

        /// <summary>
        /// Copilot fluency, 0-100: the engagement score plus a bounded agent-familiarity uplift. The
        /// y-axis of the quadrant.
        /// </summary>
        [JsonProperty("fluencyScore")]
        public double FluencyScore { get; set; }

        /// <summary>Engagement score before the agent-familiarity uplift, so the uplift is auditable.</summary>
        [JsonProperty("adoptionScore")]
        public double AdoptionScore { get; set; }

        [JsonProperty("agentsUsed")]
        public int AgentsUsed { get; set; }

        #endregion

        #region Coordination-load components

        [JsonProperty("collaborationScore")]
        public double CollaborationScore { get; set; }

        [JsonProperty("meetingScore")]
        public double MeetingScore { get; set; }

        [JsonProperty("emailScore")]
        public double EmailScore { get; set; }

        [JsonProperty("documentScore")]
        public double DocumentScore { get; set; }

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

        #endregion

        #region The work Cowork could take on - model inputs, not serialised

        // Carried across from the signal row for the Cowork estimate, and deliberately kept out of the
        // API: they are the unrounded twins of the per-active-day figures above, so serialising them would
        // put two figures for "emails sent" on every row that disagree in the decimals - and the estimate
        // they feed is a cohort-level model, never a per-person one (CsvSchema_OmitsThePerUserModelledEstimate).

        [JsonIgnore]
        public double MeetingsOrganisedPerActiveDay { get; set; }

        [JsonIgnore]
        public double MeetingsAttendedPerActiveDay { get; set; }

        [JsonIgnore]
        public double ChatAndChannelMessagesPerActiveDay { get; set; }

        [JsonIgnore]
        public double EmailsSentPerActiveDay { get; set; }

        [JsonIgnore]
        public double FilesPerActiveDay { get; set; }

        #endregion

        /// <summary>
        /// Which population this user is in: see <see cref="CopilotAdoptionScoring.CoworkTiers"/>.
        /// </summary>
        [JsonProperty("tier")]
        public string Tier { get; set; }

        /// <summary>Short display label for <see cref="Tier"/>.</summary>
        [JsonProperty("tierLabel")]
        public string TierLabel { get; set; }

        /// <summary>
        /// Whether this row's tier rests on observed Cowork use (<c>evidence</c>) or on an inference from
        /// workload and Copilot fluency (<c>inference</c>).
        ///
        /// Exported as its own field rather than left implicit in the tier name because the distinction is
        /// the entire basis on which a reader should weigh the row - and because
        /// <see cref="LicenceOpportunityRow.QualificationTier"/> already established that a
        /// "recommended" flag which hides its provenance is not good enough for a spend decision.
        /// </summary>
        [JsonProperty("basis")]
        public string Basis { get; set; }

        /// <summary>
        /// True when this user should be scoped into the Cowork spending policy now. Covers both the
        /// prime candidates and the people already using Cowork, since removing a current user from scope
        /// would break them.
        /// </summary>
        [JsonProperty("recommendForPolicy")]
        public bool RecommendForPolicy { get; set; }

        /// <summary>Plain-English justification, safe to paste into a rollout request.</summary>
        [JsonProperty("rationale")]
        public string Rationale { get; set; }

        /// <summary>
        /// All Copilot Credits billed to this user in the window, or null for "not attributable".
        ///
        /// <b>Not a Cowork figure.</b> Cowork meters against the shared Copilot Credits pool and Microsoft
        /// exposes no per-row workload discriminator, so this is the user's total credit consumption
        /// across every credit-billed Copilot workload. Labelled as such everywhere it is shown; null is
        /// rendered as "not attributable" rather than as zero, which would read as "this user costs
        /// nothing".
        /// </summary>
        [JsonProperty("totalCopilotCredits")]
        public decimal? TotalCopilotCredits { get; set; }
    }

    /// <summary>One point of the readiness quadrant: a department placed on the two axes.</summary>
    public class CoworkQuadrantPoint
    {
        [JsonProperty("segment")]
        public string Segment { get; set; }

        /// <summary>Copilot seats in the segment - the bubble size.</summary>
        [JsonProperty("licensedUsers")]
        public int LicensedUsers { get; set; }

        [JsonProperty("coordinationLoadScore")]
        public double CoordinationLoadScore { get; set; }

        [JsonProperty("fluencyScore")]
        public double FluencyScore { get; set; }

        /// <summary>Users in this segment already using Cowork regularly.</summary>
        [JsonProperty("regularCoworkUsers")]
        public int RegularCoworkUsers { get; set; }

        /// <summary>Users in this segment in the prime-candidate tier.</summary>
        [JsonProperty("primeCandidates")]
        public int PrimeCandidates { get; set; }
    }

    /// <summary>
    /// One department's Cowork rollout position, ordered so an executive can sequence a rollout by
    /// business unit rather than reading a flat list of thousands of people.
    /// </summary>
    public class CoworkSegmentRow
    {
        [JsonProperty("segment")]
        public string Segment { get; set; }

        [JsonProperty("licensedUsers")]
        public int LicensedUsers { get; set; }

        [JsonProperty("primeCandidates")]
        public int PrimeCandidates { get; set; }

        /// <summary>Prime candidates as a share of the segment's seats.</summary>
        [JsonProperty("primeCandidateRatePct")]
        public double PrimeCandidateRatePct { get; set; }

        [JsonProperty("regularCoworkUsers")]
        public int RegularCoworkUsers { get; set; }

        [JsonProperty("coworkReportTotalTasks")]
        public int CoworkReportTotalTasks { get; set; }

        [JsonProperty("coworkReportScheduledTasks")]
        public int CoworkReportScheduledTasks { get; set; }

        [JsonProperty("coworkAutomationRatioPct")]
        public double? CoworkAutomationRatioPct { get; set; }

        [JsonProperty("coworkReportRetainedUsers")]
        public int? CoworkReportRetainedUsers { get; set; }

        [JsonProperty("coworkReportRetentionPct")]
        public double? CoworkReportRetentionPct { get; set; }

        [JsonProperty("coworkAdoptionPct")]
        public double CoworkAdoptionPct { get; set; }

        [JsonProperty("averageCoordinationLoad")]
        public double AverageCoordinationLoad { get; set; }

        [JsonProperty("averageFluency")]
        public double AverageFluency { get; set; }
    }

    /// <summary>One Cowork tier with how many seat holders are in it.</summary>
    public class CoworkTierSummary
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("label")]
        public string Label { get; set; }

        /// <summary><c>evidence</c> or <c>inference</c>.</summary>
        [JsonProperty("basis")]
        public string Basis { get; set; }

        /// <summary>What this group is and what to do about it.</summary>
        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("users")]
        public int Users { get; set; }

        [JsonProperty("sharePct")]
        public double SharePct { get; set; }
    }

    /// <summary>
    /// The tenant's Copilot Credit position, shown as rollout headroom.
    ///
    /// <b>This is the shared Copilot Credits pool, not Cowork-only spend.</b> Cowork genuinely consumes
    /// from it - which is what makes it valid headroom for a rollout decision - but Copilot Studio,
    /// AI Builder and other credit-billed workloads draw on the same pool, and Microsoft publishes no way
    /// to separate them. Every label on this must say so.
    /// </summary>
    public class CoworkCreditPosition
    {
        [JsonProperty("available")]
        public bool Available { get; set; }

        [JsonProperty("snapshotUtc")]
        public DateTime? SnapshotUtc { get; set; }

        [JsonProperty("entitled")]
        public decimal? Entitled { get; set; }

        [JsonProperty("consumed")]
        public decimal? Consumed { get; set; }

        [JsonProperty("available_credits")]
        public decimal? AvailableCredits { get; set; }

        [JsonProperty("payAsYouGoConsumed")]
        public decimal? PayAsYouGoConsumed { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        /// <summary>
        /// Whether any per-user credit rows exist at all. When false the UI must not render a per-user
        /// credit column, rather than rendering a column of "not attributable".
        /// </summary>
        [JsonProperty("perUserCreditsAvailable")]
        public bool PerUserCreditsAvailable { get; set; }
    }

    /// <summary>
    /// The modelled Cowork estimate for one cohort: the time Cowork could give back ON TOP of what
    /// Microsoft 365 Copilot already saves, for the people ready for Cowork now
    /// (<see cref="CopilotAdoptionSummary.CoworkValueEstimate"/>) or every Copilot seat holder
    /// (<see cref="CopilotAdoptionSummary.CoworkFullRolloutEstimate"/>).
    ///
    /// <b>Every hour here is derived from an assumption and none of it is measured.</b> The observed
    /// inputs - the Cowork tasks already in Microsoft's report (<see cref="ObservedCoworkTasks"/>) and
    /// the work everyone else already does by hand (<see cref="Activities"/>) - are real; the share of
    /// that work handed to Cowork and the minutes saved on it are not. <see cref="Assumptions"/>
    /// travels with the numbers so no surface can render a figure without the assumption that produced
    /// it, and <see cref="IsModelled"/> exists so a consumer cannot mistake this for evidence.
    /// </summary>
    /// <remarks>
    /// <para><b>Where the time would come from, person by person.</b> The people already running
    /// Cowork tasks are counted at their actual tasks. Everyone else is modelled from what Microsoft's
    /// usage reports say they already do - the meetings they organise and attend, the emails they send,
    /// their Teams messages, the files they work on - each kind of work times the share of it they are
    /// assumed to hand to Cowork and the minutes Cowork saves on each piece (<see cref="CoworkActivities"/>).
    /// A flat tasks-a-person projection could not say where the time was, and modelled the busiest and
    /// the quietest person alike.</para>
    ///
    /// <para><b>Cowork only, on purpose.</b> This is the figure a tenant uses to justify Copilot
    /// Credits: the value of enabling Cowork for people who already hold a Copilot licence. It used to
    /// carry a Microsoft 365 Copilot layer as well, and most of the headline was that layer, for people
    /// whose licence already gives it back. Enabling Cowork does not unlock it, so the minutes here are
    /// Cowork's increment over Copilot alone, and the Copilot minutes size the decision they belong to -
    /// buying licences - in <see cref="LicenceValueEstimate"/>.</para>
    ///
    /// <para>No study has measured Cowork's time savings, alone or for people who already use
    /// Copilot, so every hour here rests on assumed shares and minutes, and every surface that shows it
    /// says so.</para>
    /// </remarks>
    public class CoworkValueEstimate
    {
        /// <summary>Always true. Present so serialised consumers carry the caveat with the payload.</summary>
        [JsonProperty("isModelled")]
        public bool IsModelled { get; set; } = true;

        /// <summary>Seat holders the estimate covers.</summary>
        [JsonProperty("cohortUsers")]
        public int CohortUsers { get; set; }

        #region Cowork tasks already running (observed)

        /// <summary>
        /// People in the cohort with Cowork tasks in Microsoft's Cowork usage report. Observed.
        /// </summary>
        [JsonProperty("coworkTaskUsers")]
        public int CoworkTaskUsers { get; set; }

        /// <summary>
        /// Their Cowork tasks, restated from the report's period as a month. Observed, not modelled.
        /// </summary>
        [JsonProperty("observedCoworkTasks")]
        public double ObservedCoworkTasks { get; set; }

        #endregion

        #region The work Cowork could take on (observed volumes, assumed shares and minutes)

        /// <summary>
        /// People in the cohort with no Cowork tasks in the report, whose own Microsoft 365 activity is
        /// modelled instead.
        /// </summary>
        [JsonProperty("projectedCoworkUsers")]
        public int ProjectedCoworkUsers { get; set; }

        /// <summary>
        /// What those people already do by hand a month, one entry per kind of work, in
        /// <see cref="CoworkActivities.All"/> order - every kind is present, at zero if nobody does it.
        /// Observed: per-active-day averages from Microsoft's usage reports, times working days a month,
        /// rounded as published.
        /// </summary>
        [JsonProperty("activities")]
        public List<CoworkActivityVolume> Activities { get; set; } = new List<CoworkActivityVolume>();

        /// <summary>
        /// Pieces of work a month the model hands to Cowork across those people: each volume times its
        /// share, summed, then rounded.
        /// </summary>
        [JsonProperty("projectedCoworkTasks")]
        public double ProjectedCoworkTasks { get; set; }

        /// <summary>Observed Cowork tasks plus the pieces of work projected, a month across the cohort.</summary>
        [JsonProperty("coworkTasks")]
        public double CoworkTasks { get; set; }

        #endregion

        #region Sense check (observed, tenant-wide)

        /// <summary>
        /// The Cowork tasks a month the tenant's own Cowork users run on average - everyone with tasks in
        /// the report, not only this cohort's. Zero when nobody has any.
        ///
        /// <para>Not an input to the hours. It is the one measured figure the model can be held against:
        /// a model that hands everyone else far more work than the people already using Cowork give it is
        /// one to tune down. Early adopters tend to use a new tool more than the people who follow, and one
        /// task can cover several pieces of work, so the two need not match.</para>
        /// </summary>
        [JsonProperty("observedTasksPerPersonPerMonth")]
        public double ObservedTasksPerPersonPerMonth { get; set; }

        /// <summary>How many people <see cref="ObservedTasksPerPersonPerMonth"/> is the average of.</summary>
        [JsonProperty("observedTaskRateUsers")]
        public int ObservedTaskRateUsers { get; set; }

        #endregion

        #region Modelled outputs

        /// <summary>
        /// Low end of the modelled monthly hours Cowork could give back across the cohort, on top of
        /// Copilot: the high end at the conservative share of the assumptions.
        /// </summary>
        [JsonProperty("hoursPerMonthLow")]
        public double HoursPerMonthLow { get; set; }

        /// <summary>
        /// High end of the modelled monthly hours: observed tasks x minutes per task, plus each kind of
        /// work x its share x its minutes.
        /// </summary>
        [JsonProperty("hoursPerMonthHigh")]
        public double HoursPerMonthHigh { get; set; }

        // Deliberately no monetary figure, and no monetary figure anywhere else in this report either.
        // Epic #559 rejected an ROI calculator because a fabricated money figure discredits the measured
        // ones beside it. The idle-licence-spend figure that #553 once allowed has since been withdrawn
        // as well: it priced idle seats from a per-SKU price typed into the page header, which is not a
        // source of truth about what a tenant pays, and a money figure derived from one gets quoted in a
        // renewal negotiation as though it were. This estimate is modelled from assumed shares and
        // minutes, so pricing it would be worse again.
        //
        // The HOURS model is kept, and is the Cowork tab's headline, on the terms that make it
        // defensible: it is always labelled as modelled, it is always published as a range, its
        // assumptions are shown beside it - with the plain statement that no study has measured Cowork -
        // and the reader can replace any of them with their own figure (TimeSavedOverrides). Converting
        // it to money is still the line the epic draws.

        #endregion

        /// <summary>
        /// The assumptions used, in plain English, for display next to the numbers. Never empty when any
        /// figure above is non-zero.
        /// </summary>
        [JsonProperty("assumptions")]
        public List<string> Assumptions { get; set; } = new List<string>();
    }

    /// <summary>
    /// What one cohort already does by hand of one kind of work Cowork could take on, a month.
    /// Observed, not modelled - see <see cref="CoworkActivities"/>.
    /// </summary>
    public class CoworkActivityVolume
    {
        /// <summary>One of the <see cref="CoworkActivities"/> keys, e.g. <c>organiseMeetings</c>.</summary>
        [JsonProperty("activity")]
        public string Activity { get; set; }

        /// <summary>The work done by hand a month by the people not yet running Cowork tasks.</summary>
        [JsonProperty("volumePerMonth")]
        public double VolumePerMonth { get; set; }
    }

    /// <summary>
    /// The Cowork tasks a month the tenant's own Cowork users run: the estimate's sense check, not one
    /// of its inputs.
    /// </summary>
    public class CoworkTaskRate
    {
        /// <summary>Their average, restated as a month. Zero when nobody has Cowork tasks in the report.</summary>
        public double TasksPerPersonPerMonth { get; set; }

        /// <summary>The number of people it is the average of.</summary>
        public int Users { get; set; }
    }

    /// <summary>
    /// One cohort's inputs to the Cowork model: the tasks observed, and the work everyone else does by
    /// hand. Everything the hours are computed from, so an estimate can be restated under a reader's own
    /// assumptions without re-running the analysis.
    /// </summary>
    public class CoworkTaskInputs
    {
        /// <summary>People in the cohort with Cowork tasks in the report.</summary>
        public int ObservedUsers { get; set; }

        /// <summary>Their tasks, restated as a month.</summary>
        public double ObservedTasksPerMonth { get; set; }

        /// <summary>
        /// Everyone else's work done by hand a month, keyed by <see cref="CoworkActivities"/> key. A kind
        /// of work that is absent counts as none.
        /// </summary>
        public IDictionary<string, double> ActivityVolumes { get; set; } = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>The tenant's own Cowork users' average, carried for the sense check. Null means none observed.</summary>
        public CoworkTaskRate ObservedRate { get; set; }
    }

    /// <summary>A page of Cowork readiness rows, matching the shape of the other paged endpoints.</summary>
    public class CoworkReadinessPage
    {
        [JsonProperty("total")]
        public int Total { get; set; }

        [JsonProperty("skip")]
        public int Skip { get; set; }

        [JsonProperty("take")]
        public int Take { get; set; }

        [JsonProperty("rows")]
        public List<CoworkReadinessRow> Rows { get; set; } = new List<CoworkReadinessRow>();

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
