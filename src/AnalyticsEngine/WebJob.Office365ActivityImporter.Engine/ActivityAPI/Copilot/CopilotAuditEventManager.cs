using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Entities.AuditLog;
using DataUtils;
using DataUtils.Sql.Inserts;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace ActivityImporter.Engine.ActivityAPI.Copilot
{

    /// <summary>
    /// Saves copilot event metadata to SQL
    /// </summary>
    public class CopilotAuditEventManager : IDisposable
    {
        private readonly ICopilotMetadataLoader _copilotEventAdaptor;
        private readonly ILogger _logger;
        private readonly InsertBatch<SPCopilotLogTempEntity> _copilotInsertsSP;
        private readonly InsertBatch<TeamsCopilotLogTempEntity> _copilotInsertsTeams;
        private readonly InsertBatch<ChatOnlyCopilotLogTempEntity> _copilotInsertsChatsNoContext;

        private ProjectResourceReader _rr;
        private int _meetingsCount = 0;
        private int _filesCount = 0;
        private int _chatOnlyCount = 0;
        private readonly AnalyticsEntitiesContext _db;


        public CopilotAuditEventManager(string connectionString, ICopilotMetadataLoader copilotEventAdaptor, ILogger logger)
        {
            _rr = new ProjectResourceReader(System.Reflection.Assembly.GetExecutingAssembly());

            _copilotEventAdaptor = copilotEventAdaptor;
            _logger = logger;

            _copilotInsertsSP = new InsertBatch<SPCopilotLogTempEntity>(connectionString, logger);
            _copilotInsertsTeams = new InsertBatch<TeamsCopilotLogTempEntity>(connectionString, logger);
            _copilotInsertsChatsNoContext = new InsertBatch<ChatOnlyCopilotLogTempEntity>(connectionString, logger);
            _db = new AnalyticsEntitiesContext();
        }

        public async Task SaveSingleCopilotEventToSqlStaging(CopilotAuditLogContent auditRecord, CommonAuditEvent baseOfficeEvent)
        {
            _chatOnlyCount = 0;
            _filesCount = 0;
            _meetingsCount = 0;

            _logger.LogInformation($"Saving copilot event metadata to SQL for event {baseOfficeEvent.Id}");

            // Save via the high-speed bulk insert code, not EF as we're doing this multi-threaded and we don't want FK conflicts

            if (auditRecord.CopilotEventData.Contexts != null && auditRecord.CopilotEventData.Contexts.Count > 0)
            {
                // Process events with context (Teams meeting, file etc).
                // Normally only one context per event, but we'll loop through them all just in case.
                foreach (var context in auditRecord.CopilotEventData.Contexts)
                {
                    // There are some known context types for Teams etc. Everything else is assumed to be a file type. 
                    if (context.Type == ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING)
                    {
                        // We need the user guid to construct the meeting ID
                        var userGuid = await _copilotEventAdaptor.GetUserIdFromUpn(baseOfficeEvent.User.UserPrincipalName);

                        // Construct meeting ID from user GUID and thread ID
                        var meetingId = StringUtils.GetOnlineMeetingId(context.Id, userGuid);

                        var meetingInfo = await _copilotEventAdaptor.GetMeetingInfo(meetingId, userGuid);

                        if (meetingInfo == null)
                        {
                            _copilotInsertsTeams.Rows.Add(new TeamsCopilotLogTempEntity
                            {
                                EventId = baseOfficeEvent.Id,
                                AppHost = auditRecord.CopilotEventData.AppHost,
                                MeetingId = meetingId,
                                MeetingCreatedUTC = null,
                                MeetingName = null
                            });
                            continue;   // Logging done in adaptor. Move to next
                        }
                        else
                        {
                            _copilotInsertsTeams.Rows.Add(new TeamsCopilotLogTempEntity
                            {
                                EventId = baseOfficeEvent.Id,
                                AppHost = auditRecord.CopilotEventData.AppHost,
                                MeetingId = meetingId,
                                MeetingCreatedUTC = meetingInfo.CreatedUTC,
                                MeetingName = meetingInfo.Subject
                            });
                        }

                        _meetingsCount++;
                        break;  // Only one meeting per event
                    }
                    else if (context.Type == ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT)
                    {
                        // Just a chat with copilot, without any specific meeting or file associated. Log the interaction.
                        AddChatOnly(auditRecord, baseOfficeEvent);
                    }
                    else
                    {
                        // Load from Graph the SPO file info
                        var spFileInfo = await _copilotEventAdaptor.GetSpoFileInfo(context.Id, baseOfficeEvent.User.UserPrincipalName);

                        if (spFileInfo != null)
                        {
                            // Use the bulk insert 
                            _copilotInsertsSP.Rows.Add(new SPCopilotLogTempEntity
                            {
                                EventId = baseOfficeEvent.Id,
                                AppHost = auditRecord.CopilotEventData.AppHost,
                                FileExtension = spFileInfo.Extension,
                                FileName = spFileInfo.Filename,
                                Url = spFileInfo.Url,
                                UrlBase = spFileInfo.SiteUrl,
                                AgentId = auditRecord.AgentId,
                                AgentName = auditRecord.AgentName,
                            });
                            _filesCount++;
                            break;  // Normally only one file per event.
                                    // There can be more documents in the context if one file references another, but we only care about the doc the user is in.
                        }
                        else
                        {
                            _logger.LogWarning($"No file info found for copilot context type '{context.Type}' with ID {context.Id}");
                            _copilotInsertsSP.Rows.Add(new SPCopilotLogTempEntity
                            {
                                EventId = baseOfficeEvent.Id,
                                AppHost = auditRecord.CopilotEventData.AppHost,
                                FileExtension = null,
                                FileName = null,
                                Url = null,
                                UrlBase = null
                            });
                        }
                    }
                }
            }
            else
            {
                // No context. Log the interaction as a chat only event
                AddChatOnly(auditRecord, baseOfficeEvent);
            }
            if (_meetingsCount > 0 || _filesCount > 0 || _chatOnlyCount > 0)
            {
                _logger.LogInformation($"Saved {_chatOnlyCount} chats, {_meetingsCount} meetings and {_filesCount} files to SQL for copilot event {baseOfficeEvent.Id}");
            }
            else
            {
                _logger.LogTrace($"No copilot event metadata saved to SQL for event {baseOfficeEvent.Id} for host '{auditRecord.CopilotEventData.AppHost}'");
            }
        }

        void AddChatOnly(CopilotAuditLogContent auditRecord, CommonAuditEvent baseOfficeEvent)
        {
            _copilotInsertsChatsNoContext.Rows.Add(new ChatOnlyCopilotLogTempEntity
            {
                EventId = baseOfficeEvent.Id,
                AppHost = auditRecord.CopilotEventData.AppHost,
                AgentId = auditRecord.AgentId,
                AgentName = auditRecord.AgentName,
            });
            _chatOnlyCount++;
        }

        public async Task CommitAllChanges()
        {
            var docsMergeSql = GetSql(ActivityImportConstants.STAGING_TABLE_COPILOT_SP, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.insert_sp_copilot_events_from_staging_table.sql");

            var teamsMergeSql = GetSql(ActivityImportConstants.STAGING_TABLE_COPILOT_TEAMS, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.insert_teams_copilot_events_from_staging_table.sql");

            var chatOnlyMergeSql = GetSql(ActivityImportConstants.STAGING_TABLE_COPILOT_CHATONLY, null);


            _logger.LogDebug($"Saving {_filesCount} files to SQL");
            await _copilotInsertsSP.SaveToStagingTable(docsMergeSql);


            _logger.LogDebug($"Saving {_meetingsCount} meetings to SQL");
            await _copilotInsertsTeams.SaveToStagingTable(teamsMergeSql);

            _logger.LogDebug($"Saving {_chatOnlyCount} chat only events to SQL");
            await _copilotInsertsChatsNoContext.SaveToStagingTable(chatOnlyMergeSql);

            // Clear lists
            _copilotInsertsSP.Rows.Clear();
            _copilotInsertsTeams.Rows.Clear();
            _copilotInsertsChatsNoContext.Rows.Clear();
        }

        string GetSql(string tempTableName, string workloadSpecificScriptName)
        {
            var commonMergeSql = _rr.ReadResourceString("WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.common_upsert_copilot_agents.sql")
                .Replace(ActivityImportConstants.STAGING_TABLE_VARNAME, tempTableName);

            var workloadSpecificSql = workloadSpecificScriptName != null ? _rr.ReadResourceString(workloadSpecificScriptName)
                .Replace(ActivityImportConstants.STAGING_TABLE_VARNAME, tempTableName) : string.Empty;
            return commonMergeSql + Environment.NewLine + workloadSpecificSql;
        }

        public void Dispose()
        {
        }
    }

    public interface ICopilotMetadataLoader
    {
        Task<SpoDocumentFileInfo> GetSpoFileInfo(string copilotId, string eventUpn);
        Task<MeetingMetadata> GetMeetingInfo(string threadId, string userGuid);
        Task<string> GetUserIdFromUpn(string userPrincipalName);
    }
}
