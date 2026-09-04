using Common.Entities;
using Common.Entities.Entities.AuditLog;
using Microsoft.Extensions.Logging;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    public class ExchangeAuditLogContent : AbstractAuditLogContent
    {

        public override async Task<bool> ProcessExtendedProperties(SaveSession saveBatch, CommonAuditEvent relatedAuditEvent, ILogger logger)
        {
            var related = await saveBatch.Database.exchange_events
                .Include(m => m.Properties.Select(p => p.name))
                .Include(m => m.Properties.Select(p => p.value))
                .Where(m => m.EventID == this.Id)
                .SingleOrDefaultAsync();
            var props = GetPropertiesAndValues(saveBatch);
            foreach (var name in props.Keys)
            {
                var value = props[name];
                if (!related.Properties.Any(p =>
                    string.Equals(p.name?.name, name.name, System.StringComparison.Ordinal)
                    && string.Equals(p.value?.value, value.value, System.StringComparison.Ordinal)))
                {
                    related.Properties.Add(new ExchangeExtendedProperties() { name = name, value = value });
                }
            }

            return props.Count > 0;
        }
    }
}
