using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// The modelled licence estimate: the time a Microsoft 365 Copilot licence could give back to the
    /// people recommended for one (<see cref="CopilotAdoptionSummary.LicenceOpportunityEstimate"/>), to
    /// the recommended candidates already using Copilot Chat without one
    /// (<see cref="CopilotAdoptionSummary.LicenceChatUsersEstimate"/>), and to every licence candidate,
    /// recommended or not (<see cref="CopilotAdoptionSummary.LicenceAllCandidatesEstimate"/>).
    ///
    /// <b>Every hour here is an assumption applied to observed volume, and none of it is measured.</b>
    /// The volumes (<see cref="AddressableMeetings"/> and its siblings) come from Microsoft's usage
    /// reports and are real; the minutes saved on each are assumptions. <see cref="Assumptions"/>
    /// travels with the numbers so no surface can render a figure without the assumption that produced
    /// it, and <see cref="IsModelled"/> exists so a consumer cannot mistake this for evidence.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this figure sits beside the licence decision.</b> The minutes per meeting, email
    /// and document are derived from Microsoft's published Copilot credits and checked against published
    /// studies of Microsoft 365 Copilot. The largest of those, a randomised field experiment, randomised
    /// who received a licence - so it measured exactly the decision this figure sizes.</para>
    ///
    /// <para><b>Deliberately no equivalent for existing seat holders.</b> The same arithmetic over the
    /// people who already hold a licence used to lead the Cowork tab, and no decision hangs on it: those
    /// people should all be using their licence already, and enabling Cowork does not unlock time the
    /// licence already gives back.</para>
    /// </remarks>
    public class LicenceValueEstimate
    {
        /// <summary>Always true. Present so serialised consumers carry the caveat with the payload.</summary>
        [JsonProperty("isModelled")]
        public bool IsModelled { get; set; } = true;

        /// <summary>Licence candidates the estimate covers - see <see cref="LicenceEstimateCohort"/>.</summary>
        [JsonProperty("cohortUsers")]
        public int CohortUsers { get; set; }

        #region Observed inputs

        /// <summary>Meetings a month across the cohort, from the usage reports. Observed, not modelled.</summary>
        [JsonProperty("addressableMeetings")]
        public double AddressableMeetings { get; set; }

        /// <summary>Emails sent and read a month across the cohort. Observed, not modelled.</summary>
        [JsonProperty("addressableMailThreads")]
        public double AddressableMailThreads { get; set; }

        /// <summary>Document touches a month across the cohort. Observed, not modelled.</summary>
        [JsonProperty("addressableDocuments")]
        public double AddressableDocuments { get; set; }

        #endregion

        #region Modelled outputs

        /// <summary>Low end of the modelled monthly hours: the high end at the conservative share.</summary>
        [JsonProperty("hoursPerMonthLow")]
        public double HoursPerMonthLow { get; set; }

        /// <summary>High end of the modelled monthly hours: every volume x its minutes-saved assumption.</summary>
        [JsonProperty("hoursPerMonthHigh")]
        public double HoursPerMonthHigh { get; set; }

        // No monetary figure, for the reasons recorded on CoworkValueEstimate: this report reports seats,
        // people and hours, never money.

        #endregion

        /// <summary>
        /// True when the licence-opportunity query returned
        /// <see cref="CopilotAdoptionOptions.MaxOpportunityCandidates"/> rows, so candidates beyond the cap
        /// were never scored and the true figure may be higher.
        /// </summary>
        [JsonProperty("candidatesCapped")]
        public bool CandidatesCapped { get; set; }

        /// <summary>
        /// The assumptions used, in plain English, for display next to the numbers. Never empty when any
        /// figure above is non-zero.
        /// </summary>
        [JsonProperty("assumptions")]
        public List<string> Assumptions { get; set; } = new List<string>();
    }

    /// <summary>
    /// Which licence candidates a <see cref="LicenceValueEstimate"/> covers. It decides only how the
    /// estimate's assumptions describe the people in it: the arithmetic is the same for every cohort.
    /// </summary>
    public enum LicenceEstimateCohort
    {
        /// <summary>
        /// The people recommended for a licence, or a subset of them - the purchase the candidate list
        /// makes the case for, and the default the portal leads with.
        /// </summary>
        Recommended,

        /// <summary>
        /// Every candidate the list ranked, recommended or not: everyone active in Copilot Chat or
        /// Microsoft 365 in the period without a Copilot licence. The figure a reader can still size a
        /// purchase with on a tenant where nobody uses Microsoft 365 heavily enough to be recommended.
        /// </summary>
        AllCandidates,
    }

    /// <summary>
    /// A reader's own time-saved assumptions, sent with an Excel export so the workbook models the same
    /// figures they were looking at in the portal.
    /// </summary>
    /// <remarks>
    /// <para>Covers both estimates. The Copilot minutes per meeting, email and document restate the
    /// licence estimate; the share and minutes for each kind of work Cowork could take on
    /// (<see cref="CoworkActivities"/>) restate the Cowork estimate; the conservative share applies to
    /// both.</para>
    ///
    /// <para>The portal lets a reader replace any of the assumptions with their own figure. Those
    /// figures live in that browser tab only - they are never persisted - so an export has to carry
    /// them, or the Excel report downloaded from a customised page would quietly model different hours
    /// from the screen it came from.</para>
    ///
    /// <para><see cref="ApplyTo"/> returns a COPY. The options it starts from belong to the cached
    /// analysis every other caller is reading, and writing one reader's figures into them would change
    /// the model for everybody until the cache expired.</para>
    ///
    /// <para>Each figure is clamped to the same bounds the portal enforces, so a hand-edited URL cannot
    /// turn a sizing model into a headline of millions of hours.</para>
    /// </remarks>
    public class TimeSavedOverrides
    {
        /// <summary>Upper bound for the per-meeting and per-document assumptions, in minutes.</summary>
        public const double MaxMinutesPerItem = 120;

        /// <summary>Upper bound for the per-email assumption, in minutes.</summary>
        public const double MaxMinutesPerEmail = 60;

        /// <summary>
        /// Upper bound for the minutes Cowork saves on one piece of work it takes on, in minutes. Higher
        /// than a meeting's: one piece can be a whole multi-step job.
        /// </summary>
        public const double MaxMinutesPerTask = 240;

        /// <summary>Minutes Copilot saves per meeting. Restates the licence estimate.</summary>
        public double? MinutesSavedPerMeeting { get; set; }

        /// <summary>Minutes Copilot saves per email. Restates the licence estimate.</summary>
        public double? MinutesSavedPerMailThread { get; set; }

        /// <summary>Minutes Copilot saves per document. Restates the licence estimate.</summary>
        public double? MinutesSavedPerDocument { get; set; }

        /// <summary>The conservative share of every assumption. Restates both estimates.</summary>
        public double? LowerBoundRatio { get; set; }

        /// <summary>Minutes credited to one observed Outlook Copilot action for already-licensed users.</summary>
        public double? SeatOutlookMinutesPerAction { get; set; }

        /// <summary>Minutes credited to one observed Word, PowerPoint or Excel Copilot action for already-licensed users.</summary>
        public double? SeatOfficeMinutesPerAction { get; set; }

        /// <summary>Minutes credited to one observed Teams meeting recap/summarise action for already-licensed users.</summary>
        public double? SeatMeetingMinutesPerAction { get; set; }

        /// <summary>Minutes credited to Copilot Chat and other uncredited actions for already-licensed users.</summary>
        public double? SeatUncreditedMinutesPerAction { get; set; }

        /// <summary>
        /// The share of each kind of work handed to Cowork, 0 to 1, keyed by <see cref="CoworkActivities"/>
        /// key. Restates the Cowork estimate. A key this build does not know is ignored.
        /// </summary>
        public IDictionary<string, double?> CoworkShares { get; } = new Dictionary<string, double?>(StringComparer.Ordinal);

        /// <summary>
        /// The minutes Cowork saves on each piece of each kind of work, keyed by <see cref="CoworkActivities"/>
        /// key. Restates the Cowork estimate. A key this build does not know is ignored.
        /// </summary>
        public IDictionary<string, double?> CoworkMinutes { get; } = new Dictionary<string, double?>(StringComparer.Ordinal);

        /// <summary>True when at least one usable figure was supplied.</summary>
        public bool Any =>
            Usable(MinutesSavedPerMeeting)
            || Usable(MinutesSavedPerMailThread)
            || Usable(MinutesSavedPerDocument)
            || Usable(LowerBoundRatio)
            || AnySeatHolder
            || CoworkActivities.All.Any(a => Usable(Lookup(CoworkShares, a.Key)) || Usable(Lookup(CoworkMinutes, a.Key)));

        /// <summary>
        /// True when a figure the Cowork estimate uses was supplied - so a workbook can say "assumptions
        /// entered in the portal" on the Cowork sheet only when its own figures changed.
        /// </summary>
        public bool AnyCowork =>
            CoworkActivities.All.Any(a => Usable(Lookup(CoworkShares, a.Key)) || Usable(Lookup(CoworkMinutes, a.Key)));

        /// <summary>True when a figure used by the already-licensed seat-holder estimate was supplied.</summary>
        public bool AnySeatHolder =>
            Usable(SeatOutlookMinutesPerAction)
            || Usable(SeatOfficeMinutesPerAction)
            || Usable(SeatMeetingMinutesPerAction)
            || Usable(SeatUncreditedMinutesPerAction)
            || Usable(LowerBoundRatio);

        /// <summary>
        /// A copy of <paramref name="options"/> with the supplied figures in place of the defaults.
        /// Figures that were not supplied, or are not a finite number, keep the configured default.
        /// </summary>
        public CopilotAdoptionOptions ApplyTo(CopilotAdoptionOptions options)
        {
            var copy = (options ?? CopilotAdoptionOptions.Default).Clone();

            if (Usable(MinutesSavedPerMeeting))
                copy.CopilotMinutesSavedPerMeeting = Clamp(MinutesSavedPerMeeting.Value, 0, MaxMinutesPerItem);
            if (Usable(MinutesSavedPerMailThread))
                copy.CopilotMinutesSavedPerMailThread = Clamp(MinutesSavedPerMailThread.Value, 0, MaxMinutesPerEmail);
            if (Usable(MinutesSavedPerDocument))
                copy.CopilotMinutesSavedPerDocument = Clamp(MinutesSavedPerDocument.Value, 0, MaxMinutesPerItem);
            if (Usable(LowerBoundRatio))
                copy.CoworkEstimateLowerBoundRatio = Clamp(LowerBoundRatio.Value, 0, 1);
            if (Usable(SeatOutlookMinutesPerAction))
                copy.CopilotSeatOutlookMinutesPerAction = Clamp(SeatOutlookMinutesPerAction.Value, 0, MaxMinutesPerEmail);
            if (Usable(SeatOfficeMinutesPerAction))
                copy.CopilotSeatOfficeMinutesPerAction = Clamp(SeatOfficeMinutesPerAction.Value, 0, MaxMinutesPerItem);
            if (Usable(SeatMeetingMinutesPerAction))
                copy.CopilotSeatMeetingMinutesPerAction = Clamp(SeatMeetingMinutesPerAction.Value, 0, MaxMinutesPerItem);
            if (Usable(SeatUncreditedMinutesPerAction))
                copy.CopilotSeatUncreditedMinutesPerAction = Clamp(SeatUncreditedMinutesPerAction.Value, 0, MaxMinutesPerTask);

            foreach (var activity in CoworkActivities.All)
            {
                var share = Lookup(CoworkShares, activity.Key);
                if (Usable(share)) activity.SetShare(copy, Clamp(share.Value, 0, 1));

                var minutes = Lookup(CoworkMinutes, activity.Key);
                if (Usable(minutes)) activity.SetMinutes(copy, Clamp(minutes.Value, 0, MaxMinutesPerTask));
            }

            return copy;
        }

        private static double? Lookup(IDictionary<string, double?> figures, string key)
        {
            return figures != null && figures.TryGetValue(key, out var value) ? value : null;
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
}
