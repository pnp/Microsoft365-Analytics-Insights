using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.AuditLog
{
    [Table("event_meta_general")]
    public class GeneralEventMetada : BaseOfficeEvent
    {
        public string json { get; set; }

        public string workload { get; set; }

    }
}
