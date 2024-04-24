using Common.Entities.Teams;
using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.ActivityReports
{
    /// <summary>
    /// One of the activity reports in Graph
    /// </summary>
    public abstract class AbstractUsageActivityLog : AbstractEFEntity, IDateLog
    {
        [Column("date")]
        public DateTime Date { get; set; }

        [Column("last_activity_date")]
        public DateTime? LastActivityDate { get; set; }

        [NotMapped]
        public abstract int AssociatedLookupId { get; set; }

        public override string ToString()
        {
            return $"{this.GetType().Name} - {Date}";
        }
    }

    /// <summary>
    /// One of the activity reports in Graph
    /// </summary>
    public class UserRelatedAbstractUsageActivity : AbstractUsageActivityLog, IUserRelatedEntity
    {

        public User User { get; set; }

        [ForeignKey(nameof(User))]
        [Column("user_id")]
        public int UserID { get; set; }
        public override int AssociatedLookupId { get => UserID; set => UserID = value; }
    }
}
