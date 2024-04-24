using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.AuditLog
{
    [Table("audit_event_prop_names")]
    public class AuditPropertyName
    {
        public int id { get; set; }

        public string name { get; set; }
    }
}
