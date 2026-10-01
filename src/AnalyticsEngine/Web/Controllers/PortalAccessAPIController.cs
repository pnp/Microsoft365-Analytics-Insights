using Common.Entities.Config;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// What the signed-in user may see, so the SPA can leave out what the server would refuse anyway.
    /// </summary>
    /// <remarks>
    /// Advisory only: every endpoint enforces its own permission, so a client that ignores this reply
    /// gets 403s rather than data. Open to every signed-in user - it is how a user without a permission
    /// finds out which one they are missing.
    /// </remarks>
    [Authorize]
    [Route("api/PortalAccess")]
    public class PortalAccessAPIController : ControllerBase
    {
        // GET: api/PortalAccess
        [HttpGet]
        [Route("")]
        public IActionResult Get()
        {
            var grant = PortalAccess.Evaluate(Request, User);
            var model = new PortalAccessModel
            {
                Enforced = grant.Enforced,
                Permissions = new PortalPermissionFlags { Administration = grant.Administration, SeePii = grant.SeePii },
                Roles = new PortalRoleNames(),
                ApplicationId = AnalyticsConfig.AppSettings.Get("ClientID"),
            };

            // A permission change only arrives with the next sign-in, and a browser must never answer a new
            // session from an old one's reply.
            Response.Headers.CacheControl = "no-store, no-cache";
            return Ok(model);
        }
    }

    /// <summary>JSON shape of <c>api/PortalAccess</c>.</summary>
    public class PortalAccessModel
    {
        /// <summary>False when <c>EnforcePortalRoles=false</c> has switched role checks off for this deployment.</summary>
        [JsonProperty("enforced")]
        public bool Enforced { get; set; }

        [JsonProperty("permissions")]
        public PortalPermissionFlags Permissions { get; set; }

        /// <summary>The app role values, so the portal can tell a user exactly what to ask for.</summary>
        [JsonProperty("roles")]
        public PortalRoleNames Roles { get; set; }

        /// <summary>
        /// The app registration's client ID - the application an administrator assigns the roles on. Not a
        /// secret: it is in every sign-in redirect.
        /// </summary>
        [JsonProperty("applicationId")]
        public string ApplicationId { get; set; }
    }

    public class PortalPermissionFlags
    {
        [JsonProperty(PortalPermissionNames.Administration)]
        public bool Administration { get; set; }

        [JsonProperty(PortalPermissionNames.SeePii)]
        public bool SeePii { get; set; }
    }

    public class PortalRoleNames
    {
        [JsonProperty(PortalPermissionNames.Administration)]
        public string Administration { get; set; } = PortalRoles.Administration;

        [JsonProperty(PortalPermissionNames.SeePii)]
        public string SeePii { get; set; } = PortalRoles.SeePii;
    }
}
