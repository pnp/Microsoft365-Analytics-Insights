using Common.Entities;
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
        private readonly AnalyticsEntitiesContext _db;

        public CopilotAuditEventManager(string connectionString, ICopilotMetadataLoader copilotEventAdaptor, ILogger logger)
        {
            _copilotEventAdaptor = copilotEventAdaptor;
            _logger = logger;

            _copilotInsertsSP = new InsertBatch<SPCopilotLogTempEntity>(connectionString, logger);
            _copilotInsertsTeams = new InsertBatch<TeamsCopilotLogTempEntity>(connectionString, logger);
            _copilotInsertsChatsNoContext = new InsertBatch<ChatOnlyCopilotLogTempEntity>(connectionString, logger);

            _db = new AnalyticsEntitiesContext();
        }
        public async Task SaveSingleCopilotEventToSql(CopilotEventData eventData, Office365Event baseOfficeEvent)
        {
            _logger.LogInformation($"Saving copilot event metadata to SQL for event {baseOfficeEvent.Id}");

            // Save via the high-speed bulk insert code, not EF as we're doing this multi-threaded and we don't want FK conflicts
            int meetingsCount = 0, filesCount = 0, chatOnlyCount = 0;

            if (eventData.Contexts != null && eventData.Contexts.Count > 0)
            {
                // Process events with context (Teams meeting, file etc).
                // Normally only one context per event, but we'll loop through them all just in case.
                foreach (var context in eventData.Contexts)
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
                            continue;   // Logging done in adaptor. Move to next
                        }
                        _copilotInsertsTeams.Rows.Add(new TeamsCopilotLogTempEntity
                        {
                            EventId = baseOfficeEvent.Id,
                            AppHost = eventData.AppHost,
                            MeetingId = meetingId,
                            MeetingCreatedUTC = meetingInfo.CreatedUTC,
                            MeetingName = meetingInfo.Subject
                        });

                        meetingsCount++;
                        break;  // Only one meeting per event
                    }
                    else if (context.Type == ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT)
                    {
                        // Just a chat with copilot, without any specific meeting or file associated. Log the interaction.
                        var copilotEvent = new CopilotChat
                        {
                            EventID = baseOfficeEvent.Id,
                            AppHost = eventData.AppHost
                        };
                        _db.CopilotChats.Add(copilotEvent);
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
                                AppHost = eventData.AppHost,
                                FileExtension = spFileInfo.Extension,
                                FileName = spFileInfo.Filename,
                                Url = spFileInfo.Url,
                                UrlBase = spFileInfo.SiteUrl
                            });
                            filesCount++;
                            break;  // Normally only one file per event.
                                    // There can be more documents in the context if one references another, but we only care about the doc the user is in.
                        }
                        else
                        {
                            _logger.LogWarning($"No file info found for copilot context type '{context.Type}' with ID {context.Id}");
                        }
                    }
                }
            }
            else
            {
                _copilotInsertsChatsNoContext.Rows.Add(new ChatOnlyCopilotLogTempEntity
                {
                    EventId = baseOfficeEvent.Id,
                    AppHost = eventData.AppHost
                });
                chatOnlyCount++;
            }
            if (meetingsCount > 0 || filesCount > 0 || chatOnlyCount > 0)
            {
                _logger.LogInformation($"Saved {chatOnlyCount} chats, {meetingsCount} meetings and {filesCount} files to SQL for copilot event {baseOfficeEvent.Id}");
            }
            else
            {
                _logger.LogTrace($"No copilot event metadata saved to SQL for event {baseOfficeEvent.Id} for host '{eventData.AppHost}'");
            }
        }

        public async Task CommitAllChanges()
        {
            var rr = new ProjectResourceReader(System.Reflection.Assembly.GetExecutingAssembly());
            var docsMergeSql = rr.ReadResourceString("WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.insert_sp_copilot_events_from_staging_table.sql")
                .Replace(ActivityImportConstants.STAGING_TABLE_VARNAME,
                ActivityImportConstants.STAGING_TABLE_COPILOT_SP);
            var teamsMergeSql = rr.ReadResourceString("WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.insert_teams_copilot_events_from_staging_table.sql")
                .Replace(ActivityImportConstants.STAGING_TABLE_VARNAME,
                ActivityImportConstants.STAGING_TABLE_COPILOT_TEAMS);


            var chatOnlyMergeSql = rr.ReadResourceString("WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.insert_chat_only_copilot_events_from_staging_table.sql")
                .Replace(ActivityImportConstants.STAGING_TABLE_VARNAME,
                ActivityImportConstants.STAGING_TABLE_COPILOT_CHATONLY);

            await _copilotInsertsSP.SaveToStagingTable(docsMergeSql);
            await _copilotInsertsTeams.SaveToStagingTable(teamsMergeSql);
            await _copilotInsertsChatsNoContext.SaveToStagingTable(chatOnlyMergeSql);
            await _db.SaveChangesAsync();
        }

        public void Dispose()
        {
            _db.Dispose();
        }
    }

    public interface ICopilotMetadataLoader
    {
        Task<SpoDocumentFileInfo> GetSpoFileInfo(string copilotId, string eventUpn);
        Task<MeetingMetadata> GetMeetingInfo(string threadId, string userGuid);
        Task<string> GetUserIdFromUpn(string userPrincipalName);
    }
}
