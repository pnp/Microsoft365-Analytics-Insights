using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Web.AnalyticsWeb.Models.Health
{
    /// <summary>
    /// String constants matching <see cref="DataUtils.Health.HealthStatus"/> so the JSON payload and
    /// the Azure Monitor alert rules use one vocabulary.
    /// </summary>
    public static class HealthStatusNames
    {
        public const string Healthy = "Healthy";
        public const string Degraded = "Degraded";
        public const string Unhealthy = "Unhealthy";
        public const string Unknown = "Unknown";
    }

    /// <summary>
    /// Base for every Health sub-section payload. Each section is loaded, cached and served
    /// independently (its own <c>api/Health/&lt;section&gt;</c> route) so a slow / failing data source
    /// degrades that one section instead of the whole page, and the SPA only fetches the section the
    /// user is actually looking at. See HEALTH-MONITORING-DESIGN.md (#144).
    /// </summary>
    public abstract class HealthSection
    {
        /// <summary>This section's own traffic-light (display only; the overall light is rolled up server-side in <see cref="HealthSummary"/>).</summary>
        [JsonProperty("status")]
        public string Status { get; set; } = HealthStatusNames.Unknown;

        [JsonProperty("reasons")]
        public List<string> Reasons { get; set; } = new List<string>();

        [JsonProperty("loadedAtUtc")]
        public DateTime LoadedAtUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>One row in the overall <see cref="HealthSummary"/> at-a-glance grid (per sub-section).</summary>
    public class SectionStatus
    {
        [JsonProperty("key")]
        public string Key { get; set; }
        [JsonProperty("label")]
        public string Label { get; set; }
        [JsonProperty("status")]
        public string Status { get; set; } = HealthStatusNames.Unknown;
        [JsonProperty("reasons")]
        public List<string> Reasons { get; set; } = new List<string>();
    }

    /// <summary>
    /// Lightweight overview served by <c>api/Health/summary</c> (the default sub-section). Rolls the
    /// individual sections up into one traffic-light, but deliberately skips the heavy SQL row-count /
    /// freshness scans (those load only when the Data sub-section is opened): the roll-up only needs
    /// database <em>reachability</em>, which is a cheap <c>SELECT 1</c> probe.
    /// </summary>
    public class HealthSummary
    {
        [JsonProperty("buildLabel")]
        public string BuildLabel { get; set; }
        [JsonProperty("loadedAtUtc")]
        public DateTime LoadedAtUtc { get; set; } = DateTime.UtcNow;
        /// <summary>False when no App Insights connection string is configured - the AI-backed sections are then unavailable.</summary>
        [JsonProperty("appInsightsConfigured")]
        public bool AppInsightsConfigured { get; set; }

        [JsonProperty("overallStatus")]
        public string OverallStatus { get; set; } = HealthStatusNames.Unknown;
        [JsonProperty("overallReasons")]
        public List<string> OverallReasons { get; set; } = new List<string>();

        /// <summary>Per-section traffic-lights so the Overview is an at-a-glance board without opening every tab.</summary>
        [JsonProperty("sections")]
        public List<SectionStatus> Sections { get; set; } = new List<SectionStatus>();
    }

    /// <summary>
    /// Data overview (SQL). Approximate row counts + DB size come from DMVs (cheap, index metadata);
    /// the last-24h/7d volume and freshness come from bounded, short-timeout scans that degrade to
    /// <see cref="RecentVolumeError"/> on a huge tenant rather than hanging the request.
    /// </summary>
    public class DataOverviewSection : HealthSection
    {
        /// <summary>Row counts are approximate (sys.dm_db_partition_stats) so a huge tenant isn't hit with COUNT(*) on every load.</summary>
        [JsonProperty("countsAreApproximate")]
        public bool CountsAreApproximate { get; set; } = true;
        [JsonProperty("hitCount")]
        public long HitCount { get; set; }
        [JsonProperty("activityCount")]
        public long ActivityCount { get; set; }
        [JsonProperty("teamsCount")]
        public long TeamsCount { get; set; }
        [JsonProperty("sentEmailCount")]
        public long SentEmailCount { get; set; }
        [JsonProperty("callRecordCount")]
        public long CallRecordCount { get; set; }
        [JsonProperty("copilotChatCount")]
        public long CopilotChatCount { get; set; }
        [JsonProperty("userCount")]
        public long UserCount { get; set; }
        [JsonProperty("teamsBeingTrackedCount")]
        public int TeamsBeingTrackedCount { get; set; }
        [JsonProperty("databaseSizeMb")]
        public long DatabaseSizeMb { get; set; }
        [JsonProperty("auditEventsLast24h")]
        public long? AuditEventsLast24h { get; set; }
        [JsonProperty("auditEventsLast7d")]
        public long? AuditEventsLast7d { get; set; }
        [JsonProperty("hitsLast24h")]
        public long? HitsLast24h { get; set; }
        [JsonProperty("hitsLast7d")]
        public long? HitsLast7d { get; set; }
        [JsonProperty("newestHitUtc")]
        public DateTime? NewestHitUtc { get; set; }
        [JsonProperty("newestAuditEventUtc")]
        public DateTime? NewestAuditEventUtc { get; set; }
        /// <summary>
        /// True when the most recent Graph Copilot per-user usage-report import found the tenant's user
        /// identities concealed (hashed), so that report was deliberately not imported. Surfaced because the
        /// symptom otherwise looks identical to "this tenant has no Copilot usage".
        /// </summary>
        [JsonProperty("copilotUsageReportsIdentitiesConcealed")]
        public bool CopilotUsageReportsIdentitiesConcealed { get; set; }
        /// <summary>When the Graph Copilot usage reports were last imported, or null if they never have been.</summary>
        [JsonProperty("copilotUsageReportLastImportUtc")]
        public DateTime? CopilotUsageReportLastImportUtc { get; set; }
        /// <summary>Errors from the most recent import of each Graph Copilot usage report, if any.</summary>
        [JsonProperty("copilotUsageReportErrors")]
        public List<string> CopilotUsageReportErrors { get; set; } = new List<string>();
        /// <summary>Set when the cheap DMV counts / DB size couldn't be read (e.g. no VIEW DATABASE STATE).</summary>
        [JsonProperty("countsError")]
        public string CountsError { get; set; }
        /// <summary>Set when the bounded 24h/7d volume + freshness scans failed or timed out (expected on very large tenants).</summary>
        [JsonProperty("recentVolumeError")]
        public string RecentVolumeError { get; set; }
        /// <summary>Set only on a hard failure (e.g. the database is unreachable).</summary>
        [JsonProperty("dataError")]
        public string DataError { get; set; }
    }

    /// <summary>Import liveness (App Insights): is each importer still looping and finishing?</summary>
    public class LivenessSection : HealthSection
    {
        [JsonProperty("appInsightsConfigured")]
        public bool AppInsightsConfigured { get; set; }
        [JsonProperty("lastCyclePerJob")]
        public List<ImportCycleRow> LastCyclePerJob { get; set; } = new List<ImportCycleRow>();
        [JsonProperty("lastSectionImports")]
        public List<SectionImportRow> LastSectionImports { get; set; } = new List<SectionImportRow>();
        [JsonProperty("lastHeartbeats")]
        public List<HeartbeatRow> LastHeartbeats { get; set; } = new List<HeartbeatRow>();
        [JsonProperty("pageViewsLast24h")]
        public long PageViewsLast24h { get; set; }
        [JsonProperty("newestPageViewUtc")]
        public DateTime? NewestPageViewUtc { get; set; }
        [JsonProperty("livenessError")]
        public string LivenessError { get; set; }
    }

    /// <summary>Exceptions overview (App Insights): a cheap catch-all early-warning of failures.</summary>
    public class ExceptionsSection : HealthSection
    {
        [JsonProperty("appInsightsConfigured")]
        public bool AppInsightsConfigured { get; set; }
        [JsonProperty("exceptionsLast24h")]
        public long ExceptionsLast24h { get; set; }
        [JsonProperty("exceptionsPerHour")]
        public List<HourCount> ExceptionsPerHour { get; set; } = new List<HourCount>();
        [JsonProperty("topExceptionTypes")]
        public List<ExceptionTypeRow> TopExceptionTypes { get; set; } = new List<ExceptionTypeRow>();
        /// <summary>Count of last-24h exceptions that look like SQL capacity / read-only failures (message text is NOT surfaced).</summary>
        [JsonProperty("sqlCapacityExceptions24h")]
        public long SqlCapacityExceptions24h { get; set; }
        [JsonProperty("exceptionsError")]
        public string ExceptionsError { get; set; }
    }

    /// <summary>Component health: runtime credential + Service Bus checks, plus App Insights HealthCheck events.</summary>
    public class ComponentsSection : HealthSection
    {
        [JsonProperty("appInsightsConfigured")]
        public bool AppInsightsConfigured { get; set; }
        [JsonProperty("componentHealth")]
        public List<ComponentHealthRow> ComponentHealth { get; set; } = new List<ComponentHealthRow>();
        [JsonProperty("componentHealthError")]
        public string ComponentHealthError { get; set; }
    }

    /// <summary>Configuration: what's turned on, what this app points at, plus schema/migration + webhook state.</summary>
    public class ConfigSection : HealthSection
    {
        [JsonProperty("enabledImports")]
        public List<string> EnabledImports { get; set; } = new List<string>();
        [JsonProperty("sqlServer")]
        public string SqlServer { get; set; }
        [JsonProperty("redisHost")]
        public string RedisHost { get; set; }
        [JsonProperty("serviceBusEndpoint")]
        public string ServiceBusEndpoint { get; set; }
        [JsonProperty("cognitiveEndpoint")]
        public string CognitiveEndpoint { get; set; }
        [JsonProperty("webAppUrl")]
        public string WebAppUrl { get; set; }
        [JsonProperty("callsImportEnabled")]
        public bool CallsImportEnabled { get; set; }
        [JsonProperty("webhookState")]
        public string WebhookState { get; set; }
        [JsonProperty("webhookExpiryUtc")]
        public DateTimeOffset? WebhookExpiryUtc { get; set; }
        [JsonProperty("webhookDetail")]
        public string WebhookDetail { get; set; }
        [JsonProperty("configError")]
        public string ConfigError { get; set; }

        /// <summary>Null = couldn't check; true = DB at this build's latest migration; false = migrations pending (DB behind build).</summary>
        [JsonProperty("schemaUpToDate")]
        public bool? SchemaUpToDate { get; set; }
        [JsonProperty("pendingMigrations")]
        public List<string> PendingMigrations { get; set; } = new List<string>();
        [JsonProperty("schemaError")]
        public string SchemaError { get; set; }
    }

    // --- Shared row DTOs (unchanged shapes from the old single HealthDashboard payload) ---

    public class ComponentHealthRow
    {
        [JsonProperty("component")]
        public string Component { get; set; }
        [JsonProperty("status")]
        public string Status { get; set; }
        [JsonProperty("detail")]
        public string Detail { get; set; }
        [JsonProperty("daysToExpiry")]
        public int? DaysToExpiry { get; set; }
        [JsonProperty("lastSeenUtc")]
        public DateTime? LastSeenUtc { get; set; }
    }

    public class ImportCycleRow
    {
        [JsonProperty("jobName")]
        public string JobName { get; set; }
        [JsonProperty("lastCycleUtc")]
        public DateTime? LastCycleUtc { get; set; }
        [JsonProperty("duration")]
        public string Duration { get; set; }
    }

    public class SectionImportRow
    {
        [JsonProperty("sectionName")]
        public string SectionName { get; set; }
        [JsonProperty("lastRunUtc")]
        public DateTime? LastRunUtc { get; set; }
        [JsonProperty("detail")]
        public string Detail { get; set; }
        [JsonProperty("jobName")]
        public string JobName { get; set; }
    }

    public class HeartbeatRow
    {
        [JsonProperty("jobName")]
        public string JobName { get; set; }
        [JsonProperty("lastBeatUtc")]
        public DateTime? LastBeatUtc { get; set; }
        [JsonProperty("lastCycleUtc")]
        public string LastCycleUtc { get; set; }
        [JsonProperty("lastCycleDurationSeconds")]
        public string LastCycleDurationSeconds { get; set; }
    }

    public class HourCount
    {
        [JsonProperty("hourUtc")]
        public DateTime? HourUtc { get; set; }
        [JsonProperty("count")]
        public long Count { get; set; }
    }

    public class ExceptionTypeRow
    {
        [JsonProperty("type")]
        public string Type { get; set; }
        [JsonProperty("problemId")]
        public string ProblemId { get; set; }
        [JsonProperty("count")]
        public long Count { get; set; }
    }
}
