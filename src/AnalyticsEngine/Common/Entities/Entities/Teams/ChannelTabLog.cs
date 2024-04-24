using Common.Entities.Teams;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Teams
{
    /// <summary>
    /// List of tabs on a channel, on a given date.
    /// </summary>
    [Table("teams_channel_tabs_log")]
    public class ChannelTabLog : ChannelRelatedLogEntity
    {

        [ForeignKey(nameof(TabDefinition))]
        [Column("tab_id")]
        public int TabID { get; set; }
        public TeamTabDefinition TabDefinition { get; set; }
    }
}
