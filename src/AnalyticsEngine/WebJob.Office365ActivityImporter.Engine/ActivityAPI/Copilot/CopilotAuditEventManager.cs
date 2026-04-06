using Common.Entities;
using DataUtils;
using DataUtils.Sql.Inserts;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace ActivityImporter.Engine.ActivityAPI.Copilot
{
    /// <summary>
    /// Saves copilot event metadata to SQL (staging tables first, then merged via supplied scripts).
    /// Responsibilities:
    /// - Adapt raw audit events into staging entities (files / meetings / chat-only / tool executions)
    /// - Accumulate batches for high-speed bulk insert
    /// - Provide per-event and batch-level logging
    /// </summary>
    public class CopilotAuditEventManager : IDisposable
    {
        private readonly ICopilotMetadataLoader _copilotEventAdaptor;
        private readonly ILogger _logger;
        private readonly InsertBatch<SPCopilotLogTempEntity> _copilotInsertsSP;
        private readonly InsertBatch<TeamsCopilotLogTempEntity> _copilotInsertsTeams;
        private readonly InsertBatch<ChatOnlyCopilotLogTempEntity> _copilotInsertsChatsNoContext;
        private readonly InsertBatch<ToolExecutionCopilotLogTempEntity> _copilotInsertsToolExecutions;
        private readonly ProjectResourceReader _rr;

        // Batch-level totals (across all processed events since last commit)
        private int _totalMeetingsCount;
        private int _totalFilesCount;
        private int _totalChatOnlyCount;
        private int _totalToolExecutionsCount;

        public CopilotAuditEventManager(string connectionString, ICopilotMetadataLoader copilotEventAdaptor, ILogger logger)
        {
            _rr = new ProjectResourceReader(System.Reflection.Assembly.GetExecutingAssembly());
            _copilotEventAdaptor = copilotEventAdaptor;
            _logger = logger;
            _copilotInsertsSP = new InsertBatch<SPCopilotLogTempEntity>(connectionString, logger);
            _copilotInsertsTeams = new InsertBatch<TeamsCopilotLogTempEntity>(connectionString, logger);
            _copilotInsertsChatsNoContext = new InsertBatch<ChatOnlyCopilotLogTempEntity>(connectionString, logger);
            _copilotInsertsToolExecutions = new InsertBatch<ToolExecutionCopilotLogTempEntity>(connectionString, logger);
        }

        /// <summary>
        /// Adapts a single Copilot audit record into staging entities. Does NOT commit to SQL.
        /// </summary>
        public async Task SaveSingleCopilotEventToSqlStaging(CopilotAuditLogContent auditRecord, CommonAuditEvent baseOfficeEvent)
        {
            if (auditRecord == null || baseOfficeEvent == null)
            {
                _logger.LogWarning("CopilotAuditEventManager received null auditRecord or baseOfficeEvent.");
                return;
            }

            // Per-event counts (for logging only)
            int eventMeetings = 0, eventFiles = 0, eventChats = 0;

            var contexts = auditRecord.CopilotEventData?.Contexts;
            if (contexts != null && contexts.Count > 0)
            {
                foreach (var context in contexts)
                {
                    // Only one meeting OR file per event is relevant; chat contexts are additive.
                    if (context.Type == ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING)
                    {
                        if (eventMeetings == 0) // safeguard against multiple meeting contexts
                        {
                            if (await TryAddMeetingAsync(context.Id, auditRecord, baseOfficeEvent))
                            {
                                eventMeetings++; _totalMeetingsCount++;
                            }
                        }
                        break; // meeting ends further processing for meeting/file
                    }
                    else if (context.Type == ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT)
                    {
                        if (eventChats == 0) // safeguard against multiple chat contexts for the same event
                        {
                            AddChatOnly(auditRecord, baseOfficeEvent);
                            eventChats++; _totalChatOnlyCount++;
                        }
                    }
                    else
                    {
                        if (eventFiles == 0) // only capture first file-relevant context
                        {
                            if (await TryAddFileAsync(context.Id, auditRecord, baseOfficeEvent))
                            {
                                eventFiles++; _totalFilesCount++;
                            }
                        }
                        break; // after first file we break out (matching prior behaviour)
                    }

                    // Preserve original logic: break after first meeting OR file context captured.
                    if (eventMeetings > 0 || eventFiles > 0)
                        break;
                }
            }
            else
            {
                // No context => treat as chat-only interaction.
                AddChatOnly(auditRecord, baseOfficeEvent);
                eventChats++; _totalChatOnlyCount++;
            }

            if (eventMeetings > 0 || eventFiles > 0 || eventChats > 0)
            {
                _logger.LogInformation($"Event {baseOfficeEvent.Id}: staged {eventChats} chat(s), {eventMeetings} meeting(s), {eventFiles} file(s).");
            }
            else
            {
                _logger.LogTrace($"Event {baseOfficeEvent.Id}: no copilot metadata to stage (host '{auditRecord.CopilotEventData?.AppHost}')");
            }
        }

        private async Task<bool> TryAddMeetingAsync(string contextId, CopilotAuditLogContent auditRecord, CommonAuditEvent baseOfficeEvent)
        {
            try
            {
                var userGuid = await _copilotEventAdaptor.GetUserIdFromUpn(baseOfficeEvent.User.UserPrincipalName);
                var meetingId = StringUtils.GetOnlineMeetingId(contextId, userGuid);
                var meetingInfo = await _copilotEventAdaptor.GetMeetingInfo(meetingId, userGuid);

                _copilotInsertsTeams.Rows.Add(new TeamsCopilotLogTempEntity
                {
                    EventId = baseOfficeEvent.Id,
                    AppHost = auditRecord.CopilotEventData.AppHost,
                    MeetingId = meetingId,
                    MeetingCreatedUTC = meetingInfo?.CreatedUTC,
                    MeetingName = meetingInfo?.Subject,
                    AgentId = auditRecord.AgentId,
                    AgentName = auditRecord.AgentName,
                    IsCustomAgent = auditRecord.IsCustomAgent,
                    AccessedResourcesJson = SerializeAccessedResources(auditRecord.CopilotEventData?.AccessedResources),
                    MessagesJson = SerializeMessages(auditRecord),
                    ModelTransparencyDetailsJson = SerializeModelTransparencyDetails(auditRecord),
                    CopilotCreditEstimateTotal = auditRecord.Cost?.TotalCredits,
                    CopilotCreditEstimateJson = SerializeCopilotCreditEstimation(auditRecord)
                });
                return true; // staged regardless of meetingInfo retrieval success
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to stage meeting metadata for event {baseOfficeEvent.Id} context {contextId}");
                return false;
            }
        }

        private async Task<bool> TryAddFileAsync(string contextId, CopilotAuditLogContent auditRecord, CommonAuditEvent baseOfficeEvent)
        {
            try
            {
                var spFileInfo = await _copilotEventAdaptor.GetSpoFileInfo(contextId, baseOfficeEvent.User.UserPrincipalName);
                _copilotInsertsSP.Rows.Add(new SPCopilotLogTempEntity
                {
                    EventId = baseOfficeEvent.Id,
                    AppHost = auditRecord.CopilotEventData.AppHost,
                    FileExtension = spFileInfo?.Extension,
                    FileName = spFileInfo?.Filename,
                    Url = spFileInfo?.Url,
                    UrlBase = spFileInfo?.SiteUrl,
                    AgentId = auditRecord.AgentId,
                    AgentName = auditRecord.AgentName,
                    IsCustomAgent = auditRecord.IsCustomAgent,
                    AccessedResourcesJson = SerializeAccessedResources(auditRecord.CopilotEventData?.AccessedResources),
                    MessagesJson = SerializeMessages(auditRecord),
                    ModelTransparencyDetailsJson = SerializeModelTransparencyDetails(auditRecord),
                    CopilotCreditEstimateTotal = auditRecord.Cost?.TotalCredits,
                    CopilotCreditEstimateJson = SerializeCopilotCreditEstimation(auditRecord)
                });
                if (spFileInfo == null)
                {
                    _logger.LogWarning($"No file info found for copilot context type with id {contextId} (event {baseOfficeEvent.Id})");
                }
                return true; // staged regardless
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to stage file metadata for event {baseOfficeEvent.Id} context {contextId}");
                return false;
            }
        }

        private void AddChatOnly(CopilotAuditLogContent auditRecord, CommonAuditEvent baseOfficeEvent)
        {
            _copilotInsertsChatsNoContext.Rows.Add(new ChatOnlyCopilotLogTempEntity
            {
                EventId = baseOfficeEvent.Id,
                AppHost = auditRecord.CopilotEventData?.AppHost ?? "Unknown",
                AgentId = auditRecord.AgentId,
                AgentName = auditRecord.AgentName,
                IsCustomAgent = auditRecord.IsCustomAgent,
                AccessedResourcesJson = SerializeAccessedResources(auditRecord.CopilotEventData?.AccessedResources),
                MessagesJson = SerializeMessages(auditRecord),
                ModelTransparencyDetailsJson = SerializeModelTransparencyDetails(auditRecord),
                CopilotCreditEstimateTotal = auditRecord.Cost?.TotalCredits,
                CopilotCreditEstimateJson = SerializeCopilotCreditEstimation(auditRecord)
            });
        }

        /// <summary>
        /// Stages AIExecuteTool audit events for bulk insert. Creates one staging row per tool×message combination.
        /// </summary>
        public Task SaveToolExecutionToSqlStaging(AIExecuteToolAuditLogContent auditRecord, CommonAuditEvent baseOfficeEvent)
        {
            if (auditRecord == null || baseOfficeEvent == null)
            {
                _logger.LogWarning("CopilotAuditEventManager.SaveToolExecutionToSqlStaging received null auditRecord or baseOfficeEvent.");
                return Task.CompletedTask;
            }

            var toolNames = auditRecord.GetToolNames();
            var messageIds = auditRecord.GetResponseMessageIds();
            var appHost = auditRecord.CopilotEventData?.AppHost;

            if (toolNames.Count == 0)
            {
                // No tool names found; still record the event with null tool name
                toolNames.Add(null);
            }

            if (messageIds.Count == 0)
            {
                // No message IDs found; still record the event with null message ID
                messageIds.Add(null);
            }

            int rowsAdded = 0;
            foreach (var toolName in toolNames)
            {
                foreach (var messageId in messageIds)
                {
                    _copilotInsertsToolExecutions.Rows.Add(new ToolExecutionCopilotLogTempEntity
                    {
                        EventId = baseOfficeEvent.Id,
                        AppHost = appHost,
                        ToolName = toolName,
                        MessageId = messageId
                    });
                    rowsAdded++;
                    _totalToolExecutionsCount++;
                }
            }

            _logger.LogInformation($"Event {baseOfficeEvent.Id}: staged {rowsAdded} tool execution(s).");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Serializes AccessedResources list to JSON for staging table storage
        /// </summary>
        internal string SerializeAccessedResources(IEnumerable<AccessedResource> accessedResources)
        {
            if (accessedResources == null || accessedResources.Count() == 0)
            {
                return null;
            }

            try
            {
                return JsonConvert.SerializeObject(accessedResources);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to serialize AccessedResources");
                return null;
            }
        }

        /// <summary>
        /// Serializes Messages from CopilotAuditLogContent to JSON for staging table storage.
        /// Uses the ParsedAuditEvent property which contains the deserialized audit event data.
        /// Only serializes response messages (IsPrompt = false), not user prompts.
        /// </summary>
        internal string SerializeMessages(CopilotAuditLogContent auditRecord)
        {
            if (auditRecord?.ParsedAuditEvent?.Messages == null || auditRecord.ParsedAuditEvent.Messages.Count == 0)
            {
                return null;
            }

            try
            {
                // Filter out prompt messages - only serialize responses
                var responseMessages = auditRecord.ParsedAuditEvent.Messages.Where(m => !m.IsPrompt).ToList();

                if (responseMessages.Count == 0)
                {
                    return null;
                }

                return JsonConvert.SerializeObject(responseMessages);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to serialize Messages from audit record");
                return null;
            }
        }

        /// <summary>
        /// Serializes ModelTransparencyDetails from CopilotAuditLogContent to JSON for staging table storage.
        /// Used to track which AI models (e.g., DEEP_LEO for deep reasoning) were used in the conversation.
        /// </summary>
        internal string SerializeModelTransparencyDetails(CopilotAuditLogContent auditRecord)
        {
            if (auditRecord?.ParsedAuditEvent?.ModelTransparencyDetails == null ||
                auditRecord.ParsedAuditEvent.ModelTransparencyDetails.Count == 0)
            {
                return null;
            }

            try
            {
                return JsonConvert.SerializeObject(auditRecord.ParsedAuditEvent.ModelTransparencyDetails);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to serialize ModelTransparencyDetails from audit record");
                return null;
            }
        }

        /// <summary>
        /// Serializes CopilotCreditEstimation from CopilotAuditLogContent to JSON for staging table storage.
        /// Contains breakdown of Copilot Credits consumed including generative answers, tenant graph grounding, deep reasoning, etc.
        /// </summary>
        internal string SerializeCopilotCreditEstimation(CopilotAuditLogContent auditRecord)
        {
            if (auditRecord?.Cost == null)
            {
                return null;
            }

            try
            {
                return JsonConvert.SerializeObject(auditRecord.Cost);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to serialize CopilotCreditEstimation from audit record");
                return null;
            }
        }

        /// <summary>
        /// Commits all staged entities to their respective staging tables + merge scripts, then clears internal state.
        /// </summary>
        public async Task CommitAllChanges()
        {
            var docsMergeSql = GetSql(ActivityImportConstants.STAGING_TABLE_COPILOT_SP, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.insert_sp_copilot_events_from_staging_table.sql");
            var teamsMergeSql = GetSql(ActivityImportConstants.STAGING_TABLE_COPILOT_TEAMS, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.insert_teams_copilot_events_from_staging_table.sql");
            var chatOnlyMergeSql = GetSql(ActivityImportConstants.STAGING_TABLE_COPILOT_CHATONLY, null);
            var toolExecutionsMergeSql = GetToolExecutionsSql(ActivityImportConstants.STAGING_TABLE_COPILOT_TOOL_EXECUTIONS);

            _logger.LogDebug($"Committing batch: {_totalFilesCount} file(s), {_totalMeetingsCount} meeting(s), {_totalChatOnlyCount} chat-only event(s), {_totalToolExecutionsCount} tool execution(s) to SQL.");

            await _copilotInsertsSP.SaveToStagingTable(docsMergeSql);
            await _copilotInsertsTeams.SaveToStagingTable(teamsMergeSql);
            await _copilotInsertsChatsNoContext.SaveToStagingTable(chatOnlyMergeSql);
            await _copilotInsertsToolExecutions.SaveToStagingTable(toolExecutionsMergeSql);

            // Clear lists & counters for next batch
            _copilotInsertsSP.Rows.Clear();
            _copilotInsertsTeams.Rows.Clear();
            _copilotInsertsChatsNoContext.Rows.Clear();
            _copilotInsertsToolExecutions.Rows.Clear();
            _totalFilesCount = 0;
            _totalMeetingsCount = 0;
            _totalChatOnlyCount = 0;
            _totalToolExecutionsCount = 0;
        }

        private string GetSql(string tempTableName, string workloadSpecificScriptName)
        {
            var commonMergeSql = _rr.ReadResourceString("WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.common_upsert_copilot_agents.sql")
                .Replace(ActivityImportConstants.STAGING_TABLE_VARNAME, tempTableName);

            var workloadSpecificSql = workloadSpecificScriptName != null
                ? _rr.ReadResourceString(workloadSpecificScriptName).Replace(ActivityImportConstants.STAGING_TABLE_VARNAME, tempTableName)
                : string.Empty;
            return commonMergeSql + Environment.NewLine + workloadSpecificSql;
        }

        private string GetToolExecutionsSql(string tempTableName)
        {
            return _rr.ReadResourceString("WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.upsert_copilot_tool_executions.sql")
                .Replace(ActivityImportConstants.STAGING_TABLE_VARNAME, tempTableName);
        }

        public void Dispose()
        {
            // Nothing disposable currently. Placeholder for future enhancements.
        }
    }

    public interface ICopilotMetadataLoader
    {
        Task<SpoDocumentFileInfo> GetSpoFileInfo(string copilotId, string eventUpn);
        Task<MeetingMetadata> GetMeetingInfo(string threadId, string userGuid);
        Task<string> GetUserIdFromUpn(string userPrincipalName);
    }
}
