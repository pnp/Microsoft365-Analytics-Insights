using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    public class AccountController : Controller
    {
        [HttpGet]
        public IActionResult CredentialsInvalid()
        {
            return View();
        }

        /// <summary>
        /// Sends an OpenID Connect sign-in request.
        /// </summary>
        public IActionResult SignIn()
        {
            if (User?.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction("Index", "Home");
            }

            return Challenge(
                new AuthenticationProperties { RedirectUri = "/" },
                OpenIdConnectDefaults.AuthenticationScheme);
        }

        /// <summary>
        /// Asks Entra ID for the delegated Teams permissions that Teams deep analytics needs (issue #670).
        /// </summary>
        /// <remarks>
        /// Ordinary sign-in no longer requests these scopes. This marked challenge returns only to the
        /// Teams permissions page, so it cannot be used as an open redirect.
        /// </remarks>
        [Authorize]
        [RequirePortalPermission(PortalPermission.Administration)]
        public IActionResult ConnectTeams()
        {
            return Challenge(
                DelegatedGraphConsent.CreateTeamsConnectProperties(),
                OpenIdConnectDefaults.AuthenticationScheme);
        }

        /// <summary>
        /// Signs out of both the local cookie and the identity provider, then returns to
        /// <see cref="SignOutCallback"/>.
        /// </summary>
        /// <remarks>
        /// <c>new</c> is deliberate: this is the routed <c>/Account/SignOut</c> action and it
        /// intentionally shadows <see cref="ControllerBase.SignOut()"/>, whose parameterless form would
        /// sign out of the default scheme only. The body still calls the base overload that takes the
        /// properties and both scheme names. Declaring the hiding keeps CS0114 from masking a genuine
        /// accidental shadow elsewhere later.
        /// </remarks>
        public new IActionResult SignOut()
        {
            var callbackUrl = Url.Action(nameof(SignOutCallback), "Account", values: null, protocol: Request.Scheme);

            return SignOut(
                new AuthenticationProperties { RedirectUri = callbackUrl },
                CookieAuthenticationDefaults.AuthenticationScheme,
                OpenIdConnectDefaults.AuthenticationScheme);
        }

        public IActionResult SignOutCallback()
        {
            if (User?.Identity?.IsAuthenticated == true)
            {
                // Redirect to home page if the user is authenticated.
                return RedirectToAction("Index", "Home");
            }

            return View();
        }
    }
}
