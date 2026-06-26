using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;

namespace Common.Entities.Config
{
    /// <summary>
    /// Config for the entire solution
    /// </summary>
    public class AppConfig
    {
        public AppConfig()
        {
            this.ConnectionStrings = new AppConnectionStrings();

            this.AppInsightsConnectionString = ConfigurationManager.AppSettings.Get(nameof(AppInsightsConnectionString));

            this.AppInsightsContainerName = ConfigurationManager.AppSettings["AppInsightsContainerName"];

            this.BuildLabel = BuildConstants.BuildLabel;

            this.ClientID = ConfigurationManager.AppSettings.Get("ClientID");
            this.ClientSecret = ConfigurationManager.AppSettings.Get("ClientSecret");
            this.TenantDomain = ConfigurationManager.AppSettings.Get("TenantDomain");
            this.TenantGUID = Guid.Parse(ConfigurationManager.AppSettings.Get("TenantGUID"));
            this.AADInstance = ConfigurationManager.AppSettings.Get("AADInstance");
            this.KeyVaultUrl = ConfigurationManager.AppSettings.Get("KeyVaultUrl");

            // New: UserGroupsFilter (optional)
            this.UserGroupsFilter = ConfigurationManager.AppSettings.Get("UserGroupsFilter");

            var useClientCertificate = ConfigurationManager.AppSettings.Get("UseClientCertificate");
            if (!string.IsNullOrEmpty(useClientCertificate))
            {
                bool.TryParse(useClientCertificate, out var useClientCertificateBool);
                this.UseClientCertificate = useClientCertificateBool;
            }
            if (string.IsNullOrEmpty(this.AADInstance))
            {
                this.AADInstance = "https://login.microsoftonline.com/";
            }
            this.WebAppURL = ConfigurationManager.AppSettings.Get("WebAppURL");

            // Preserve default if the config value is missing or invalid (TryParse would otherwise overwrite with TimeSpan.Zero)
            this.ChunkSize = TimeSpan.TryParse(ConfigurationManager.AppSettings.Get("ChunkSize"), out var ts)
                ? ts
                : TimeSpan.FromDays(1);
            this.ContentTypesString = ConfigurationManager.AppSettings.Get("ContentTypesListAsString") ?? "Audit.SharePoint";

            // Preserve default of 6 if config value is missing or invalid
            this.DaysBeforeNowToDownload = int.TryParse(ConfigurationManager.AppSettings.Get("DaysBeforeNowToDownload"), out var daysBeforeNowToDownload)
                ? daysBeforeNowToDownload
                : 6;

            // Optional: how many days before today to start reading hits from App Insights.
            // Can be overridden via the -readHitsDaysBeforeToday command line argument.
            var readHitsDaysBeforeTodayString = ConfigurationManager.AppSettings.Get("ReadHitsDaysBeforeToday");
            if (!string.IsNullOrEmpty(readHitsDaysBeforeTodayString))
            {
                int readHitsDaysBeforeTodayInt;
                if (int.TryParse(readHitsDaysBeforeTodayString, out readHitsDaysBeforeTodayInt) && readHitsDaysBeforeTodayInt > 0)
                {
                    this.ReadHitsDaysBeforeToday = readHitsDaysBeforeTodayInt;
                }
            }

            // Time chunk overlap in minutes to prevent missing events at boundaries.
            // Preserve default of 5 if config value is missing or invalid.
            this.TimeChunkOverlapMinutes = int.TryParse(ConfigurationManager.AppSettings.Get("TimeChunkOverlapMinutes"), out var timeChunkOverlapMinutes)
                ? timeChunkOverlapMinutes
                : 5;


            this.CognitiveEndpoint = ConfigurationManager.AppSettings.Get("CognitiveEndpoint");
            this.CognitiveKey = ConfigurationManager.AppSettings.Get("CognitiveKey");


            var importJobSettingsString = ConfigurationManager.AppSettings.Get("ImportJobSettings");
            this.ImportJobSettings = new ImportTaskSettings(importJobSettingsString);

            this.StatsApiSecret = ConfigurationManager.AppSettings.Get("StatsApiSecret");
            this.StatsApiUrl = ConfigurationManager.AppSettings.Get("StatsApiUrl");

            var metadataRefreshMinutes = ConfigurationManager.AppSettings.Get("MetadataRefreshMinutes");
            if (!string.IsNullOrEmpty(metadataRefreshMinutes)
                && int.TryParse(metadataRefreshMinutes, out var metadataRefreshMinutesInt)
                && metadataRefreshMinutesInt >= 0)
            {
                this.MetadataRefreshMinutes = metadataRefreshMinutesInt;
            }

            // Optional flag to bypass the "recently imported" gate for usage reports (default false).
            // Replaces a #if DEBUG override so it can be toggled in any build.
            var forceUsageReportsImport = ConfigurationManager.AppSettings.Get("ForceUsageReportsImport");
            if (!string.IsNullOrEmpty(forceUsageReportsImport)
                && bool.TryParse(forceUsageReportsImport, out var forceUsageReportsImportBool))
            {
                this.ForceUsageReportsImport = forceUsageReportsImportBool;
            }

            // Optional cap on simultaneous Activity API summary fetches (default 8).
            // Prevents burst throttling when (contentTypes × timeChunks) is large.
            this.MaxSummaryFetchConcurrency = int.TryParse(ConfigurationManager.AppSettings.Get("MaxSummaryFetchConcurrency"), out var maxSummaryFetchConcurrency)
                && maxSummaryFetchConcurrency > 0
                ? maxSummaryFetchConcurrency
                : 8;

            // Import "aggressiveness" preset (High | Balanced | Gentle, default Balanced). It provides
            // the default values for the burst/cadence knobs below so an admin can ease up CPU usage
            // with a single setting. Any explicit per-knob AppSetting still overrides the preset.
            this.ImportAggressiveness = ParseAggressiveness(ConfigurationManager.AppSettings.Get("ImportAggressiveness"));
            var preset = GetPreset(this.ImportAggressiveness);

            // Max simultaneous threads used to full-load audit reports (Office 365 Management Activity
            // API). Was a hardcoded 20; lower values reduce peak CPU on the (often 1-vCPU) App Service
            // plan. Preset-derived unless explicitly set & > 0.
            this.MaxAuditReportLoadConcurrency = int.TryParse(ConfigurationManager.AppSettings.Get("MaxAuditReportLoadConcurrency"), out var maxAuditReportLoadConcurrency)
                && maxAuditReportLoadConcurrency > 0
                ? maxAuditReportLoadConcurrency
                : preset.MaxAuditReportLoadConcurrency;

            // Max simultaneous threads InsertBatch uses to commit staged rows to SQL (was a hardcoded 20
            // inside ParallelListProcessor). Every InsertBatch importer - audit-event persistence, Copilot,
            // Power Platform and App Insights hits - funnels its SQL commit through this one choke point,
            // so it is the lever for the SQL Server CPU/DTU burst on commit. Lower values reduce the SQL
            // peak (and, for insert-bound commits, total SQL CPU). Preset-derived unless explicitly set & > 0.
            this.MaxSqlCommitConcurrency = int.TryParse(ConfigurationManager.AppSettings.Get("MaxSqlCommitConcurrency"), out var maxSqlCommitConcurrency)
                && maxSqlCommitConcurrency > 0
                ? maxSqlCommitConcurrency
                : preset.MaxSqlCommitConcurrency;

            // Minutes the WebJob waits between import cycles (was a hardcoded 10). Preset-derived unless
            // explicitly set & > 0.
            this.ImportCyclePauseMinutes = int.TryParse(ConfigurationManager.AppSettings.Get("ImportCyclePauseMinutes"), out var importCyclePauseMinutes)
                && importCyclePauseMinutes > 0
                ? importCyclePauseMinutes
                : preset.ImportCyclePauseMinutes;

            // Minimum hours between the "static" non-fresh Graph imports (user metadata, user Teams
            // apps). These barely change intraday, so by default they run once a day instead of every
            // cycle. 0 disables the gate (runs every cycle). Preset-derived (High=0 i.e. legacy
            // every-cycle, Balanced/Gentle=24) unless explicitly set >= 0.
            this.GraphMetadataImportIntervalHours = int.TryParse(ConfigurationManager.AppSettings.Get("GraphMetadataImportIntervalHours"), out var graphMetadataImportIntervalHours)
                && graphMetadataImportIntervalHours >= 0
                ? graphMetadataImportIntervalHours
                : preset.NonFreshGraphIntervalHours;

            // Minimum hours between Teams crawls. Teams analytics (channel messages/reactions) is fresher
            // than user metadata and crawls incrementally via delta tokens, so it has its own knob and
            // can be made more frequent without un-gating the static imports. Same preset defaults.
            this.GraphTeamsImportIntervalHours = int.TryParse(ConfigurationManager.AppSettings.Get("GraphTeamsImportIntervalHours"), out var graphTeamsImportIntervalHours)
                && graphTeamsImportIntervalHours >= 0
                ? graphTeamsImportIntervalHours
                : preset.NonFreshGraphIntervalHours;

            // One-off force flag: bypass the cadence gate for the non-fresh Graph imports (user metadata,
            // user apps, Teams) for this run. Mirrors ForceUsageReportsImport. Default false.
            var forceGraphMetadataImport = ConfigurationManager.AppSettings.Get("ForceGraphMetadataImport");
            if (!string.IsNullOrEmpty(forceGraphMetadataImport)
                && bool.TryParse(forceGraphMetadataImport, out var forceGraphMetadataImportBool))
            {
                this.ForceGraphMetadataImport = forceGraphMetadataImportBool;
            }

            // One-off start offset (minutes) applied before the first import cycle, used to stagger the
            // two WebJobs so they don't peak on the shared App Service plan at the same time. 0 disables.
            // Default = half the cycle pause. Invalid/empty falls back to that default.
            this.ImportStartStaggerMinutes = int.TryParse(ConfigurationManager.AppSettings.Get("ImportStartStaggerMinutes"), out var importStartStaggerMinutes)
                && importStartStaggerMinutes >= 0
                ? importStartStaggerMinutes
                : Math.Max(0, this.ImportCyclePauseMinutes / 2);
        }

