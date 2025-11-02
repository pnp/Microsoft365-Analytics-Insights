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
            generalEvent.workload = this.Workload;
            generalEvent.json = this.OriginalImportFileContents;

            return true;
        }
    }
}
