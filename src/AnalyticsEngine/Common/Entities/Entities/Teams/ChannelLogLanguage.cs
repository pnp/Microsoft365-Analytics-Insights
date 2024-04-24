using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.Teams
{
    /// <summary>
    /// Ties a language to a channel-log
    /// </summary>
    [Table("teams_channel_stats_log_langs")]
    public class ChannelLogLanguage : AbstractEFEntity
    {
        [ForeignKey("Language")]
        [Column("language_id")]
        public int LanguageID { get; set; }
        public Language Language { get; set; }

        [ForeignKey("ChannelStatsLog")]
        [Column("channel_stats_log_id")]
        public int ChannelStatsLogID { get; set; }
        public ChannelStatsLog ChannelStatsLog { get; set; }
    }
}