        /// <summary>
        /// Parses the <c>ImportAggressiveness</c> AppSetting, defaulting to
        /// <see cref="ImportAggressivenessLevel.Balanced"/> when missing or invalid.
        /// </summary>
        private static ImportAggressivenessLevel ParseAggressiveness(string raw)
        {
            if (!string.IsNullOrWhiteSpace(raw) && Enum.TryParse<ImportAggressivenessLevel>(raw.Trim(), ignoreCase: true, out var level))
            {
                return level;
            }
            return ImportAggressivenessLevel.Balanced;
        }

        /// <summary>
        /// Preset default values for the burst/cadence knobs, keyed by aggressiveness level.
        /// Audit events &amp; hits keep the same cycle cadence at every level; only the audit burst
        /// concurrency (and, for Gentle, the cycle pause) change.
        /// </summary>
        private static AggressivenessPreset GetPreset(ImportAggressivenessLevel level)
        {
            switch (level)
            {
                case ImportAggressivenessLevel.High:
                    return new AggressivenessPreset { MaxAuditReportLoadConcurrency = 20, MaxSqlCommitConcurrency = 20, ImportCyclePauseMinutes = 10, NonFreshGraphIntervalHours = 0 };
                case ImportAggressivenessLevel.Gentle:
                    return new AggressivenessPreset { MaxAuditReportLoadConcurrency = 3, MaxSqlCommitConcurrency = 3, ImportCyclePauseMinutes = 20, NonFreshGraphIntervalHours = 24 };
                case ImportAggressivenessLevel.Balanced:
                default:
                    return new AggressivenessPreset { MaxAuditReportLoadConcurrency = 8, MaxSqlCommitConcurrency = 8, ImportCyclePauseMinutes = 10, NonFreshGraphIntervalHours = 24 };
            }
        }

