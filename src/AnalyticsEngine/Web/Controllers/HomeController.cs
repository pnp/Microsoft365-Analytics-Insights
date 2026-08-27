using System.Collections.Generic;
using System.Net;
using System.Runtime.Caching;
using System.Web;
using System.Web.Mvc;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        // Read and written through this single constant: the two used to differ, so the cached copy
        // was stored under a key nothing ever read and index.html was re-read from disk every request.
        private const string PortalIndexCacheKey = "portalIndexHtml";

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
        /// it are then loaded as ordinary static files under /Scripts/portal/build/.
        /// </summary>
        private ActionResult ServePortalApp()
        {
            var cache = MemoryCache.Default;
            var fileContents = cache[PortalIndexCacheKey] as string;

            if (fileContents == null)
            {
                string indexFile = Server.MapPath("~/Scripts/portal/build/index.html");

                if (!System.IO.File.Exists(indexFile))
                {
                    return new HttpStatusCodeResult(HttpStatusCode.NotFound,
                        "The portal SPA has not been built. Run 'npm run build' in Scripts/portal (or build the Web project).");
                }

                // Fetch the file contents.
                fileContents = System.IO.File.ReadAllText(indexFile);

#if !DEBUG
                var policy = new CacheItemPolicy();
                policy.ChangeMonitors.Add(new HostFileChangeMonitor(new List<string> { indexFile }));
                cache.Set(PortalIndexCacheKey, fileContents, policy);
#endif
            }

            // index.html names content-hashed chunks, so a browser holding a cached copy after a
            // redeploy asks for chunks that no longer exist ("Failed to fetch dynamically imported
            // module"). It must always be revalidated; the hashed assets it points at stay cacheable.
            Response.Cache.SetCacheability(HttpCacheability.NoCache);
            Response.Cache.SetNoStore();

            return Content(fileContents, "text/html");
        }
    }
}