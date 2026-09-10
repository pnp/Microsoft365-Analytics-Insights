using Common.Entities;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    public class ExchangeAuditLogContent : AbstractAuditLogContent
    {
        /// <summary>
        /// Exchange events have no per-event metadata left to write. The event_meta_exchange row
        /// (including object_id) is created by the staging merge in "Insert Activity from Staging Table.sql",
        /// and the audit_event_exchange_props / audit_event_prop_names / audit_event_prop_vals tables that
        /// used to store the record's ExtendedProperties were retired - nobody read them.
        /// </summary>
        public override Task<bool> ProcessExtendedProperties(SaveSession saveBatch, CommonAuditEvent relatedAuditEvent, ILogger logger)
        {
            return Task.FromResult(false);
        }
    }
}
