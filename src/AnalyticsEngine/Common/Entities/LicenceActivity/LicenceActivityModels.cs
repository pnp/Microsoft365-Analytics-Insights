using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;

namespace Common.Entities.LicenceActivity
{
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivityAvailability
    {
        public bool Available { get; set; }
        public bool CanViewUsers { get; set; }
        public int MinimumDays { get; set; } = LicenceActivityQuery.MinimumDays;
        public int MaximumDays { get; set; } = LicenceActivityQuery.MaximumDays;
        public List<string> Messages { get; set; } = new List<string>();
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public abstract class LicenceActivitySnapshot
    {
        public string SnapshotId { get; set; }
        public DateTime GeneratedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivityOverview : LicenceActivitySnapshot
    {
        public LicenceActivityQuery Query { get; set; }
        public int DistinctAssignedUsers { get; set; }
        public List<LicenceActivitySku> Licences { get; set; } = new List<LicenceActivitySku>();
        public List<LicenceActivityCoverage> Coverage { get; set; } = new List<LicenceActivityCoverage>();
        public List<LicenceActivityDemographic> Departments { get; set; } = new List<LicenceActivityDemographic>();
        public List<LicenceActivityDemographic> Countries { get; set; } = new List<LicenceActivityDemographic>();
        public bool DemographicsTruncated { get; set; }
        public List<string> Messages { get; set; } = new List<string>();
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivitySku
    {
        public int LicenceTypeId { get; set; }
        public string Name { get; set; }
        public string SkuId { get; set; }
        public int AssignedUsers { get; set; }
        public List<LicenceActivityDistribution> Workloads { get; set; } = new List<LicenceActivityDistribution>();
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivityDistribution
    {
        public string Workload { get; set; }
        public int High { get; set; }
        public int Moderate { get; set; }
        public int Low { get; set; }
        public int Zero { get; set; }
        public int Unknown { get; set; }
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivityDemographic
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int AssignedUsers { get; set; }
        public List<LicenceActivityDistribution> Workloads { get; set; } = new List<LicenceActivityDistribution>();
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivityCoverage
    {
        public string Workload { get; set; }
        public string Status { get; set; }
        public string Source { get; set; }
        public string Measure { get; set; }
        public string Granularity { get; set; }
        public string Message { get; set; }
        public DateTime? EffectiveFromUtc { get; set; }
        public DateTime? EffectiveToUtc { get; set; }
        public DateTime? LatestImportUtc { get; set; }
        public int LagDays { get; set; }
        public int? ReportPeriodDays { get; set; }
        public int ExpectedSamples { get; set; }
        public int ObservedSamples { get; set; }
        public int UnmatchedUsers { get; set; }
        public List<DateTime> SnapshotDates { get; set; } = new List<DateTime>();
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivityUsers : LicenceActivitySnapshot
    {
        public string OverviewId { get; set; }
        public LicenceActivityQuery Query { get; set; }
        public int TotalUsers { get; set; }
        public int RankedUsers { get; set; }
        public List<LicenceActivityUser> MostActive { get; set; } = new List<LicenceActivityUser>();
        public List<LicenceActivityUser> LeastActive { get; set; } = new List<LicenceActivityUser>();
        public List<LicenceActivityUser> Users { get; set; } = new List<LicenceActivityUser>();
        public List<string> Messages { get; set; } = new List<string>();
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivityUser
    {
        public int UserId { get; set; }
        public string UserPrincipalName { get; set; }
        public string Department { get; set; }
        public string Country { get; set; }
        public bool? AccountEnabled { get; set; }
        public List<LicenceActivityEvidence> Workloads { get; set; } = new List<LicenceActivityEvidence>();
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class LicenceActivityEvidence
    {
        public string Workload { get; set; }
        public string Status { get; set; }
        public string Band { get; set; }
        public string Source { get; set; }
        public string Measure { get; set; }
        public int ActiveSamples { get; set; }
        public int ObservedSamples { get; set; }
        public int ExpectedSamples { get; set; }
        public double? AverageActions { get; set; }
        public DateTime? LastActivityUtc { get; set; }
    }
}
