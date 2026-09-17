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


    public static class CopilotAdoptionComparisonModes
    {
        public const string PreviousPeriod = "previousPeriod";
        public const string SamePeriodLastQuarter = "samePeriodLastQuarter";
    }

    public static class CopilotAdoptionTargetMetricCodes
    {
        public const string AdoptionRatePct = "adoptionRatePct";
        public const string HabitRatePct = "habitRatePct";
        public const string ReclaimableSeats = "reclaimableSeats";
        public const string ReclaimCertainSeats = "reclaimCertainSeats";
        public const string ReclaimProbableSeats = "reclaimProbableSeats";
        public const string ReclaimReviewSeats = "reclaimReviewSeats";
        public const string NeverUsedUsers = "neverUsedUsers";
        public const string DormantUsers = "dormantUsers";
        public const string AverageAdoptionScore = "averageAdoptionScore";
        public const string MedianAdoptionScore = "medianAdoptionScore";
        public const string UnlicensedActiveUsers = "unlicensedActiveUsers";
        public const string RecommendedForLicence = "recommendedForLicence";
    }

    public class CopilotAdoptionMetricDelta
    {
        [JsonProperty("metric")] public string Metric { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("currentValue")] public double CurrentValue { get; set; }
        [JsonProperty("priorValue")] public double PriorValue { get; set; }
        [JsonProperty("change")] public double Change { get; set; }
        [JsonProperty("unit")] public string Unit { get; set; }
        [JsonProperty("denominatorCurrent")] public double? DenominatorCurrent { get; set; }
        [JsonProperty("denominatorPrior")] public double? DenominatorPrior { get; set; }
        [JsonProperty("denominatorChange")] public double? DenominatorChange { get; set; }
    }

    public class CopilotAdoptionPeriodMovement
    {
        [JsonProperty("mode")] public string Mode { get; set; }
        [JsonProperty("available")] public bool Available { get; set; }
        [JsonProperty("comparable")] public bool Comparable { get; set; }
        [JsonProperty("currentPeriodEnd")] public DateTime? CurrentPeriodEnd { get; set; }
        [JsonProperty("priorPeriodEnd")] public DateTime? PriorPeriodEnd { get; set; }
        [JsonProperty("periodDays")] public int PeriodDays { get; set; }
        [JsonProperty("comparisonLabel")] public string ComparisonLabel { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("deltas")] public List<CopilotAdoptionMetricDelta> Deltas { get; set; } = new List<CopilotAdoptionMetricDelta>();
    }

    public class CopilotAdoptionTarget
    {
        [JsonProperty("id")] public int Id { get; set; }
        [JsonProperty("metric")] public string Metric { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("scopeType")] public string ScopeType { get; set; }
        [JsonProperty("scopeValue")] public string ScopeValue { get; set; }
        [JsonProperty("targetValue")] public double TargetValue { get; set; }
        [JsonProperty("owner")] public string Owner { get; set; }
        [JsonProperty("baselinePeriodEnd")] public DateTime BaselinePeriodEnd { get; set; }
        [JsonProperty("baselinePeriodDays")] public int BaselinePeriodDays { get; set; }
        [JsonProperty("baselineValue")] public double BaselineValue { get; set; }
        [JsonProperty("baselineOptionsHash")] public string BaselineOptionsHash { get; set; }
        [JsonProperty("baselineScoringOptionsHash")] public string BaselineScoringOptionsHash { get; set; }
        [JsonProperty("targetDate")] public DateTime TargetDate { get; set; }
        [JsonProperty("createdUtc")] public DateTime CreatedUtc { get; set; }
        [JsonProperty("createdBy")] public string CreatedBy { get; set; }
        [JsonProperty("currentValue")] public double? CurrentValue { get; set; }
        [JsonProperty("progressPct")] public double? ProgressPct { get; set; }
        [JsonProperty("comparable")] public bool Comparable { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
    }

    public class CopilotAdoptionCreateTargetRequest
    {
        [JsonProperty("metric")] public string Metric { get; set; }
        [JsonProperty("scopeType")] public string ScopeType { get; set; }
        [JsonProperty("scopeValue")] public string ScopeValue { get; set; }
        [JsonProperty("targetValue")] public double TargetValue { get; set; }
        [JsonProperty("owner")] public string Owner { get; set; }
        [JsonProperty("baselinePeriodEnd")] public DateTime? BaselinePeriodEnd { get; set; }
        [JsonProperty("baselinePeriodDays")] public int? BaselinePeriodDays { get; set; }
        [JsonProperty("targetDate")] public DateTime TargetDate { get; set; }
        [JsonProperty("createdBy")] public string CreatedBy { get; set; }
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
}
