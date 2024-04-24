using Common.DataUtils.Sql;
using System;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot
{

    public abstract class SPLogTempEntity
    {
        [Column("url_base", true)]
        public string UrlBase { get; set; } = null;
    }

    [TempTableName(ActivityImportConstants.STAGING_TABLE_COPILOT_TEAMS)]
    internal class TeamsCopilotLogTempEntity
    {
        [Column("event_id")]
        public Guid EventId { get; set; }

        [Column("app_host")]
        public string AppHost { get; set; } = null;

        [Column("meeting_id")]
        public string MeetingId { get; internal set; } = null;

        [Column("meeting_created_utc")]
        public DateTime MeetingCreatedUTC { get; internal set; }

        [Column("meeting_name")]
        public string MeetingName { get; internal set; } = null;
    }

    [TempTableName(ActivityImportConstants.STAGING_TABLE_COPILOT_SP)]
    internal class SPCopilotLogTempEntity : SPLogTempEntity
    {
        [Column("event_id")]
        public Guid EventId { get; set; }

        [Column("app_host")]
        public string AppHost { get; set; } = null;

        [Column("file_name")]
        public string FileName { get; set; } = null;

        [Column("file_extension")]
        public string FileExtension { get; set; } = null;

        [Column("url")]
        public string Url { get; set; } = null;
    }

    /// <summary>
    /// Class for inserting staging data to temp SQL table
    /// </summary>
    [TempTableName(ActivityImportConstants.STAGING_TABLE_ACTIVITY_SP)]
    internal class SPAuditLogTempEntity : SPLogTempEntity
    {
        public SPAuditLogTempEntity(AbstractAuditLogContent abtractLog, string userNameOrHash)
        {

            Id = abtractLog.Id;
            UserName = userNameOrHash;
            OperationName = abtractLog.Operation;
            TimeStamp = abtractLog.CreationTime;
            TypeName = abtractLog.ItemType;
            ObjectId = abtractLog.ObjectId;
            Workload = abtractLog.Workload;

            if (abtractLog is SharePointAuditLogContent)
            {
                var spLog = (SharePointAuditLogContent)abtractLog;

                FileName = spLog.SourceFileName;
                ExtensionName = spLog.SourceFileExtension;
                UrlBase = spLog.SiteUrl;
            }
        }

        [Column("log_id")]
        public Guid Id { get; set; } = Guid.Empty;

        [Column("user_name")]
        public string UserName { get; set; } = null;

        [Column("file_name", true)]
        public string FileName { get; set; } = null;

        [Column("extension_name", true)]
        public string ExtensionName { get; set; } = null;

        [Column("operation_name")]
        public string OperationName { get; set; } = null;

        [Column("time_stamp")]
        public DateTime TimeStamp { get; set; } = DateTime.MinValue;

        [Column("workload")]
        public string Workload { get; set; } = null;

        [Column("type_name")]
        public string TypeName { get; set; } = null;

        [Column("object_id")]
        public string ObjectId { get; set; } = null;
    }
}
