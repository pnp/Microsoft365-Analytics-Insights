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

        // Contexts serialized as JSON. ALL of them - the file/meeting resolution above only ever
        // uses the first file or meeting context, so this is the only place the rest survive.
        [Column("contexts_json", true)]
        public string ContextsJson { get; set; }

        // AISystemPlugin entries serialized as JSON
        [Column("ai_system_plugins_json", true)]
        public string AISystemPluginsJson { get; set; }

        // Conversation thread the interaction belongs to (CopilotEventData.ThreadId). Left as the
        // default nvarchar(max) staging column and trimmed with LEFT() in the merge instead of
        // declaring a bounded SqlTypeOverride: InsertBatch DROPS a whole row whose value exceeds a
        // bounded staging column, and losing an entire interaction over a long thread id would be a
        // far worse outcome than truncating the id.
        [Column("thread_id", true)]
        public string ThreadId { get; set; }

        // Region of the Copilot service that served the interaction (audit record ClientRegion)
        [Column("client_region", true)]
        public string ClientRegion { get; set; }

        // Copilot audit-log schema version of this record (audit record CopilotLogVersion)
        [Column("copilot_log_version", true)]
        public string CopilotLogVersion { get; set; }

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

        // Must match dbo.urls.full_url (nvarchar(850), see migration ShrinkUrlsFullUrlColumn /
        // issue #122) so the join in "insert_sp_copilot_events_from_staging_table.sql" can use
        // IX_urls_full_url instead of an implicit type conversion that defeats the index.
        // nvarchar (not varchar) so Unicode URLs (e.g. Greek) aren't corrupted. See #122 (#108/#109).
        [Column("url", true, SqlTypeOverride = "nvarchar(850)")]
        public string Url { get; set; } = null;
    }

}
