using Common.Entities;
using System.Collections.Generic;
using System.Net;
using System.Runtime.Caching;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        private readonly Microsoft.AspNetCore.Hosting.IWebHostEnvironment _env;

        public HomeController(Microsoft.AspNetCore.Hosting.IWebHostEnvironment env)
        {
            _env = env;
        }
        // Read and written through this single constant: the two used to differ, so the cached copy
        // was stored under a key nothing ever read and index.html was re-read from disk every request.
        // Internal so the host tests can clear the process-wide cache before serving their own page.
        internal const string PortalIndexCacheKey = "portalIndexHtml";

        // Root of the site. The whole admin experience (home/system status, Teams permissions,
        // user lookup) is now the SPA, so "/" serves it. It's served through this [Authorize]'d
        // action (rather than as a static file) so OIDC sign-in still gates access; the SPA then
        // gets a Graph token via SiteTokenAPI. The system-status data the old home page rendered
        // server-side now comes from api/SystemStatus.
        public ActionResult Index()
        {
            return ServePortalApp();
        }

        public ActionResult CredentialsInvalid()
        {
            return View();
        }

        // Back-compat aliases for the SPA's old URLs.
        public ActionResult AdminApp()
        {
            return RedirectToAction("Index");
        }

        public ActionResult TeamsAuthApp()
        {
            return RedirectToAction("Index");
        }

        /// <summary>
        /// Serves the built portal SPA's index.html. The SPA's hashed JS/CSS assets referenced by
        /// it are then loaded from wwwroot/assets as ordinary static files under /assets/.
        /// </summary>
        private ActionResult ServePortalApp()
        {
            var cache = MemoryCache.Default;
            var fileContents = cache[PortalIndexCacheKey] as string;

            if (fileContents == null)
            {
                string indexFile = System.IO.Path.Combine(_env.WebRootPath ?? _env.ContentRootPath, "index.html");

                if (!System.IO.File.Exists(indexFile))
                {
                    return StatusCode((int)HttpStatusCode.NotFound, "The portal SPA has not been built. Run 'npm run build' in Scripts/portal (or build the Web project).");
                }

                // Fetch the file contents.
                fileContents = InjectBuildLabel(System.IO.File.ReadAllText(indexFile), BuildConstants.BuildLabel);

#if !DEBUG
                var policy = new CacheItemPolicy();
                policy.ChangeMonitors.Add(new HostFileChangeMonitor(new List<string> { indexFile }));
                cache.Set(PortalIndexCacheKey, fileContents, policy);
#endif
            }

            // index.html names content-hashed chunks, so a browser holding a cached copy after a
            // redeploy asks for chunks that no longer exist ("Failed to fetch dynamically imported
            // module"). It must always be revalidated; the hashed assets it points at stay cacheable.
            Response.Headers.CacheControl = "no-cache, no-store";

            return Content(fileContents, "text/html");
        }

        /// <summary>
        /// The token in the portal's index.html that stands in for the running build's label.
        /// Matches the <c>__name__</c> convention the rest of the build uses for substitutions.
        /// </summary>
        internal const string BuildLabelPlaceholder = "__BuildLabel__";

        /// <summary>
        /// Stamps the running build's label into the portal's index.html.
        /// </summary>
        /// <remarks>
        /// The SPA prints the build label in the footer of a printed report, so it has to be in
        /// the page before <c>window.print()</c> runs - which rules out fetching it. It also
        /// cannot come from api/SystemStatus, which COUNT(*)s whole tables and is far too
        /// expensive to call on every page just to name a version. index.html is already served
        /// through this action, so the label is substituted in here: free at runtime, and correct
        /// for any deployment rather than only for builds the CI pipeline happened to patch.
        ///
        /// The value is JavaScript-encoded because it lands inside a quoted string literal in an
        /// inline script. It is a build constant rather than user input today, but a substitution
        /// into executable script is not somewhere to rely on that staying true.
        /// </remarks>
        internal static string InjectBuildLabel(string html, string buildLabel)
        {
            if (html == null) return null;
            // System.Web.HttpUtility ships in the .NET shared framework (System.Web.HttpUtility.dll) with
            // the same JavaScriptStringEncode as .NET Framework; it is the only System.Web type used here.
            return html.Replace(BuildLabelPlaceholder, System.Web.HttpUtility.JavaScriptStringEncode(buildLabel ?? string.Empty));
        }
    }
}
