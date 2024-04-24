using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;

namespace Common.Entities.Config
{
    public class AppConfig
    {
        public AppConfig()
        {
            this.ConnectionStrings = new AppConnectionStrings();

            this.AppInsightsConnectionString = ConfigurationManager.AppSettings.Get(nameof(AppInsightsConnectionString));

            this.AppInsightsContainerName = ConfigurationManager.AppSettings["AppInsightsContainerName"];
            this.AppInsightsApiKey = ConfigurationManager.AppSettings["AppInsightsApiKey"];
            this.AppInsightsAppId = ConfigurationManager.AppSettings["AppInsightsAppId"];


            this.ClientID = ConfigurationManager.AppSettings.Get("ClientID");
            this.ClientSecret = ConfigurationManager.AppSettings.Get("ClientSecret");
            this.TenantDomain = ConfigurationManager.AppSettings.Get("TenantDomain");
            this.TenantGUID = Guid.Parse(ConfigurationManager.AppSettings.Get("TenantGUID"));
            this.AADInstance = ConfigurationManager.AppSettings.Get("AADInstance");
            this.KeyVaultUrl = ConfigurationManager.AppSettings.Get("KeyVaultUrl");

            var useClientCertificate = ConfigurationManager.AppSettings.Get("UseClientCertificate");
            if (!string.IsNullOrEmpty(useClientCertificate))
            {
                var useClientCertificateBool = false;
                bool.TryParse(useClientCertificate, out useClientCertificateBool);
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



            this.CognitiveEndpoint = ConfigurationManager.AppSettings.Get("CognitiveEndpoint");
            this.CognitiveKey = ConfigurationManager.AppSettings.Get("CognitiveKey");


            var importJobSettingsString = ConfigurationManager.AppSettings.Get("ImportJobSettings");
            this.ImportJobSettings = new ImportTaskSettings(importJobSettingsString);

            this.StatsApiSecret = ConfigurationManager.AppSettings.Get("StatsApiSecret");
            this.StatsApiUrl = ConfigurationManager.AppSettings.Get("StatsApiUrl");
        }


        public string AppInsightsContainerName { get; set; }
        public string AppInsightsApiKey { get; set; }
        public string AppInsightsAppId { get; set; }

        public string AppInsightsConnectionString { get; set; }

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
        public string Authority
        {
            get
            {
                return this.AADInstance + this.TenantGUID;
            }
        }

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
                string[] tokens = ContentTypesString.Split(";".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                return tokens.ToList();
            }
        }

        public string ContentTypesString { get; set; }

        public int DaysBeforeNowToDownload { get; set; }
        public string CognitiveEndpoint { get; set; }
        public string CognitiveKey { get; set; }

        public bool IsValidCognitiveConfig
        {
            get
            {
                return !(string.IsNullOrEmpty(this.CognitiveEndpoint) || string.IsNullOrEmpty(this.CognitiveKey));
            }
        }

        public ImportTaskSettings ImportJobSettings { get; set; }

        public string StatsApiSecret { get; set; } = null;
        public string StatsApiUrl { get; set; } = null;

        public AppConnectionStrings ConnectionStrings { get; set; } = null;

    }
}
