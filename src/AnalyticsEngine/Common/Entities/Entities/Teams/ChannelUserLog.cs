using Common.Entities.Teams;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Teams
{
    /// <summary>
    /// User activity for a team channel, on a given date.
    /// </summary>
    [Table("teams_channel_user_log")]
    public class ChannelUserLog : ChannelRelatedLogEntity, IUserRelatedEntity
    {

        public int Posts { get; set; }
        public int Replies { get; set; }
        public int MentionsMade { get; set; }
        public int MentionsIncluded { get; set; }

        [ForeignKey(nameof(User))]
        [Column("user_id")]
        public int UserID { get; set; }
        public User User { get; set; }
    }
}
