using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Redis;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Web.Mvc;
using Web.AnalyticsWeb.Models;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        private readonly IWebHostEnvironment _webHostEnvironment;
        private readonly IMemoryCache _cache;

        public HomeController(IWebHostEnvironment webHostEnvironment, IMemoryCache cache)
        {
            _cache = cache;
            _webHostEnvironment = webHostEnvironment;
        }

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
            var fileContents = _cache.Get<string>("filecontents");

            if (fileContents == null)
            {
                string webRootPath = _webHostEnvironment.WebRootPath;
                string contentRootPath = _webHostEnvironment.ContentRootPath;

                var filePaths = new List<string>();
                var templateFile = Path.Combine(webRootPath, "/Scripts/teams-permission-grant/build/index.html");

                filePaths.Add(templateFile);

                // Fetch the file contents.  
                fileContents = System.IO.File.ReadAllText(templateFile);

#if !DEBUG
                _cache.Set("filecontents", fileContents);
#endif

            }

            this.ViewBag.Contents = fileContents;
            return View();
        }
    }
}
