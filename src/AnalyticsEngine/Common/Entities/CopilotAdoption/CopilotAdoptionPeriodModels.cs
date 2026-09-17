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

    public class CopilotAdoptionCreateCohortRequest
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("actionCode")] public string ActionCode { get; set; }
        [JsonProperty("createdBy")] public string CreatedBy { get; set; }
        [JsonProperty("holdoutPercentage")] public int HoldoutPercentage { get; set; }
    }

    public class CopilotAdoptionCreateInterventionRequest : CopilotAdoptionCreateCohortRequest
    {
        [JsonProperty("owner")] public string Owner { get; set; }
        [JsonProperty("interventionType")] public string InterventionType { get; set; }
        [JsonProperty("guidanceResource")] public string GuidanceResource { get; set; }
        [JsonProperty("startedUtc")] public DateTime? StartedUtc { get; set; }
        [JsonProperty("dueUtc")] public DateTime? DueUtc { get; set; }
        [JsonProperty("completedUtc")] public DateTime? CompletedUtc { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("intendedOutcome")] public string IntendedOutcome { get; set; }
        [JsonProperty("notes")] public string Notes { get; set; }
        [JsonProperty("intendedReinvestmentType")] public string IntendedReinvestmentType { get; set; }
        [JsonProperty("intendedReinvestmentDescription")] public string IntendedReinvestmentDescription { get; set; }
    }

    public class CopilotAdoptionCohort
    {
        [JsonProperty("cohortId")] public int CohortId { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("actionCode")] public string ActionCode { get; set; }
        [JsonProperty("createdUtc")] public DateTime CreatedUtc { get; set; }
        [JsonProperty("createdBy")] public string CreatedBy { get; set; }
        [JsonProperty("baselinePeriodEnd")] public DateTime BaselinePeriodEnd { get; set; }
        [JsonProperty("baselinePeriodDays")] public int BaselinePeriodDays { get; set; }
        [JsonProperty("baselineOptionsHash")] public string BaselineOptionsHash { get; set; }
        [JsonProperty("closedUtc")] public DateTime? ClosedUtc { get; set; }
        [JsonProperty("closedBy")] public string ClosedBy { get; set; }
        [JsonProperty("memberCount")] public int MemberCount { get; set; }
        [JsonProperty("holdoutCount")] public int HoldoutCount { get; set; }
    }

    public class CopilotAdoptionCohortMember
    {
        [JsonProperty("cohortId")] public int CohortId { get; set; }
        [JsonProperty("userId")] public int UserId { get; set; }
        [JsonProperty("userPrincipalName")] public string UserPrincipalName { get; set; }
        [JsonProperty("mail")] public string Mail { get; set; }
        [JsonProperty("department")] public string Department { get; set; }
        [JsonProperty("baselineBand")] public AdoptionBand BaselineBand { get; set; }
        [JsonProperty("baselineBandName")] public string BaselineBandName { get; set; }
        [JsonProperty("baselineScore")] public double BaselineScore { get; set; }
        [JsonProperty("baselineActiveDays")] public int BaselineActiveDays { get; set; }
        [JsonProperty("holdoutControl")] public bool HoldoutControl { get; set; }
    }

    public class CopilotAdoptionIntervention
    {
        [JsonProperty("interventionId")] public int InterventionId { get; set; }
        [JsonProperty("cohortId")] public int CohortId { get; set; }
        [JsonProperty("cohortName")] public string CohortName { get; set; }
        [JsonProperty("actionCode")] public string ActionCode { get; set; }
        [JsonProperty("owner")] public string Owner { get; set; }
        [JsonProperty("interventionType")] public string InterventionType { get; set; }
        [JsonProperty("guidanceResource")] public string GuidanceResource { get; set; }
        [JsonProperty("startedUtc")] public DateTime? StartedUtc { get; set; }
        [JsonProperty("dueUtc")] public DateTime? DueUtc { get; set; }
        [JsonProperty("completedUtc")] public DateTime? CompletedUtc { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("intendedOutcome")] public string IntendedOutcome { get; set; }
        [JsonProperty("notes")] public string Notes { get; set; }
        [JsonProperty("intendedReinvestmentType")] public string IntendedReinvestmentType { get; set; }
        [JsonProperty("intendedReinvestmentDescription")] public string IntendedReinvestmentDescription { get; set; }
        [JsonProperty("createdUtc")] public DateTime CreatedUtc { get; set; }
        [JsonProperty("memberCount")] public int MemberCount { get; set; }
        [JsonProperty("isOverdue")] public bool IsOverdue { get; set; }
        [JsonProperty("isUnstarted")] public bool IsUnstarted { get; set; }
    }

    public class CopilotAdoptionInterventionOutcome
    {
        [JsonProperty("intervention")] public CopilotAdoptionIntervention Intervention { get; set; }
        [JsonProperty("cohort")] public CopilotAdoptionCohort Cohort { get; set; }
        [JsonProperty("followupPeriodEnd")] public DateTime FollowupPeriodEnd { get; set; }
        [JsonProperty("methodLabel")] public string MethodLabel { get; set; }
        [JsonProperty("observational")] public bool Observational { get; set; }
        [JsonProperty("refused")] public bool Refused { get; set; }
        [JsonProperty("refusalReason")] public string RefusalReason { get; set; }
        [JsonProperty("matchingCriteria")] public string MatchingCriteria { get; set; }
        [JsonProperty("treatedN")] public int TreatedN { get; set; }
        [JsonProperty("controlN")] public int ControlN { get; set; }
        [JsonProperty("treatedChange")] public double TreatedChange { get; set; }
        [JsonProperty("controlChange")] public double ControlChange { get; set; }
        [JsonProperty("differenceInDifferences")] public double DifferenceInDifferences { get; set; }
        [JsonProperty("effectSizeLabel")] public string EffectSizeLabel { get; set; }
        [JsonProperty("controlComposition")] public List<CopilotAdoptionControlCompositionRow> ControlComposition { get; set; } = new List<CopilotAdoptionControlCompositionRow>();
        [JsonProperty("leadingIndicators")] public List<CopilotAdoptionLeadingIndicatorOutcome> LeadingIndicators { get; set; } = new List<CopilotAdoptionLeadingIndicatorOutcome>();
    }

    public class CopilotAdoptionControlCompositionRow
    {
        [JsonProperty("department")] public string Department { get; set; }
        [JsonProperty("band")] public string Band { get; set; }
        [JsonProperty("scoreBucket")] public string ScoreBucket { get; set; }
        [JsonProperty("users")] public int Users { get; set; }
    }

    public class CopilotAdoptionLeadingIndicatorOutcome
    {
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("treatedChange")] public double TreatedChange { get; set; }
        [JsonProperty("controlChange")] public double ControlChange { get; set; }
        [JsonProperty("differenceInDifferences")] public double DifferenceInDifferences { get; set; }
        [JsonProperty("movementLabel")] public string MovementLabel { get; set; }
    }

    public class CopilotAdoptionWorkloadSnapshotRow
    {
        public int UserId { get; set; }
        public long TeamsMessages { get; set; }
        public long TeamsMeetings { get; set; }
        public long EmailsSent { get; set; }
        public long EmailsRead { get; set; }
        public long FilesViewedOrEdited { get; set; }
    }
}
