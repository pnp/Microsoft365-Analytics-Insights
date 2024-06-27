using Common.Entities.Config;
using DataUtils;
using DataUtils.Http;

namespace WebJob.Office365ActivityImporter.Engine
{
    public abstract class AbstractApiLoader
    {
        protected readonly AnalyticsLogger _telemetry;
        protected readonly AppConfig _settings;

        protected AbstractApiLoader(AnalyticsLogger telemetry, AppConfig settings)
        {
            this._telemetry = telemetry;
            this._settings = settings;
        }
    }

    public abstract class AbstractActivityApiLoaderWithHttpClient : AbstractApiLoader
    {
        protected ConfidentialClientApplicationThrottledHttpClient _httpClient;
        protected AbstractActivityApiLoaderWithHttpClient(AnalyticsLogger telemetry, ConfidentialClientApplicationThrottledHttpClient httpClient, AppConfig settings)
            : base(telemetry, settings)
        {
            _httpClient = httpClient;
        }
    }
}
