using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Redis;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
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
                    DataCounts = await BuildDataCountsAsync(db, s),
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
                };

                return Ok(model);
            }
        }

        /// <summary>
        /// Record counts for the main tables. Reuses the counts SystemStatus already loaded (hits,
        /// audit events, teams) and adds the other headline tables.
        /// </summary>
        private static async Task<List<NamedCountModel>> BuildDataCountsAsync(AnalyticsEntitiesContext db, SystemStatus s)
        {
            return new List<NamedCountModel>
            {
                new NamedCountModel("Users", await db.users.CountAsync()),
                new NamedCountModel("Web page hits", s.HitCount),
                new NamedCountModel("Audit events", s.ActivityCount),
                new NamedCountModel("Copilot interactions", await db.CopilotChats.CountAsync()),
                new NamedCountModel("Sent emails", await db.SentEmails.CountAsync()),
                new NamedCountModel("Teams discovered", s.TeamsCount),
                new NamedCountModel("Teams with tracking enabled", s.TeamsBeingTrackedCount),
                new NamedCountModel("Teams calls", await db.CallRecords.CountAsync()),
                new NamedCountModel("SharePoint sites", await db.sites.CountAsync()),
                new NamedCountModel("Tracked URLs", await db.urls.CountAsync()),
            };
        }
    }
}
