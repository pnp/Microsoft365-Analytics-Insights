using Newtonsoft.Json;
using System.Collections.Generic;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// Modelled time Microsoft 365 Copilot has already given back to people who hold a Copilot seat.
    ///
    /// <b>Hours only, never money.</b> Counts are observed Copilot audit interactions and distinct
    /// person-meeting contexts for seat holders. Default email/document credits are based on Microsoft's
    /// Copilot assisted-hours metric definitions. That method uses duration for meeting summarisation;
    /// this import identifies neither that intent nor a reliable duration. Its meeting credit is instead
    /// an editable assumption per person-meeting context. Copilot Chat, agents and Cowork default to
    /// zero here because Microsoft does not publish a per-prompt credit for this model. Cowork is excluded
    /// altogether; existing-user Cowork benefit is not estimated.
    ///
    /// Sources: https://learn.microsoft.com/en-us/viva/insights/advanced/reference/metrics and
    /// https://learn.microsoft.com/en-us/viva/insights/org-team-insights/copilot-dashboard#details-on-the-copilot-assisted-hours-metric.
    /// </summary>
    public class SeatHolderTimeSavedEstimate
    {
        [JsonProperty("isModelled")]
        public bool IsModelled { get; set; } = true;

        [JsonProperty("cohortUsers")]
        public int CohortUsers { get; set; }

        [JsonProperty("excludedUsageReportSourcedUsers")]
        public int ExcludedUsageReportSourcedUsers { get; set; }

        [JsonProperty("observedOutlookActions")]
        public double ObservedOutlookActions { get; set; }

        [JsonProperty("observedOfficeActions")]
        public double ObservedOfficeActions { get; set; }

        [JsonProperty("observedTeamsMeetingActions")]
        public double ObservedTeamsMeetingActions { get; set; }

        [JsonProperty("observedUncreditedActions")]
        public double ObservedUncreditedActions { get; set; }

        [JsonProperty("credits")]
        public SeatHolderTimeSavedCredits Credits { get; set; } = new SeatHolderTimeSavedCredits();

        [JsonProperty("hoursPerMonthLow")]
        public double HoursPerMonthLow { get; set; }

        [JsonProperty("hoursPerMonthHigh")]
        public double HoursPerMonthHigh { get; set; }

        [JsonProperty("assumptions")]
        public List<string> Assumptions { get; set; } = new List<string>();

        [JsonProperty("byBand")]
        public List<SeatHolderTimeSavedSegment> ByBand { get; set; } = new List<SeatHolderTimeSavedSegment>();

        [JsonProperty("byDepartment")]
        public List<SeatHolderTimeSavedSegment> ByDepartment { get; set; } = new List<SeatHolderTimeSavedSegment>();

        /// <summary>A shallow copy, so a reader's trimmed summary can replace a list without editing the cached estimate.</summary>
        internal SeatHolderTimeSavedEstimate ShallowCopy() => (SeatHolderTimeSavedEstimate)MemberwiseClone();
    }

    public class SeatHolderTimeSavedCredits
    {
        [JsonProperty("outlookMinutesPerAction")]
        public double OutlookMinutesPerAction { get; set; }

        [JsonProperty("officeMinutesPerAction")]
        public double OfficeMinutesPerAction { get; set; }

        [JsonProperty("teamsMeetingMinutesPerAction")]
        public double TeamsMeetingMinutesPerAction { get; set; }

        [JsonProperty("uncreditedMinutesPerAction")]
        public double UncreditedMinutesPerAction { get; set; }

        [JsonProperty("lowerBoundRatio")]
        public double LowerBoundRatio { get; set; }
    }

    public class SeatHolderTimeSavedSegment
    {
        [JsonProperty("segment")]
        public string Segment { get; set; }

        [JsonProperty("cohortUsers")]
        public int CohortUsers { get; set; }

        [JsonProperty("observedOutlookActions")]
        public double ObservedOutlookActions { get; set; }

        [JsonProperty("observedOfficeActions")]
        public double ObservedOfficeActions { get; set; }

        [JsonProperty("observedTeamsMeetingActions")]
        public double ObservedTeamsMeetingActions { get; set; }

        [JsonProperty("observedUncreditedActions")]
        public double ObservedUncreditedActions { get; set; }

        [JsonProperty("hoursPerMonthLow")]
        public double HoursPerMonthLow { get; set; }

        [JsonProperty("hoursPerMonthHigh")]
        public double HoursPerMonthHigh { get; set; }
    }

    internal class SeatHolderTimeSavedUserRow
    {
        public int UserId { get; set; }
        public string Department { get; set; }
        public long OutlookActions { get; set; }
        public long OfficeActions { get; set; }
        public long TeamsMeetingActions { get; set; }
        public long UncreditedActions { get; set; }
    }
}
