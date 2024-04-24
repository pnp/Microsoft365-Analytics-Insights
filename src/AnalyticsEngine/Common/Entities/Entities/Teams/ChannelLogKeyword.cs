using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Teams
{
    /// <summary>
    /// Ties a keyword to a channel-log
    /// </summary>
    [Table("teams_channel_stats_log_keywords")]
    public class ChannelLogKeyword : AbstractEFEntity
    {
        [ForeignKey("KeyWord")]
        [Column("keyword_id")]
        public int KeyWordID { get; set; }
        public KeyWord KeyWord { get; set; }

        [ForeignKey("ChannelStatsLog")]
        [Column("channel_stats_log_id")]
        public int ChannelStatsLogID { get; set; }
        public ChannelStatsLog ChannelStatsLog { get; set; }

        [Column("keyword_count")]
        public int KeyWordCount { get; set; }
    }
}
