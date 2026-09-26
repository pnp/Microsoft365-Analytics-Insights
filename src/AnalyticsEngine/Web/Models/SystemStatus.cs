using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Common.Entities;
using Common.Entities.Calls;
using Common.Entities.Config;
using Common.Entities.Installer;
using Common.Entities.Redis;
using Newtonsoft.Json;
using System;
using System.Data.Entity;
using System.Linq;
using System.Runtime.Caching;
using System.Threading.Tasks;
using Web.AnalyticsWeb.Models.Calls;

namespace Web.AnalyticsWeb.Models
{
    public class SystemStatus
    {
        #region Constructors

        public SystemStatus(string json)
        {
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    // The base type the web-jobs read the same JSON with: the installer's
                    // SolutionInstallConfig would drag the whole installer engine into the web app.
                    this.Config = JsonConvert.DeserializeObject<BaseSolutionInstallConfig>(json);
                }
                catch (JsonReaderException)
                {
                    // Nothing. Show no config object
                }
            }

            this.ConfigJson = json;
        }
        protected SystemStatus() { }

        #endregion

        #region Props

        public BaseSolutionInstallConfig Config { get; set; }

        public string ConfigJson { get; set; }

        public string BuildLabel { get; set; }

        public bool HasValidConfig
        {
            get { return this.Config != null; }
        }

        public string WebAppConfigSQL { get; set; }
        public string WebAppConfigRedis { get; set; }
        public string WebAppConfigServiceBus { get; set; }
        public string WebAppConfigCognitive { get; set; }
        public bool CognitiveServiceEnabled { get; set; }
        public string WebAppBaseURL { get; set; }
        public bool YammerAuth { get; set; }

        /// <summary>Whether the Teams calls import is switched on for this deployment.</summary>
        public bool CallsImportEnabled { get; set; }

        /// <summary>Status of the Graph call-records webhook subscription that drives the Teams calls import.</summary>
        public CallWebhookSubscriptionState CallWebhookState { get; set; }

        /// <summary>When the call-records webhook subscription expires (only set when it is active).</summary>
        public DateTimeOffset? CallWebhookExpiry { get; set; }

        /// <summary>Extra detail for the webhook status, e.g. the error message when it couldn't be checked.</summary>
        public string CallWebhookStatusDetail { get; set; }

        #endregion

        internal async static Task<SystemStatus> LoadFrom(AnalyticsEntitiesContext db, CacheConnectionManager cache)
        {
            SystemStatus status = null;


            // Load config
            var latestConfig = await db.ConfigStates.OrderByDescending(s => s.DateApplied).Take(1).ToListAsync();
            if (latestConfig.Count == 1 && !string.IsNullOrEmpty(latestConfig[0].ConfigJson))
            {
                try
                {
                    status = new SystemStatus(latestConfig[0].ConfigJson);
                }
                catch (JsonReaderException)
                {
                    status = new UnknownConfigSystemStatus();
                }
            }
            else
            {
                status = new UnknownConfigSystemStatus();
            }

            status.BuildLabel = Common.Entities.BuildConstants.BuildLabel;

            // Config
            var config = new AppConfig();
            status.WebAppConfigCognitive = config.CognitiveEndpoint;
            status.WebAppConfigRedis = string.IsNullOrWhiteSpace(config.ConnectionStrings.RedisConnectionString)
                ? "(not configured - Teams deep analytics disabled)"
                : StackExchange.Redis.ConfigurationOptions.Parse(config.ConnectionStrings.RedisConnectionString).SslHost;
            status.WebAppConfigSQL = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(config.ConnectionStrings.DatabaseConnectionString).DataSource;
            status.WebAppConfigServiceBus = string.IsNullOrWhiteSpace(config.ConnectionStrings.ServiceBusConnectionString)
                ? "(disabled)"
                : ServiceBusConnectionStringProperties.Parse(config.ConnectionStrings.ServiceBusConnectionString).Endpoint.ToString();
            status.CognitiveServiceEnabled = config.IsValidCognitiveConfig;
            status.WebAppBaseURL = config.WebAppURL;

            await status.LoadCallWebhookStatus(config);

            return status;
        }

        /// <summary>
        /// Works out the state of the Teams call-records webhook subscription for display on the
        /// homepage. If calls import is off there is nothing to check; otherwise it asks Microsoft
        /// Graph whether a matching subscription is currently registered, and when it expires - using
        /// the same matching rules as the importer's <c>CallWebhook</c>, over Graph's REST API rather
        /// than the Graph SDK. The Graph result is cached briefly so we don't call Graph on every page
        /// load, and any failure is caught so a Graph error never breaks the homepage.
        /// </summary>
        private async Task LoadCallWebhookStatus(AppConfig config)
        {
            this.CallsImportEnabled = config.ImportJobSettings != null && config.ImportJobSettings.Calls;

            if (!this.CallsImportEnabled)
            {
                this.CallWebhookState = CallWebhookSubscriptionState.Disabled;
                return;
            }

            if (string.IsNullOrWhiteSpace(config.WebAppURL))
            {
                this.CallWebhookState = CallWebhookSubscriptionState.Error;
                this.CallWebhookStatusDetail = "WebAppURL is not configured, so the webhook subscription URL can't be determined.";
                return;
            }

            // Build the notification URL exactly as the importer web-job does when it registers the
            // subscription, so the lookup matches the subscription Graph actually holds.
            var webhookUrlString = config.WebAppURL + "api/CallRecordWebhook";

            // A Graph call on every homepage load would be wasteful and rate-limitable, so cache the
            // outcome for a few minutes. Errors are intentionally NOT cached, so a transient Graph
            // failure recovers on the next page load.
            var cacheKey = "CallWebhookStatus::" + webhookUrlString;
            if (MemoryCache.Default.Get(cacheKey) is CachedCallWebhookStatus cached)
            {
                this.CallWebhookState = cached.State;
                this.CallWebhookExpiry = cached.Expiry;
                this.CallWebhookStatusDetail = cached.Detail;
                return;
            }

            try
            {
                var subscriptions = new GraphRestCallRecordSubscriptionReader(
                    new ClientSecretCredential(config.TenantGUID.ToString(), config.ClientID, config.ClientSecret));
                var info = await CallRecordSubscriptionStatus.ReadAsync(subscriptions, new Uri(webhookUrlString));

                if (info.Exists)
                {
                    this.CallWebhookState = CallWebhookSubscriptionState.Active;
                    this.CallWebhookExpiry = info.ExpirationDateTime;
                }
                else
                {
                    this.CallWebhookState = CallWebhookSubscriptionState.Missing;
                }

                MemoryCache.Default.Set(
                    cacheKey,
                    new CachedCallWebhookStatus { State = this.CallWebhookState, Expiry = this.CallWebhookExpiry, Detail = this.CallWebhookStatusDetail },
                    DateTimeOffset.UtcNow.AddMinutes(5));
            }
            catch (Exception ex)
            {
                this.CallWebhookState = CallWebhookSubscriptionState.Error;
                this.CallWebhookStatusDetail = ex.Message;
            }
        }

        private class CachedCallWebhookStatus
        {
            public CallWebhookSubscriptionState State { get; set; }
            public DateTimeOffset? Expiry { get; set; }
            public string Detail { get; set; }
        }
    }

    /// <summary>
    /// State of the Teams call-records webhook subscription, surfaced on the homepage.
    /// </summary>
    public enum CallWebhookSubscriptionState
    {
        /// <summary>Teams calls import is switched off, so no subscription is expected.</summary>
        Disabled,

        /// <summary>A matching call-records subscription is active in Microsoft Graph.</summary>
        Active,

        /// <summary>Calls import is on but no matching subscription was found (the importer registers/renews it each cycle).</summary>
        Missing,

        /// <summary>Calls import is on but the subscription status couldn't be checked (e.g. a Graph error).</summary>
        Error,
    }

    public class UnknownConfigSystemStatus : SystemStatus
    {
        public UnknownConfigSystemStatus() : this(string.Empty)
        {
        }
        public UnknownConfigSystemStatus(string json) : base(json)
        {
            base.Config = new BaseSolutionInstallConfig();
        }
    }
}