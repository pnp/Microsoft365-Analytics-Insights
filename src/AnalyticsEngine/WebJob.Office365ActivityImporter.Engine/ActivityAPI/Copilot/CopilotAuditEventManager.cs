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
    /// - Adapt raw audit events into staging entities (files / meetings / chat-only)
    /// - Accumulate batches for high-speed bulk insert
    /// - Provide per-event and batch-level logging
    /// </summary>
    public class CopilotAuditEventManager : IDisposable
    {
        // Chunk size for the staging-table inserts. ParallelListProcessor spreads the batch across one
        // connection per chunk (capped at its own max), so a smaller chunk => more parallel inserts. The
        // default (10000) meant a single thread for every realistic Copilot batch (<= a couple thousand rows),
        // serializing every row's insert round-trip. On Azure SQL those round-trips are network-latency-bound,
        // so parallelizing them is a large win; on LocalDB (no latency) it's a no-op. Kept modest so we don't
        // open an excessive number of connections for the shared global temp table.
        private const int STAGING_INSERTS_PER_THREAD = 200;

        private readonly ICopilotMetadataLoader _copilotEventAdaptor;
        private readonly ILogger _logger;
        private readonly bool _resolveResourceMetadata;
        private readonly InsertBatch<SPCopilotLogTempEntity> _copilotInsertsSP;
        private readonly InsertBatch<TeamsCopilotLogTempEntity> _copilotInsertsTeams;
        private readonly InsertBatch<ChatOnlyCopilotLogTempEntity> _copilotInsertsChatsNoContext;
        private readonly ProjectResourceReader _rr;

        // Batch-level totals (across all processed events since last commit)
        private int _totalMeetingsCount;
        private int _totalFilesCount;
        private int _totalChatOnlyCount;

        public CopilotAuditEventManager(string connectionString, ICopilotMetadataLoader copilotEventAdaptor, ILogger logger, bool resolveResourceMetadata = true)
        {
            if (string.IsNullOrEmpty(connectionString)) throw new ArgumentException($"'{nameof(connectionString)}' cannot be null or empty.", nameof(connectionString));
            _copilotEventAdaptor = copilotEventAdaptor ?? throw new ArgumentNullException(nameof(copilotEventAdaptor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _resolveResourceMetadata = resolveResourceMetadata;
            _rr = new ProjectResourceReader(System.Reflection.Assembly.GetExecutingAssembly());
            _copilotInsertsSP = new InsertBatch<SPCopilotLogTempEntity>(connectionString, logger);
            _copilotInsertsTeams = new InsertBatch<TeamsCopilotLogTempEntity>(connectionString, logger);
            _copilotInsertsChatsNoContext = new InsertBatch<ChatOnlyCopilotLogTempEntity>(connectionString, logger);
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

            // Agent-metadata-only mode: when resource resolution is disabled, stage every interaction as a
            // chat-only record - which still carries the agent id/name/type, cost, messages and accessed
            // resources - and skip all file/meeting Graph resolution. This removes the serial, network-bound
            // Graph calls from the save path for tenants that only want Copilot agent-level reporting.
            if (!_resolveResourceMetadata)
            {
                AddChatOnly(auditRecord, baseOfficeEvent);
                _totalChatOnlyCount++;
                _logger.LogTrace($"Event {baseOfficeEvent.Id}: staged agent metadata only (Copilot resource resolution disabled).");
                return;
            }

            // Per-event counts (for logging only)
            int eventMeetings = 0, eventFiles = 0, eventChats = 0;

            var contexts = auditRecord.CopilotEventData?.Contexts;
            if (contexts != null && contexts.Count > 0)
            {
                // NOTE: per the Copilot schema (https://learn.microsoft.com/office/office-365-management-api/copilot-schema)
                // Contexts is an unordered collection. Current behaviour: the first meeting OR file context
                // ends iteration; chat contexts are additive but only the first is staged. This is intentional
                // and verified by CopilotEventManagerMeetingContextTakesPriorityOverChat /
                // CopilotEventManagerFileContextBreaksBeforeChat tests. If product semantics ever require
                // capturing trailing chat contexts after a meeting/file, those tests must be updated together
                // with this loop.
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
                _logger.LogDebug($"Event {baseOfficeEvent.Id}: staged {eventChats} chat(s), {eventMeetings} meeting(s), {eventFiles} file(s).");
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
                // Skip the Graph lookup for contexts that can never resolve to a SharePoint/OneDrive file
                // (local drive paths like C:\..., UNC \\server\share, the "DataAgent" sentinel). On one large
                // customer's export these accounted for a chunk of the "No file info found" warnings - each a doomed
                // Graph round-trip (network + throttling risk) that always returned nothing. We still stage the row
                // exactly as a null lookup would (so reporting is unchanged), just without the call. Any other
                // context (URL, opaque id, or empty) is resolved exactly as before.
                SpoDocumentFileInfo spFileInfo = null;
                if (ShouldSkipGraphFileLookup(contextId))
                {
                    _logger.LogTrace($"Skipping Graph file lookup for non-SharePoint copilot context id {contextId} (event {baseOfficeEvent.Id})");
                }
                else
                {
                    spFileInfo = await _copilotEventAdaptor.GetSpoFileInfo(contextId, baseOfficeEvent.User.UserPrincipalName);
                    if (spFileInfo == null)
                    {
                        // Expected and high-volume: many copilot contexts (e.g. securitycopilot.microsoft.com
                        // hosts) are not resolvable SharePoint files, so Graph legitimately returns nothing.
                        // These were ~thousands of warnings per cycle; log at Debug so they don't drown real
                        // warnings. The row is still staged exactly as a null lookup (reporting unchanged).
                        _logger.LogDebug($"No file info found for copilot context type with id {contextId} (event {baseOfficeEvent.Id})");
                    }
                }

                _copilotInsertsSP.Rows.Add(new SPCopilotLogTempEntity
                {
                    EventId = baseOfficeEvent.Id,
                    AppHost = auditRecord.CopilotEventData.AppHost,
                    FileExtension = spFileInfo?.Extension,
                    FileName = spFileInfo?.Filename,
                    // Keep the SharePoint URL within the urls.full_url column width (nvarchar(850)):
                    // strip the volatile xsdata token, else reduce to the page path. See issue #122.
                    Url = StringUtils.EnsureUrlWithinLength(spFileInfo?.Url, Common.Entities.Url.FullUrlMaxLength),
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
                return true; // staged regardless
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to stage file metadata for event {baseOfficeEvent.Id} context {contextId}");
                return false;
            }
        }

        /// <summary>
        /// True when <paramref name="contextId"/> is a known reference that Graph can never resolve to a
        /// SharePoint / OneDrive file - a local drive path (<c>C:\...</c>), a UNC path
        /// (<c>\\server\share</c>) or the <c>DataAgent</c> sentinel - so the lookup should be skipped.
        /// Copilot events routinely reference such non-SharePoint contexts (a user asking Copilot about a file
        /// on their desktop); resolving them via Graph is a guaranteed miss. Deliberately conservative: returns
        /// false (i.e. still attempt) for anything else, including URLs, opaque ids and null/empty - so behaviour
        /// is unchanged for every context except the clearly-non-SharePoint ones.
        /// </summary>
        public static bool ShouldSkipGraphFileLookup(string contextId)
        {
            if (string.IsNullOrWhiteSpace(contextId))
            {
                return false;
            }

            var id = contextId.Trim();

            // Local drive path, e.g. C:\Users\... or D:/...
            if (id.Length >= 3 && char.IsLetter(id[0]) && id[1] == ':' && (id[2] == '\\' || id[2] == '/'))
            {
                return true;
            }

            // UNC path, e.g. \\server\share\...
            if (id.StartsWith("\\\\", StringComparison.Ordinal))
            {
                return true;
            }

            // Known non-file sentinel.
            if (id.Equals("DataAgent", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
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
        /// Serializes AccessedResources list to JSON for staging table storage
        /// </summary>
        internal string SerializeAccessedResources(IEnumerable<AccessedResource> accessedResources)
        {
            if (accessedResources == null || !accessedResources.Any())
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

            _logger.LogDebug($"Committing batch: {_totalFilesCount} file(s), {_totalMeetingsCount} meeting(s), {_totalChatOnlyCount} chat-only event(s) to SQL.");

            // Per-staging-table timing. Each of the three Copilot staging tables runs the shared
            // accessed-resource / agents merge (common_upsert_copilot_agents.sql), which on Copilot-heavy
            // tenants is the dominant save cost - so time each separately to see which workload's merge is
            // expensive (the chat-only path carries accessed resources too, so it is often the largest).
            var swSp = System.Diagnostics.Stopwatch.StartNew();
            await _copilotInsertsSP.SaveToStagingTable(STAGING_INSERTS_PER_THREAD, docsMergeSql);
            swSp.Stop();
            var swTeams = System.Diagnostics.Stopwatch.StartNew();
            await _copilotInsertsTeams.SaveToStagingTable(STAGING_INSERTS_PER_THREAD, teamsMergeSql);
            swTeams.Stop();
            var swChat = System.Diagnostics.Stopwatch.StartNew();
            await _copilotInsertsChatsNoContext.SaveToStagingTable(STAGING_INSERTS_PER_THREAD, chatOnlyMergeSql);
            swChat.Stop();

            var copilotTimingMsg = $"Copilot commit timing: SP-docs {(swSp.Elapsed.TotalMilliseconds / 1000.0).ToString("n1")}s ({_totalFilesCount.ToString("n0")} file event(s)), " +
                $"Teams {(swTeams.Elapsed.TotalMilliseconds / 1000.0).ToString("n1")}s ({_totalMeetingsCount.ToString("n0")} meeting event(s)), " +
                $"chat-only {(swChat.Elapsed.TotalMilliseconds / 1000.0).ToString("n1")}s ({_totalChatOnlyCount.ToString("n0")} chat-only event(s)).";
            // Surface a slow Copilot merge (the usual bottleneck) at Information so it stands out in the traces;
            // keep routine fast batches at Debug to avoid per-batch noise.
            if (swSp.Elapsed.TotalMilliseconds + swTeams.Elapsed.TotalMilliseconds + swChat.Elapsed.TotalMilliseconds >= 5000)
                _logger.LogInformation(copilotTimingMsg);
            else
                _logger.LogDebug(copilotTimingMsg);

            // Clear lists & counters for next batch
            _copilotInsertsSP.Rows.Clear();
            _copilotInsertsTeams.Rows.Clear();
            _copilotInsertsChatsNoContext.Rows.Clear();
            _totalFilesCount = 0;
            _totalMeetingsCount = 0;
            _totalChatOnlyCount = 0;
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
