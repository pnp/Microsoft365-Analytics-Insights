using Common.Entities.Config;
using DataUtils;
using DataUtils.Http;

namespace WebJob.Office365ActivityImporter.Engine
{
    public abstract class AbstractApiLoader
    {
        protected readonly AnalyticsLogger _logger;
        protected readonly AppConfig _settings;

        protected AbstractApiLoader(AnalyticsLogger logger, AppConfig settings)
        {
            this._logger = logger;
            this._settings = settings;
        }
    }

    public abstract class AbstractActivityApiLoaderWithHttpClient : AbstractApiLoader
    {
        protected ConfidentialClientApplicationThrottledHttpClient _httpClient;
        protected AbstractActivityApiLoaderWithHttpClient(AnalyticsLogger logger, ConfidentialClientApplicationThrottledHttpClient httpClient, AppConfig settings)
            : base(logger, settings)
        {
            _httpClient = httpClient;
        }
    }
}
