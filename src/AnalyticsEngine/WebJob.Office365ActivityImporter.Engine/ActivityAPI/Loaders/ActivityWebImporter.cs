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

        public ActivityWebImporter(AppConfig settings, AnalyticsLogger logger, int maxSavesPerBatch) : base(settings, logger, maxSavesPerBatch)
        {
            var auth = new ActivityAPIAppIndentityOAuthContext(logger, settings.ClientID, settings.TenantGUID.ToString(), settings.ClientSecret, settings.KeyVaultUrl, settings.UseClientCertificate);
            var httpClient = new ConfidentialClientApplicationThrottledHttpClient(auth, false, logger);
            _activityReportWebLoader = new ActivityReportWebLoader(httpClient, logger, settings.TenantGUID.ToString());
            _contentMetaDataLoader = new WebContentMetaDataLoader(logger, httpClient, settings);
            _activitySubscriptionManager = new ActivitySubscriptionManager(settings, logger, httpClient);
        }


        /// <summary>
        /// Unit tests constructors
        /// </summary>
        public ActivityWebImporter(ConfidentialClientApplicationThrottledHttpClient httpClient, AppConfig settings, AnalyticsLogger logger, int maxSavesPerBatch) : base(settings, logger, maxSavesPerBatch)
        {
            _activityReportWebLoader = new ActivityReportWebLoader(httpClient, logger, settings.TenantGUID.ToString());
            _contentMetaDataLoader = new WebContentMetaDataLoader(logger, httpClient, settings);
            _activitySubscriptionManager = new ActivitySubscriptionManager(settings, logger, httpClient);
        }
        public ActivityWebImporter(ConfidentialClientApplicationThrottledHttpClient fakeClient, AppConfig s, AnalyticsLogger logger) :
            this(fakeClient, s, logger, 1)
        {
        }


        public override IActivityReportLoader<ActivityReportInfo> ReportLoader => _activityReportWebLoader;
        public override ContentMetaDataLoader<ActivityReportInfo> ContentMetaDataLoader => _contentMetaDataLoader;

        public override IActivitySubscriptionManager ActivitySubscriptionManager => _activitySubscriptionManager;
    }
}
