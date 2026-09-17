using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Common.Entities.CopilotAdoption
{
    public class CopilotAdoptionPublishedPeriod
    {
        [JsonProperty("run")]
        public CopilotAdoptionPeriodRun Run { get; set; }

        [JsonProperty("analysis")]
        public CopilotAdoptionAnalysis Analysis { get; set; } = new CopilotAdoptionAnalysis();
    }

    public class CopilotAdoptionPeriodRun
    {
        [JsonProperty("periodEnd")] public DateTime PeriodEnd { get; set; }
        [JsonProperty("periodDays")] public int PeriodDays { get; set; }
        [JsonProperty("optionsHash")] public string OptionsHash { get; set; }
        [JsonProperty("auditAvailable")] public bool AuditAvailable { get; set; }
        [JsonProperty("reportObfuscated")] public bool ReportObfuscated { get; set; }
        [JsonProperty("reportPeriodDays")] public int ReportPeriodDays { get; set; }
        [JsonProperty("licensedUsers")] public int LicensedUsers { get; set; }
        [JsonProperty("scoredUsers")] public int ScoredUsers { get; set; }
        [JsonProperty("publishedUtc")] public DateTime PublishedUtc { get; set; }
        [JsonProperty("dataCutoffUtc")] public DateTime DataCutoffUtc { get; set; }
        [JsonProperty("coverageStatus")] public string CoverageStatus { get; set; }
    }

    public static class CopilotAdoptionPeriodPublishStatus
    {
        public const string Published = "published";
        public const string AlreadyPublished = "alreadyPublished";
        public const string NotDue = "notDue";
    }

    public class CopilotAdoptionPeriodPublishResult
    {
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("run")] public CopilotAdoptionPeriodRun Run { get; set; }
        [JsonProperty("message")] public string Message { get; set; }

        public static CopilotAdoptionPeriodPublishResult Published(CopilotAdoptionPeriodRun run)
        {
            return new CopilotAdoptionPeriodPublishResult
            {
                Status = CopilotAdoptionPeriodPublishStatus.Published,
                Run = run,
                Message = "The closed Copilot Adoption period was published."
            };
        }

        public static CopilotAdoptionPeriodPublishResult AlreadyPublished(CopilotAdoptionPeriodRun run)
        {
            return new CopilotAdoptionPeriodPublishResult
            {
                Status = CopilotAdoptionPeriodPublishStatus.AlreadyPublished,
                Run = run,
                Message = "The Copilot Adoption period has already been published."
            };
        }

        public static CopilotAdoptionPeriodPublishResult NotDue(string message)
        {
            return new CopilotAdoptionPeriodPublishResult
            {
                Status = CopilotAdoptionPeriodPublishStatus.NotDue,
                Message = message
            };
        }
    }

    public class CopilotAdoptionPeriodComparisonGate
    {
        [JsonProperty("left")] public CopilotAdoptionPeriodRun Left { get; set; }
        [JsonProperty("right")] public CopilotAdoptionPeriodRun Right { get; set; }
        [JsonProperty("optionsComparable")] public bool OptionsComparable { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
    }

    public class CopilotAdoptionStoredPeriodFactRow : LicensedUserUsageRow
    {
        public DateTime PeriodEnd { get; set; }
        public int PeriodDays { get; set; }
        public DateTime DataCutoffUtc { get; set; }
        public string SeatLicenceTypeIds { get; set; }
        public DateTime? SeatFirstObservedUtc { get; set; }
        public string SignalSource { get; set; }
        public int ActiveWeeks { get; set; }
        public string CoverageStatus { get; set; }
    }

    public static class CopilotAdoptionCohortTransitions
    {
        public const string Retained = "retained";
        public const string Reactivated = "reactivated";
        public const string Lapsed = "lapsed";
        public const string Reclaimed = "reclaimed";
        public const string NewlyAssigned = "newlyAssigned";
        public const string StillAtRisk = "stillAtRisk";
    }

    public class CopilotAdoptionCohortComparison
    {
        [JsonProperty("gate")]
        public CopilotAdoptionPeriodComparisonGate Gate { get; set; } = new CopilotAdoptionPeriodComparisonGate();

        [JsonProperty("summary")]
        public CopilotAdoptionCohortSummary Summary { get; set; } = new CopilotAdoptionCohortSummary();

        [JsonProperty("transitions")]
        public List<CopilotAdoptionCohortTransitionSummary> Transitions { get; set; } = new List<CopilotAdoptionCohortTransitionSummary>();

        [JsonProperty("flows")]
        public List<CopilotAdoptionCohortFlowSummary> Flows { get; set; } = new List<CopilotAdoptionCohortFlowSummary>();

        [JsonProperty("activation")]
        public CopilotAdoptionActivationSummary Activation { get; set; } = new CopilotAdoptionActivationSummary();

        [JsonIgnore]
        public List<CopilotAdoptionCohortUserRow> Rows { get; set; } = new List<CopilotAdoptionCohortUserRow>();
    }

    public class CopilotAdoptionCohortSummary
    {
        [JsonProperty("earlierPopulation")]
        public int EarlierPopulation { get; set; }

        [JsonProperty("currentPopulation")]
        public int CurrentPopulation { get; set; }

        [JsonProperty("newlyAssigned")]
        public int NewlyAssigned { get; set; }

        [JsonProperty("earlierPopulationTransitionTotal")]
        public int EarlierPopulationTransitionTotal { get; set; }

        [JsonProperty("transitionsSumToEarlierPopulation")]
        public bool TransitionsSumToEarlierPopulation { get; set; }

        [JsonProperty("reclaimCaveat")]
        public string ReclaimCaveat { get; set; }

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class CopilotAdoptionCohortTransitionSummary
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("users")]
        public int Users { get; set; }

        [JsonProperty("shareOfEarlierPopulationPct")]
        public double ShareOfEarlierPopulationPct { get; set; }
    }

    public class CopilotAdoptionCohortFlowSummary
    {
        [JsonProperty("fromBand")]
        public string FromBand { get; set; }

        [JsonProperty("toBand")]
        public string ToBand { get; set; }

        [JsonProperty("transition")]
        public string Transition { get; set; }

        [JsonProperty("users")]
        public int Users { get; set; }
    }

    public class CopilotAdoptionActivationSummary
    {
        [JsonProperty("activationWindowDays")]
        public int ActivationWindowDays { get; set; }

        [JsonProperty("knownSeatStartUsers")]
        public int KnownSeatStartUsers { get; set; }

        [JsonProperty("seatDateUnknownUsers")]
        public int SeatDateUnknownUsers { get; set; }

        [JsonProperty("assignedBeforeHistoryUsers")]
        public int AssignedBeforeHistoryUsers { get; set; }

        [JsonProperty("newSeatsAssignedInPeriod")]
        public int NewSeatsAssignedInPeriod { get; set; }

        [JsonProperty("activatedWithinWindow")]
        public int ActivatedWithinWindow { get; set; }

        [JsonProperty("activationRatePct")]
        public double ActivationRatePct { get; set; }

        [JsonProperty("neverActivatedUsers")]
        public int NeverActivatedUsers { get; set; }

        [JsonProperty("tooNewToJudgeUsers")]
        public int TooNewToJudgeUsers { get; set; }

        [JsonProperty("medianDaysToFirstUse")]
        public double? MedianDaysToFirstUse { get; set; }

        [JsonProperty("distribution")]
        public List<CopilotAdoptionActivationDistributionBucket> Distribution { get; set; } = new List<CopilotAdoptionActivationDistributionBucket>();

        [JsonProperty("byDepartment")]
        public List<CopilotAdoptionActivationSegment> ByDepartment { get; set; } = new List<CopilotAdoptionActivationSegment>();

        [JsonProperty("caveat")]
        public string Caveat { get; set; }
    }

    public class CopilotAdoptionActivationDistributionBucket
    {
        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("users")]
        public int Users { get; set; }

        [JsonProperty("sharePct")]
        public double SharePct { get; set; }
    }

    public class CopilotAdoptionActivationSegment
    {
        [JsonProperty("segment")]
        public string Segment { get; set; }

        [JsonProperty("newSeatsAssignedInPeriod")]
        public int NewSeatsAssignedInPeriod { get; set; }

        [JsonProperty("activatedWithinWindow")]
        public int ActivatedWithinWindow { get; set; }

        [JsonProperty("activationRatePct")]
        public double ActivationRatePct { get; set; }

        [JsonProperty("neverActivatedUsers")]
        public int NeverActivatedUsers { get; set; }

        [JsonProperty("seatDateUnknownUsers")]
        public int SeatDateUnknownUsers { get; set; }
    }

    public class CopilotAdoptionCohortUserRow
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

        [JsonProperty("manager")]
        public string ManagerUserPrincipalName { get; set; }

        [JsonProperty("accountEnabled")]
        public bool? AccountEnabled { get; set; }

        [JsonProperty("existedInEarlierPeriod")]
        public bool ExistedInEarlierPeriod { get; set; }

        [JsonProperty("existsInCurrentPeriod")]
        public bool ExistsInCurrentPeriod { get; set; }

        [JsonProperty("activeInEarlierPeriod")]
        public bool ActiveInEarlierPeriod { get; set; }

        [JsonProperty("activeInCurrentPeriod")]
        public bool ActiveInCurrentPeriod { get; set; }

        [JsonProperty("fromBand")]
        public string FromBand { get; set; }

        [JsonProperty("toBand")]
        public string ToBand { get; set; }

        [JsonProperty("transition")]
        public string Transition { get; set; }

        [JsonProperty("transitionLabel")]
        public string TransitionLabel { get; set; }

        [JsonProperty("reclaimInterpretation")]
        public string ReclaimInterpretation { get; set; }

        [JsonProperty("seatFirstObservedUtc")]
        public DateTime? SeatFirstObservedUtc { get; set; }

        [JsonProperty("firstInteractionUtc")]
        public DateTime? FirstInteractionUtc { get; set; }

        [JsonProperty("daysToFirstUse")]
        public int? DaysToFirstUse { get; set; }

        [JsonProperty("activationState")]
        public string ActivationState { get; set; }
    }

    public class CopilotAdoptionCohortUserPage
    {
        [JsonProperty("total")]
        public int Total { get; set; }

        [JsonProperty("skip")]
        public int Skip { get; set; }

        [JsonProperty("take")]
        public int Take { get; set; }

        [JsonProperty("rows")]
        public List<CopilotAdoptionCohortUserRow> Rows { get; set; } = new List<CopilotAdoptionCohortUserRow>();

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
