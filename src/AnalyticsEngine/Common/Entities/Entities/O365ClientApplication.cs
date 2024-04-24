using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    [Table("o365_client_applications")]
    public class O365ClientApplication : AbstractEFEntityWithName
    {
        // https://techcommunity.microsoft.com/t5/microsoft-stream-blog/global-admin-pro-tip-learn-how-to-build-video-analytics/ba-p/365267
        [Column("client_application_id")]
        public Guid ClientApplicationId { get; set; }

        public static Guid UNKNOWN_CLIENT_APP_ID { get { return new Guid("00000000-0000-0000-0000-000000000000"); } }
    }
}
