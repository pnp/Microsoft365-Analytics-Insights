using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{

    /// <summary>
    /// Office 365 audit events that have been processed already. 
    /// </summary>
    [Table("ignored_audit_events")]
    public class IgnoredEvent
    {
        [Key]
        public System.Guid event_id { get; set; }
        public System.DateTime processed_timestamp { get; set; }
    }
}
