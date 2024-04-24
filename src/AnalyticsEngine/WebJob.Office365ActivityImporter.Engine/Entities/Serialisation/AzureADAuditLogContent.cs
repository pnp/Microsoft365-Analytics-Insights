using Common.Entities;
using Common.Entities.Entities.AuditLog;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    public class AzureADAuditLogContent : AbstractAuditLogContent
    {

        public override async Task<bool> ProcessExtendedProperties(SaveSession saveBatch, Office365Event relatedAuditEvent)
        {
            var related2 = await saveBatch.Database.azure_ad_events.Where(m => m.EventID == this.Id).SingleOrDefaultAsync();

            // Read each extended property
            var props = GetPropertiesAndValues(saveBatch);
            foreach (var name in props.Keys)
            {
                // Add new propery with lookups
                related2.Properties.Add(new AzureADExtendedProperties() { name = name, value = props[name] });
            }

            return props.Count > 0;
        }
    }
}
