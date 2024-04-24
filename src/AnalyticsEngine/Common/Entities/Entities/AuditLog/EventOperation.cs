
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// The operation done that generated the audit-event. FilePreviewed, FileCheckedOut, New-Mailbox, etc
    /// </summary>
    /// 
    [Table("event_operations")]
    public partial class EventOperation : AbstractEFEntity
    {
        public EventOperation()
        {
        }

        [Column("operation_name")]
        public string Name { get; set; }

    }
}
