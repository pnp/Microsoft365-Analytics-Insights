using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Redis;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Serves the system-status data that used to be the server-rendered home page, now consumed
    /// by the SPA's Home page.
    /// </summary>
    [Authorize]
    [RoutePrefix("api/SystemStatus")]
    public class SystemStatusAPIController : ApiController
    {
        // GET: api/SystemStatus
        [HttpGet]
        [Route("")]
        public async Task<IHttpActionResult> Get()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var appConfig = new AppConfig();
                // Redis is optional for the web app, so tolerate it not being configured.
                var cache = CacheConnectionManager.TryGetConnectionManager(appConfig.ConnectionStrings.RedisConnectionString, tenantId: appConfig.TenantGUID.ToString(), clientId: appConfig.ClientID, clientSecret: appConfig.ClientSecret);
                var s = await SystemStatus.LoadFrom(db, cache);

                var model = new SystemStatusApiModel
                {
                    BuildLabel = s.BuildLabel,
                    HasValidConfig = s.HasValidConfig,
                    HitCount = s.HitCount,
                    ActivityCount = s.ActivityCount,
                    TeamsCount = s.TeamsCount,
                    TeamsBeingTrackedCount = s.TeamsBeingTrackedCount,
                    WebhookEndpointUrl = (s.WebAppBaseURL ?? string.Empty) + "api/CallRecordWebhook",
                    CallsImportEnabled = s.CallsImportEnabled,
                    CallWebhookState = s.CallWebhookState.ToString(),
                    CallWebhookExpiry = s.CallWebhookExpiry,
                    CallWebhookStatusDetail = s.CallWebhookStatusDetail,
                    WebAppConfigSQL = s.WebAppConfigSQL,
                    WebAppConfigRedis = s.WebAppConfigRedis,
                    WebAppConfigCognitive = s.WebAppConfigCognitive,
                    CognitiveServiceEnabled = s.CognitiveServiceEnabled,
                    WebAppConfigServiceBus = s.WebAppConfigServiceBus,
                    ConfigJson = s.ConfigJson,
                };

                return Ok(model);
            }
        }
    }
}
