using Common.Entities;
using Common.Entities.Entities.AuditLog;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    public class ExchangeAuditLogContent : AbstractAuditLogContent
    {

        public override async Task<bool> ProcessExtendedProperties(SaveSession saveBatch, Office365Event relatedAuditEvent)
        {
            var related = await saveBatch.Database.exchange_events.Where(m => m.EventID == this.Id).SingleOrDefaultAsync();
            var props = GetPropertiesAndValues(saveBatch);
            foreach (var name in props.Keys)
            {
                // Add new propery with lookups
                (related as ExchangeEventMetadata).Properties.Add(new ExchangeExtendedProperties() { name = name, value = props[name] });
            }

            return props.Count > 0;
        }
    }
}
