using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Cookies;
using Microsoft.Owin.Security.OpenIdConnect;
using System.Web;
using System.Web.Mvc;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    public class AccountController : Controller
    {

        [HttpGet]
        public ActionResult CredentialsInvalid()
        {
            return View();
        }

        public void SignIn()
        {
            // Send an OpenID Connect sign-in request.
            if (!Request.IsAuthenticated)
            {
                HttpContext.GetOwinContext().Authentication.Challenge(new AuthenticationProperties { RedirectUri = "/" },
                    OpenIdConnectAuthenticationDefaults.AuthenticationType);
            }
        }

        /// <summary>
        /// Asks Entra ID for the delegated Teams permissions that Teams deep analytics needs (issue #670).
        /// </summary>
        /// <remarks>
        /// Signing in no longer requests these, so a tenant that hasn't granted them - often because it doesn't use
        /// Teams deep analytics - can still use the portal. The Teams permissions page links here when the site has
        /// no Graph token for the admin. This re-runs the OIDC challenge with the Teams scopes. The callback redeems
        /// the code for them (<see cref="DelegatedGraphTokenCapture"/>) and returns to the Teams permissions page,
        /// with an outcome key if Entra ID said no. The return address is fixed, so this can't be used as an open
        /// redirect.
        /// </remarks>
        [Authorize]
        [RequirePortalMvcPermission(PortalPermission.Administration)]
        public void ConnectTeams()
        {
            HttpContext.GetOwinContext().Authentication.Challenge(
                DelegatedGraphConsent.CreateTeamsConnectProperties(),
                OpenIdConnectAuthenticationDefaults.AuthenticationType);
        }

        [HttpGet]
        [Authorize]
        [RequirePortalMvcPermission(PortalPermission.Administration)]
        public ActionResult ConnectAgentCosts(string intent)
        {
            var properties = AgentCostConsent.ReadIntent(intent, User as System.Security.Claims.ClaimsPrincipal);
            if (properties == null) return new HttpStatusCodeResult(403);
            HttpContext.GetOwinContext().Authentication.Challenge(properties, AgentCostConsent.AuthenticationType);
            return new HttpUnauthorizedResult();
        }

        public void SignOut()
        {
            string callbackUrl = Url.Action("SignOutCallback", "Account", routeValues: null, protocol: Request.Url.Scheme);

            HttpContext.GetOwinContext().Authentication.SignOut(
                new AuthenticationProperties { RedirectUri = callbackUrl },
                OpenIdConnectAuthenticationDefaults.AuthenticationType, CookieAuthenticationDefaults.AuthenticationType);
        }

        public ActionResult SignOutCallback()
        {
            if (Request.IsAuthenticated)
            {
                // Redirect to home page if the user is authenticated.
                return RedirectToAction("Index", "Home");
            }

            return View();
        }
    }
}
