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
    /// Refuses an API call unless it came from the portal's own scripts on this origin. Put it on every action that
    /// changes something: it is the site's cross-site request forgery (CSRF) protection.
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
}
