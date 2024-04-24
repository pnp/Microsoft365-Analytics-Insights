using Common.Entities.Teams;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities
{
    /// <summary>
    /// Association between user & Team
    /// </summary>
    [Table("team_membership_log")]
    public class TeamMembershipLog : TeamLog
    {

        public User User { get; set; }

        [ForeignKey("User")]
        [Column("user_id")]
        public int UserID { get; set; }
    }
}
