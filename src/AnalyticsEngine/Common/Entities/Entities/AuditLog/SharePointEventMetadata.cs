using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.AuditLog
{
    [Table("event_meta_sharepoint")]
    public class SharePointEventMetadata : BaseOfficeEvent
    {
        public virtual SPEventFileExtension file_extension { get; set; }

        public virtual SPEventFileName file_name { get; set; }

        public virtual Url url { get; set; }

        public Web related_web { get; set; }

        public virtual SPEventType item_type { get; set; }
    }
}
