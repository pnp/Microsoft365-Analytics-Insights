using Common.Entities;
using DataUtils;
using DataUtils.Sql.Inserts;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
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
        private readonly ICopilotMetadataLoader _copilotEventAdaptor;
        private readonly ILogger _logger;
        private readonly bool _resolveResourceMetadata;
        private readonly ICopilotStagingWriter _stagingWriter;

        // Batch-level totals (across all processed events since last commit)
        private int _totalMeetingsCount;
        private int _totalFilesCount;
        private int _totalChatOnlyCount;

        /// <summary>
        /// Production entry point: builds the SQL staging writer from a connection string. Kept as a thin
        /// overload so SaveSession.Init() and every other existing call site is unchanged. See issue #367.
        /// </summary>
        public CopilotAuditEventManager(string connectionString, ICopilotMetadataLoader copilotEventAdaptor, ILogger logger, bool resolveResourceMetadata = true)
            : this(BuildSqlStagingWriter(connectionString, copilotEventAdaptor, logger), copilotEventAdaptor, logger, resolveResourceMetadata)
        {
        }

        /// <summary>
        /// Validates in the original order - connection string, then adaptor, then logger - before building
        /// the writer. A constructor initialiser runs before the constructor body, so without this the
        /// writer's own logger check would fire first and change the ParamName reported when more than one
        /// argument is null.
        /// </summary>
        private static ICopilotStagingWriter BuildSqlStagingWriter(string connectionString, ICopilotMetadataLoader copilotEventAdaptor, ILogger logger)
        {
            if (string.IsNullOrEmpty(connectionString)) throw new ArgumentException($"'{nameof(connectionString)}' cannot be null or empty.", nameof(connectionString));
            if (copilotEventAdaptor == null) throw new ArgumentNullException(nameof(copilotEventAdaptor));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            return new SqlCopilotStagingWriter(connectionString, logger);
        }

        /// <summary>
        /// Testable entry point: the adaptation rules run against any <see cref="ICopilotStagingWriter"/>,
        /// so the context-priority order, agent-metadata-only mode and the null-record guard can all be
        /// asserted with no database.
        /// </summary>
        internal CopilotAuditEventManager(ICopilotStagingWriter stagingWriter, ICopilotMetadataLoader copilotEventAdaptor, ILogger logger, bool resolveResourceMetadata = true)
        {
            _stagingWriter = stagingWriter ?? throw new ArgumentNullException(nameof(stagingWriter));
            _copilotEventAdaptor = copilotEventAdaptor ?? throw new ArgumentNullException(nameof(copilotEventAdaptor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _resolveResourceMetadata = resolveResourceMetadata;
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
                //
                // Note this loop only decides which SINGLE context gets RESOLVED (Graph lookup) into
                // copilot_event_files / copilot_event_meetings. Every context - including the ones skipped
                // here - is staged verbatim via contexts_json and lands in dbo.copilot_event_contexts, so
                // the unordered-collection caveat above no longer means data is lost.
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
                            else if (eventMeetings == 0 && eventFiles == 0 && eventChats == 0)
                            {
                                AddChatOnly(auditRecord, baseOfficeEvent);
                                eventChats++; _totalChatOnlyCount++;
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
                            else if (eventMeetings == 0 && eventFiles == 0 && eventChats == 0)
                            {
                                AddChatOnly(auditRecord, baseOfficeEvent);
                                eventChats++; _totalChatOnlyCount++;
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

                var row = new TeamsCopilotLogTempEntity
                {
                    MeetingId = meetingId,
                    MeetingCreatedUTC = meetingInfo?.CreatedUTC,
                    MeetingName = meetingInfo?.Subject,
                };
                PopulateCommonStagingFields(row, auditRecord, baseOfficeEvent);
                _stagingWriter.StageTeams(row);
                return true; // staged regardless of meetingInfo retrieval success
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    $"Failed to resolve optional meeting metadata for event {baseOfficeEvent.Id} context {contextId}; staging the Copilot interaction without meeting enrichment.");
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

                var row = new SPCopilotLogTempEntity
                {
                    FileExtension = spFileInfo?.Extension,
                    FileName = spFileInfo?.Filename,
                    // Keep the SharePoint URL within the urls.full_url column width (nvarchar(850)):
                    // strip the volatile xsdata token, else reduce to the page path. See issue #122.
                    Url = StringUtils.EnsureUrlWithinLength(spFileInfo?.Url, Common.Entities.Url.FullUrlMaxLength),
                    UrlBase = spFileInfo?.SiteUrl,
                };
                PopulateCommonStagingFields(row, auditRecord, baseOfficeEvent);
                _stagingWriter.StageSharePoint(row);
                return true; // staged regardless
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    $"Failed to resolve optional file metadata for event {baseOfficeEvent.Id} context {contextId}; staging the Copilot interaction without file enrichment.");
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
            var row = new ChatOnlyCopilotLogTempEntity();
            PopulateCommonStagingFields(row, auditRecord, baseOfficeEvent);
            _stagingWriter.StageChatOnly(row);
        }

        /// <summary>
        /// Fills the fields every Copilot staging row carries, regardless of workload (SharePoint
        /// file / Teams meeting / chat-only). Kept in one place so a newly persisted audit field
        /// can't be wired into two of the three staging paths and silently dropped on the third.
        /// </summary>
        private void PopulateCommonStagingFields(BaseCopilotLogTempEntity row, CopilotAuditLogContent auditRecord, CommonAuditEvent baseOfficeEvent)
        {
            row.EventId = baseOfficeEvent.Id;
            // app_host is a NOT NULL staging column, so fall back rather than failing the whole
            // batch insert on a payload that omits AppHost (previously only the chat-only path did).
            row.AppHost = auditRecord.CopilotEventData?.AppHost ?? "Unknown";
            row.AgentId = auditRecord.AgentId;
            row.AgentName = auditRecord.AgentName;
            row.IsCustomAgent = auditRecord.IsCustomAgent;
            row.AccessedResourcesJson = SerializeAccessedResources(auditRecord.CopilotEventData?.AccessedResources);
            row.MessagesJson = SerializeMessages(auditRecord);
            row.ModelTransparencyDetailsJson = SerializeModelTransparencyDetails(auditRecord);
            row.ContextsJson = SerializeContexts(auditRecord.CopilotEventData?.Contexts);
            row.AISystemPluginsJson = SerializeAISystemPlugins(auditRecord);
            row.ThreadId = auditRecord.CopilotEventData?.ThreadId;
            row.ClientRegion = auditRecord.ClientRegion;
            row.CopilotLogVersion = auditRecord.CopilotLogVersion;
            row.CopilotCreditEstimateTotal = auditRecord.Cost?.TotalCredits;
            row.CopilotCreditEstimateJson = SerializeCopilotCreditEstimation(auditRecord);
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
        ///
        /// BOTH prompts and responses are serialized. Previously only responses (IsPrompt = false)
        /// were kept, because the only field persisted was the message id and a prompt row carried
        /// no extra information. Now that the schema's Size is persisted, the prompt row is the only
        /// source of the interaction's input volume, and the persisted is_prompt flag would be a
        /// constant if prompts were still dropped. Cost estimation is unaffected: it reads
        /// ParsedAuditEvent directly and does its own IsPrompt filtering.
        /// </summary>
        internal string SerializeMessages(CopilotAuditLogContent auditRecord)
        {
            if (auditRecord?.ParsedAuditEvent?.Messages == null || auditRecord.ParsedAuditEvent.Messages.Count == 0)
            {
                return null;
            }

            try
            {
                return JsonConvert.SerializeObject(auditRecord.ParsedAuditEvent.Messages);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to serialize Messages from audit record");
                return null;
            }
        }

        /// <summary>
        /// Serializes the interaction's Contexts to JSON for staging table storage. ALL contexts are
        /// serialized, including the ones the file/meeting resolution above ignores (it only ever
        /// acts on the first file or meeting context), so nothing in an unordered Contexts collection
        /// is silently discarded.
        /// </summary>
        internal string SerializeContexts(IEnumerable<Context> contexts)
        {
            if (contexts == null || !contexts.Any())
            {
                return null;
            }

            try
            {
                return JsonConvert.SerializeObject(contexts);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to serialize Contexts");
                return null;
            }
        }

        /// <summary>
        /// Serializes the AISystemPlugin entries (plugins / connectors that grounded the answer) to
        /// JSON for staging table storage. Prefers the strongly-typed CopilotEventData collection and
        /// falls back to the parsed audit event, since the two are populated from the same payload
        /// but different call sites construct one or the other.
        /// </summary>
        internal string SerializeAISystemPlugins(CopilotAuditLogContent auditRecord)
        {
            var plugins = auditRecord?.CopilotEventData?.AISystemPlugin;
            if (plugins == null || plugins.Count == 0)
            {
                plugins = auditRecord?.ParsedAuditEvent?.AISystemPlugin;
            }

            if (plugins == null || plugins.Count == 0)
            {
                return null;
            }

            try
            {
                return JsonConvert.SerializeObject(plugins);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to serialize AISystemPlugin entries from audit record");
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
            _logger.LogDebug($"Committing batch: {_totalFilesCount} file(s), {_totalMeetingsCount} meeting(s), {_totalChatOnlyCount} chat-only event(s) to SQL.");

            // The staging tables, merge scripts and per-table timing now live in the writer (see #367).
            // The timings come back so this trace keeps interleaving them with the per-workload event
            // counts, which only the manager knows.
            var timings = await _stagingWriter.CommitAllChanges();

            var copilotTimingMsg = $"Copilot commit timing: SP-docs {(timings.SharePoint.TotalMilliseconds / 1000.0).ToString("n1")}s ({_totalFilesCount.ToString("n0")} file event(s)), " +
                $"Teams {(timings.Teams.TotalMilliseconds / 1000.0).ToString("n1")}s ({_totalMeetingsCount.ToString("n0")} meeting event(s)), " +
                $"chat-only {(timings.ChatOnly.TotalMilliseconds / 1000.0).ToString("n1")}s ({_totalChatOnlyCount.ToString("n0")} chat-only event(s)).";
            // Surface a slow Copilot merge (the usual bottleneck) at Information so it stands out in the traces;
            // keep routine fast batches at Debug to avoid per-batch noise.
            if (timings.Total.TotalMilliseconds >= 5000)
                _logger.LogInformation(copilotTimingMsg);
            else
                _logger.LogDebug(copilotTimingMsg);

            // Clear counters for next batch. The staged rows are cleared by the writer.
            _totalFilesCount = 0;
            _totalMeetingsCount = 0;
            _totalChatOnlyCount = 0;
        }

        /// <summary>
        /// Repairs any <c>dbo.copilot_chats</c> rows that are missing the denormalised <c>user_id</c> /
        /// <c>time_stamp</c> columns, copying them from the parent audit event.
        ///
        /// <para>
        /// Migration <c>DenormaliseCopilotChatUserAndTime</c> backfills existing rows once and is then stamped,
        /// but the columns stay NULLable - so a chat inserted by an OLDER importer during the upgrade window,
        /// or into a clustered-key range the backfill had already passed, keeps NULL for ever. Every Copilot
        /// report filters <c>c.time_stamp &gt;= @from</c>, and NULL fails that comparison, so such rows would be
        /// silently missing from the figures. That is the defect class issue #360 was raised for, so the
        /// importer heals them rather than relying on the upgrade being performed perfectly.
        /// </para>
        /// <para>
        /// <b>Called once per import CYCLE from the web-job top level</b> (<c>Program.cs</c>, immediately after
        /// the <c>DownloadActivityData</c> try/catch and OUTSIDE it), deliberately not from the merge SQL, not
        /// per save batch, and not from <c>ActivityImporter.LoadReportsAndSave</c>. Each of those was tried and
        /// each leaves a hole: the merge only runs when its staging queue has rows; the save path is skipped
        /// when a cycle downloads nothing; and <c>LoadReportsAndSave</c> is skipped when the activity import
        /// throws or when no organisation URLs are configured. The repair only needs SQL, so it must not be
        /// gated on the import having succeeded.
        /// </para>
        /// <para>
        /// Costs 4 logical reads when there is nothing to do (a seek to the head of
        /// <c>IX_copilot_chats_time_stamp_user_id</c>, where NULLs sort first), measured on a 12M-row table.
        /// Never allowed to fail an import: an import that saved its data is a success even if this
        /// maintenance step could not run, and it is simply retried on the next cycle.
        /// </para>
        /// <para>
        /// The command timeout is finite on purpose. <c>CommandTimeout = 0</c> would mean that if the UPDATE
        /// were ever blocked on a lock it would wait for ever - and a hang is not an exception, so the
        /// try/catch below could not rescue it and the web job's cycle would stall indefinitely. Timing out
        /// and retrying on the next cycle is strictly safer for a best-effort maintenance step.
        /// </para>
        /// </summary>
        public static async Task RepairDenormalisedColumnsAsync(string connectionString, ILogger logger)
        {
            if (string.IsNullOrEmpty(connectionString)) return;

            // Generous enough for the bounded 1M-row drain (measured ~107s for 12M rows), but finite.
            const int repairTimeoutSecs = 1800;

            try
            {
                var rr = new ProjectResourceReader(System.Reflection.Assembly.GetExecutingAssembly());
                var sql = rr.ReadResourceString(
                    "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.repair_denormalised_copilot_columns.sql");

                using (var con = new SqlConnection(connectionString))
                {
                    await con.OpenAsync();
                    using (var cmd = new SqlCommand(sql, con) { CommandTimeout = repairTimeoutSecs })
                    {
                        var repaired = await cmd.ExecuteScalarAsync();
                        var count = repaired == null || repaired == DBNull.Value ? 0L : Convert.ToInt64(repaired);
                        if (count > 0)
                        {
                            logger.LogWarning(
                                $"Repaired {count:n0} Copilot interaction(s) that were missing their denormalised "
                                + "user/timestamp columns, and were therefore invisible to the Copilot Adoption "
                                + "report. This is expected shortly after upgrading; if it keeps happening, an "
                                + "older importer build is still writing to this database.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not repair denormalised Copilot columns this cycle. The import itself succeeded; "
                    + "any affected interactions stay hidden from Copilot reports until a later cycle repairs them.");
            }
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
