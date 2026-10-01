using Common.Entities.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System;
using System.Collections.Specialized;
using System.Linq;
using System.Security.Claims;
using System.Security.Principal;

namespace Web.AnalyticsWeb.Security
{
    /// <summary>
    /// What a signed-in portal user may see beyond aggregate insights. Issues #660 and #661.
    /// </summary>
    /// <remarks>
    /// Anyone who can sign in to the portal reads aggregate insights, as before. Each permission unlocks
    /// one thing on top of that, and the two are deliberately independent: Administration does not imply
    /// See PII, so a deployment can be run by someone who never sees data about an individual.
    /// </remarks>
    public enum PortalPermission
    {
        /// <summary>The Administration area and the APIs behind it.</summary>
        Administration,

        /// <summary>Data about an identifiable individual: per-person rows, named lists and their exports.</summary>
        SeePii,
    }

    /// <summary>
    /// The Entra ID app roles that grant each <see cref="PortalPermission"/>.
    /// </summary>
    /// <remarks>
    /// App roles rather than delegated scopes: an app role says what a <i>user</i> may do in this
    /// application and is assigned to users or groups on the enterprise application, whereas a delegated
    /// scope says what an <i>app</i> may do on a user's behalf. They are defined on the runtime app
    /// registration (the one the portal signs in with) and arrive in the ID token's <c>roles</c> claim.
    /// The values are part of the product's contract with every deployment's app registration, so
    /// renaming one locks every assigned user out: treat them like a public API.
    /// </remarks>
    public static class PortalRoles
    {
        public const string Administration = "Portal.Administration";
        public const string SeePii = "Portal.SeePII";

        public static string For(PortalPermission permission)
        {
            switch (permission)
            {
                case PortalPermission.Administration: return Administration;
                case PortalPermission.SeePii: return SeePii;
                default: throw new ArgumentOutOfRangeException(nameof(permission), permission, null);
            }
        }
    }

    /// <summary>
    /// Whether the portal checks app roles at all. Read from the <c>EnforcePortalRoles</c> app setting.
    /// </summary>
    public sealed class PortalAccessPolicy
    {
        /// <summary>App setting that switches role checks off when set to <c>false</c>.</summary>
        public const string EnforcementSettingName = "EnforcePortalRoles";

        public static readonly PortalAccessPolicy Enforcing = new PortalAccessPolicy(true);
        public static readonly PortalAccessPolicy NotEnforcing = new PortalAccessPolicy(false);

        private PortalAccessPolicy(bool enforced)
        {
            Enforced = enforced;
        }

        /// <summary>
        /// True when the app roles decide what a user may see. False restores the behaviour from before
        /// the roles existed, where everyone who can sign in holds every permission.
        /// </summary>
        public bool Enforced { get; }

        /// <summary>
        /// Parses the setting. Only an explicit <c>false</c> turns enforcement off: an absent, empty or
        /// mistyped value fails closed, because a typo in a security switch must never widen access.
        /// </summary>
        public static PortalAccessPolicy FromSettings(NameValueCollection settings)
        {
            var raw = settings?[EnforcementSettingName];
            var switchedOff = bool.TryParse(raw?.Trim(), out var enforce) && !enforce;
            return switchedOff ? NotEnforcing : Enforcing;
        }

        /// <summary>The deployment's policy. App Service app settings override appsettings.json at runtime.</summary>
        public static PortalAccessPolicy FromAppSettings()
        {
            var settings = new NameValueCollection
            {
                [EnforcementSettingName] = AnalyticsConfig.AppSettings.Get(EnforcementSettingName),
            };
            return FromSettings(settings);
        }

        /// <summary>What <paramref name="principal"/> may see under this policy.</summary>
        public PortalAccessGrant Evaluate(IPrincipal principal)
        {
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return new PortalAccessGrant(Enforced, administration: false, seePii: false);
            }

            if (!Enforced)
            {
                return new PortalAccessGrant(false, administration: true, seePii: true);
            }

            return new PortalAccessGrant(true,
                administration: HasRole(principal, PortalRoles.Administration),
                seePii: HasRole(principal, PortalRoles.SeePii));
        }

