using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.AuditLog
{
    [Table("event_meta_stream")]
    public class StreamEventMetada : BaseOfficeEvent
    {
        public StreamVideo Video { get; set; }



        public O365ClientApplication ClientApplication { get; set; }

        [ForeignKey(nameof(ClientApplication))]
        [Column("o365_client_application_id")]
        public int ClientAppID { get; set; }

        [ForeignKey(nameof(Video))]
        [Column("video_id")]
        public int VideoID { get; set; }
    }
}
