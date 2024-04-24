using Common.Entities.Entities;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Teams
{

    /// <summary>
    /// Entity with a relation back to a Team
    /// </summary>
    public abstract class TeamRelatedEntity : AbstractEFEntity
    {

        public TeamDefinition Team { get; set; }

        [ForeignKey(nameof(Team))]
        [Column("team_id")]
        public int TeamID { get; set; }

    }

    /// <summary>
    /// Entity with a relation back to a Channel
    /// </summary>
    public abstract class ChannelRelatedLogEntity : AbstractEFEntity, IDateLog
    {

        [Column("date")]
        public DateTime Date { get; set; }

        [ForeignKey(nameof(Channel))]
        [Column("channel_id")]
        public int ChannelID { get; set; }

        public TeamChannel Channel { get; set; }
    }

    /// <summary>
    /// Entity which can be added & deleted from a Team.
    /// </summary>
    public abstract class TeamLog : TeamRelatedEntity, IDateLog
    {
        [Column("date")]
        public DateTime Date { get; set; }
    }


    public abstract class AbstractGraphEFEntityWithName : AbstractEFEntityWithName, IGraphEntity
    {
        /// <summary>
        /// ID given by Graph
        /// </summary>
        [Column("graph_id")]
        [MaxLength(100)]
        [Required]
        public string GraphID { get; set; }
    }

    public interface IDateLog
    {
        DateTime Date { get; set; }
    }

    public interface IGraphEntity
    {
        string GraphID { get; set; }
    }

    public interface IUserRelatedEntity
    {
        [ForeignKey(nameof(User))]
        [Column("user_id")]
        int UserID { get; set; }

        User User { get; set; }
    }
}
