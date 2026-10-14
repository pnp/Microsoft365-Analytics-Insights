using Common.Entities.Agent365;
using Common.Entities.Config;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models.Agent365;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>Serves the latest completed Microsoft Agent 365 package-catalog snapshot.</summary>
    [Authorize]
    [RequirePortalPermission(PortalPermission.Administration)]
    [RoutePrefix("api/Agent365Catalog")]
    public class Agent365PackageCatalogAPIController : ApiController
    {
        private readonly IAgent365PackageCatalogStore _store;

        public Agent365PackageCatalogAPIController() : this(new Agent365PackageCatalogStore())
        {
        }

        public Agent365PackageCatalogAPIController(IAgent365PackageCatalogStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        [HttpGet]
        [Route("")]
        public async Task<IHttpActionResult> Get(int offset = 0, int pageSize = 50, bool neverUsedOnly = false)
        {
            if (offset < 0 || offset > 1000000 || pageSize < 1 || pageSize > 500)
            {
                return BadRequest();
            }

            var config = new AppConfig();
            var health = await _store.GetImportHealthAsync();
            var page = await _store.GetCurrentPageAsync(offset, pageSize, neverUsedOnly);
            return Ok(new Agent365PackageCatalogApiResponse
            {
                ImportEnabled = config.ImportJobSettings.Agent365PackageCatalog,
                LastAttemptUtc = health.LastAttemptUtc,
                LastAttemptCompletedUtc = health.LastAttemptCompletedUtc,
                LastAttemptSucceeded = health.LastAttemptSucceeded,
                LastAttemptError = health.LastAttemptError,
                LastSuccessfulImportUtc = health.LastSuccessfulImportUtc,
                TotalCount = page.TotalCount,
                Offset = offset,
                PageSize = pageSize,
                NeverUsedOnly = neverUsedOnly,
                PackageCount = health.PackageCount,
                NeverUsedCount = health.NeverUsedCount,
                Packages = page.Packages.Select(ToApiPackage).ToList()
            });
        }

        private static Agent365PackageCatalogApiPackage ToApiPackage(Agent365Package package)
        {
            return new Agent365PackageCatalogApiPackage
            {
                PackageId = package.PackageId,
                DisplayName = package.DisplayName,
                PackageType = package.PackageType,
                Platform = package.Platform,
                Publisher = package.Publisher,
                ManifestId = package.ManifestId,
                Version = package.Version,
                IsBlocked = package.IsBlocked,
                LastModifiedUtc = package.LastModifiedUtc,
                LastUsedUtc = package.LastUsedUtc,
                LastUsedDateTimeProvided = package.LastUsedDateTimeProvided,
                KnownNeverUsed = package.LastUsedDateTimeProvided && !package.LastUsedUtc.HasValue,
                ActiveUsers = package.ActiveUsers,
                TotalSessions = package.TotalSessions,
                TotalRunTimeHours = package.TotalRunTimeHours,
                ExceptionRate = package.ExceptionRate,
                SupportedHosts = string.IsNullOrWhiteSpace(package.SupportedHostsJson)
                    ? new List<string>()
                    : JsonConvert.DeserializeObject<List<string>>(package.SupportedHostsJson) ?? new List<string>(),
                Elements = (package.Elements ?? new List<Agent365PackageElement>())
                    .Select(element => new Agent365PackageCatalogApiElement
                    {
                        ElementType = element.ElementType,
                        ElementId = element.ElementId
                    })
                    .ToList()
            };
        }
    }
}