        /// <summary>
        /// Whether the principal carries an app role, however the token handler chose to name the claim.
        /// </summary>
        /// <remarks>
        /// The ID token calls it <c>roles</c>, and <c>JwtSecurityTokenHandler</c>'s default inbound map
        /// rewrites that to <see cref="ClaimTypes.Role"/>. Which of the two ends up in the auth cookie
        /// depends on the handler's settings, so both are accepted rather than betting on one. The value
        /// comparison ignores case: the administrator who types the role controls both sides, and a
        /// capitalisation slip should not read as a missing permission.
        /// </remarks>
        internal static bool HasRole(IPrincipal principal, string role)
        {
            if (principal == null || string.IsNullOrEmpty(role)) return false;
            if (principal.IsInRole(role)) return true;

            return principal is ClaimsPrincipal claims && claims.Claims.Any(c =>
                IsRoleClaimType(c.Type) && string.Equals(c.Value?.Trim(), role, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsRoleClaimType(string type) =>
            string.Equals(type, "roles", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "role", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The permissions one signed-in user holds.</summary>
    public sealed class PortalAccessGrant
    {
        public PortalAccessGrant(bool enforced, bool administration, bool seePii)
        {
            Enforced = enforced;
            Administration = administration;
            SeePii = seePii;
        }

        public bool Enforced { get; }
        public bool Administration { get; }
        public bool SeePii { get; }

        public bool Has(PortalPermission permission)
        {
            switch (permission)
            {
                case PortalPermission.Administration: return Administration;
                case PortalPermission.SeePii: return SeePii;
                default: return false;
            }
        }
    }

    /// <summary>
    /// Resolves the policy for an ASP.NET Core request and evaluates the caller against it.
    /// </summary>
    public static class PortalAccess
    {
        /// <summary>
        /// The policy for a request. A test host can pin one in
        /// <see cref="HttpContext.Items"/> under <c>typeof(PortalAccessPolicy)</c>; production never does,
        /// and reads the app setting.
        /// </summary>
        public static PortalAccessPolicy PolicyFor(HttpRequest request)
        {
            if (request?.HttpContext?.Items.TryGetValue(typeof(PortalAccessPolicy), out var pinned) == true
                && pinned is PortalAccessPolicy policy)
            {
                return policy;
            }

            return PortalAccessPolicy.FromAppSettings();
        }

        public static PortalAccessGrant Evaluate(HttpRequest request, IPrincipal principal)
            => PolicyFor(request).Evaluate(principal);
    }

    /// <summary>
    /// The body of the 403 a caller gets for an endpoint its permissions do not cover.
    /// </summary>
    /// <remarks>
    /// The API reports the fact - which permission, which app role - and the SPA writes the sentence in the
    /// reader's language from <see cref="Code"/> and <see cref="Permission"/>. <see cref="Message"/> is the
    /// English fallback for anything that is not the SPA (a download link, a script), and
    /// <c>serverAuthoredText.test.ts</c> fails if it drifts from the English catalog entry.
    /// </remarks>
    public sealed class PortalPermissionDeniedModel
    {
        public const string ErrorCode = "portalPermissionRequired";

        internal const string AdministrationMessage =
            "This needs the Administration permission. Ask an Entra ID administrator to assign you the Portal.Administration app role.";

        internal const string SeePiiMessage =
            "This shows information about individual people, which needs the See PII permission. Ask an Entra ID administrator to assign you the Portal.SeePII app role.";

        [JsonProperty("code")]
        public string Code { get; set; } = ErrorCode;

        /// <summary><c>administration</c> or <c>seePii</c>: the same names <c>api/PortalAccess</c> uses.</summary>
        [JsonProperty("permission")]
        public string Permission { get; set; }

        /// <summary>The app role that would grant it, so an administrator knows exactly what to assign.</summary>
        [JsonProperty("role")]
        public string Role { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        public static PortalPermissionDeniedModel For(PortalPermission permission)
        {
            return new PortalPermissionDeniedModel
            {
                Permission = PortalPermissionNames.For(permission),
                Role = PortalRoles.For(permission),
                Message = permission == PortalPermission.Administration ? AdministrationMessage : SeePiiMessage,
            };
        }
    }

    /// <summary>
    /// The 403 for a caller whose permissions do not cover an endpoint, in a form that caller can read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// JSON for the SPA and for scripts, whatever <c>Accept</c> they send. Left to content negotiation, Web API
    /// answers a browser navigation - whose <c>Accept</c> ranks <c>application/xml</c> above <c>*/*</c> -
    /// with the body serialised as XML, because the XML formatter is still registered.
    /// </para>
    /// <para>
    /// A document navigation - an export link opened from a bookmark, or after the permission was withdrawn -
    /// gets <see cref="PortalPermissionDeniedModel.Message"/> as plain text instead, because the person who
    /// clicked has to be able to read it. The Copilot Adoption export's not-ready response makes the same call.
    /// </para>
    /// </remarks>
    public static class PortalPermissionDenied
    {
        public static IActionResult Result(HttpRequest request, PortalPermission permission)
        {
            var body = PortalPermissionDeniedModel.For(permission);
            if (request?.HttpContext != null)
            {
                request.HttpContext.Response.Headers.CacheControl = "no-store, private";
            }

            if (IsDocumentNavigation(request))
            {
                return new ContentResult
                {
                    Content = body.Message,
                    ContentType = "text/plain; charset=utf-8",
                    StatusCode = StatusCodes.Status403Forbidden,
                };
            }

            return new JsonResult(body) { StatusCode = StatusCodes.Status403Forbidden };
        }

        /// <summary>The same test the sign-in middleware uses to tell a page load from a script's request.</summary>
        internal static bool IsDocumentNavigation(HttpRequest request)
        {
            if (request == null) return false;
            return HeaderIs(request, "Sec-Fetch-Mode", "navigate") || HeaderIs(request, "Sec-Fetch-Dest", "document");
        }

        private static bool HeaderIs(HttpRequest request, string name, string value)
        {
            return request.Headers.TryGetValue(name, out var values)
                && values.Any(v => string.Equals(v?.Trim(), value, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The wire names of the permissions, shared by the 403 body and <c>api/PortalAccess</c>.</summary>
    public static class PortalPermissionNames
    {
        public const string Administration = "administration";
        public const string SeePii = "seePii";

        public static string For(PortalPermission permission)
            => permission == PortalPermission.Administration ? Administration : SeePii;
    }
}
