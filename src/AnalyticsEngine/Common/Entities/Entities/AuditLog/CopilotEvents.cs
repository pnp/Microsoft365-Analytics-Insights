using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.AuditLog
{

    /// <summary>
    /// A copilot interaction event. May not be related to any file or meeting. 
    /// Relates back to a common audit event.
    /// </summary>
    [Table("event_copilot_chats")]
    public class CopilotChat : BaseOfficeEvent
    {
        [Column("app_host")]
        public string AppHost { get; set; } = null;
    }

    /// <summary>
    /// An event with more data specific to copilot. File/meeting/etc.
    /// Links to common copilot chat event, which links to common audit event.
    /// </summary>
    public abstract class BaseCopilotSpecificEvent
    {
        [Key]
        [ForeignKey(nameof(RelatedChat))]
        [Column("copilot_chat_id")]
        public Guid ChatId { get; set; }

        public CopilotChat RelatedChat { get; set; } = null;

        public abstract string GetEventDescription();
    }


    [Table("event_copilot_files")]
    public class CopilotEventMetadataFile : BaseCopilotSpecificEvent
    {
        [ForeignKey(nameof(FileExtension))]
        [Column("file_extension_id")]
        public int? FileExtensionId { get; set; } = 0;
        public SPEventFileExtension FileExtension { get; set; } = null;

        [ForeignKey(nameof(FileName))]
        [Column("file_name_id")]
        public int? FileNameId { get; set; } = 0;
        public SPEventFileName FileName { get; set; } = null;

        [ForeignKey(nameof(Url))]
        [Column("url_id")]
        public int UrlId { get; set; } = 0;
        public Url Url { get; set; } = null;

        [ForeignKey(nameof(Site))]
        [Column("site_id")]
        public int SiteId { get; set; } = 0;
        public Site Site { get; set; } = null;

        public override string GetEventDescription()
        {
            return $"{FileName?.Name}";
        }
    }

    [Table("event_copilot_meetings")]
    public class CopilotEventMetadataMeeting : BaseCopilotSpecificEvent
    {
        [ForeignKey(nameof(OnlineMeeting))]
        [Column("meeting_id")]
        public int OnlineMeetingId { get; set; }

        public OnlineMeeting OnlineMeeting { get; set; } = null;

        public override string GetEventDescription()
        {
            return $"{OnlineMeeting.Name}";
        }
    }

}
