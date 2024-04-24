using Common.Entities.Teams;
using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Teams
{
    [Table("teams_user_channel_reactions")]
    public class TeamsUserReaction : AbstractEFEntity, IUserRelatedEntity, IDateLog
    {

        [Column("reaction_id")]
        [ForeignKey(nameof(Reaction))]
        public int? ReactionID { get; set; }

        public TeamsReactionType Reaction { get; set; }

        [Column("user_id")]
        [ForeignKey(nameof(User))]
        public int UserID { get; set; }
        public User User { get; set; }


        [Column("channel_id")]
        [ForeignKey(nameof(Channel))]
        public int ChannelID { get; set; }
        public TeamChannel Channel { get; set; }

        [Column("date")]
        public DateTime Date { get; set; }
    }

    [Table("teams_reaction_types")]
    public class TeamsReactionType : AbstractEFEntityWithName
    {
    }
}
