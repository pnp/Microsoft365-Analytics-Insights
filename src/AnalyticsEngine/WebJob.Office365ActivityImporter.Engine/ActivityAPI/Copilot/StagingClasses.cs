using DataUtils.Sql;
using System;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot
{
    public abstract class BaseCopilotLogTempEntity
    {
        [Column("app_host")]
        public string AppHost { get; set; } = null;

        [Column("event_id")]
        public Guid EventId { get; set; }


        [Column("agent_name", true)]
        public string AgentName { get; set; }

        [Column("agent_id", true)]
        public string AgentId { get; set; }

        [Column("is_custom_agent", true)]
        public bool? IsCustomAgent { get; set; }

        // AccessedResources serialized as JSON
        [Column("accessed_resources_json", true)]
        public string AccessedResourcesJson { get; set; }

        // Messages serialized as JSON
        [Column("messages_json", true)]
        public string MessagesJson { get; set; }

        // Model Transparency Details serialized as JSON
        [Column("model_transparency_json", true)]
        public string ModelTransparencyDetailsJson { get; set; }

        // Copilot Credit estimate total
        [Column("copilot_credit_estimate_total", true)]
        public int? CopilotCreditEstimateTotal { get; set; }

        // Copilot Credit estimate breakdown serialized as JSON
        [Column("copilot_credit_estimate_json", true)]
        public string CopilotCreditEstimateJson { get; set; }
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

        [Column("meeting_created_utc", true)]
        public DateTime? MeetingCreatedUTC { get; internal set; }

        [Column("meeting_name", true)]
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

        [Column("file_name", true)]
        public string FileName { get; set; } = null;

        [Column("file_extension", true)]
        public string FileExtension { get; set; } = null;

        [Column("url", true)]
        public string Url { get; set; } = null;
    }

}
