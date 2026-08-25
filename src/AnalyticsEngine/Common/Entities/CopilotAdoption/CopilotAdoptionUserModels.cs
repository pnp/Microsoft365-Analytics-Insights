using Newtonsoft.Json;
using System;

namespace Common.Entities.CopilotAdoption
{
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
}
