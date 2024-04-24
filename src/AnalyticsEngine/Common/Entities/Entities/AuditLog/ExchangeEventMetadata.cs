using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.AuditLog
{
    [Table("event_meta_exchange")]
    public class ExchangeEventMetadata : BaseExtendedPropertiesEvent<ExchangeExtendedProperties>
    {
        public string object_id { get; set; }

    }
}
