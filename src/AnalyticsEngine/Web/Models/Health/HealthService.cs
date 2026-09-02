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
using System.Linq;
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
    /// Performance: the SQL section uses a short per-query command timeout, runs its two heavy tables in
    /// parallel on separate contexts, and folds each table's 24h/7d counts + freshness into a single pass
    /// (see <see cref="SqlHealthDataSource"/>). On a very large tenant those scans degrade to a per-metric
    /// error instead of hanging the request (mirrors <c>ProfilingStatusAPIController</c>) - the cheap DMV
    /// approximate counts still show. The overall roll-up (Overview) deliberately skips those scans
    /// entirely and only probes database reachability with <c>SELECT 1</c>.
    ///
    /// Reuses the app's existing Entra credential (honouring certificate auth) + App Insights connection
    /// string, so no new API key or config is required. See HEALTH-MONITORING-DESIGN.md (#144).
    ///
    /// SQL access is behind <see cref="IHealthDataSource"/> and caching behind <see cref="IHealthCache"/>
    /// so the section-building logic is testable without a database (issues #379 / #381).
    /// </summary>
    public class HealthService
    {
        public const int CacheSeconds = 60;

        // A full activity import cycle should complete at least this often (see HEALTH-MONITORING-DESIGN.md).
        private const int CycleSlaHours = 24;

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
        private readonly SemaphoreSlim _summaryGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _dataGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _livenessGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _exceptionsGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _componentsGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _configGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _credGate = new SemaphoreSlim(1, 1);

        private readonly IHealthDataSource _dataSource;
        private readonly IHealthCache _cache;

        /// <summary>
        /// The instance the Health API serves every request from. It is deliberately a singleton: the
        /// per-key gates above are instance state, so a new instance per request would lose the
        /// single-flight protection that stops a burst of page opens stampeding a cold section.
        /// </summary>
        public static readonly HealthService Default = new HealthService(new SqlHealthDataSource(), MemoryCacheHealthCache.Instance);

        public HealthService(IHealthDataSource dataSource, IHealthCache cache)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        // --- Public, independently-cached section loaders (one per api/Health route) ---

        public Task<HealthSummary> LoadSummaryAsync(AppConfig config)
            => GetOrBuildAsync(SummaryKey, _summaryGate, () => BuildSummaryAsync(config));

        public Task<DataOverviewSection> LoadDataAsync()
            => GetOrBuildAsync(DataKey, _dataGate, BuildDataAsync);

        public Task<LivenessSection> LoadLivenessAsync(AppConfig config)
            => GetOrBuildAsync(LivenessKey, _livenessGate, () => BuildLivenessAsync(config));

        public Task<ExceptionsSection> LoadExceptionsAsync(AppConfig config)
            => GetOrBuildAsync(ExceptionsKey, _exceptionsGate, () => BuildExceptionsAsync(config));

        public Task<ComponentsSection> LoadComponentsAsync(AppConfig config)
            => GetOrBuildAsync(ComponentsKey, _componentsGate, () => BuildComponentsAsync(config));

        public Task<ConfigSection> LoadConfigAsync(AppConfig config)
            => GetOrBuildAsync(ConfigKey, _configGate, () => BuildConfigAsync(config));

        /// <summary>Cache-or-build helper: single-flight per key, caches the result (even when it carries per-section errors) for <see cref="CacheSeconds"/>.</summary>
        private async Task<T> GetOrBuildAsync<T>(string key, SemaphoreSlim gate, Func<Task<T>> build) where T : class
        {
            if (_cache.TryGet<T>(key, out var hit)) return hit;

            await gate.WaitAsync();
            try
            {
                // Re-check: another request may have built it while we waited for the gate.
                if (_cache.TryGet<T>(key, out var hit2)) return hit2;

                var built = await build();
                _cache.Set(key, built, TimeSpan.FromSeconds(CacheSeconds));
                return built;
            }
            finally
            {
                gate.Release();
            }
        }

        // --- Overview / overall roll-up ---

        private async Task<HealthSummary> BuildSummaryAsync(AppConfig config)
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
            var dbProbeTask = _dataSource.ProbeDatabaseAsync();
            await Task.WhenAll(configTask, componentsTask, livenessTask, exceptionsTask, dbProbeTask);

            var cfg = configTask.Result;
            var components = componentsTask.Result;
            var liveness = livenessTask.Result;
            var exceptions = exceptionsTask.Result;
            var dataError = dbProbeTask.Result?.Error;

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
                HealthDataSectionRules.DataProbeStatus(dataError),
                new SectionStatus { Key = "liveness", Label = "Import liveness", Status = liveness.Status, Reasons = liveness.Reasons },
                new SectionStatus { Key = "exceptions", Label = "Exceptions", Status = exceptions.Status, Reasons = exceptions.Reasons },
                new SectionStatus { Key = "components", Label = "Component health", Status = components.Status, Reasons = components.Reasons },
                new SectionStatus { Key = "config", Label = "Configuration", Status = cfg.Status, Reasons = cfg.Reasons }
            };

            return summary;
        }

        // --- Data overview (SQL) ---

        private async Task<DataOverviewSection> BuildDataAsync()
        {
            var counts = await _dataSource.GetDatabaseCountsAsync();

            // Recent volume + freshness on the two biggest fact tables, run in parallel on separate
            // contexts. Skipped entirely when the cheap block already failed hard (the database is
            // unreachable), which is what the old inline code did too.
            RecentVolumeResult hits = null, audit = null;
            if (string.IsNullOrEmpty(counts?.DataError))
            {
                var hitsTask = _dataSource.GetRecentVolumeAsync("hits", "hit_timestamp");
                var auditTask = _dataSource.GetRecentVolumeAsync("audit_events", "time_stamp");
                await Task.WhenAll(hitsTask, auditTask);
                hits = hitsTask.Result;
                audit = auditTask.Result;
            }

            return HealthDataSectionRules.BuildDataSection(counts, hits, audit);
        }

        // --- Configuration (config + schema + webhook) ---

        private async Task<ConfigSection> BuildConfigAsync(AppConfig config)
        {
            var section = new ConfigSection();

            // Schema / migration status: read-only, compares this build's migrations against
            // __MigrationHistory. Does NOT apply anything.
            try
            {
                section.PendingMigrations = (await _dataSource.GetPendingMigrationsAsync()).ToList();
                section.SchemaUpToDate = section.PendingMigrations.Count == 0;
            }
            catch (Exception ex)
            {
                section.SchemaError = HealthDataSectionRules.InnermostMessage(ex);
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
                    if (config.ImportJobSettings.ImportPowerPlatform) section.EnabledImports.Add("Power Platform");
                    if (config.ImportJobSettings.GraphUsersMetadata) section.EnabledImports.Add("User metadata");
                    if (config.ImportJobSettings.GraphUsageReports) section.EnabledImports.Add("Usage reports");
                    if (config.ImportJobSettings.GraphCopilotUsageReports) section.EnabledImports.Add("Copilot usage reports (Graph)");
                    if (config.ImportJobSettings.GraphTeams) section.EnabledImports.Add("Teams");
                    if (config.ImportJobSettings.WebTraffic) section.EnabledImports.Add("Web traffic");
                    if (config.ImportJobSettings.SentEmails) section.EnabledImports.Add("Sent emails");
                    if (config.ImportJobSettings.Calls) section.EnabledImports.Add("Teams calls");
                }

                var webhook = await _dataSource.GetCallWebhookStatusAsync();
                section.CallsImportEnabled = webhook.CallsImportEnabled;
                section.WebhookState = webhook.WebhookState;
                section.WebhookExpiryUtc = webhook.WebhookExpiryUtc;
                section.WebhookDetail = webhook.WebhookDetail;
            }
            catch (Exception ex)
            {
                section.ConfigError = HealthDataSectionRules.InnermostMessage(ex);
            }

            var input = new HealthRollupInput
            {
                NowUtc = section.LoadedAtUtc,
                SchemaUpToDate = section.SchemaUpToDate,
                PendingMigrationCount = section.PendingMigrations.Count,
                CallsImportEnabled = section.CallsImportEnabled,
                WebhookState = section.WebhookState
            };
            HealthDataSectionRules.SetStatusFromRollup(section, input);
            if (!string.IsNullOrEmpty(section.ConfigError) || !string.IsNullOrEmpty(section.SchemaError))
                HealthDataSectionRules.RaiseAtLeastDegraded(section, "Some configuration couldn't be read.");

            return section;
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

        // --- Component health (runtime credential + Service Bus + App Insights HealthCheck) ---

        private async Task<ComponentsSection> BuildComponentsAsync(AppConfig config)
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
                section.ComponentHealthError = "Couldn't build the Entra credential: " + HealthDataSectionRules.InnermostMessage(ex);
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
                        if (string.IsNullOrEmpty(section.ComponentHealthError)) section.ComponentHealthError = HealthDataSectionRules.InnermostMessage(ex);
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
            HealthDataSectionRules.SetStatusFromRollup(section, input);
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
                    Detail = "Couldn't check the runtime certificate: " + HealthDataSectionRules.InnermostMessage(ex),
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
                var detail = HealthDataSectionRules.InnermostMessage(ex);

                // A network-layer rejection (rather than an auth failure) in a private deployment almost always
                // means the namespace has public access disabled but no private endpoint - which needs Premium.
                // See issue #228; without this hint the 401 reads like an RBAC problem.
                if (LooksLikeNetworkRejection(detail))
                {
                    detail += " This looks like a network-level block rather than a permissions problem: in a private (VNet) deployment "
                        + "Service Bus must be on the Premium SKU with a private endpoint, otherwise the namespace is unreachable and "
                        + "Teams calls will not import. Either migrate the namespace to Premium, or re-enable public network access on it.";
                }

                UpsertComponent(section, new ComponentHealthRow
                {
                    Component = "ServiceBus",
                    Status = HealthStatusNames.Degraded,
                    Detail = "Couldn't read the Teams calls queue depth: " + detail,
                    LastSeenUtc = DateTime.UtcNow
                });
            }
        }

        /// <summary>
        /// Service Bus rejects a blocked IP before the token is even validated, so the message mentions IP
        /// filtering / VNet service endpoints rather than authorisation.
        /// </summary>
        private static bool LooksLikeNetworkRejection(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            return message.IndexOf("Ip has been prevented", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("IP Filter", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("Virtual Network", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("prevented to connect to the endpoint", StringComparison.OrdinalIgnoreCase) >= 0;
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

        private async Task<LivenessSection> BuildLivenessAsync(AppConfig config)
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
                section.LivenessError = "Couldn't build the Entra credential: " + HealthDataSectionRules.InnermostMessage(ex);
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
                    if (string.IsNullOrEmpty(section.LivenessError)) section.LivenessError = HealthDataSectionRules.InnermostMessage(ex);
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
            HealthDataSectionRules.SetStatusFromRollup(section, input);
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

        private async Task<ExceptionsSection> BuildExceptionsAsync(AppConfig config)
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
                section.ExceptionsError = "Couldn't build the Entra credential: " + HealthDataSectionRules.InnermostMessage(ex);
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
                    if (string.IsNullOrEmpty(section.ExceptionsError)) section.ExceptionsError = HealthDataSectionRules.InnermostMessage(ex);
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
            HealthDataSectionRules.SetStatusFromRollup(section, input);
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

        // --- Shared credential helper ---

        /// <summary>Builds (and briefly caches) the app's Entra credential, honouring certificate auth so the AI sections don't each re-hit Key Vault.</summary>
        private async Task<TokenCredential> GetCredentialAsync(AppConfig config)
        {
            if (_cache.TryGet<TokenCredential>(CredKey, out var cached)) return cached;

            await _credGate.WaitAsync();
            try
            {
                if (_cache.TryGet<TokenCredential>(CredKey, out var cached2)) return cached2;

                var built = await BuildCredential(config);
                _cache.Set(CredKey, built, TimeSpan.FromSeconds(CacheSeconds));
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
    }
}
