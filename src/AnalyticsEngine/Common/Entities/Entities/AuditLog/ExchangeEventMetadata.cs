using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.AuditLog
{
    [Table("event_meta_exchange")]
    public class ExchangeEventMetadata : BaseOfficeEvent
    {
        public string object_id { get; set; }

    }
}
