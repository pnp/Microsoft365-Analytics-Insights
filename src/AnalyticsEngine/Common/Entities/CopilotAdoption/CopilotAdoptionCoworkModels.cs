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
    /// The modelled time-saved estimate for one cohort: the people ready for Cowork now
    /// (<see cref="CopilotAdoptionSummary.CoworkValueEstimate"/>) or every Copilot seat holder
    /// (<see cref="CopilotAdoptionSummary.CoworkFullRolloutEstimate"/>).
    ///
    /// <b>Every field here is derived from an assumption and none of it is measured.</b> The observed
    /// inputs (<see cref="AddressableMeetings"/>, <see cref="ObservedCoworkTasks"/> and friends) are real;
    /// the conversion to hours is not. <see cref="Assumptions"/> travels with the numbers so no surface
    /// can render a figure without the assumption that produced it, and <see cref="IsModelled"/> exists
    /// so a consumer cannot mistake this for evidence.
    /// </summary>
    /// <remarks>
    /// <para><b>Two layers, because the evidence behind them is different.</b> The Microsoft 365
    /// Copilot layer applies minutes per meeting, email and document that are derived from Microsoft's
    /// published Copilot credits and checked against published Copilot studies. The Cowork layer sits
    /// ON TOP of it - Cowork tasks times minutes per task - and no study has measured it, alone or for
    /// people who already have Copilot. Blending the two would let Copilot's evidence stand behind
    /// Cowork's figure, which is the one a tenant is being asked to buy Copilot Credits for. So each layer
    /// is published separately and <see cref="HoursPerMonthHigh"/> is simply their sum.</para>
    /// </remarks>
    public class CoworkValueEstimate
    {
        /// <summary>Always true. Present so serialised consumers carry the caveat with the payload.</summary>
        [JsonProperty("isModelled")]
        public bool IsModelled { get; set; } = true;

        /// <summary>Seat holders the estimate covers.</summary>
        [JsonProperty("cohortUsers")]
        public int CohortUsers { get; set; }

        #region Observed inputs

        /// <summary>Meetings per month across the cohort, from the usage reports. Observed, not modelled.</summary>
        [JsonProperty("addressableMeetings")]
        public double AddressableMeetings { get; set; }

        /// <summary>Mail volume per month across the cohort. Observed, not modelled.</summary>
        [JsonProperty("addressableMailThreads")]
        public double AddressableMailThreads { get; set; }

        /// <summary>Document touches per month across the cohort. Observed, not modelled.</summary>
        [JsonProperty("addressableDocuments")]
        public double AddressableDocuments { get; set; }

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

        #region Cowork projection (assumed)

        /// <summary>People in the cohort with no Cowork tasks in the report, who are projected instead.</summary>
        [JsonProperty("projectedCoworkUsers")]
        public int ProjectedCoworkUsers { get; set; }

        /// <summary>The Cowork tasks a month each projected person is assumed to run.</summary>
        [JsonProperty("coworkTasksPerPersonPerMonth")]
        public double CoworkTasksPerPersonPerMonth { get; set; }

        /// <summary>
        /// Where <see cref="CoworkTasksPerPersonPerMonth"/> came from - see <see cref="CoworkTaskRateBases"/>.
        /// </summary>
        [JsonProperty("coworkTaskRateBasis")]
        public string CoworkTaskRateBasis { get; set; } = CoworkTaskRateBases.Assumed;

        /// <summary>
        /// For an observed rate, how many people it is the average of: everyone in the tenant with Cowork
        /// tasks in the report, not only this cohort's. Zero otherwise.
        /// </summary>
        [JsonProperty("coworkTaskRateUsers")]
        public int CoworkTaskRateUsers { get; set; }

        /// <summary>Observed plus projected Cowork tasks a month across the cohort.</summary>
        [JsonProperty("coworkTasks")]
        public double CoworkTasks { get; set; }

        #endregion

        #region Modelled outputs

        /// <summary>Low end of the Microsoft 365 Copilot layer, in hours a month.</summary>
        [JsonProperty("copilotHoursPerMonthLow")]
        public double CopilotHoursPerMonthLow { get; set; }

        /// <summary>High end of the Microsoft 365 Copilot layer, in hours a month.</summary>
        [JsonProperty("copilotHoursPerMonthHigh")]
        public double CopilotHoursPerMonthHigh { get; set; }

        /// <summary>Low end of the Cowork layer - on top of Copilot - in hours a month.</summary>
        [JsonProperty("coworkHoursPerMonthLow")]
        public double CoworkHoursPerMonthLow { get; set; }

        /// <summary>High end of the Cowork layer - on top of Copilot - in hours a month.</summary>
        [JsonProperty("coworkHoursPerMonthHigh")]
        public double CoworkHoursPerMonthHigh { get; set; }

        /// <summary>
        /// Low end of the modelled monthly hours saved across the cohort: the two layers' low ends added
        /// together, so the parts a reader sees always add up to the total they see.
        /// </summary>
        [JsonProperty("hoursPerMonthLow")]
        public double HoursPerMonthLow { get; set; }

        /// <summary>High end of the modelled monthly hours saved: the two layers' high ends added together.</summary>
        [JsonProperty("hoursPerMonthHigh")]
        public double HoursPerMonthHigh { get; set; }

        // Deliberately no monetary figure, and no monetary figure anywhere else in this report either.
        // Epic #559 rejected an ROI calculator because a fabricated money figure discredits the measured
        // ones beside it. The idle-licence-spend figure that #553 once allowed has since been withdrawn
        // as well: it priced idle seats from a per-SKU price typed into the page header, which is not a
        // source of truth about what a tenant pays, and a money figure derived from one gets quoted in a
        // renewal negotiation as though it were. This estimate is modelled from assumed minutes per
        // meeting, email, document and Cowork task, so pricing it would be worse again.
        //
        // The HOURS model is kept, and is now the Cowork tab's headline, on the terms that make it
        // defensible: it is always labelled as modelled, it is always published as a range, its
        // assumptions are shown beside it with the published evidence for each - or the plain statement
        // that there is none yet, for Cowork - and the reader can replace any of them with their own
        // figure (CoworkTimeSavedOverrides). Converting it to money is still the line the epic draws.

        #endregion

        /// <summary>
        /// The assumptions used, in plain English, for display next to the numbers. Never empty when any
        /// figure above is non-zero.
        /// </summary>
        [JsonProperty("assumptions")]
        public List<string> Assumptions { get; set; } = new List<string>();
    }

    /// <summary>Where the Cowork task rate a projection uses came from.</summary>
    public static class CoworkTaskRateBases
    {
        /// <summary>
        /// The average of the people with Cowork tasks in Microsoft's Cowork usage report. Measured, but
        /// from early adopters, who tend to use a new tool more than the people who follow them.
        /// </summary>
        public const string Observed = "observed";

        /// <summary>
        /// <see cref="CopilotAdoptionOptions.CoworkAssumedTasksPerPersonPerMonth"/>: a placeholder, used
        /// only because nobody's Cowork tasks were in the report.
        /// </summary>
        public const string Assumed = "assumed";

        /// <summary>A figure the reader entered in the portal, carried with an Excel export.</summary>
        public const string Custom = "custom";
    }

    /// <summary>The Cowork tasks a month the model projects each not-yet-observed person at, and why.</summary>
    public class CoworkTaskRate
    {
        public double TasksPerPersonPerMonth { get; set; }

        /// <summary>See <see cref="CoworkTaskRateBases"/>.</summary>
        public string Basis { get; set; } = CoworkTaskRateBases.Assumed;

        /// <summary>For an observed rate, the number of people it is the average of.</summary>
        public int Users { get; set; }
    }

    /// <summary>One cohort's Cowork task inputs to the model: what was observed, and the rate for everyone else.</summary>
    public class CoworkTaskInputs
    {
        /// <summary>People in the cohort with Cowork tasks in the report.</summary>
        public int ObservedUsers { get; set; }

        /// <summary>Their tasks, restated as a month.</summary>
        public double ObservedTasksPerMonth { get; set; }

        /// <summary>The rate everyone else in the cohort is projected at.</summary>
        public CoworkTaskRate Rate { get; set; }
    }

    /// <summary>
    /// A reader's own time-saved assumptions, sent with an Excel export so the workbook models the same
    /// figures they were looking at in the portal.
    /// </summary>
    /// <remarks>
    /// <para>The portal lets a reader replace any of the minutes-saved assumptions with their own
    /// figure. Those figures live in that browser tab only - they are never persisted - so an export has
    /// to carry them, or the Excel report downloaded from a customised page would quietly model
    /// different hours from the screen it came from.</para>
    ///
    /// <para><see cref="ApplyTo"/> returns a COPY. The options it starts from belong to the cached
    /// analysis every other caller is reading, and writing one reader's figures into them would change
    /// the model for everybody until the cache expired.</para>
    ///
    /// <para>Each figure is clamped to the same bounds the portal enforces, so a hand-edited URL cannot
    /// turn a rollout-sizing model into a headline of millions of hours.</para>
    /// </remarks>
    public class CoworkTimeSavedOverrides
    {
        /// <summary>Upper bound for the per-meeting and per-document assumptions, in minutes.</summary>
        public const double MaxMinutesPerItem = 120;

        /// <summary>Upper bound for the per-email assumption, in minutes.</summary>
        public const double MaxMinutesPerEmail = 60;

        /// <summary>
        /// Upper bound for the per-Cowork-task assumption, in minutes. Higher than a meeting's: one task
        /// can be a whole piece of multi-step work.
        /// </summary>
        public const double MaxMinutesPerTask = 240;

        /// <summary>Upper bound for the Cowork tasks a month each projected person runs: ten a working day.</summary>
        public const double MaxTasksPerPersonPerMonth = 200;

        public double? MinutesSavedPerMeeting { get; set; }

        public double? MinutesSavedPerMailThread { get; set; }

        public double? MinutesSavedPerDocument { get; set; }

        public double? LowerBoundRatio { get; set; }

        public double? MinutesSavedPerTask { get; set; }

        /// <summary>
        /// The Cowork tasks a month the reader expects each not-yet-observed person to run. Not an option:
        /// it replaces the published rate - observed or placeholder - via <see cref="TaskRateFor"/>.
        /// </summary>
        public double? TasksPerPersonPerMonth { get; set; }

        /// <summary>True when at least one usable figure was supplied.</summary>
        public bool Any =>
            Usable(MinutesSavedPerMeeting)
            || Usable(MinutesSavedPerMailThread)
            || Usable(MinutesSavedPerDocument)
            || Usable(LowerBoundRatio)
            || Usable(MinutesSavedPerTask)
            || Usable(TasksPerPersonPerMonth);

        /// <summary>
        /// A copy of <paramref name="options"/> with the supplied figures in place of the defaults.
        /// Figures that were not supplied, or are not a finite number, keep the configured default.
        /// </summary>
        public CopilotAdoptionOptions ApplyTo(CopilotAdoptionOptions options)
        {
            var copy = (options ?? CopilotAdoptionOptions.Default).Clone();

            if (Usable(MinutesSavedPerMeeting))
                copy.CoworkMinutesSavedPerMeeting = Clamp(MinutesSavedPerMeeting.Value, 0, MaxMinutesPerItem);
            if (Usable(MinutesSavedPerMailThread))
                copy.CoworkMinutesSavedPerMailThread = Clamp(MinutesSavedPerMailThread.Value, 0, MaxMinutesPerEmail);
            if (Usable(MinutesSavedPerDocument))
                copy.CoworkMinutesSavedPerDocument = Clamp(MinutesSavedPerDocument.Value, 0, MaxMinutesPerItem);
            if (Usable(LowerBoundRatio))
                copy.CoworkEstimateLowerBoundRatio = Clamp(LowerBoundRatio.Value, 0, 1);
            if (Usable(MinutesSavedPerTask))
                copy.CoworkMinutesSavedPerTask = Clamp(MinutesSavedPerTask.Value, 0, MaxMinutesPerTask);

            return copy;
        }

        /// <summary>
        /// The Cowork task rate to restate <paramref name="published"/> with: the reader's own figure when
        /// they supplied one, otherwise the rate the estimate was published with, and its basis.
        /// </summary>
        public CoworkTaskRate TaskRateFor(CoworkValueEstimate published)
        {
            if (Usable(TasksPerPersonPerMonth))
            {
                return new CoworkTaskRate
                {
                    TasksPerPersonPerMonth = Clamp(TasksPerPersonPerMonth.Value, 0, MaxTasksPerPersonPerMonth),
                    Basis = CoworkTaskRateBases.Custom,
                };
            }

            return PublishedTaskRate(published);
        }

        /// <summary>The rate, basis and averaged population an estimate was published with.</summary>
        public static CoworkTaskRate PublishedTaskRate(CoworkValueEstimate published)
        {
            return new CoworkTaskRate
            {
                TasksPerPersonPerMonth = published?.CoworkTasksPerPersonPerMonth ?? 0,
                Basis = published?.CoworkTaskRateBasis ?? CoworkTaskRateBases.Assumed,
                Users = published?.CoworkTaskRateUsers ?? 0,
            };
        }

        private static bool Usable(double? value)
        {
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Min(max, Math.Max(min, value));
        }
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
