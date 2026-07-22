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

            // Resolve Copilot file/meeting metadata from Graph. Default true so behaviour is unchanged unless
            // a tenant opts out. (The Power Platform workload is toggled separately on ImportJobSettings.)
            this.ResolveCopilotResourceMetadata = bool.TryParse(ConfigurationManager.AppSettings.Get("ResolveCopilotResourceMetadata"), out var resolveCopilotResourceMetadata)
                ? resolveCopilotResourceMetadata
                : true;

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
        /// Resolve Copilot file + meeting resource metadata from the Graph (file name/site, meeting
        /// name/date). Default true. When false, Copilot interactions are still imported with their agent
        /// metadata (agent id/name/type, cost, messages, accessed resources) but the per-event Graph
        /// resolution is skipped - removing serial, network-bound calls from the save path. Set false when
        /// only Copilot agent-level reporting is needed.
        /// </summary>
        public bool ResolveCopilotResourceMetadata { get; set; }

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
    }
}
