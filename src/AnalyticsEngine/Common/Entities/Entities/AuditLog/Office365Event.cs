using System.ComponentModel.DataAnnotations.Schema;


namespace Common.Entities
{
    /// <summary>
    /// The common event entity for any workload. Workload specific events link back to this.
    /// </summary>
    [Table("audit_events")]
    public class Office365Event
    {
        [Column("id")]
        public System.Guid Id { get; set; }

        [Column("time_stamp")]
        public System.DateTime TimeStamp { get; set; }


        [ForeignKey(nameof(Operation))]
        [Column("operation_id")]
        public int? OperationId { get; set; } = 0;
        public EventOperation Operation { get; set; } = null;

        [ForeignKey(nameof(User))]
        [Column("user_id")]
        public int? UserId { get; set; } = 0;
        public User User { get; set; } = null;

        [Column("event_data")]
        public string EventData { get; set; }
    }
}
