using Common.Entities;
using Microsoft.Extensions.Logging;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    public class GeneralAuditLogContent : AbstractAuditLogContent
    {
        public override async Task<bool> ProcessExtendedProperties(SaveSession saveBatch, CommonAuditEvent relatedAuditEvent, ILogger logger)
        {
            var generalEvent = await saveBatch.Database.general_audit_events.Where(m => m.EventID == this.Id).SingleOrDefaultAsync();

            // The staging merge only writes event_meta_general for workloads it doesn't have a dedicated
            // metadata table for. If the row isn't there this event belongs to one of those workloads (or its
            // staging row was skipped), so there's nothing to annotate.
            if (generalEvent == null)
            {
                return false;
            }

            generalEvent.workload = this.Workload;
            generalEvent.json = this.OriginalImportFileContents;

            return true;
        }
    }
}
