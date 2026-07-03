using Azure.Identity;
using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using DataUtils.AppInsights;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Runtime.Caching;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models
{
    /// <summary>
    /// Read-only "is it working?" view for the in-app Health tab. Aggregates, best-effort:
    ///  - Component health   (App Insights <c>HealthCheck</c> custom events)
    ///  - Import liveness     (App Insights <c>FinishedImportCycle</c> / <c>FinishedSectionImport</c> / <c>ImporterHeartbeat</c>)
    ///  - Exceptions overview (App Insights <c>exceptions</c> table)
    ///  - Data overview       (SQL counts + freshness, as the Home page does)
    ///
    /// Each card is loaded independently: a data-source hiccup degrades that card (sets its error text)
    /// but never errors the whole page. Reuses the app's existing Entra credential + App Insights
    /// connection string, so no new API key or config is required. See HEALTH-MONITORING-DESIGN.md (#144).
    /// </summary>
    public class HealthDashboard
    {
        private const string CacheKey = "healthdashboard:v1";
        public const int CacheSeconds = 60;

        [JsonProperty("buildLabel")]
        public string BuildLabel { get; set; }

        [JsonProperty("loadedAtUtc")]
        public DateTime LoadedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>False when no App Insights connection string is configured - the AI-backed cards are then unavailable.</summary>
        [JsonProperty("appInsightsConfigured")]
        public bool AppInsightsConfigured { get; set; }

        // --- Component health card ---
        [JsonProperty("componentHealth")]
        public List<ComponentHealthRow> ComponentHealth { get; set; } = new List<ComponentHealthRow>();
        [JsonProperty("componentHealthError")]
        public string ComponentHealthError { get; set; }

        // --- Import liveness card ---
        [JsonProperty("lastCyclePerJob")]
        public List<ImportCycleRow> LastCyclePerJob { get; set; } = new List<ImportCycleRow>();
        [JsonProperty("lastSectionImports")]
        public List<SectionImportRow> LastSectionImports { get; set; } = new List<SectionImportRow>();
        [JsonProperty("lastHeartbeats")]
        public List<HeartbeatRow> LastHeartbeats { get; set; } = new List<HeartbeatRow>();
        [JsonProperty("livenessError")]
        public string LivenessError { get; set; }

        // --- Exceptions overview card ---
        [JsonProperty("exceptionsLast24h")]
        public long ExceptionsLast24h { get; set; }
        [JsonProperty("exceptionsPerHour")]
        public List<HourCount> ExceptionsPerHour { get; set; } = new List<HourCount>();
        [JsonProperty("topExceptionTypes")]
        public List<ExceptionTypeRow> TopExceptionTypes { get; set; } = new List<ExceptionTypeRow>();
        [JsonProperty("exceptionsError")]
        public string ExceptionsError { get; set; }

        // --- Data overview card (SQL) ---
        [JsonProperty("hitCount")]
        public int HitCount { get; set; }
        [JsonProperty("activityCount")]
        public int ActivityCount { get; set; }
        [JsonProperty("teamsCount")]
        public int TeamsCount { get; set; }
        [JsonProperty("teamsBeingTrackedCount")]
        public int TeamsBeingTrackedCount { get; set; }
        [JsonProperty("newestHitUtc")]
        public DateTime? NewestHitUtc { get; set; }
        [JsonProperty("newestAuditEventUtc")]
        public DateTime? NewestAuditEventUtc { get; set; }
        [JsonProperty("dataError")]
        public string DataError { get; set; }

        internal static async Task<HealthDashboard> LoadFrom(AppConfig config)
        {
            var cached = MemoryCache.Default.Get(CacheKey) as HealthDashboard;
            if (cached != null) return cached;

            var model = new HealthDashboard { BuildLabel = BuildConstants.BuildLabel };

            await model.LoadSqlDataOverview();
            await model.LoadAppInsightsCards(config);

            MemoryCache.Default.Set(CacheKey, model, new CacheItemPolicy
            {
                AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(CacheSeconds)
            });
            return model;
        }

        private async Task LoadSqlDataOverview()
        {
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    HitCount = await db.hits.CountAsync();
                    ActivityCount = await db.AuditEventsCommon.CountAsync();
                    TeamsCount = await db.Teams.CountAsync();
                    TeamsBeingTrackedCount = await db.Teams.Where(t => t.HasRefreshToken).CountAsync();

                    var newestHit = await db.hits
                        .OrderByDescending(h => h.hit_timestamp)
                        .Select(h => (DateTime?)h.hit_timestamp)
                        .FirstOrDefaultAsync();
                    NewestHitUtc = newestHit.HasValue ? DateTime.SpecifyKind(newestHit.Value, DateTimeKind.Utc) : (DateTime?)null;

                    var newestAudit = await db.AuditEventsCommon
                        .OrderByDescending(e => e.TimeStamp)
                        .Select(e => (DateTime?)e.TimeStamp)
                        .FirstOrDefaultAsync();
                    NewestAuditEventUtc = newestAudit.HasValue ? DateTime.SpecifyKind(newestAudit.Value, DateTimeKind.Utc) : (DateTime?)null;
                }
            }
            catch (Exception ex)
            {
                DataError = ex.Message;
            }
        }

        private async Task LoadAppInsightsCards(AppConfig config)
        {
            if (string.IsNullOrEmpty(config?.AppInsightsConnectionString))
            {
                AppInsightsConfigured = false;
                return;
            }
            AppInsightsConfigured = true;

            AppInsightsQueryClient client = null;
            try
            {
                var credential = new ClientSecretCredential(config.TenantGUID.ToString(), config.ClientID, config.ClientSecret);
                client = new AppInsightsQueryClient(config.AppInsightsConnectionString, credential, AnalyticsLogger.ConsoleOnlyTracer());

                await LoadComponentHealth(client);
                await LoadLiveness(client);
                await LoadExceptions(client);
            }
            catch (Exception ex)
            {
                // Failure building the client / credential - flag every AI card so the page still renders.
                var msg = ex.Message;
                ComponentHealthError = ComponentHealthError ?? msg;
                LivenessError = LivenessError ?? msg;
                ExceptionsError = ExceptionsError ?? msg;
            }
            finally
            {
                client?.Dispose();
            }
        }

        private async Task LoadComponentHealth(AppInsightsQueryClient client)
        {
            try
            {
                var table = await client.RunQueryAsync(QueryComponentHealth);
                foreach (var row in table.Rows)
                {
                    ComponentHealth.Add(new ComponentHealthRow
                    {
                        Component = table.GetString(row, "Component"),
                        Status = table.GetString(row, "Status"),
                        Detail = table.GetString(row, "Detail"),
                        DaysToExpiry = table.GetInt(row, "DaysToExpiry"),
                        LastSeenUtc = table.GetDateTimeUtc(row, "LastSeen")
                    });
                }
            }
            catch (Exception ex)
            {
                ComponentHealthError = ex.Message;
            }
        }

        private async Task LoadLiveness(AppInsightsQueryClient client)
        {
            try
            {
                var cycles = await client.RunQueryAsync(QueryLastCyclePerJob);
                foreach (var row in cycles.Rows)
                {
                    LastCyclePerJob.Add(new ImportCycleRow
                    {
                        JobName = cycles.GetString(row, "JobName"),
                        LastCycleUtc = cycles.GetDateTimeUtc(row, "LastCycle"),
                        Duration = cycles.GetString(row, "Duration")
                    });
                }

                var sections = await client.RunQueryAsync(QueryLastSectionImports);
                foreach (var row in sections.Rows)
                {
                    LastSectionImports.Add(new SectionImportRow
                    {
                        SectionName = sections.GetString(row, "SectionName"),
                        LastRunUtc = sections.GetDateTimeUtc(row, "LastRun"),
                        Detail = sections.GetString(row, "Detail"),
                        JobName = sections.GetString(row, "JobName")
                    });
                }

                var beats = await client.RunQueryAsync(QueryLastHeartbeats);
                foreach (var row in beats.Rows)
                {
                    LastHeartbeats.Add(new HeartbeatRow
                    {
                        JobName = beats.GetString(row, "JobName"),
                        LastBeatUtc = beats.GetDateTimeUtc(row, "LastBeat"),
                        LastCycleUtc = beats.GetString(row, "LastCycleUtc"),
                        LastCycleDurationSeconds = beats.GetString(row, "LastCycleDurationSeconds")
                    });
                }
            }
            catch (Exception ex)
            {
                LivenessError = ex.Message;
            }
        }

        private async Task LoadExceptions(AppInsightsQueryClient client)
        {
            try
            {
                var perHour = await client.RunQueryAsync(QueryExceptionsPerHour);
                foreach (var row in perHour.Rows)
                {
                    var count = perHour.GetLong(row, "Count") ?? 0;
                    ExceptionsPerHour.Add(new HourCount
                    {
                        HourUtc = perHour.GetDateTimeUtc(row, "timestamp"),
                        Count = count
                    });
                    ExceptionsLast24h += count;
                }

                var types = await client.RunQueryAsync(QueryTopExceptionTypes);
                foreach (var row in types.Rows)
                {
                    TopExceptionTypes.Add(new ExceptionTypeRow
                    {
                        Type = types.GetString(row, "type"),
                        ProblemId = types.GetString(row, "problemId"),
                        Count = types.GetLong(row, "Count") ?? 0
                    });
                }
            }
            catch (Exception ex)
            {
                ExceptionsError = ex.Message;
            }
        }

        #region KQL

        private const string QueryComponentHealth =
            "customEvents " +
            "| where name == \"HealthCheck\" " +
            "| extend Component = tostring(customDimensions.Component) " +
            "| summarize arg_max(timestamp, *) by Component " +
            "| project Component, Status = tostring(customDimensions.Status), Detail = tostring(customDimensions.Detail), DaysToExpiry = tostring(customDimensions.DaysToExpiry), LastSeen = timestamp " +
            "| order by Component asc";

        private const string QueryLastCyclePerJob =
            "customEvents " +
            "| where name == \"FinishedImportCycle\" " +
            "| summarize arg_max(timestamp, *) by operation_Name " +
            "| project JobName = operation_Name, LastCycle = timestamp, Duration = tostring(customDimensions.context) " +
            "| order by JobName asc";

        private const string QueryLastSectionImports =
            "customEvents " +
            "| where name == \"FinishedSectionImport\" " +
            "| extend SectionName = tostring(split(tostring(customDimensions.context), \":\")[0]) " +
            "| summarize arg_max(timestamp, *) by SectionName " +
            "| project SectionName, LastRun = timestamp, Detail = tostring(customDimensions.context), JobName = operation_Name " +
            "| order by SectionName asc";

        private const string QueryLastHeartbeats =
            "customEvents " +
            "| where name == \"ImporterHeartbeat\" " +
            "| extend JobName = tostring(customDimensions.JobName) " +
            "| summarize arg_max(timestamp, *) by JobName " +
            "| project JobName, LastBeat = timestamp, LastCycleUtc = tostring(customDimensions.LastCycleUtc), LastCycleDurationSeconds = tostring(customDimensions.LastCycleDurationSeconds) " +
            "| order by JobName asc";

        private const string QueryExceptionsPerHour =
            "exceptions " +
            "| where timestamp > ago(24h) " +
            "| summarize Count = count() by bin(timestamp, 1h) " +
            "| order by timestamp asc";

        private const string QueryTopExceptionTypes =
            "exceptions " +
            "| where timestamp > ago(24h) " +
            "| summarize Count = count() by type, problemId " +
            "| top 10 by Count desc";

        #endregion
    }

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
