using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities
{
    public abstract class BaseOfficeEvent
    {
        /// <summary>
        /// Foriegn key for "Event" only
        /// </summary>
        [Key]
        [ForeignKey(nameof(AuditEvent))]
        [Column("event_id")]
        public Guid EventID { get; set; }


        public CommonAuditEvent AuditEvent { get; set; }

    }
}
