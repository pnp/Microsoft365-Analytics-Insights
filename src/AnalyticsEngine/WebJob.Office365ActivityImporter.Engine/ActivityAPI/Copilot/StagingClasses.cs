using Common.DataUtils.Sql;
using System;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot
{
    public abstract class BaseCopilotLogTempEntity
    {
        [Column("app_host")]
        public string AppHost { get; set; } = null;

        [Column("event_id")]
        public Guid EventId { get; set; }
    }

    [TempTableName(ActivityImportConstants.STAGING_TABLE_COPILOT_CHATONLY)]
    public class ChatOnlyCopilotLogTempEntity : BaseCopilotLogTempEntity
    {
    }

    [TempTableName(ActivityImportConstants.STAGING_TABLE_COPILOT_TEAMS)]
    internal class TeamsCopilotLogTempEntity : BaseCopilotLogTempEntity
    {

        [Column("meeting_id")]
        public string MeetingId { get; internal set; } = null;

        [Column("meeting_created_utc")]
        public DateTime MeetingCreatedUTC { get; internal set; }

        [Column("meeting_name")]
        public string MeetingName { get; internal set; } = null;
    }

    /// <summary>
    /// SharePoint event temp entity
    /// </summary>
    [TempTableName(ActivityImportConstants.STAGING_TABLE_COPILOT_SP)]
    internal class SPCopilotLogTempEntity : BaseCopilotLogTempEntity
    {
        [Column("url_base", true)]
        public string UrlBase { get; set; } = null;

        [Column("file_name")]
        public string FileName { get; set; } = null;

        [Column("file_extension")]
        public string FileExtension { get; set; } = null;

        [Column("url")]
        public string Url { get; set; } = null;
    }

}
