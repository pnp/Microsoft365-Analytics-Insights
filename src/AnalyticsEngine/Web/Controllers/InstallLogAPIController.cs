using Common.Entities;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// The install log: the history of configurations applied to the solution (the
    /// <c>sys_configs</c> table). The most recent entry is the current configuration.
    /// </summary>
    [Authorize]
    [RoutePrefix("api/InstallLog")]
    public class InstallLogAPIController : ApiController
    {
        // Cap how many history rows we return; the table grows by one per install/upgrade.
        private const int MaxEntries = 200;

        // GET: api/InstallLog
        [HttpGet]
        [Route("")]
        public async Task<IHttpActionResult> Get()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var rows = await db.ConfigStates
                    .OrderByDescending(c => c.DateApplied)
                    .Take(MaxEntries)
                    .Select(c => new InstallLogEntryModel
                    {
                        Id = c.ID,
                        DateApplied = c.DateApplied,
                        InstalledByUser = c.InstalledByUser,
                        Messages = c.Messages,
                        ConfigJson = c.ConfigJson,
                    })
                    .ToListAsync();

                // The newest applied configuration is the current one.
                if (rows.Count > 0)
                {
                    rows[0].IsCurrent = true;
                }

                return Ok(rows);
            }
        }
    }
}
