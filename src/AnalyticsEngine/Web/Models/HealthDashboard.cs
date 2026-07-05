using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using DataUtils.AppInsights;
using DataUtils.Health;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Migrations;
using System.Linq;
using System.Runtime.Caching;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models
{
    /// <summary>
    /// Read-only "is it working?" view for the in-app Health tab. Aggregates, best-effort:
    ///  - Overall status     (a single traffic-light rolled up from every card below)
    ///  - Component health    (runtime credential + Service Bus checks done here today; App Insights <c>HealthCheck</c> events when the emitter lands)
    ///  - Import liveness      (App Insights <c>FinishedImportCycle</c> / <c>FinishedSectionImport</c> / <c>ImporterHeartbeat</c> + web-tracker <c>pageViews</c>)
    ///  - Exceptions overview  (App Insights <c>exceptions</c> table, incl. a SQL-capacity/read-only sub-count)
    ///  - Data overview        (cheap approximate SQL counts + freshness + recent volume + DB size)
    ///  - Configuration        (enabled imports, resource endpoints, Teams call webhook, schema/migration version)
    ///
    /// Each card is loaded independently: a data-source hiccup degrades that card (sets its error text)
    /// but never errors the whole page. Reuses the app's existing Entra credential (honouring
    /// certificate auth) + App Insights connection string, so no new API key or config is required.
    /// See HEALTH-MONITORING-DESIGN.md (#144).
    /// </summary>
    public class HealthDashboard
    {
        private const string CacheKey = "healthdashboard:v2";
        public const int CacheSeconds = 60;

        // A full activity import cycle should complete at least this often (see HEALTH-MONITORING-DESIGN.md).
        private const int CycleSlaHours = 24;

        // Only builds one aggregation at a time on a cold cache, so a burst of page opens can't
        // stampede the query API / DB with N simultaneous full aggregations.
        private static readonly SemaphoreSlim _buildLock = new SemaphoreSlim(1, 1);

        [JsonProperty("buildLabel")]
        public string BuildLabel { get; set; }

        [JsonProperty("loadedAtUtc")]
        public DateTime LoadedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>False when no App Insights connection string is configured - the AI-backed cards are then unavailable.</summary>
        [JsonProperty("appInsightsConfigured")]
        public bool AppInsightsConfigured { get; set; }

        // --- Overall status (rolled up from the cards below) ---
        [JsonProperty("overallStatus")]
        public string OverallStatus { get; set; } = HealthStatusNames.Unknown;
        [JsonProperty("overallReasons")]
        public List<string> OverallReasons { get; set; } = new List<string>();

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
        [JsonProperty("pageViewsLast24h")]
        public long PageViewsLast24h { get; set; }
        [JsonProperty("newestPageViewUtc")]
        public DateTime? NewestPageViewUtc { get; set; }
        [JsonProperty("livenessError")]
        public string LivenessError { get; set; }

        // --- Exceptions overview card ---
        [JsonProperty("exceptionsLast24h")]
        public long ExceptionsLast24h { get; set; }
        [JsonProperty("exceptionsPerHour")]
        public List<HourCount> ExceptionsPerHour { get; set; } = new List<HourCount>();
        [JsonProperty("topExceptionTypes")]
        public List<ExceptionTypeRow> TopExceptionTypes { get; set; } = new List<ExceptionTypeRow>();
        /// <summary>Count of exceptions in the last 24h that look like SQL capacity / read-only failures (message text is NOT surfaced).</summary>
        [JsonProperty("sqlCapacityExceptions24h")]
        public long SqlCapacityExceptions24h { get; set; }
        [JsonProperty("exceptionsError")]
        public string ExceptionsError { get; set; }

        // --- Data overview card (SQL) ---
        /// <summary>Row counts are approximate (from sys.dm_db_partition_stats) so a huge tenant isn't hit with COUNT(*) on every load.</summary>
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
        public long AuditEventsLast24h { get; set; }
        [JsonProperty("auditEventsLast7d")]
        public long AuditEventsLast7d { get; set; }
        [JsonProperty("hitsLast24h")]
        public long HitsLast24h { get; set; }
        [JsonProperty("hitsLast7d")]
        public long HitsLast7d { get; set; }
        [JsonProperty("newestHitUtc")]
        public DateTime? NewestHitUtc { get; set; }
        [JsonProperty("newestAuditEventUtc")]
        public DateTime? NewestAuditEventUtc { get; set; }
        [JsonProperty("dataError")]
        public string DataError { get; set; }

        // --- Schema / migration status ---
        /// <summary>Null when it couldn't be checked; true when the DB is at this build's latest migration; false when migrations are pending (DB behind this build).</summary>
        [JsonProperty("schemaUpToDate")]
        public bool? SchemaUpToDate { get; set; }
        [JsonProperty("pendingMigrations")]
        public List<string> PendingMigrations { get; set; } = new List<string>();
        [JsonProperty("schemaError")]
        public string SchemaError { get; set; }

        // --- Configuration card ---
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

        internal static async Task<HealthDashboard> LoadFrom(AppConfig config)
        {
            var cached = MemoryCache.Default.Get(CacheKey) as HealthDashboard;
            if (cached != null) return cached;

            await _buildLock.WaitAsync();
            try
            {
                // Re-check: another request may have built it while we waited for the lock.
                cached = MemoryCache.Default.Get(CacheKey) as HealthDashboard;
                if (cached != null) return cached;

                var model = new HealthDashboard { BuildLabel = BuildConstants.BuildLabel };

                await model.LoadSqlDataOverview();
                model.LoadSchemaStatus();
                await model.LoadConfigAndWebhook();
                await model.LoadRuntimeAndAppInsights(config);
                model.OverallStatus = EvaluateOverall(model, out var reasons);
                model.OverallReasons = reasons;

                MemoryCache.Default.Set(CacheKey, model, new CacheItemPolicy
                {
                    AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(CacheSeconds)
                });
                return model;
            }
            finally
            {
                _buildLock.Release();
            }
        }

        private async Task LoadSqlDataOverview()
        {
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    // Approximate counts + DB size come from DMVs (sys.dm_db_partition_stats /
                    // sys.database_files), which need VIEW DATABASE STATE. Isolate them so a locked-down
                    // SQL login still gets the more important recent-volume + freshness signals below.
                    try
                    {
                        var approx = await LoadApproxRowCounts(db);
                        ActivityCount = ApproxFor(approx, "audit_events");
                        HitCount = ApproxFor(approx, "hits");
                        TeamsCount = ApproxFor(approx, "teams");
                        SentEmailCount = ApproxFor(approx, "sent_emails");
                        CallRecordCount = ApproxFor(approx, "call_records");
                        CopilotChatCount = ApproxFor(approx, "copilot_chats");
                        UserCount = ApproxFor(approx, "users");
                        DatabaseSizeMb = await LoadDatabaseSizeMb(db);
                    }
                    catch (Exception dmvEx)
                    {
                        // Counts stay 0; the recent-volume / freshness rows below are the real "is it flowing" signal.
                        System.Diagnostics.Debug.WriteLine("Health: approximate counts unavailable: " + dmvEx.Message);
                    }

                    // Teams-being-tracked is a filtered count on a small table (thousands of rows) - cheap.
                    TeamsBeingTrackedCount = await db.Teams.Where(t => t.HasRefreshToken).CountAsync();

                    // Recent volume on the two big fact tables. Their timestamp columns are indexed
                    // (the newest-row queries below already rely on it), so a range count is a cheap seek.
                    var cutoff24 = DateTime.UtcNow.AddHours(-24);
                    var cutoff7 = DateTime.UtcNow.AddDays(-7);
                    AuditEventsLast24h = await db.AuditEventsCommon.Where(e => e.TimeStamp > cutoff24).LongCountAsync();
                    AuditEventsLast7d = await db.AuditEventsCommon.Where(e => e.TimeStamp > cutoff7).LongCountAsync();
                    HitsLast24h = await db.hits.Where(h => h.hit_timestamp > cutoff24).LongCountAsync();
                    HitsLast7d = await db.hits.Where(h => h.hit_timestamp > cutoff7).LongCountAsync();

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

        private static async Task<Dictionary<string, long>> LoadApproxRowCounts(AnalyticsEntitiesContext db)
        {
            const string sql =
                "SELECT o.name AS TableName, SUM(ps.row_count) AS Rows " +
                "FROM sys.dm_db_partition_stats ps " +
                "JOIN sys.objects o ON o.object_id = ps.object_id " +
                "WHERE ps.index_id IN (0,1) AND o.[type] = 'U' " +
                "GROUP BY o.name";
            var rows = await db.Database.SqlQuery<TableRowCount>(sql).ToListAsync();
            var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                if (!string.IsNullOrEmpty(r.TableName)) dict[r.TableName] = r.Rows;
            }
            return dict;
        }

        private static long ApproxFor(Dictionary<string, long> counts, string tableName)
            => counts != null && counts.TryGetValue(tableName, out var n) ? n : 0;

        private static async Task<long> LoadDatabaseSizeMb(AnalyticsEntitiesContext db)
        {
            // type = 0 => data files (exclude the log). size is in 8 KB pages.
            const string sql = "SELECT CAST(ISNULL(SUM(CAST(size AS BIGINT)), 0) * 8 / 1024 AS BIGINT) FROM sys.database_files WHERE [type] = 0";
            var result = await db.Database.SqlQuery<long>(sql).ToListAsync();
            return result.FirstOrDefault();
        }

        private void LoadSchemaStatus()
        {
            try
            {
                var migrationsConfig = new Common.Entities.Migrations.Configuration();
                var migrator = new DbMigrator(migrationsConfig);
                // Read-only: compares this build's migrations against __MigrationHistory. Does NOT apply anything.
                PendingMigrations = migrator.GetPendingMigrations().ToList();
                SchemaUpToDate = PendingMigrations.Count == 0;
            }
            catch (Exception ex)
            {
                SchemaError = ex.Message;
            }
        }

        private async Task LoadConfigAndWebhook()
        {
            try
            {
                var config = new AppConfig();
                SqlServer = SafeSqlServer(config);
                RedisHost = string.IsNullOrWhiteSpace(config.ConnectionStrings?.RedisConnectionString) ? "(not configured)" : "(configured)";
                ServiceBusEndpoint = string.IsNullOrWhiteSpace(config.ConnectionStrings?.ServiceBusConnectionString) ? "(not configured)" : "(configured)";
                CognitiveEndpoint = string.IsNullOrWhiteSpace(config.CognitiveEndpoint) ? "(not configured)" : config.CognitiveEndpoint;
                WebAppUrl = config.WebAppURL;

                if (config.ImportJobSettings != null)
                {
                    if (config.ImportJobSettings.ActivityLog) EnabledImports.Add("Activity/audit");
                    if (config.ImportJobSettings.Copilot) EnabledImports.Add("Copilot");
                    if (config.ImportJobSettings.GraphUsersMetadata) EnabledImports.Add("User metadata");
                    if (config.ImportJobSettings.GraphUserApps) EnabledImports.Add("User apps");
                    if (config.ImportJobSettings.GraphUsageReports) EnabledImports.Add("Usage reports");
                    if (config.ImportJobSettings.GraphTeams) EnabledImports.Add("Teams");
                    if (config.ImportJobSettings.WebTraffic) EnabledImports.Add("Web traffic");
                    if (config.ImportJobSettings.SentEmails) EnabledImports.Add("Sent emails");
                    if (config.ImportJobSettings.Calls) EnabledImports.Add("Teams calls");
                }

                using (var db = new AnalyticsEntitiesContext())
                {
                    // Reuse the homepage's tested logic (config from the applied installer config + a
                    // cached Graph lookup of the Teams call-records webhook subscription).
                    var status = await SystemStatus.LoadFrom(db, null);
                    CallsImportEnabled = status.CallsImportEnabled;
                    WebhookState = status.CallWebhookState.ToString();
                    WebhookExpiryUtc = status.CallWebhookExpiry;
                    WebhookDetail = status.CallWebhookStatusDetail;
                }
            }
            catch (Exception ex)
            {
                ConfigError = ex.Message;
            }
        }

        private static string SafeSqlServer(AppConfig config)
        {
            try
            {
                return new System.Data.SqlClient.SqlConnectionStringBuilder(config.ConnectionStrings.DatabaseConnectionString).DataSource;
            }
            catch
            {
                return "(unknown)";
            }
        }

        private async Task LoadRuntimeAndAppInsights(AppConfig config)
        {
            // Runtime credential expiry is the single most valuable proactive signal (a silently
            // expired secret/cert stops all data flow), so surface it here today rather than waiting
            // for the runtime HealthCheck emitter. Certificate expiry is exact; a client secret's
            // expiry is not visible at runtime.
            await LoadCredentialHealth(config);

            AppInsightsConfigured = !string.IsNullOrEmpty(config?.AppInsightsConnectionString);

            TokenCredential credential = null;
            try
            {
                credential = await BuildCredential(config);
            }
            catch (Exception ex)
            {
                var msg = "Couldn't build the Entra credential: " + ex.Message;
                ComponentHealthError = ComponentHealthError ?? msg;
                LivenessError = LivenessError ?? msg;
                ExceptionsError = ExceptionsError ?? msg;
                return;
            }

            await LoadServiceBusHealth(config, credential);

            if (!AppInsightsConfigured) return;

            AppInsightsQueryClient client = null;
            try
            {
                client = new AppInsightsQueryClient(config.AppInsightsConnectionString, credential, AnalyticsLogger.ConsoleOnlyTracer());
                await LoadComponentHealth(client);
                await LoadLiveness(client);
                await LoadExceptions(client);
            }
            catch (Exception ex)
            {
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

        /// <summary>Builds the app's Entra credential, honouring certificate auth (never assumes a client secret).</summary>
        private static async Task<TokenCredential> BuildCredential(AppConfig config)
        {
            if (config.UseClientCertificate)
            {
                var cert = await AuthHelper.RetrieveKeyVaultCertificate(AuthHelper.CertificateName, config.KeyVaultUrl, AnalyticsLogger.ConsoleOnlyTracer());
                return new ClientCertificateCredential(config.TenantGUID.ToString(), config.ClientID, cert);
            }
            return new ClientSecretCredential(config.TenantGUID.ToString(), config.ClientID, config.ClientSecret);
        }

        private async Task LoadCredentialHealth(AppConfig config)
        {
            try
            {
                if (config.UseClientCertificate)
                {
                    X509Certificate2 cert = await AuthHelper.RetrieveKeyVaultCertificate(AuthHelper.CertificateName, config.KeyVaultUrl, AnalyticsLogger.ConsoleOnlyTracer());
                    var daysToExpiry = (int)Math.Floor((cert.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays);
                    string status;
                    if (daysToExpiry < 0) status = HealthStatusNames.Unhealthy;
                    else if (daysToExpiry < 14) status = HealthStatusNames.Degraded;
                    else status = HealthStatusNames.Healthy;

                    UpsertComponent(new ComponentHealthRow
                    {
                        Component = "Credential",
                        Status = status,
                        Detail = daysToExpiry < 0
                            ? "Runtime certificate has EXPIRED - data flow will stop."
                            : $"Runtime certificate '{AuthHelper.CertificateName}' valid; expires {cert.NotAfter.ToUniversalTime():yyyy-MM-dd}.",
                        DaysToExpiry = daysToExpiry,
                        LastSeenUtc = DateTime.UtcNow
                    });
                }
                else
                {
                    // The app is running and authenticating, so the secret is currently valid; its
                    // expiry date is not readable at runtime from the secret value itself.
                    UpsertComponent(new ComponentHealthRow
                    {
                        Component = "Credential",
                        Status = HealthStatusNames.Healthy,
                        Detail = "Client-secret auth: currently valid. Secret expiry is not visible at runtime - use certificate auth to get an expiry warning, or track it in the app registration.",
                        LastSeenUtc = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                UpsertComponent(new ComponentHealthRow
                {
                    Component = "Credential",
                    Status = HealthStatusNames.Degraded,
                    Detail = "Couldn't check the runtime certificate: " + ex.Message,
                    LastSeenUtc = DateTime.UtcNow
                });
            }
        }

        private async Task LoadServiceBusHealth(AppConfig config, TokenCredential credential)
        {
            // Only relevant when Teams calls import is on (that's what uses the Service Bus queue).
            var sbConn = config?.ConnectionStrings?.ServiceBusConnectionString;
            if (config?.ImportJobSettings == null || !config.ImportJobSettings.Calls || string.IsNullOrWhiteSpace(sbConn))
            {
                return;
            }

            try
            {
                var sbProps = ServiceBusConnectionStringProperties.Parse(sbConn);
                var admin = new ServiceBusAdministrationClient(sbProps.FullyQualifiedNamespace, credential);
                var runtime = await admin.GetQueueRuntimePropertiesAsync(sbProps.EntityPath);
                var props = runtime.Value;

                string status = props.DeadLetterMessageCount > 0 ? HealthStatusNames.Degraded : HealthStatusNames.Healthy;
                UpsertComponent(new ComponentHealthRow
                {
                    Component = "ServiceBus",
                    Status = status,
                    Detail = $"Teams calls queue '{sbProps.EntityPath}': {props.ActiveMessageCount} active, {props.DeadLetterMessageCount} dead-lettered.",
                    LastSeenUtc = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                UpsertComponent(new ComponentHealthRow
                {
                    Component = "ServiceBus",
                    Status = HealthStatusNames.Degraded,
                    Detail = "Couldn't read the Teams calls queue depth: " + ex.Message,
                    LastSeenUtc = DateTime.UtcNow
                });
            }
        }

        /// <summary>Adds or replaces a component row by name (runtime checks take precedence over any older telemetry row).</summary>
        private void UpsertComponent(ComponentHealthRow row)
        {
            ComponentHealth.RemoveAll(c => string.Equals(c.Component, row.Component, StringComparison.OrdinalIgnoreCase));
            ComponentHealth.Add(row);
            ComponentHealth = ComponentHealth.OrderBy(c => c.Component).ToList();
        }

        private async Task LoadComponentHealth(AppInsightsQueryClient client)
        {
            try
            {
                var table = await client.RunQueryAsync(QueryComponentHealth);
                foreach (var row in table.Rows)
                {
                    var component = table.GetString(row, "Component");
                    // Runtime checks done above (Credential, Service Bus) are fresher than telemetry - keep them.
                    if (ComponentHealth.Any(c => string.Equals(c.Component, component, StringComparison.OrdinalIgnoreCase))) continue;

                    UpsertComponent(new ComponentHealthRow
                    {
                        Component = component,
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

                // Web-tracker liveness: are pageViews actually arriving in App Insights? Distinguishes
                // "tracker not deployed on the site" from "AppInsightsImporter not running".
                var pv = await client.RunQueryAsync(QueryPageViewsLast24h);
                if (pv.RowCount > 0)
                {
                    var first = pv.Rows[0];
                    PageViewsLast24h = pv.GetLong(first, "Count") ?? 0;
                    NewestPageViewUtc = pv.GetDateTimeUtc(first, "Last");
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

                // SQL capacity / read-only is a distinct catastrophic state - count it out of the noise.
                // Only the COUNT is surfaced; exception message text (which can contain data) is not.
                var cap = await client.RunQueryAsync(QuerySqlCapacityExceptions);
                if (cap.RowCount > 0)
                {
                    SqlCapacityExceptions24h = cap.GetLong(cap.Rows[0], "Count") ?? 0;
                }
            }
            catch (Exception ex)
            {
                ExceptionsError = ex.Message;
            }
        }

        /// <summary>
        /// Rolls the individual cards up into one traffic-light. Delegates to the pure, unit-tested
        /// <see cref="DataUtils.Health.HealthRollup"/> so the rules live in one testable place.
        /// </summary>
        public static string EvaluateOverall(HealthDashboard m, out List<string> reasons)
        {
            var input = new HealthRollupInput
            {
                NowUtc = m.LoadedAtUtc,
                DataError = m.DataError,
                SchemaUpToDate = m.SchemaUpToDate,
                PendingMigrationCount = m.PendingMigrations?.Count ?? 0,
                SqlCapacityExceptions24h = m.SqlCapacityExceptions24h,
                CallsImportEnabled = m.CallsImportEnabled,
                WebhookState = m.WebhookState,
                AppInsightsConfigured = m.AppInsightsConfigured,
                AnyTelemetryQueryError = !string.IsNullOrEmpty(m.LivenessError)
                    || !string.IsNullOrEmpty(m.ExceptionsError)
                    || !string.IsNullOrEmpty(m.ComponentHealthError),
                CycleSlaHours = CycleSlaHours,
                Components = m.ComponentHealth
                    .Select(c => new ComponentStatusInput { Component = c.Component, Status = c.Status, Detail = c.Detail })
                    .ToList(),
                Jobs = m.LastCyclePerJob
                    .Select(j => new JobLivenessInput { JobName = j.JobName, LastCycleUtc = j.LastCycleUtc })
                    .ToList(),
            };

            var status = HealthRollup.Evaluate(input, out reasons);
            return status.ToString();
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

        private const string QueryPageViewsLast24h =
            "pageViews " +
            "| where timestamp > ago(24h) " +
            "| summarize Count = count(), Last = max(timestamp)";

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

        private const string QuerySqlCapacityExceptions =
            "exceptions " +
            "| where timestamp > ago(24h) " +
            "| where (outerMessage has \"read-only\") or (outerMessage has \"database is full\") or (outerMessage has \"insufficient disk space\") or (type contains \"SqlException\") " +
            "| summarize Count = count()";

        #endregion

        private class TableRowCount
        {
            public string TableName { get; set; }
            public long Rows { get; set; }
        }
    }

    /// <summary>String constants matching <see cref="DataUtils.Health.HealthStatus"/> so the JSON payload and alert rules use one vocabulary.</summary>
    public static class HealthStatusNames
    {
        public const string Healthy = "Healthy";
        public const string Degraded = "Degraded";
        public const string Unhealthy = "Unhealthy";
        public const string Unknown = "Unknown";
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
