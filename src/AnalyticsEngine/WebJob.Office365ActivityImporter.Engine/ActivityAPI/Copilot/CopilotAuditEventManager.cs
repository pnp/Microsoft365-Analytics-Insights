using Common.DataUtils;
using Common.DataUtils.Sql.Inserts;
using Common.Entities;
using Common.Entities.Entities.AuditLog;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
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
        private readonly InsertBatch<SPCopilotLogTempEntity> _spCopilotInserts;
        private readonly InsertBatch<TeamsCopilotLogTempEntity> _teamsCopilotInserts;
        private readonly AnalyticsEntitiesContext _db;

        public CopilotAuditEventManager(string connectionString, ICopilotMetadataLoader copilotEventAdaptor, ILogger logger)
        {
            _copilotEventAdaptor = copilotEventAdaptor;
            _logger = logger;

            _spCopilotInserts = new InsertBatch<SPCopilotLogTempEntity>(connectionString, logger);
            _teamsCopilotInserts = new InsertBatch<TeamsCopilotLogTempEntity>(connectionString, logger);

            _db = new AnalyticsEntitiesContext();
        }

        public async Task SaveSingleCopilotEventToSql(CopilotEventData eventData, Office365Event baseOfficeEvent)
        {
            _logger.LogInformation($"Saving copilot event metadata to SQL for event {baseOfficeEvent.Id}");

            // Save via the high-speed bulk insert code, not EF as we're doing this multi-threaded and we don't want FK conflicts
            int meetingsCount = 0, filesCount = 0;
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
                    _teamsCopilotInserts.Rows.Add(new TeamsCopilotLogTempEntity
                    {
                        EventId = baseOfficeEvent.Id,
                        AppHost = eventData.AppHost,
                        MeetingId = meetingId,
                        MeetingCreatedUTC = meetingInfo.CreatedUTC,
                        MeetingName = meetingInfo.Subject
                    });

                    meetingsCount++;
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
                        _spCopilotInserts.Rows.Add(new SPCopilotLogTempEntity
                        {
                            EventId = baseOfficeEvent.Id,
                            AppHost = eventData.AppHost,
                            FileExtension = spFileInfo.Extension,
                            FileName = spFileInfo.Filename,
                            Url = spFileInfo.Url,
                            UrlBase = spFileInfo.SiteUrl
                        });
                        filesCount++;
                    }
                    else
                    {
                        _logger.LogWarning($"No file info found for copilot context type '{context.Type}' with ID {context.Id}");
                    }
                }
            }

            var additionalAppHosts = new[] { "Teams", "bizchat", "Loop", "Whiteboard", "Bing", "Office" };

            // Some events don't have contexts, but may be just chats with Copilot in Teams or Bing
            if (eventData.Contexts.Count == 0 && additionalAppHosts.Any(x => x.Equals(eventData.AppHost)))
            {
                var copilotEvent = new CopilotChat
                {
                    EventID = baseOfficeEvent.Id,
                    AppHost = eventData.AppHost
                };
                _db.CopilotChats.Add(copilotEvent);
            }

            if (meetingsCount > 0 || filesCount > 0)
            {
                _logger.LogInformation($"Saved {meetingsCount} meetings and {filesCount} files to SQL for event {baseOfficeEvent.Id}");
            }
            else
            {
                // AppChat?
                _logger.LogTrace($"No copilot event metadata saved to SQL for event {baseOfficeEvent.Id} for host '{eventData.AppHost}'");
            }
        }

        public async Task CommitAllChanges()
        {
            var rr = new ResourceReader(System.Reflection.Assembly.GetExecutingAssembly());
            var docsMergeSql = rr.ReadResourceStringFromExecutingAssembly("WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.insert_sp_copilot_events_from_staging_table.sql")
                .Replace(ActivityImportConstants.STAGING_TABLE_VARNAME,
                ActivityImportConstants.STAGING_TABLE_COPILOT_SP);
            var teamsMergeSql = rr.ReadResourceStringFromExecutingAssembly("WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.insert_teams_copilot_events_from_staging_table.sql")
                .Replace(ActivityImportConstants.STAGING_TABLE_VARNAME,
                ActivityImportConstants.STAGING_TABLE_COPILOT_TEAMS);

            await _spCopilotInserts.SaveToStagingTable(docsMergeSql);
            await _teamsCopilotInserts.SaveToStagingTable(teamsMergeSql);
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
