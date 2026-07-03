using System.Collections.Generic;
using System.Net;
using System.Runtime.Caching;
using System.Web.Mvc;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        // Root of the site. The whole admin experience (home/system status, Teams permissions,
        // user lookup) is now the SPA, so "/" serves it. It's served through this [Authorize]'d
        // action (rather than as a static file) so OIDC sign-in still gates access; the SPA then
        // gets a Graph token via SiteTokenAPI. The system-status data the old home page rendered
        // server-side now comes from api/SystemStatus.
        public ActionResult Index()
        {
            return ServeAdminApp();
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
        /// Serves the built admin SPA's index.html. The SPA's hashed JS/CSS assets referenced by
        /// it are then loaded as ordinary static files under /Scripts/admin-app/build/.
        /// </summary>
        private ActionResult ServeAdminApp()
        {
            var cache = MemoryCache.Default;
            var fileContents = cache["adminAppIndexHtml"] as string;

            if (fileContents == null)
            {
                string indexFile = Server.MapPath("~/Scripts/admin-app/build/index.html");

                if (!System.IO.File.Exists(indexFile))
                {
                    return new HttpStatusCodeResult(HttpStatusCode.NotFound,
                        "The admin app has not been built. Run 'npm run build' in Scripts/admin-app (or build the Web project).");
                }

                // Fetch the file contents.
                fileContents = System.IO.File.ReadAllText(indexFile);

#if !DEBUG
                var policy = new CacheItemPolicy();
                policy.ChangeMonitors.Add(new HostFileChangeMonitor(new List<string> { indexFile }));
                cache.Set("adminAppIndexHtml", fileContents, policy);
#endif
            }

            return Content(fileContents, "text/html");
        }
    }
}