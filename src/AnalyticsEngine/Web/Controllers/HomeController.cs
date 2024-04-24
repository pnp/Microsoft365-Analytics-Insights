using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Redis;
using System.Collections.Generic;
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
            var db = new AnalyticsEntitiesContext();

            var cache = CacheConnectionManager.GetConnectionManager(new AppConfig().ConnectionStrings.RedisConnectionString);
            var s = await SystemStatus.LoadFrom(db, cache);

            return View(s);
        }
        public ActionResult CredentialsInvalid()
        {
            return View();
        }
        public ActionResult TeamsAuthApp()
        {
            // Inject content from react output
            var cache = MemoryCache.Default;
            var fileContents = cache["filecontents"] as string;

            if (fileContents == null)
            {
                CacheItemPolicy policy = new CacheItemPolicy();

                List<string> filePaths = new List<string>();
                string templateFile = Server.MapPath("~/Scripts/teams-permission-grant/build/index.html");

                filePaths.Add(templateFile);

                policy.ChangeMonitors.Add(new
                HostFileChangeMonitor(filePaths));

                // Fetch the file contents.  
                fileContents = System.IO.File.ReadAllText(templateFile);

#if !DEBUG
                cache.Set("filecontents", fileContents, policy);
#endif

            }

            this.ViewBag.Contents = fileContents;
            return View();
        }

    }
}