using Common.Entities.Config;
using DataUtils;
using DataUtils.Http;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Loaders
{

    /// <summary>
    /// Web-loading activity importer
    /// </summary>
    public class ActivityWebImporter : ActivityImporter<ActivityReportInfo>
    {
        private ActivityReportWebLoader _activityReportWebLoader;
        private WebContentMetaDataLoader _contentMetaDataLoader;
        private ActivitySubscriptionManager _activitySubscriptionManager;

        public ActivityWebImporter(AppConfig settings, AnalyticsLogger telemetry, int maxSavesPerBatch) : base(settings, telemetry, maxSavesPerBatch)
        {
            var auth = new ActivityAPIAppIndentityOAuthContext(telemetry, settings.ClientID, settings.TenantGUID.ToString(), settings.ClientSecret, settings.KeyVaultUrl, settings.UseClientCertificate);
            var httpClient = new ConfidentialClientApplicationThrottledHttpClient(auth, false, telemetry);
            _activityReportWebLoader = new ActivityReportWebLoader(httpClient, telemetry, settings.TenantGUID.ToString());
            _contentMetaDataLoader = new WebContentMetaDataLoader(telemetry, httpClient, settings);
            _activitySubscriptionManager = new ActivitySubscriptionManager(settings, telemetry, httpClient);
        }


        /// <summary>
        /// Unit tests constructors
        /// </summary>
        public ActivityWebImporter(ConfidentialClientApplicationThrottledHttpClient httpClient, AppConfig settings, AnalyticsLogger telemetry, int maxSavesPerBatch) : base(settings, telemetry, maxSavesPerBatch)
        {
            _activityReportWebLoader = new ActivityReportWebLoader(httpClient, telemetry, settings.TenantGUID.ToString());
            _contentMetaDataLoader = new WebContentMetaDataLoader(telemetry, httpClient, settings);
            _activitySubscriptionManager = new ActivitySubscriptionManager(settings, telemetry, httpClient);
        }
        public ActivityWebImporter(ConfidentialClientApplicationThrottledHttpClient fakeClient, AppConfig s, AnalyticsLogger telemetry) :
            this(fakeClient, s, telemetry, 1)
        {
        }


        public override IActivityReportLoader<ActivityReportInfo> ReportLoader => _activityReportWebLoader;
        public override ContentMetaDataLoader<ActivityReportInfo> ContentMetaDataLoader => _contentMetaDataLoader;

        public override IActivitySubscriptionManager ActivitySubscriptionManager => _activitySubscriptionManager;
    }
}
