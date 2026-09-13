using Microsoft.AspNetCore.Http;
using System;

namespace Web.AnalyticsWeb
{
    /// <summary>
    /// Decides which requests are SPA API calls rather than top-level browser navigations.
    /// </summary>
    /// <remarks>
    /// Ported verbatim in behaviour from the OWIN <c>Startup.Auth</c> implementation. The distinction
    /// matters because an expired session must produce a status code the portal can act on for a
    /// background call, but must still redirect to sign-in for a navigation - the Copilot adoption CSV
    /// and workbook exports are plain <c>&lt;a href&gt;</c> links to <c>[Authorize]</c>'d /api routes and
    /// need the redirect to deliver the download.
    /// </remarks>
    public static class AuthRequestRules
    {
        /// <summary>
        /// Response header set on the 401 that replaces the sign-in redirect for API calls, so the SPA can
        /// tell "your session has expired, re-authenticate" apart from an authenticated-but-unauthorised
        /// 401 (e.g. SiteTokenAPI reporting that it has no Graph refresh token for this session).
        /// </summary>
        public const string SessionExpiredHeader = "X-Auth-Session-Expired";

        /// <summary>
        /// True for calls the SPA makes with fetch/XHR rather than a top-level navigation.
        /// </summary>
        public static bool IsApiRequest(HttpRequest request)
        {
            if (request == null) return false;

            // The portal's apiFetch always sends this and a browser navigation never does, so it is the
            // one unambiguous signal that a script is waiting for a status code.
            if (string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Otherwise fall back to the path - but NOT for a top-level navigation.
            if (IsDocumentNavigation(request)) return false;

            return request.Path.StartsWithSegments("/api");
        }

        /// <summary>
        /// True when the browser is navigating the top-level document (a link, address bar or form post)
        /// rather than making a background request.
        /// </summary>
        /// <remarks>
        /// Uses the Fetch Metadata request headers, which every current browser sends. On a browser old
        /// enough to omit them this returns false and the <c>/api</c> path rule applies as before.
        /// </remarks>
        private static bool IsDocumentNavigation(HttpRequest request)
        {
            return string.Equals(request.Headers["Sec-Fetch-Mode"], "navigate", StringComparison.OrdinalIgnoreCase)
                || string.Equals(request.Headers["Sec-Fetch-Dest"], "document", StringComparison.OrdinalIgnoreCase);
        }
    }
}
