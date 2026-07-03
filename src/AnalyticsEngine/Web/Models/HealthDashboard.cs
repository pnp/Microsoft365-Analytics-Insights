using Azure.Identity;
using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using DataUtils.AppInsights;
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

        public string BuildLabel { get; set; }
        public DateTime LoadedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>False when no App Insights connection string is configured - the AI-backed cards are then unavailable.</summary>
        public bool AppInsightsConfigured { get; set; }

        // --- Component health card ---
        public List<ComponentHealthRow> ComponentHealth { get; set; } = new List<ComponentHealthRow>();
        public string ComponentHealthError { get; set; }

        // --- Import liveness card ---
        public List<ImportCycleRow> LastCyclePerJob { get; set; } = new List<ImportCycleRow>();
        public List<SectionImportRow> LastSectionImports { get; set; } = new List<SectionImportRow>();
        public List<HeartbeatRow> LastHeartbeats { get; set; } = new List<HeartbeatRow>();
        public string LivenessError { get; set; }

        // --- Exceptions overview card ---
        public long ExceptionsLast24h { get; set; }
        public List<HourCount> ExceptionsPerHour { get; set; } = new List<HourCount>();
        public List<ExceptionTypeRow> TopExceptionTypes { get; set; } = new List<ExceptionTypeRow>();
        public string ExceptionsError { get; set; }

        // --- Data overview card (SQL) ---
        public int HitCount { get; set; }
        public int ActivityCount { get; set; }
        public int TeamsCount { get; set; }
        public int TeamsBeingTrackedCount { get; set; }
        public DateTime? NewestHitUtc { get; set; }
        public DateTime? NewestAuditEventUtc { get; set; }
        public string DataError { get; set; }

        /// <summary>Whole-cycle SLA: a full activity import cycle should complete at least once every 24h.</summary>
        public const int CycleSlaHours = 24;

        /// <summary>Minutes since a UTC timestamp, or null when the timestamp is null.</summary>
        public static double? MinutesAgo(DateTime? utc)
        {
            if (!utc.HasValue) return null;
            return (DateTime.UtcNow - DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc)).TotalMinutes;
        }

        /// <summary>Human-readable "N min/hours/days ago" for a UTC timestamp.</summary>
        public static string HowLongAgo(DateTime? utc)
        {
            var mins = MinutesAgo(utc);
            if (!mins.HasValue) return "never";
            if (mins.Value < 1) return "just now";
            if (mins.Value < 60) return $"{Math.Round(mins.Value)} min ago";
            if (mins.Value < 60 * 24) return $"{Math.Round(mins.Value / 60, 1)} hours ago";
            return $"{Math.Round(mins.Value / 60 / 24, 1)} days ago";
        }

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

                    NewestHitUtc = await db.hits
                        .OrderByDescending(h => h.hit_timestamp)
                        .Select(h => (DateTime?)h.hit_timestamp)
                        .FirstOrDefaultAsync();
                    NewestAuditEventUtc = await db.AuditEventsCommon
                        .OrderByDescending(e => e.TimeStamp)
                        .Select(e => (DateTime?)e.TimeStamp)
                        .FirstOrDefaultAsync();
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
        public string Component { get; set; }
        public string Status { get; set; }
        public string Detail { get; set; }
        public int? DaysToExpiry { get; set; }
        public DateTime? LastSeenUtc { get; set; }
    }

    public class ImportCycleRow
    {
        public string JobName { get; set; }
        public DateTime? LastCycleUtc { get; set; }
        public string Duration { get; set; }
    }

    public class SectionImportRow
    {
        public string SectionName { get; set; }
        public DateTime? LastRunUtc { get; set; }
        public string Detail { get; set; }
        public string JobName { get; set; }
    }

    public class HeartbeatRow
    {
        public string JobName { get; set; }
        public DateTime? LastBeatUtc { get; set; }
        public string LastCycleUtc { get; set; }
        public string LastCycleDurationSeconds { get; set; }
    }

    public class HourCount
    {
        public DateTime? HourUtc { get; set; }
        public long Count { get; set; }
    }

    public class ExceptionTypeRow
    {
        public string Type { get; set; }
        public string ProblemId { get; set; }
        public long Count { get; set; }
    }
}
