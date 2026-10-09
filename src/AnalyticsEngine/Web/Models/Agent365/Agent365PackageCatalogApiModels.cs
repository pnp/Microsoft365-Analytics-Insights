using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Web.AnalyticsWeb.Models.Agent365
{
    public class Agent365PackageCatalogApiResponse
    {
        [JsonProperty("importEnabled")]
        public bool ImportEnabled { get; set; }

        [JsonProperty("lastAttemptUtc")]
        public DateTime? LastAttemptUtc { get; set; }

        [JsonProperty("lastAttemptCompletedUtc")]
        public DateTime? LastAttemptCompletedUtc { get; set; }

        [JsonProperty("lastAttemptSucceeded")]
        public bool? LastAttemptSucceeded { get; set; }

        [JsonProperty("lastAttemptError")]
        public string LastAttemptError { get; set; }

        [JsonProperty("lastSuccessfulImportUtc")]
        public DateTime? LastSuccessfulImportUtc { get; set; }

        [JsonProperty("totalCount")]
        public int TotalCount { get; set; }

        [JsonProperty("offset")]
        public int Offset { get; set; }

        [JsonProperty("pageSize")]
        public int PageSize { get; set; }

        [JsonProperty("neverUsedOnly")]
        public bool NeverUsedOnly { get; set; }

        [JsonProperty("packageCount")]
        public int PackageCount { get; set; }

        [JsonProperty("neverUsedCount")]
        public int NeverUsedCount { get; set; }

        [JsonProperty("packages")]
        public List<Agent365PackageCatalogApiPackage> Packages { get; set; } = new List<Agent365PackageCatalogApiPackage>();
    }

    public class Agent365PackageCatalogApiPackage
    {
        [JsonProperty("packageId")]
        public string PackageId { get; set; }

        [JsonProperty("displayName")]
        public string DisplayName { get; set; }

        [JsonProperty("packageType")]
        public string PackageType { get; set; }

        [JsonProperty("platform")]
        public string Platform { get; set; }

        [JsonProperty("publisher")]
        public string Publisher { get; set; }

        [JsonProperty("manifestId")]
        public string ManifestId { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("isBlocked")]
        public bool? IsBlocked { get; set; }

        [JsonProperty("lastModifiedUtc")]
        public DateTime? LastModifiedUtc { get; set; }

        [JsonProperty("lastUsedUtc")]
        public DateTime? LastUsedUtc { get; set; }

        [JsonProperty("lastUsedDateTimeProvided")]
        public bool LastUsedDateTimeProvided { get; set; }

        [JsonProperty("knownNeverUsed")]
        public bool KnownNeverUsed { get; set; }

        [JsonProperty("activeUsers")]
        public int? ActiveUsers { get; set; }

        [JsonProperty("totalSessions")]
        public int? TotalSessions { get; set; }

        [JsonProperty("totalRunTimeHours")]
        public double? TotalRunTimeHours { get; set; }

        [JsonProperty("exceptionRate")]
        public double? ExceptionRate { get; set; }

        [JsonProperty("supportedHosts")]
        public List<string> SupportedHosts { get; set; } = new List<string>();

        [JsonProperty("elements")]
        public List<Agent365PackageCatalogApiElement> Elements { get; set; } = new List<Agent365PackageCatalogApiElement>();
    }

    public class Agent365PackageCatalogApiElement
    {
        [JsonProperty("elementType")]
        public string ElementType { get; set; }

        [JsonProperty("elementId")]
        public string ElementId { get; set; }
    }
}
