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

            this.BuildLabel = ConfigurationManager.AppSettings["BuildLabel"];

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

            var ts = TimeSpan.FromDays(1);     // default
            TimeSpan.TryParse(ConfigurationManager.AppSettings.Get("ChunkSize"), out ts);
            this.ChunkSize = ts;
            this.ContentTypesString = ConfigurationManager.AppSettings.Get("ContentTypesListAsString") ?? "Audit.SharePoint";

            int daysBeforeNowToDownload = 6;
            int.TryParse(ConfigurationManager.AppSettings.Get("DaysBeforeNowToDownload"), out daysBeforeNowToDownload);
            this.DaysBeforeNowToDownload = daysBeforeNowToDownload;

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

            // Time chunk overlap in minutes to prevent missing events at boundaries
            int timeChunkOverlapMinutes = 5;
            int.TryParse(ConfigurationManager.AppSettings.Get("TimeChunkOverlapMinutes"), out timeChunkOverlapMinutes);
            this.TimeChunkOverlapMinutes = timeChunkOverlapMinutes;


            this.CognitiveEndpoint = ConfigurationManager.AppSettings.Get("CognitiveEndpoint");
            this.CognitiveKey = ConfigurationManager.AppSettings.Get("CognitiveKey");


            var importJobSettingsString = ConfigurationManager.AppSettings.Get("ImportJobSettings");
            this.ImportJobSettings = new ImportTaskSettings(importJobSettingsString);

            this.StatsApiSecret = ConfigurationManager.AppSettings.Get("StatsApiSecret");
            this.StatsApiUrl = ConfigurationManager.AppSettings.Get("StatsApiUrl");

            var metadataRefreshMinutes = ConfigurationManager.AppSettings.Get("MetadataRefreshMinutes");
            if (!string.IsNullOrEmpty(metadataRefreshMinutes))
            {
                int metadataRefreshMinutesInt = 24 * 60; // 24 hours
                int.TryParse(metadataRefreshMinutes, out metadataRefreshMinutesInt);
                if (metadataRefreshMinutesInt < -1)
                {
                    this.MetadataRefreshMinutes = metadataRefreshMinutesInt;
                }
            }

            // New optional flag: UseRBACForServiceBus (default false)
            var useRbacForSb = ConfigurationManager.AppSettings.Get("UseRBACForServiceBus");
            if (!string.IsNullOrEmpty(useRbacForSb))
            {
                if (bool.TryParse(useRbacForSb, out var parsed))
                {
                    this.UseRBACForServiceBus = parsed;
                }
            }
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
        /// When true, use RBAC (AAD) auth to connect to Service Bus instead of SAS connection string.
        /// Default false.
        /// </summary>
        public bool UseRBACForServiceBus { get; set; } = false;
    }
}
