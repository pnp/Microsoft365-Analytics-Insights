using Common.Entities.Config;
using Common.Entities.State;
using System;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    [RequirePortalPermission(PortalPermission.Administration)]
    [RoutePrefix("api/AgentCostConnection")]
    public sealed class AgentCostConnectionAPIController : ApiController
    {
        private readonly Func<AgentCostConnectionStore> _openStore;
        public AgentCostConnectionAPIController() : this(() => AgentCostConnectionStore.TryOpen(new AppConfig())) { }
        internal AgentCostConnectionAPIController(Func<AgentCostConnectionStore> openStore) { _openStore = openStore; }

        [HttpGet, Route("")]
        public async Task<IHttpActionResult> Status()
        {
            var store = _openStore();
            if (store == null) return Ok(new { state = "storageNotConfigured" });
            try { return Ok(new { state = await store.GetStatusAsync() }); }
            catch (Exception) { return Content(HttpStatusCode.ServiceUnavailable, new { code = "storageUnavailable" }); }
        }

        [HttpPost, Route("begin"), RequireSameOriginXhr]
        public async Task<IHttpActionResult> Begin()
        {
            var store = _openStore();
            if (store == null) return Content(HttpStatusCode.ServiceUnavailable, new { code = "storageNotConfigured" });
            try
            {
                // A read proves the durable store is reachable before taking the admin away from the portal.
                await store.GetHeadAsync();
                var intent = AgentCostConsent.CreateIntent(User as ClaimsPrincipal);
                return Ok(new { url = "/Account/ConnectAgentCosts?intent=" + Uri.EscapeDataString(intent) });
            }
            catch (AgentCostConnectionException) { return Content(HttpStatusCode.Forbidden, new { code = "identityMismatch" }); }
            catch (Exception) { return Content(HttpStatusCode.ServiceUnavailable, new { code = "storageUnavailable" }); }
        }

        [HttpPost, Route("disconnect"), RequireSameOriginXhr]
        public async Task<IHttpActionResult> Disconnect()
        {
            var store = _openStore();
            if (store == null) return Content(HttpStatusCode.ServiceUnavailable, new { code = "storageNotConfigured" });
            try
            {
                await store.DisconnectAsync();
                return Ok(new { state = "disconnected" });
            }
            catch (Exception) { return Content(HttpStatusCode.ServiceUnavailable, new { code = "storageUnavailable" }); }
        }
    }
}
