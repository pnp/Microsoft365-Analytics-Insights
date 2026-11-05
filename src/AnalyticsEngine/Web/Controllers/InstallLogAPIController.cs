using Common.Entities;
using Common.Entities.Installer;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// The install log: the history of configurations applied to the solution (the
    /// <c>sys_configs</c> table). The most recent entry is the current configuration.
    /// </summary>
    [Authorize]
    [RequirePortalPermission(PortalPermission.Administration)]
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

                return Ok(PrepareForDisplay(rows));
            }
        }

        /// <summary>
        /// Removes every secret from the configuration snapshots, and marks the newest entry as the current one.
        /// </summary>
        /// <remarks>
        /// Done here, on the way out, because installers before this build recorded the encrypted SQL administrator
        /// password and both app registration secrets in every snapshot, and those rows stay in the table. Administration
        /// does not grant those credentials. A snapshot that is not valid JSON is withheld whole rather than shown raw.
        /// </remarks>
        internal static List<InstallLogEntryModel> PrepareForDisplay(List<InstallLogEntryModel> rows)
        {
            foreach (var row in rows)
            {
                row.ConfigJson = InstallConfigSnapshotRedactor.Redact(row.ConfigJson);
            }

            if (rows.Count > 0)
            {
                rows[0].IsCurrent = true;
            }

            return rows;
        }
    }
}
