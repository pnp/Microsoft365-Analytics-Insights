using Newtonsoft.Json;
using System;

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
}
