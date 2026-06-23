using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Redis;
using System.Collections.Generic;
using System.Net;
using System.Runtime.Caching;
using System.Threading.Tasks;
using System.Web.Mvc;
using Web.AnalyticsWeb.Models;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        public async Task<ActionResult> Index()
        {
            // Load most recent status
            using (var db = new AnalyticsEntitiesContext())
            {
                var appConfig = new AppConfig();
                var cache = CacheConnectionManager.GetConnectionManager(appConfig.ConnectionStrings.RedisConnectionString, tenantId: appConfig.TenantGUID.ToString(), clientId: appConfig.ClientID, clientSecret: appConfig.ClientSecret);
                var s = await SystemStatus.LoadFrom(db, cache);

                return View(s);
            }
        }
        public ActionResult CredentialsInvalid()
        {
            return View();
        }
        public ActionResult AdminApp()
        {
            // Serve the built admin SPA's index.html directly. This action exists (rather than
            // letting IIS serve the static file) so the page is protected by [Authorize] / OIDC
            // auth - a raw static file would bypass it. The SPA's hashed JS/CSS assets referenced
            // by this index.html are then loaded as ordinary static files under
            // /Scripts/admin-app/build/.
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

        // Back-compat: the SPA used to live at /TeamsAuthApp when it only did Teams permission grants.
        public ActionResult TeamsAuthApp()
        {
            return RedirectToAction("AdminApp");
        }

    }
}