        private sealed class AggressivenessPreset
        {
            public int MaxAuditReportLoadConcurrency { get; set; }
            public int MaxSqlCommitConcurrency { get; set; }
            public int ImportCyclePauseMinutes { get; set; }

            /// <summary>Default daily-gate (hours) for the non-fresh Graph imports. 0 = every cycle (legacy).</summary>
            public int NonFreshGraphIntervalHours { get; set; }
        }

        public string BuildLabel { get; set; }
        public string AppInsightsContainerName { get; set; }
        public string AppInsightsConnectionString { get; set; }

        public int MetadataRefreshMinutes { get; set; } = 24 * 60; // 24 hours

        public string ClientID { get; set; }
        public string ClientSecret { get; set; }
        public string TenantDomain { get; set; }
        public Guid TenantGUID { get; set; }
        public bool UseClientCertificate { get; set; } = false;

        public string KeyVaultUrl { get; set; }

        /// <summary>
        /// Default: https://login.microsoftonline.com/
        /// </summary>
        public string AADInstance { get; set; }
        public string WebAppURL { get; set; }

        /// <summary>
        /// Default {AADInstance}/{TenantGUID} (https://login.microsoftonline.com/0000-000-00000/)
        /// </summary>
        public string Authority => this.AADInstance + this.TenantGUID;


