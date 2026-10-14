using Common.Entities.Config;
using Common.Entities.State;
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    [RequirePortalPermission(PortalPermission.Administration)]
    [Route("api/AgentCostConnection")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public sealed class AgentCostConnectionAPIController : ControllerBase
    {
        private readonly Func<AgentCostConnectionStore> _openStore;
        public AgentCostConnectionAPIController() : this(() => AgentCostConnectionStore.TryOpen(new AppConfig())) { }
        internal AgentCostConnectionAPIController(Func<AgentCostConnectionStore> openStore) { _openStore = openStore; }

        [HttpGet, Route("")]
        public async Task<IActionResult> Status()
        {
            var store = _openStore();
            if (store == null) return Ok(new { state = "storageNotConfigured" });
            try { return Ok(new { state = await store.GetStatusAsync() }); }
            catch (Exception) { return StatusCode(503, new { code = "storageUnavailable" }); }
        }

        [HttpPost, Route("begin"), RequireSameOriginXhr]
        public async Task<IActionResult> Begin([FromServices] IDataProtectionProvider protection)
        {
            var store = _openStore();
            if (store == null) return StatusCode(503, new { code = "storageNotConfigured" });
            try
            {
                // A read proves the durable store is reachable before taking the admin away from the portal.
                await store.GetHeadAsync();
                var intent = AgentCostConsent.CreateIntent(User, protection);
                return Ok(new { url = "/Account/ConnectAgentCosts?intent=" + Uri.EscapeDataString(intent) });
            }
            catch (AgentCostConnectionException) { return StatusCode(403, new { code = "identityMismatch" }); }
            catch (Exception) { return StatusCode(503, new { code = "storageUnavailable" }); }
        }

        [HttpPost, Route("disconnect"), RequireSameOriginXhr]
        public async Task<IActionResult> Disconnect()
        {
            var store = _openStore();
            if (store == null) return StatusCode(503, new { code = "storageNotConfigured" });
            try
            {
                await store.DisconnectAsync();
                return Ok(new { state = "disconnected" });
            }
            catch (Exception) { return StatusCode(503, new { code = "storageUnavailable" }); }
        }
    }
}
