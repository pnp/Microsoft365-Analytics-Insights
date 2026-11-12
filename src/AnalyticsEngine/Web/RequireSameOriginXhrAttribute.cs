using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;

namespace Web.AnalyticsWeb
{
    /// <summary>
    /// Refuses an API call unless it came from the portal's own scripts on this origin. This is the test behind the
    /// site's cross-site request forgery (CSRF) protection, which <see cref="SameOriginStateChangeFilter"/> applies to
    /// every state-changing Web API action; the attribute on an action states the same requirement explicitly.
    /// </summary>
    /// <remarks>
    /// The API authenticates with the site's auth cookie, and a browser attaches that cookie to a request whichever
    /// page started it. Without this check, any page the signed-in admin happened to visit could submit a form to
    /// such an action and have it run with their session.
    ///
    /// <para>Two checks. Either is enough on its own in a current browser; together they cover older ones too:</para>
    /// <list type="bullet">
    /// <item><c>X-Requested-With: XMLHttpRequest</c>, which the portal's <c>apiFetch</c> sends on every call. An HTML
    /// form cannot set a header at all, and a script on another origin can only add one after a CORS preflight. This
    /// site's CORS policy (<see cref="AllowCorsForOrgUrlsAttribute"/>) never allows credentials, so the browser
    /// refuses to send the cookie with such a request. A request that carries both the header and the cookie can
    /// only have come from this origin.</item>
    /// <item><c>Sec-Fetch-Site</c>, which every current browser sets itself and a page cannot forge. When present it
    /// must say <c>same-origin</c>. Other subdomains of the same site are refused as well: on a shared host such as
    /// <c>azurewebsites.net</c> they belong to someone else.</item>
    /// </list>
    /// A refused request gets a bare 403 and the action does not run. The portal always sends the header, so a
    /// genuine admin never sees it.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class RequireSameOriginXhrAttribute : Attribute, IAuthorizationFilter
    {
        internal const string RequestedWithHeader = "X-Requested-With";
        internal const string RequestedWithValue = "XMLHttpRequest";
        internal const string FetchSiteHeader = "Sec-Fetch-Site";

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            if (!IsSameOriginPortalRequest(context.HttpContext.Request))
            {
                context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            }
        }

        /// <summary>The ASP.NET Core request, as the filter sees it.</summary>
        internal static bool IsSameOriginPortalRequest(HttpRequest request)
        {
            if (request == null)
            {
                return false;
            }

            return IsSameOriginScriptRequest(
                request.Headers[RequestedWithHeader].Select(v => v?.Trim()),
                request.Headers[FetchSiteHeader].Select(v => v?.Trim()));
        }

        internal static bool IsSameOriginScriptRequest(HttpRequestMessage request)
        {
            if (request == null)
            {
                return false;
            }

            return IsSameOriginScriptRequest(HeaderValues(request, RequestedWithHeader), HeaderValues(request, FetchSiteHeader));
        }

        private static bool IsSameOriginScriptRequest(IEnumerable<string> requestedWith, IEnumerable<string> fetchSiteValues)
        {
            if (!requestedWith.Any(v => string.Equals(v, RequestedWithValue, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var fetchSite = fetchSiteValues.ToList();
            return fetchSite.Count == 0
                || fetchSite.All(v => string.Equals(v, "same-origin", StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<string> HeaderValues(HttpRequestMessage request, string name)
        {
            return request.Headers.TryGetValues(name, out var values)
                ? values.Select(v => v?.Trim())
                : Enumerable.Empty<string>();
        }
    }

    /// <summary>
    /// The MVC counterpart, for the account actions a page starts with a form POST: sign-out and the Teams connection.
    /// Refuses the request with a 403 unless a page on this origin sent it.
    /// </summary>
    /// <remarks>
    /// Both were GET actions, so any page the signed-in admin visited could sign them out, or - once consent had been
    /// granted, when Entra ID asks nothing - run the Teams connection, just by linking to them (#853, R6-8). They are now
    /// <c>[HttpPost]</c>. A form on another site can still POST, and a browser that doesn't default the auth cookie to
    /// <c>SameSite=Lax</c> sends the cookie with it, so the request must also say where it came from:
    /// <c>Sec-Fetch-Site: same-origin</c>, which every current browser sends and a page cannot forge; or, from a browser
    /// that doesn't send that, an <c>Origin</c> - failing that a <c>Referer</c> - on this host. A request with none of
    /// them is refused.
    ///
    /// <para>net10: an ASP.NET Core authorization filter. <see cref="SameOriginStateChangeFilter"/> leaves the MVC
    /// controllers alone (their form posts carry no <c>X-Requested-With</c>), so on those two actions this is the
    /// defence. The Web API 2 build's refusal is a bare 403 with a status description; ASP.NET Core has no reason
    /// phrase to set, so the same sentence is the body.</para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class RequireSameOriginFormPostAttribute : Attribute, IAuthorizationFilter
    {
        internal const string RefusedMessage = "Refused: this request did not come from this site.";

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (!IsSameOrigin(context.HttpContext?.Request))
            {
                context.Result = new ContentResult
                {
                    StatusCode = StatusCodes.Status403Forbidden,
                    Content = RefusedMessage,
                    ContentType = "text/plain; charset=utf-8",
                };
            }
        }

        internal static bool IsSameOrigin(HttpRequest request)
        {
            // The host only: behind App Service's front end, or another proxy, the scheme and port the app sees need not
            // be the browser's
            var here = request?.Host.Host;
            if (string.IsNullOrEmpty(here))
            {
                return false;
            }

            var fetchSite = request.Headers[RequireSameOriginXhrAttribute.FetchSiteHeader].ToString();
            if (!string.IsNullOrWhiteSpace(fetchSite))
            {
                return string.Equals(fetchSite.Trim(), "same-origin", StringComparison.OrdinalIgnoreCase);
            }

            var source = request.Headers.Origin.ToString();
            if (string.IsNullOrWhiteSpace(source))
            {
                source = request.Headers.Referer.ToString();
            }
            return Uri.TryCreate(source?.Trim(), UriKind.Absolute, out var from)
                && string.Equals(from.Host, here, StringComparison.OrdinalIgnoreCase);
        }
    }
}
