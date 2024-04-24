using Common.Entities.Entities.Teams;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Teams
{
    [Table("teams_channels")]
    public class TeamChannel : TeamRelatedEntity, IGraphEntity
    {
        public TeamChannel()
        {
        }

        [MaxLength(100)]
        [Column("graph_id")]
        [Required]
        public string GraphID { get; set; }


        [Column("name")]
        [MaxLength(100)]
        public string Name { get; set; }

        public List<TeamsUserReaction> Reactions { get; set; } = new List<TeamsUserReaction>();


        public List<ChannelStatsLog> DailyStats { get; set; } = new List<ChannelStatsLog>();
    }
}
