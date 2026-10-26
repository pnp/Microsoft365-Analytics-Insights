using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities
{
    [Table("online_meetings")]
    public class OnlineMeeting : AbstractEFEntityWithName
    {
        [Column("created")]
        public DateTime CreatedUTC { get; set; }

        /// <summary>
        /// {user object id}_{meeting thread id}, from StringUtils.GetOnlineMeetingId. In SQL it is a UNIQUE nvarchar(850)
        /// (migration UniqueOnlineMeetingIds) although the model leaves it unbounded. The index has IGNORE_DUP_KEY: a raw-SQL
        /// INSERT of an id already stored - even a second NULL - skips that row with a warning, but an EF SaveChanges of one
        /// throws DbUpdateConcurrencyException, because the ignored row is not there to read back. Look the meeting up first.
        /// </summary>
        [Column("meeting_id")]
        public string MeetingId { get; set; } = null;
    }
}