        /// <summary>
        /// Time-span to query API for in a single request
        /// </summary>
        public TimeSpan ChunkSize { get; set; }

        /// <summary>
        /// List of content-types to import
        /// </summary>
        public List<string> ContentTypesToRead
        {
            get
            {
                var tokens = ContentTypesString.Split(";".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                return tokens.ToList();
            }
        }

        public string ContentTypesString { get; set; }

        public int DaysBeforeNowToDownload { get; set; }

        /// <summary>
        /// Optional: how many days before today to start reading hits from App Insights.
        /// When set, overrides the default scan-from date logic in the App Insights importer.
        /// The -readHitsDaysBeforeToday command line argument takes precedence over this value.
        /// </summary>
        public int? ReadHitsDaysBeforeToday { get; set; } = null;

        /// <summary>
        /// Number of minutes to overlap between time chunks to prevent missing events at boundaries.
        /// Default: 5 minutes
        /// </summary>
        public int TimeChunkOverlapMinutes { get; set; } = 5;

        public string CognitiveEndpoint { get; set; }
        public string CognitiveKey { get; set; }

        /// <summary>
        /// True when we have enough configuration to talk to Azure AI Language. Valid if the
        /// endpoint is set AND we have either a key (legacy auth) OR the runtime service
        /// principal credentials (used for RBAC/Entra ID auth when the resource has key auth
        /// disabled - <c>403 AuthenticationTypeDisabled</c>).
        /// </summary>
        public bool IsValidCognitiveConfig =>
            !string.IsNullOrEmpty(this.CognitiveEndpoint) &&
            (!string.IsNullOrEmpty(this.CognitiveKey) || HasRuntimeServicePrincipal);

        private bool HasRuntimeServicePrincipal =>
            this.TenantGUID != Guid.Empty &&
            !string.IsNullOrEmpty(this.ClientID) &&
            !string.IsNullOrEmpty(this.ClientSecret);

        /// <summary>
        /// Builds a <see cref="DataUtils.CognitiveServicesClient"/> from the configured
        /// cognitive endpoint. Initially uses key auth when the key is set, otherwise builds
        /// an RBAC client (<see cref="Azure.Identity.ClientSecretCredential"/> from the
        /// runtime service principal). The returned wrapper also auto-retries with RBAC when
        /// a key-auth call is rejected at runtime (e.g. <c>403 AuthenticationTypeDisabled</c>),
        /// so callers don't need to react to <c>disableLocalAuth</c> being toggled on the
        /// resource.
        /// Returns <c>null</c> when no usable configuration is present.
        /// </summary>
        public DataUtils.CognitiveServicesClient CreateCognitiveServicesClient(Microsoft.Extensions.Logging.ILogger logger = null)
        {
            return DataUtils.CognitiveServicesClient.TryCreate(
                this.CognitiveEndpoint,
                this.CognitiveKey,
                this.TenantGUID == Guid.Empty ? null : this.TenantGUID.ToString(),
                this.ClientID,
                this.ClientSecret,
                logger);
        }

        public ImportTaskSettings ImportJobSettings { get; set; }

        public string StatsApiSecret { get; set; } = null;
        public string StatsApiUrl { get; set; } = null;

        public AppConnectionStrings ConnectionStrings { get; set; } = null;

        /// <summary>
        /// Optional filter for user groups
        /// </summary>
        public string UserGroupsFilter { get; set; }

        /// <summary>
        /// When true, bypasses the "recently imported" gate for Graph usage reports and runs every invocation.
        /// Intended for development/manual reruns. Default false.
        /// </summary>
        public bool ForceUsageReportsImport { get; set; } = false;

        /// <summary>
        /// Maximum simultaneous Activity API summary fetches (per importer instance).
        /// Default 8. Lower values reduce throttling risk; higher values increase wall-clock throughput.
        /// </summary>
        public int MaxSummaryFetchConcurrency { get; set; } = 8;

        /// <summary>
        /// Import "aggressiveness" preset that supplies the default burst/cadence knobs below.
        /// Default <see cref="ImportAggressivenessLevel.Balanced"/>. Set via the
        /// <c>ImportAggressiveness</c> AppSetting (High | Balanced | Gentle).
        /// </summary>
        public ImportAggressivenessLevel ImportAggressiveness { get; set; } = ImportAggressivenessLevel.Balanced;

        /// <summary>
        /// Maximum simultaneous threads used to full-load audit reports from the Office 365 Management
        /// Activity API. Lower values reduce peak CPU. Preset-derived (High=20, Balanced=8, Gentle=3)
        /// unless the <c>MaxAuditReportLoadConcurrency</c> AppSetting is set &amp; &gt; 0.
        /// </summary>
        public int MaxAuditReportLoadConcurrency { get; set; } = 8;

        /// <summary>
        /// Maximum simultaneous threads <see cref="DataUtils.Sql.Inserts.InsertBatch{T}"/> uses to commit
        /// staged rows to SQL (was a hardcoded 20 inside <c>ParallelListProcessor</c>). This is the single
        /// choke point for every InsertBatch importer's SQL commit (audit-event persistence, Copilot,
        /// Power Platform, App Insights hits), so lowering it eases the SQL Server CPU/DTU burst on commit.
        /// Preset-derived (High=20, Balanced=8, Gentle=3) unless the <c>MaxSqlCommitConcurrency</c>
        /// AppSetting is set &amp; &gt; 0. Applied at WebJob startup via
        /// <c>InsertBatchConcurrency.MaxConcurrentThreads</c>.
        /// </summary>
        public int MaxSqlCommitConcurrency { get; set; } = 8;

        /// <summary>
        /// Minutes the WebJob waits between import cycles. Preset-derived (High/Balanced=10, Gentle=20)
        /// unless the <c>ImportCyclePauseMinutes</c> AppSetting is set &amp; &gt; 0.
        /// </summary>
        public int ImportCyclePauseMinutes { get; set; } = 10;

        /// <summary>
        /// Minimum hours between the "static" non-fresh Graph imports (user metadata, user Teams apps).
        /// Preset-derived (High=0 i.e. every cycle/legacy, Balanced/Gentle=24) unless the
        /// <c>GraphMetadataImportIntervalHours</c> AppSetting is set &gt;= 0. 0 disables the gate.
        /// </summary>
        public int GraphMetadataImportIntervalHours { get; set; } = 24;

        /// <summary>
        /// Minimum hours between Teams crawls (channel messages/reactions, etc). Separate from
        /// <see cref="GraphMetadataImportIntervalHours"/> because Teams data is fresher and crawls
        /// incrementally. Preset-derived (High=0, Balanced/Gentle=24) unless the
        /// <c>GraphTeamsImportIntervalHours</c> AppSetting is set &gt;= 0. 0 disables the gate.
        /// </summary>
        public int GraphTeamsImportIntervalHours { get; set; } = 24;

        /// <summary>
        /// When true, bypasses the cadence gate for the non-fresh Graph imports (user metadata, user
        /// Teams apps, Teams crawl) for one run - the equivalent of <see cref="ForceUsageReportsImport"/>
        /// for those imports. Default false.
        /// </summary>
        public bool ForceGraphMetadataImport { get; set; } = false;

        /// <summary>
        /// One-off start offset (minutes) applied before the first import cycle, used to stagger the
        /// two WebJobs so they don't peak on the shared App Service plan simultaneously. Default = half
        /// the cycle pause. 0 disables.
        /// </summary>
        public int ImportStartStaggerMinutes { get; set; } = 5;
    }

    /// <summary>
    /// Import aggressiveness presets. Higher levels favour wall-clock speed (more parallelism,
    /// shorter pauses); lower levels favour low, steady CPU usage on the shared App Service plan.
    /// Audit events &amp; hits keep the same cycle cadence at every level.
    /// </summary>
    public enum ImportAggressivenessLevel
    {
        /// <summary>Fastest, highest peak CPU. Full legacy behaviour: 20 audit-load threads, 10-min pause, and the non-fresh Graph imports run every cycle (interval 0).</summary>
        High,

        /// <summary>Default. Modestly eased: 8 audit-load threads, 10-min pause, non-fresh Graph imports daily-gated (24h).</summary>
        Balanced,

        /// <summary>Lowest CPU: 3 audit-load threads, 20-min pause, non-fresh Graph imports daily-gated (24h).</summary>
        Gentle
    }
}
