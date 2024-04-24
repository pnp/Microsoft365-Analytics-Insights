using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.AuditLog
{
    [Table("audit_event_prop_vals")]
    public class AuditPropertyValue
    {

        public int id { get; set; }

        public string value { get; set; }
    }
}
