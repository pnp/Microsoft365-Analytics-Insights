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
        /// <summary>Version of the Microsoft guidance catalogue attached to recommended actions.</summary>
        [JsonProperty("guidanceCatalogueVersion")]
        public string GuidanceCatalogueVersion { get; set; } = CopilotAdoptionGuidanceCatalogue.Version;

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

        /// <summary>
        /// Active days needed before the depth component is trusted at face value.
        ///
        /// Depth is interactions per <i>active</i> day, which is the right shape - it stops an
        /// intermittent-but-intensive user being penalised twice for the same low frequency. But it
        /// divides by a number the user controls, so a single active day makes the average trivially
        /// easy to max out: five prompts crammed into one afternoon scored full marks for depth and
        /// banded the user <see cref="AdoptionBand.Developing"/> - "a habit is forming" - when they had
        /// in fact tried Copilot once and never come back. A user active on <i>fewer</i> days could
        /// therefore outscore one active on more.
        ///
        /// Below this many active days the depth component is scaled down in proportion, so a small
        /// sample earns a correspondingly small share of the marks. At or above it, nothing changes -
        /// a genuinely deep user is unaffected.
        /// </summary>
        [JsonProperty("depthMinActiveDays")]
        public double DepthMinActiveDays { get; set; } = 3;

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
        /// How long a newly-created Entra user is protected from reclaim recommendations. This is an
        /// account-age proxy for the future seat-tenure history (#277): a new starter with five days of
        /// Copilot ownership must not be judged against a full-window target, and a new starter with no
        /// prompts yet belongs in "too new to judge", not in a list of seats to take away.
        /// </summary>
        [JsonProperty("reclaimGraceDays")]
        public int ReclaimGraceDays { get; set; } = 30;

        /// <summary>
        /// Days from first observed Copilot seat assignment in which a new seat should reach first use.
        /// Defaults to the same 30-day grace concept as reclaim scoring, so a newly assigned seat is never
        /// both "too new to judge" and "failed to activate" on the same day.
        /// </summary>
        [JsonProperty("activationWindowDays")]
        public int ActivationWindowDays { get; set; } = 30;

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

        /// <summary>
        /// How many agents the inventory query returns.
        /// </summary>
        /// <remarks>
        /// Was 500, which was chosen when <see cref="CopilotAdoptionSql.AgentUsageSql"/> was expensive
        /// enough to time out. That query now reads the interaction table once rather than four times and
        /// completes in a few seconds, so the cap no longer buys anything - and it was actively harmful:
        /// a tenant with more agents than this had its inventory, its "agents to retire" count and its
        /// health breakdown all silently reduced to whichever agents happened to be busiest. Those are
        /// exactly the figures an agent clean-up is run from, and a clean-up driven from a third of the
        /// estate leaves the other two thirds in place.
        /// <para>
        /// Still capped rather than unbounded, because every returned agent is scored in C# and held in
        /// the cached analysis - but at a level a real tenant is unlikely to reach. When it IS reached
        /// the page says so explicitly rather than quietly truncating.
        /// </para>
        /// </remarks>
        [JsonProperty("maxAgents")]
        public int MaxAgents { get; set; } = 5000;

        /// <summary>How many unlicensed Copilot users are pulled in to describe that population.</summary>
        [JsonProperty("maxUnlicensedUsersScored")]
        public int MaxUnlicensedUsersScored { get; set; } = 50000;

        #endregion

        #region Licence-opportunity tuning

        /// <summary>
        /// Weight of "already using Copilot Chat without a licence" in the licence-opportunity score.
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

        /// <summary>
        /// Unlicensed Copilot interactions that score full marks for that component, expressed over
        /// <see cref="OpportunityCopilotTargetBasisDays"/> and scaled to the reporting window by
        /// <see cref="CopilotAdoptionScoring.OpportunityCopilotTargetForWindow"/>.
        ///
        /// It has to be scaled, because the three Microsoft 365 components are per-active-day averages
        /// while this one is a raw total for the window. Left unscaled, the same person clears the bar
        /// over 180 days and misses it over 7 - so a candidate's recommendation would flip purely
        /// because the reader changed the period drop-down, which is indefensible in a list used to
        /// decide who gets a paid seat.
        /// </summary>
        [JsonProperty("opportunityCopilotTarget")]
        public double OpportunityCopilotTarget { get; set; } = 20;

        /// <summary>
        /// The window length <see cref="OpportunityCopilotTarget"/> is expressed over. 28 days matches
        /// Microsoft's usage-report month, and matches the default reporting window so the out-of-the-box
        /// behaviour is unchanged.
        ///
        /// Deliberately its own value rather than reusing
        /// <see cref="HabitBucketNormalisationDays"/>: coupling them would mean retuning the habit
        /// buckets silently changed who is recommended for a licence, which are unrelated decisions.
        /// </summary>
        [JsonProperty("opportunityCopilotTargetBasisDays")]
        public int OpportunityCopilotTargetBasisDays { get; set; } = 28;

        /// <summary>Teams messages + meetings on a typical active day that score full marks.</summary>
        [JsonProperty("opportunityCollaborationTarget")]
        public double OpportunityCollaborationTarget { get; set; } = 60;

        /// <summary>Emails sent + read on a typical active day that score full marks.</summary>
        [JsonProperty("opportunityEmailTarget")]
        public double OpportunityEmailTarget { get; set; } = 80;

        /// <summary>Files viewed or edited on a typical active day that score full marks.</summary>
        [JsonProperty("opportunityDocumentTarget")]
        public double OpportunityDocumentTarget { get; set; } = 40;

        /// <summary>
        /// Opportunity score at or above which an unlicensed user is counted as a recommended licence
        /// candidate in the headline figures.
        /// </summary>
        [JsonProperty("opportunityRecommendScore")]
        public double OpportunityRecommendScore { get; set; } = 50;

        /// <summary>
        /// Distinct days of unlicensed Copilot use that prove demand on their own, regardless of the
        /// composite score.
        ///
        /// Without this the weighting contradicts its own stated intent. The Copilot component is worth
        /// <see cref="OpportunityUnlicensedCopilotWeight"/> (35) and the bar is
        /// <see cref="OpportunityRecommendScore"/> (50), so the one signal that actually <i>proves</i>
        /// demand for Copilot could never clear it alone - while general Microsoft 365 busyness
        /// (25 + 20 + 20 = 65) could. Somebody using Copilot Chat daily without a licence was therefore
        /// not recommended for one, and somebody who had never opened Copilot was.
        ///
        /// Requiring several distinct days rather than a raw interaction count is what makes it
        /// "recurrent" rather than "tried it once", and it does not need normalising when the reporting
        /// period changes - a day is a day whatever the window length.
        ///
        /// Microsoft's own readiness guidance takes the same position, listing Copilot Chat users as
        /// licence candidates ahead of the recommendation based on general Microsoft 365 engagement.
        /// </summary>
        [JsonProperty("opportunityProvenDemandMinActiveDays")]
        public int OpportunityProvenDemandMinActiveDays { get; set; } = 3;

        #endregion

        #region Cowork readiness

        /// <summary>
        /// Weight of Teams collaboration volume (chat + channel messages) in the coordination-load score.
        ///
        /// The four coordination weights should sum to 100, so the resulting score reads as a percentage.
        /// They are separate from the <c>Opportunity*</c> family on purpose: that family answers "would this
        /// person use Copilot at all?", this one answers "does this person have work Cowork could take off
        /// them?". Re-tuning one must not silently move the other.
        /// </summary>
        [JsonProperty("coworkCollaborationWeight")]
        public double CoworkCollaborationWeight { get; set; } = 20;

        /// <summary>
        /// Weight of meetings in the coordination-load score - the heaviest of the four.
        ///
        /// Meetings are weighted above raw message volume because they are the clearest marker of the
        /// multi-step, multi-person coordination Cowork is built to absorb: a meeting implies preparation,
        /// notes, follow-ups and scheduling, which is a chain of delegable tasks rather than a single
        /// message. A person in back-to-back meetings has more recoverable time than one who simply sends a
        /// lot of chat.
        /// </summary>
        [JsonProperty("coworkMeetingWeight")]
        public double CoworkMeetingWeight { get; set; } = 35;

        /// <summary>Weight of email volume (sent + read) in the coordination-load score.</summary>
        [JsonProperty("coworkEmailWeight")]
        public double CoworkEmailWeight { get; set; } = 25;

        /// <summary>
        /// Weight of document work (SharePoint / OneDrive files viewed or edited) in the coordination-load
        /// score. Cowork produces and revises artefacts, so document churn is direct evidence of work it
        /// can do.
        /// </summary>
        [JsonProperty("coworkDocumentWeight")]
        public double CoworkDocumentWeight { get; set; } = 20;

        /// <summary>
        /// Teams messages on a typical active day that score full marks for the collaboration component.
        ///
        /// All four Cowork targets are expressed <b>per active day</b>, matching how
        /// <see cref="CopilotAdoptionSql"/> reduces Graph's daily usage reports. That is what keeps the
        /// score meaning the same thing at any reporting-window length - a raw window total would make the
        /// same person qualify over 180 days and fail over 7.
        /// </summary>
        [JsonProperty("coworkCollaborationTarget")]
        public double CoworkCollaborationTarget { get; set; } = 50;

        /// <summary>Meetings on a typical active day that score full marks. 5 is a heavily-scheduled day.</summary>
        [JsonProperty("coworkMeetingTarget")]
        public double CoworkMeetingTarget { get; set; } = 5;

        /// <summary>Emails sent + read on a typical active day that score full marks.</summary>
        [JsonProperty("coworkEmailTarget")]
        public double CoworkEmailTarget { get; set; } = 80;

        /// <summary>Files viewed or edited on a typical active day that score full marks.</summary>
        [JsonProperty("coworkDocumentTarget")]
        public double CoworkDocumentTarget { get; set; } = 30;

        /// <summary>
        /// Coordination load at or above which a user has enough delegable work for Cowork to be worth
        /// enabling. Below it, Cowork is unlikely to repay the credits it consumes.
        /// </summary>
        [JsonProperty("coworkLoadMinScore")]
        public double CoworkLoadMinScore { get; set; } = 50;

        /// <summary>
        /// Copilot engagement at or above which a user is fluent enough to delegate multi-step work.
        ///
        /// Defaults to the same value as <see cref="EstablishedScore"/> - the "habit formed" line - because
        /// that is exactly the claim being made: Cowork is a step up from Copilot, not an entry point. Left
        /// as its own option so the Cowork bar can be raised without moving every band on the main report.
        /// Someone who has not yet formed a Copilot habit will not hand a multi-day task to an agent, and
        /// enabling them first wastes both the credits and the change-management effort.
        /// </summary>
        [JsonProperty("coworkFluencyMinScore")]
        public double CoworkFluencyMinScore { get; set; } = 50;

        /// <summary>
        /// Distinct days of Cowork use inside the reporting window that count as regular adoption rather
        /// than a trial.
        ///
        /// Counted in <b>days, not interactions</b>, for the same reason
        /// <see cref="OpportunityProvenDemandMinActiveDays"/> is: a day is a day whatever the window
        /// length, so the verdict does not move when the reader changes the period drop-down. One long
        /// afternoon of experimentation is not adoption.
        /// </summary>
        [JsonProperty("coworkRegularMinActiveDays")]
        public int CoworkRegularMinActiveDays { get; set; } = 3;

        /// <summary>
        /// Points added to a user's Copilot fluency score when they have already used at least one Copilot
        /// agent, capped so it can never manufacture fluency on its own.
        ///
        /// Using an agent is the nearest existing behaviour to delegating work to Cowork - it is the same
        /// mental step of handing a task to something that acts on your behalf - so it is genuine evidence
        /// of readiness that the engagement score does not otherwise capture. Deliberately a modest uplift:
        /// it should promote a borderline user, not carry an inactive one over the bar.
        /// </summary>
        [JsonProperty("coworkAgentFamiliarityUplift")]
        public double CoworkAgentFamiliarityUplift { get; set; } = 10;

        /// <summary>How many Cowork readiness rows are pulled into memory to be scored.</summary>
        [JsonProperty("maxCoworkUsersScored")]
        public int MaxCoworkUsersScored { get; set; } = 50000;

        #endregion

        #region Cowork value estimate (MODELLED - not measured)

        /// <summary>
        /// Minutes of preparation, note-taking and follow-up that Cowork is assumed to absorb per meeting.
        /// </summary>
        /// <remarks>
        /// <b>This and its siblings are assumptions, not measurements.</b> Nothing in the database observes
        /// time saved, and this product cannot measure it. What the database <i>does</i> observe is the
        /// volume of delegable work - meetings, mail threads, document touches - and these constants turn
        /// that observed volume into an illustrative range.
        /// <para>
        /// Everything derived from them must be labelled as modelled, must be rendered with the assumption
        /// visible on the same surface, and must never be mixed into a figure presented as evidence. The
        /// rest of this report is defensible because it shows its working; an unlabelled hours-saved number
        /// quoted in a board pack would discredit all of it.
        /// </para>
        /// </remarks>
        [JsonProperty("coworkMinutesSavedPerMeeting")]
        public double CoworkMinutesSavedPerMeeting { get; set; } = 5;

        /// <summary>Minutes assumed saved per mail thread Cowork drafts, triages or summarises.</summary>
        [JsonProperty("coworkMinutesSavedPerMailThread")]
        public double CoworkMinutesSavedPerMailThread { get; set; } = 1;

        /// <summary>Minutes assumed saved per document Cowork drafts, revises or summarises.</summary>
        [JsonProperty("coworkMinutesSavedPerDocument")]
        public double CoworkMinutesSavedPerDocument { get; set; } = 3;

        /// <summary>
        /// Fraction of the assumption applied to produce the <b>low</b> end of the reported range; the high
        /// end uses the assumption as stated.
        ///
        /// The estimate is published as a range rather than a single number because a point estimate
        /// invites precision that does not exist. A reader who sees "120-240 hours a month" understands
        /// they are being shown a model; one who sees "183 hours" believes it was counted.
        /// </summary>
        [JsonProperty("coworkEstimateLowerBoundRatio")]
        public double CoworkEstimateLowerBoundRatio { get; set; } = 0.5;

        /// <summary>
        /// Fully-loaded hourly cost used to express the modelled time saving in money.
        ///
        /// <b>Null by default, and null means no monetary figure is produced at all.</b> There is no
        /// defensible default for this - it varies by role, country and employer - so the tool does not
        /// invent one. An admin who wants a currency figure supplies the rate and owns it; until then the
        /// estimate is reported in hours only.
        /// </summary>
        [JsonProperty("coworkLoadedCostPerHour")]
        public double? CoworkLoadedCostPerHour { get; set; }

        /// <summary>
        /// Currency code for <see cref="CoworkLoadedCostPerHour"/>, used only as a display label. Not
        /// defaulted, and no conversion is ever performed: the tool reports the number it was given in the
        /// units it was given.
        /// </summary>
        [JsonProperty("coworkCurrencyCode")]
        public string CoworkCurrencyCode { get; set; }

        #endregion

        /// <summary>
        /// Hard ceiling on how many licensed users are pulled into memory to be scored. The scored set
        /// is held in C# (not SQL) so that the scoring rules have exactly one implementation, which is
        /// unit-testable and shared with any future scheduled report - see
        /// <see cref="CopilotAdoptionScoring"/>. Copilot seats are purchased individually, so even a
        /// very large customer is far below this; if it is ever hit the result carries an explicit
        /// warning rather than silently truncating a licence-spend report.
        ///
        /// <para>
        /// <b>Deliberately not raised to the 200,000-user design point.</b> Raising it was considered as
        /// a way to make the oldest-record bias below less likely to bite, and rejected: the scored set
        /// is materialised in memory and held in a ten-minute result cache, and every row carries
        /// several strings including a full prose recommendation, so a four-fold raise is a four-fold
        /// increase in retained memory per cached analysis. This page already has a history of timing
        /// out and of losing its AppDomain mid-run, which is why the lifecycle telemetry exists. Raising
        /// the cap would also not fix the bias - a tenant past the new cap is truncated exactly as
        /// unfairly - so it would trade a real memory risk for no correctness gain. If the cap is ever
        /// genuinely binding for a customer, the fix is exact SQL aggregates for the headline figures,
        /// measured per the repository's benchmarking rule, not a bigger number here.
        /// </para>
        /// </summary>
        [JsonProperty("maxLicensedUsersScored")]
        public int MaxLicensedUsersScored { get; set; } = 50000;

        /// <summary>
        /// How many unlicensed candidates the database ranks and returns for the opportunity list.
        /// </summary>
        /// <remarks>
        /// Raised from 5,000 to match its sibling caps. At 5,000 this can become the binding constraint
        /// on a large tenant rather than a safety valve, and the failure mode is particularly poor: the
        /// "recommended for a licence" headline is a COUNT of this list, so a tenant with more
        /// candidates than the cap saw a headline KPI that was simply the cap value - a number that
        /// looks like a finding and is actually a limit. Since this list is what a licence-purchase
        /// case is built from, understating it understates the spend being justified.
        /// <para>
        /// This is not free. On a synthetic 200k-user tenant the opportunity query roughly doubled
        /// (5,242ms -&gt; 12,146ms) because it now returns ten times the rows. That bench is a worst case -
        /// it has far more unlicensed candidates than a real tenant - and the step has ample headroom
        /// since the interaction-table fixes landed, so a correct headline is worth the cost.
        /// </para>
        /// <para>
        /// The better fix, deferred: take the headline COUNT from its own cheap aggregate (as
        /// <c>UnlicensedActiveUsersSql</c> already does for the sibling KPI) and leave the returned LIST
        /// capped at something a human would act on. Then the number is always true and the list stays
        /// small. That is a wiring change across the summary and the API, not a constant.
        /// </para>
        /// </remarks>
        [JsonProperty("maxOpportunityCandidates")]
        public int MaxOpportunityCandidates { get; set; } = 50000;

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

        /// <summary>
        /// Organisational field used for the accountability roll-up. Defaults to the direct manager:
        /// that is the narrow governance-safe first cut for issue #556, while still allowing tenants
        /// whose spending is owned by department, country, office or company to point the same roll-up
        /// at the unit they actually manage.
        /// </summary>
        [JsonProperty("accountabilityDimension")]
        public string AccountabilityDimension { get; set; } = CopilotAdoptionAccountabilityDimensions.DirectManager;

        public static CopilotAdoptionOptions Default => new CopilotAdoptionOptions();
    }

    /// <summary>Allowed accountability dimensions. Used as an allow-list before anything reaches SQL.</summary>
    public static class CopilotAdoptionAccountabilityDimensions
    {
        public const string DirectManager = "directManager";
        public const string Department = "department";
        public const string Country = "country";
        public const string Office = "office";
        public const string Company = "company";
    }
}
