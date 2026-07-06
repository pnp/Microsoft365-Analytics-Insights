using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using DataUtils.AppInsights;
using DataUtils.Health;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Migrations;
using System.Data.SqlClient;
using System.Linq;
using System.Runtime.Caching;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.Health
{
    /// <summary>
    /// Builds the in-app Health tab's "is it working?" data, one <b>independently cached</b> sub-section
    /// at a time (Data / Import liveness / Exceptions / Component health / Configuration) plus a cheap
    /// roll-up <see cref="HealthSummary"/>. The SPA fetches only the sub-section the user is looking at,
    /// so opening Health no longer runs every SQL scan + App Insights query at once.
    ///
    /// Performance: the SQL section uses a short per-query <see cref="SqlQueryTimeoutSecs"/> command
    /// timeout, runs its two heavy tables in parallel on separate contexts, and folds each table's
    /// 24h/7d counts + freshness into a single pass. On a very large tenant those scans degrade to a
    /// per-metric error instead of hanging the request (mirrors <c>ProfilingStatusAPIController</c>) -
    /// the cheap DMV approximate counts still show. The overall roll-up (Overview) deliberately skips
    /// those scans entirely and only probes database reachability with <c>SELECT 1</c>.
    ///
    /// Reuses the app's existing Entra credential (honouring certificate auth) + App Insights connection
    /// string, so no new API key or config is required. See HEALTH-MONITORING-DESIGN.md (#144).
    /// </summary>
    public static class HealthService
    {
        public const int CacheSeconds = 60;

        // A full activity import cycle should complete at least this often (see HEALTH-MONITORING-DESIGN.md).
        private const int CycleSlaHours = 24;

        // Per-query SQL timeout. AnalyticsEntitiesContext sets an infinite command timeout (for long
        // importer/migration work); here a single unindexed scan of audit_events / hits on a big tenant
        // would otherwise run until Azure App Service kills the HTTP request (~230s) -> 500. Cap each
        // query so it degrades to a per-metric error instead.
        private const int SqlQueryTimeoutSecs = 20;
        // The overall roll-up only needs "can we reach the DB?", so its probe is capped even shorter.
        private const int DbProbeTimeoutSecs = 10;

        private const string CacheVersion = "v3";
        private const string SummaryKey = "health:summary:" + CacheVersion;
        private const string DataKey = "health:data:" + CacheVersion;
        private const string LivenessKey = "health:liveness:" + CacheVersion;
        private const string ExceptionsKey = "health:exceptions:" + CacheVersion;
        private const string ComponentsKey = "health:components:" + CacheVersion;
        private const string ConfigKey = "health:config:" + CacheVersion;
        private const string CredKey = "health:cred:" + CacheVersion;

        // One gate per cache key so a burst of page opens can't stampede a cold section with N
        // simultaneous builds, but different sections still build concurrently.
        private static readonly SemaphoreSlim _summaryGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim _dataGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim _livenessGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim _exceptionsGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim _componentsGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim _configGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim _credGate = new SemaphoreSlim(1, 1);

        // --- Public, independently-cached section loaders (one per api/Health route) ---

        public static Task<HealthSummary> LoadSummaryAsync(AppConfig config)
            => GetOrBuildAsync(SummaryKey, _summaryGate, () => BuildSummaryAsync(config));

        public static Task<DataOverviewSection> LoadDataAsync()
            => GetOrBuildAsync(DataKey, _dataGate, BuildDataAsync);

        public static Task<LivenessSection> LoadLivenessAsync(AppConfig config)
            => GetOrBuildAsync(LivenessKey, _livenessGate, () => BuildLivenessAsync(config));

        public static Task<ExceptionsSection> LoadExceptionsAsync(AppConfig config)
            => GetOrBuildAsync(ExceptionsKey, _exceptionsGate, () => BuildExceptionsAsync(config));

        public static Task<ComponentsSection> LoadComponentsAsync(AppConfig config)
            => GetOrBuildAsync(ComponentsKey, _componentsGate, () => BuildComponentsAsync(config));

        public static Task<ConfigSection> LoadConfigAsync(AppConfig config)
            => GetOrBuildAsync(ConfigKey, _configGate, () => BuildConfigAsync(config));

        /// <summary>Cache-or-build helper: single-flight per key, caches the result (even when it carries per-section errors) for <see cref="CacheSeconds"/>.</summary>
        private static async Task<T> GetOrBuildAsync<T>(string key, SemaphoreSlim gate, Func<Task<T>> build) where T : class
        {
            if (MemoryCache.Default.Get(key) is T hit) return hit;

            await gate.WaitAsync();
            try
            {
                // Re-check: another request may have built it while we waited for the gate.
                if (MemoryCache.Default.Get(key) is T hit2) return hit2;

                var built = await build();
                MemoryCache.Default.Set(key, built, new CacheItemPolicy
                {
                    AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(CacheSeconds)
                });
                return built;
            }
            finally
            {
                gate.Release();
            }
        }

        // --- Overview / overall roll-up ---

        private static async Task<HealthSummary> BuildSummaryAsync(AppConfig config)
        {
            var summary = new HealthSummary
            {
                BuildLabel = BuildConstants.BuildLabel,
                AppInsightsConfigured = !string.IsNullOrEmpty(config?.AppInsightsConnectionString)
            };

            // Load the cheap-to-medium sections in parallel (each own context / cached credential) and
            // reuse their caches, so clicking into a tab afterwards is instant. The heavy SQL Data
            // section is NOT loaded here - the roll-up only needs DB reachability (a cheap SELECT 1).
            var configTask = LoadConfigAsync(config);
            var componentsTask = LoadComponentsAsync(config);
            var livenessTask = LoadLivenessAsync(config);
            var exceptionsTask = LoadExceptionsAsync(config);
            var dbProbeTask = ProbeDatabaseAsync();
            await Task.WhenAll(configTask, componentsTask, livenessTask, exceptionsTask, dbProbeTask);

            var cfg = configTask.Result;
            var components = componentsTask.Result;
            var liveness = livenessTask.Result;
            var exceptions = exceptionsTask.Result;
            var dataError = dbProbeTask.Result;

            var input = new HealthRollupInput
            {
                NowUtc = summary.LoadedAtUtc,
                DataError = dataError,
                SchemaUpToDate = cfg.SchemaUpToDate,
                PendingMigrationCount = cfg.PendingMigrations?.Count ?? 0,
                SqlCapacityExceptions24h = exceptions.SqlCapacityExceptions24h,
                CallsImportEnabled = cfg.CallsImportEnabled,
                WebhookState = cfg.WebhookState,
                AppInsightsConfigured = summary.AppInsightsConfigured,
                AnyTelemetryQueryError = !string.IsNullOrEmpty(liveness.LivenessError)
                    || !string.IsNullOrEmpty(exceptions.ExceptionsError)
                    || !string.IsNullOrEmpty(components.ComponentHealthError),
                CycleSlaHours = CycleSlaHours,
                Components = components.ComponentHealth
                    .Select(c => new ComponentStatusInput { Component = c.Component, Status = c.Status, Detail = c.Detail })
                    .ToList(),
                Jobs = liveness.LastCyclePerJob
                    .Select(j => new JobLivenessInput { JobName = j.JobName, LastCycleUtc = j.LastCycleUtc })
                    .ToList()
            };

            summary.OverallStatus = HealthRollup.Evaluate(input, out var reasons).ToString();
            summary.OverallReasons = reasons;

            // At-a-glance grid. Data's row comes from the reachability probe (its real counts load on the
            // Data tab), so the Overview stays cheap.
            summary.Sections = new List<SectionStatus>
            {
                DataProbeStatus(dataError),
                new SectionStatus { Key = "liveness", Label = "Import liveness", Status = liveness.Status, Reasons = liveness.Reasons },
                new SectionStatus { Key = "exceptions", Label = "Exceptions", Status = exceptions.Status, Reasons = exceptions.Reasons },
                new SectionStatus { Key = "components", Label = "Component health", Status = components.Status, Reasons = components.Reasons },
                new SectionStatus { Key = "config", Label = "Configuration", Status = cfg.Status, Reasons = cfg.Reasons }
            };

            return summary;
        }

        private static SectionStatus DataProbeStatus(string dataError)
        {
            return string.IsNullOrEmpty(dataError)
                ? new SectionStatus { Key = "data", Label = "Data overview", Status = HealthStatusNames.Healthy, Reasons = new List<string> { "Database reachable (open the Data tab for counts)." } }
                : new SectionStatus { Key = "data", Label = "Data overview", Status = HealthStatusNames.Unhealthy, Reasons = new List<string> { "Database query failed: " + dataError } };
        }

        /// <summary>Cheap "can we reach the DB?" probe for the overall roll-up - no table scans. Returns the error message, or null when reachable.</summary>
        private static async Task<string> ProbeDatabaseAsync()
        {
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    db.Database.CommandTimeout = DbProbeTimeoutSecs;
                    await db.Database.SqlQuery<int>("SELECT 1").ToListAsync();
                }
                return null;
            }
            catch (Exception ex)
            {
                return InnermostMessage(ex);
            }
        }

        // --- Data overview (SQL) ---

        private static async Task<DataOverviewSection> BuildDataAsync()
        {
            var section = new DataOverviewSection();

            // Approximate counts + DB size come from DMVs (sys.dm_db_partition_stats / sys.database_files),
            // which need VIEW DATABASE STATE. Isolate them so a locked-down SQL login still gets the more
            // important recent-volume + freshness signals below.
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    db.Database.CommandTimeout = SqlQueryTimeoutSecs;
                    try
                    {
                        var approx = await LoadApproxRowCounts(db);
                        section.ActivityCount = ApproxFor(approx, "audit_events");
                        section.HitCount = ApproxFor(approx, "hits");
                        section.TeamsCount = ApproxFor(approx, "teams");
                        section.SentEmailCount = ApproxFor(approx, "sent_emails");
                        section.CallRecordCount = ApproxFor(approx, "call_records");
                        section.CopilotChatCount = ApproxFor(approx, "copilot_chats");
                        section.UserCount = ApproxFor(approx, "users");
                        section.DatabaseSizeMb = await LoadDatabaseSizeMb(db);
                    }
                    catch (Exception dmvEx)
                    {
                        // Counts stay 0; the recent-volume / freshness rows below are the real "is it flowing" signal.
                        section.CountsError = InnermostMessage(dmvEx);
                    }

                    // Teams-being-tracked is a filtered count on a small table (thousands of rows) - cheap.
                    section.TeamsBeingTrackedCount = await db.Teams.Where(t => t.HasRefreshToken).CountAsync();
                }
            }
            catch (Exception ex)
            {
                section.DataError = InnermostMessage(ex);
            }

            // Recent volume + freshness on the two biggest fact tables. Their timestamp columns are NOT
            // indexed (all indexes on hits/audit_events are FK indexes - see Create DB.sql), so these are
            // clustered-index scans. We therefore: fold each table's 24h/7d counts + newest into ONE pass,
            // run the two tables in parallel on separate contexts, and cap the command timeout so on a huge
            // tenant they degrade to RecentVolumeError instead of hanging the request. (DataError above is
            // only for a hard connection failure.)
            if (string.IsNullOrEmpty(section.DataError))
            {
                var hitsTask = RecentVolumeAsync("hits", "hit_timestamp");
                var auditTask = RecentVolumeAsync("audit_events", "time_stamp");
                await Task.WhenAll(hitsTask, auditTask);
                var hits = hitsTask.Result;
                var audit = auditTask.Result;

                if (string.IsNullOrEmpty(hits.Error))
                {
                    section.HitsLast24h = hits.Last24h;
                    section.HitsLast7d = hits.Last7d;
                    section.NewestHitUtc = hits.Newest;
                }
                if (string.IsNullOrEmpty(audit.Error))
                {
                    section.AuditEventsLast24h = audit.Last24h;
                    section.AuditEventsLast7d = audit.Last7d;
                    section.NewestAuditEventUtc = audit.Newest;
                }

                var volumeErrors = new[] { hits.Error, audit.Error }.Where(e => !string.IsNullOrEmpty(e)).Distinct().ToList();
                if (volumeErrors.Count > 0) section.RecentVolumeError = string.Join("; ", volumeErrors);
            }

            ComputeDataStatus(section);
            return section;
        }

        private static void ComputeDataStatus(DataOverviewSection s)
        {
            if (!string.IsNullOrEmpty(s.DataError))
            {
                s.Status = HealthStatusNames.Unhealthy;
                s.Reasons = new List<string> { "Database query failed: " + s.DataError };
                return;
            }

            var reasons = new List<string>();
            if (!string.IsNullOrEmpty(s.CountsError)) reasons.Add("Approximate counts unavailable: " + s.CountsError);
            if (!string.IsNullOrEmpty(s.RecentVolumeError)) reasons.Add("Recent-volume scan didn't complete: " + s.RecentVolumeError);

            if (reasons.Count > 0)
            {
                s.Status = HealthStatusNames.Degraded;
                s.Reasons = reasons;
            }
            else
            {
                s.Status = HealthStatusNames.Healthy;
                s.Reasons = new List<string> { "All checks passing." };
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

        /// <summary>
        /// One-pass 24h + 7d count and newest-timestamp for a fact table. <paramref name="table"/> and
        /// <paramref name="tsColumn"/> are compile-time constants (not user input), so there's no injection
        /// risk. Runs with a short command timeout so an unindexed scan on a huge tenant degrades to an error.
        /// </summary>
        private static async Task<RecentVolume> RecentVolumeAsync(string table, string tsColumn)
        {
            var volume = new RecentVolume();
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    db.Database.CommandTimeout = SqlQueryTimeoutSecs;
                    var p24 = new SqlParameter("@c24", DateTime.UtcNow.AddHours(-24));
                    var p7 = new SqlParameter("@c7", DateTime.UtcNow.AddDays(-7));
                    var sql =
                        $"SELECT SUM(CASE WHEN [{tsColumn}] > @c24 THEN CAST(1 AS BIGINT) ELSE 0 END) AS Last24h, " +
                        $"SUM(CASE WHEN [{tsColumn}] > @c7 THEN CAST(1 AS BIGINT) ELSE 0 END) AS Last7d, " +
                        $"MAX([{tsColumn}]) AS Newest " +
                        $"FROM [dbo].[{table}]";
                    var r = (await db.Database.SqlQuery<RecentVolumeRow>(sql, p24, p7).ToListAsync()).FirstOrDefault();
                    if (r != null)
                    {
                        volume.Last24h = r.Last24h ?? 0;
                        volume.Last7d = r.Last7d ?? 0;
                        volume.Newest = r.Newest.HasValue ? DateTime.SpecifyKind(r.Newest.Value, DateTimeKind.Utc) : (DateTime?)null;
                    }
                }
            }
            catch (Exception ex)
            {
                volume.Error = InnermostMessage(ex);
            }
            return volume;
        }

        // --- Configuration (config + schema + webhook) ---

        private static async Task<ConfigSection> BuildConfigAsync(AppConfig config)
        {
            var section = new ConfigSection();

            // Schema / migration status: read-only, compares this build's migrations against
            // __MigrationHistory. Does NOT apply anything.
            try
            {
                var migrationsConfig = new Common.Entities.Migrations.Configuration();
                var migrator = new DbMigrator(migrationsConfig);
                section.PendingMigrations = migrator.GetPendingMigrations().ToList();
                section.SchemaUpToDate = section.PendingMigrations.Count == 0;
            }
            catch (Exception ex)
            {
                section.SchemaError = InnermostMessage(ex);
            }

            try
            {
                section.SqlServer = SafeSqlServer(config);
                section.RedisHost = string.IsNullOrWhiteSpace(config.ConnectionStrings?.RedisConnectionString) ? "(not configured)" : "(configured)";
                section.ServiceBusEndpoint = string.IsNullOrWhiteSpace(config.ConnectionStrings?.ServiceBusConnectionString) ? "(not configured)" : "(configured)";
                section.CognitiveEndpoint = string.IsNullOrWhiteSpace(config.CognitiveEndpoint) ? "(not configured)" : config.CognitiveEndpoint;
                section.WebAppUrl = config.WebAppURL;

                if (config.ImportJobSettings != null)
                {
                    if (config.ImportJobSettings.ActivityLog) section.EnabledImports.Add("Activity/audit");
                    if (config.ImportJobSettings.Copilot) section.EnabledImports.Add("Copilot");
                    if (config.ImportJobSettings.GraphUsersMetadata) section.EnabledImports.Add("User metadata");
                    if (config.ImportJobSettings.GraphUserApps) section.EnabledImports.Add("User apps");
                    if (config.ImportJobSettings.GraphUsageReports) section.EnabledImports.Add("Usage reports");
                    if (config.ImportJobSettings.GraphTeams) section.EnabledImports.Add("Teams");
                    if (config.ImportJobSettings.WebTraffic) section.EnabledImports.Add("Web traffic");
                    if (config.ImportJobSettings.SentEmails) section.EnabledImports.Add("Sent emails");
                    if (config.ImportJobSettings.Calls) section.EnabledImports.Add("Teams calls");
                }

                using (var db = new AnalyticsEntitiesContext())
                {
                    // Reuse the homepage's tested logic (config from the applied installer config + a
                    // cached Graph lookup of the Teams call-records webhook subscription).
                    var status = await SystemStatus.LoadFrom(db, null);
                    section.CallsImportEnabled = status.CallsImportEnabled;
                    section.WebhookState = status.CallWebhookState.ToString();
                    section.WebhookExpiryUtc = status.CallWebhookExpiry;
                    section.WebhookDetail = status.CallWebhookStatusDetail;
                }
            }
            catch (Exception ex)
            {
                section.ConfigError = InnermostMessage(ex);
            }

            var input = new HealthRollupInput
            {
                NowUtc = section.LoadedAtUtc,
                SchemaUpToDate = section.SchemaUpToDate,
                PendingMigrationCount = section.PendingMigrations.Count,
                CallsImportEnabled = section.CallsImportEnabled,
                WebhookState = section.WebhookState
            };
            SetStatusFromRollup(section, input);
            if (!string.IsNullOrEmpty(section.ConfigError) || !string.IsNullOrEmpty(section.SchemaError))
                RaiseAtLeastDegraded(section, "Some configuration couldn't be read.");

            return section;
        }

        private static string SafeSqlServer(AppConfig config)
        {
            try
            {
                return new SqlConnectionStringBuilder(config.ConnectionStrings.DatabaseConnectionString).DataSource;
            }
            catch
            {
                return "(unknown)";
            }
        }

        // --- Component health (runtime credential + Service Bus + App Insights HealthCheck) ---

        private static async Task<ComponentsSection> BuildComponentsAsync(AppConfig config)
        {
            var section = new ComponentsSection
            {
                AppInsightsConfigured = !string.IsNullOrEmpty(config?.AppInsightsConnectionString)
            };

            // Runtime credential expiry is the single most valuable proactive signal (a silently expired
            // secret/cert stops all data flow), so surface it here rather than waiting for a telemetry
            // HealthCheck emitter. Certificate expiry is exact; a client secret's expiry isn't visible.
            await LoadCredentialHealth(config, section);

            TokenCredential credential = null;
            try
            {
                credential = await GetCredentialAsync(config);
            }
            catch (Exception ex)
            {
                section.ComponentHealthError = "Couldn't build the Entra credential: " + InnermostMessage(ex);
            }

            if (credential != null)
            {
                await LoadServiceBusHealth(config, credential, section);

                if (section.AppInsightsConfigured)
                {
                    AppInsightsQueryClient client = null;
                    try
                    {
                        client = new AppInsightsQueryClient(config.AppInsightsConnectionString, credential, AnalyticsLogger.ConsoleOnlyTracer());
                        await LoadComponentHealthFromTelemetry(client, section);
                    }
                    catch (Exception ex)
                    {
                        if (string.IsNullOrEmpty(section.ComponentHealthError)) section.ComponentHealthError = InnermostMessage(ex);
                    }
                    finally
                    {
                        client?.Dispose();
                    }
                }
            }

            var input = new HealthRollupInput
            {
                NowUtc = section.LoadedAtUtc,
                AppInsightsConfigured = section.AppInsightsConfigured,
                AnyTelemetryQueryError = !string.IsNullOrEmpty(section.ComponentHealthError),
                Components = section.ComponentHealth
                    .Select(c => new ComponentStatusInput { Component = c.Component, Status = c.Status, Detail = c.Detail })
                    .ToList()
            };
            SetStatusFromRollup(section, input);
            return section;
        }

        private static async Task LoadCredentialHealth(AppConfig config, ComponentsSection section)
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

                    UpsertComponent(section, new ComponentHealthRow
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
                    // The app is running and authenticating, so the secret is currently valid; its expiry
                    // date is not readable at runtime from the secret value itself.
                    UpsertComponent(section, new ComponentHealthRow
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
                UpsertComponent(section, new ComponentHealthRow
                {
                    Component = "Credential",
                    Status = HealthStatusNames.Degraded,
                    Detail = "Couldn't check the runtime certificate: " + InnermostMessage(ex),
                    LastSeenUtc = DateTime.UtcNow
                });
            }
        }

        private static async Task LoadServiceBusHealth(AppConfig config, TokenCredential credential, ComponentsSection section)
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
                UpsertComponent(section, new ComponentHealthRow
                {
                    Component = "ServiceBus",
                    Status = status,
                    Detail = $"Teams calls queue '{sbProps.EntityPath}': {props.ActiveMessageCount} active, {props.DeadLetterMessageCount} dead-lettered.",
                    LastSeenUtc = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                UpsertComponent(section, new ComponentHealthRow
                {
                    Component = "ServiceBus",
                    Status = HealthStatusNames.Degraded,
                    Detail = "Couldn't read the Teams calls queue depth: " + InnermostMessage(ex),
                    LastSeenUtc = DateTime.UtcNow
                });
            }
        }

        /// <summary>Adds or replaces a component row by name (runtime checks take precedence over any older telemetry row).</summary>
        private static void UpsertComponent(ComponentsSection section, ComponentHealthRow row)
        {
            section.ComponentHealth.RemoveAll(c => string.Equals(c.Component, row.Component, StringComparison.OrdinalIgnoreCase));
            section.ComponentHealth.Add(row);
            section.ComponentHealth = section.ComponentHealth.OrderBy(c => c.Component).ToList();
        }

        private static async Task LoadComponentHealthFromTelemetry(AppInsightsQueryClient client, ComponentsSection section)
        {
            var table = await client.RunQueryAsync(QueryComponentHealth);
            foreach (var row in table.Rows)
            {
                var component = table.GetString(row, "Component");
                // Runtime checks done above (Credential, Service Bus) are fresher than telemetry - keep them.
                if (section.ComponentHealth.Any(c => string.Equals(c.Component, component, StringComparison.OrdinalIgnoreCase))) continue;

                UpsertComponent(section, new ComponentHealthRow
                {
                    Component = component,
                    Status = table.GetString(row, "Status"),
                    Detail = table.GetString(row, "Detail"),
                    DaysToExpiry = table.GetInt(row, "DaysToExpiry"),
                    LastSeenUtc = table.GetDateTimeUtc(row, "LastSeen")
                });
            }
        }

        // --- Import liveness (App Insights) ---

        private static async Task<LivenessSection> BuildLivenessAsync(AppConfig config)
        {
            var section = new LivenessSection
            {
                AppInsightsConfigured = !string.IsNullOrEmpty(config?.AppInsightsConnectionString)
            };

            if (!section.AppInsightsConfigured)
            {
                section.Status = HealthStatusNames.Unknown;
                section.Reasons = new List<string> { "Application Insights is not configured." };
                return section;
            }

            TokenCredential credential = null;
            try
            {
                credential = await GetCredentialAsync(config);
            }
            catch (Exception ex)
            {
                section.LivenessError = "Couldn't build the Entra credential: " + InnermostMessage(ex);
            }

            if (credential != null)
            {
                AppInsightsQueryClient client = null;
                try
                {
                    client = new AppInsightsQueryClient(config.AppInsightsConnectionString, credential, AnalyticsLogger.ConsoleOnlyTracer());
                    await LoadLiveness(client, section);
                }
                catch (Exception ex)
                {
                    if (string.IsNullOrEmpty(section.LivenessError)) section.LivenessError = InnermostMessage(ex);
                }
                finally
                {
                    client?.Dispose();
                }
            }

            var input = new HealthRollupInput
            {
                NowUtc = section.LoadedAtUtc,
                CycleSlaHours = CycleSlaHours,
                AppInsightsConfigured = true,
                AnyTelemetryQueryError = !string.IsNullOrEmpty(section.LivenessError),
                Jobs = section.LastCyclePerJob
                    .Select(j => new JobLivenessInput { JobName = j.JobName, LastCycleUtc = j.LastCycleUtc })
                    .ToList()
            };
            SetStatusFromRollup(section, input);
            return section;
        }

        private static async Task LoadLiveness(AppInsightsQueryClient client, LivenessSection section)
        {
            var cycles = await client.RunQueryAsync(QueryLastCyclePerJob);
            foreach (var row in cycles.Rows)
            {
                section.LastCyclePerJob.Add(new ImportCycleRow
                {
                    JobName = cycles.GetString(row, "JobName"),
                    LastCycleUtc = cycles.GetDateTimeUtc(row, "LastCycle"),
                    Duration = cycles.GetString(row, "Duration")
                });
            }

            var sections = await client.RunQueryAsync(QueryLastSectionImports);
            foreach (var row in sections.Rows)
            {
                section.LastSectionImports.Add(new SectionImportRow
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
                section.LastHeartbeats.Add(new HeartbeatRow
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
                section.PageViewsLast24h = pv.GetLong(first, "Count") ?? 0;
                section.NewestPageViewUtc = pv.GetDateTimeUtc(first, "Last");
            }
        }

        // --- Exceptions overview (App Insights) ---

        private static async Task<ExceptionsSection> BuildExceptionsAsync(AppConfig config)
        {
            var section = new ExceptionsSection
            {
                AppInsightsConfigured = !string.IsNullOrEmpty(config?.AppInsightsConnectionString)
            };

            if (!section.AppInsightsConfigured)
            {
                section.Status = HealthStatusNames.Unknown;
                section.Reasons = new List<string> { "Application Insights is not configured." };
                return section;
            }

            TokenCredential credential = null;
            try
            {
                credential = await GetCredentialAsync(config);
            }
            catch (Exception ex)
            {
                section.ExceptionsError = "Couldn't build the Entra credential: " + InnermostMessage(ex);
            }

            if (credential != null)
            {
                AppInsightsQueryClient client = null;
                try
                {
                    client = new AppInsightsQueryClient(config.AppInsightsConnectionString, credential, AnalyticsLogger.ConsoleOnlyTracer());
                    await LoadExceptions(client, section);
                }
                catch (Exception ex)
                {
                    if (string.IsNullOrEmpty(section.ExceptionsError)) section.ExceptionsError = InnermostMessage(ex);
                }
                finally
                {
                    client?.Dispose();
                }
            }

            var input = new HealthRollupInput
            {
                NowUtc = section.LoadedAtUtc,
                AppInsightsConfigured = true,
                AnyTelemetryQueryError = !string.IsNullOrEmpty(section.ExceptionsError),
                SqlCapacityExceptions24h = section.SqlCapacityExceptions24h
            };
            SetStatusFromRollup(section, input);
            return section;
        }

        private static async Task LoadExceptions(AppInsightsQueryClient client, ExceptionsSection section)
        {
            var perHour = await client.RunQueryAsync(QueryExceptionsPerHour);
            foreach (var row in perHour.Rows)
            {
                var count = perHour.GetLong(row, "Count") ?? 0;
                section.ExceptionsPerHour.Add(new HourCount
                {
                    HourUtc = perHour.GetDateTimeUtc(row, "timestamp"),
                    Count = count
                });
                section.ExceptionsLast24h += count;
            }

            var types = await client.RunQueryAsync(QueryTopExceptionTypes);
            foreach (var row in types.Rows)
            {
                section.TopExceptionTypes.Add(new ExceptionTypeRow
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
                section.SqlCapacityExceptions24h = cap.GetLong(cap.Rows[0], "Count") ?? 0;
            }
        }

        // --- Shared credential + roll-up helpers ---

        /// <summary>Builds (and briefly caches) the app's Entra credential, honouring certificate auth so the AI sections don't each re-hit Key Vault.</summary>
        private static async Task<TokenCredential> GetCredentialAsync(AppConfig config)
        {
            if (MemoryCache.Default.Get(CredKey) is TokenCredential cached) return cached;

            await _credGate.WaitAsync();
            try
            {
                if (MemoryCache.Default.Get(CredKey) is TokenCredential cached2) return cached2;

                var built = await BuildCredential(config);
                MemoryCache.Default.Set(CredKey, built, DateTimeOffset.UtcNow.AddSeconds(CacheSeconds));
                return built;
            }
            finally
            {
                _credGate.Release();
            }
        }

        private static async Task<TokenCredential> BuildCredential(AppConfig config)
        {
            if (config.UseClientCertificate)
            {
                var cert = await AuthHelper.RetrieveKeyVaultCertificate(AuthHelper.CertificateName, config.KeyVaultUrl, AnalyticsLogger.ConsoleOnlyTracer());
                return new ClientCertificateCredential(config.TenantGUID.ToString(), config.ClientID, cert);
            }
            return new ClientSecretCredential(config.TenantGUID.ToString(), config.ClientID, config.ClientSecret);
        }

        /// <summary>Sets a section's own traffic-light from the pure, unit-tested <see cref="HealthRollup"/> so every section + the overall use one rule set.</summary>
        private static void SetStatusFromRollup(HealthSection section, HealthRollupInput input)
        {
            section.Status = HealthRollup.Evaluate(input, out var reasons).ToString();
            section.Reasons = reasons;
        }

        /// <summary>Bumps a section to at least Degraded (used when a section partially failed to load).</summary>
        private static void RaiseAtLeastDegraded(HealthSection section, string reason)
        {
            if (!string.Equals(section.Status, HealthStatusNames.Unhealthy, StringComparison.OrdinalIgnoreCase))
                section.Status = HealthStatusNames.Degraded;
            section.Reasons.RemoveAll(r => r == "All checks passing.");
            if (!section.Reasons.Contains(reason)) section.Reasons.Add(reason);
        }

        /// <summary>EF wraps SQL errors; the innermost message (the SqlException) is the useful one.</summary>
        private static string InnermostMessage(Exception ex)
        {
            var e = ex;
            while (e.InnerException != null)
            {
                e = e.InnerException;
            }
            return e.Message;
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

        private class RecentVolumeRow
        {
            public long? Last24h { get; set; }
            public long? Last7d { get; set; }
            public DateTime? Newest { get; set; }
        }

        private class RecentVolume
        {
            public long Last24h { get; set; }
            public long Last7d { get; set; }
            public DateTime? Newest { get; set; }
            public string Error { get; set; }
        }
    }
}
