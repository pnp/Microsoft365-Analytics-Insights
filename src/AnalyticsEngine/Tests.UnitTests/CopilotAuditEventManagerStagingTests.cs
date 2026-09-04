using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Coverage for the Copilot adaptation rules that issue #367 freed from the SQL dependency - the
    /// context-priority order, agent-metadata-only mode and the null-record guard.
    ///
    /// These run with zero SQL Server, Graph, Redis or Service Bus: the manager now takes an
    /// <c>ICopilotStagingWriter</c>, so an in-memory writer captures what would have been staged.
    /// The pre-existing CopilotTests suite still covers the SQL merge end to end.
    /// </summary>
    [TestClass]
    public class CopilotAuditEventManagerStagingTests : CopilotTestBase
    {
        // Must contain the literal "19:meeting_" - StringUtils.GetMeetingIdFragmentFromMeetingThreadUrl
        // parses from there, and throws (so the row is not staged) for anything else.
        private const string MeetingContextId = "https://contoso.teams.com/threads/19:meeting_NDkyZTJhMWEtM2Y@thread.v2";
        private const string ChatContextId = "https://contoso.teams.com/threads/19:chat@thread.v2";
        private const string FileContextId = "https://contoso.sharepoint.com/sites/example/Shared Documents/καλημέρα.docx";

        private static CommonAuditEvent NewEvent()
        {
            return new CommonAuditEvent
            {
                TimeStamp = new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc),
                Operation = new EventOperation { Name = "Copilot Interaction" },
                User = new User { AzureAdId = "00000000-0000-0000-0000-000000000000", UserPrincipalName = "chris@contoso.onmicrosoft.com" },
                Id = Guid.NewGuid()
            };
        }

        private static CopilotAuditLogContent ContentWith(params Context[] contexts)
        {
            return new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData
                {
                    AppHost = "Teams",
                    Contexts = new List<Context>(contexts)
                }
            };
        }

        private static Context Meeting() => new Context { Id = MeetingContextId, Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING };
        private static Context Chat() => new Context { Id = ChatContextId, Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT };
        private static Context File() => new Context { Id = FileContextId, Type = "SharePointFile" };

        private static CopilotAuditEventManager NewManager(InMemoryCopilotStagingWriter writer, bool resolveResourceMetadata = true)
        {
            return new CopilotAuditEventManager(writer, new FakeCopilotMetadataLoader(), NullLogger.Instance, resolveResourceMetadata);
        }

        [TestMethod]
        public async Task CopilotEventManager_MeetingContext_TakesPriorityOverChat_StagesMeetingOnly()
        {
            // Documented, load-bearing behaviour: a meeting context ends iteration, so a trailing chat
            // context is never staged. The `break` is intentional - see the note in the manager.
            var writer = new InMemoryCopilotStagingWriter();

            await NewManager(writer).SaveSingleCopilotEventToSqlStaging(ContentWith(Meeting(), Chat()), NewEvent());

            Assert.AreEqual(1, writer.TeamsRows.Count, "The meeting context must be staged.");
            Assert.AreEqual(0, writer.ChatOnlyRows.Count, "The trailing chat context must not also be staged.");
            Assert.AreEqual(0, writer.SharePointRows.Count);
        }

        [TestMethod]
        public async Task CopilotEventManager_FileContext_BreaksBeforeChat()
        {
            var writer = new InMemoryCopilotStagingWriter();

            await NewManager(writer).SaveSingleCopilotEventToSqlStaging(ContentWith(File(), Chat()), NewEvent());

            Assert.AreEqual(1, writer.SharePointRows.Count, "The file context must be staged.");
            Assert.AreEqual(0, writer.ChatOnlyRows.Count, "The trailing chat context must not also be staged.");
            Assert.AreEqual(0, writer.TeamsRows.Count);
        }

        [TestMethod]
        public async Task CopilotEventManager_MultipleMeetingContexts_StagesOnlyOne()
        {
            // Pins the `eventMeetings == 0` guard specifically - the regression that used to put the same
            // event_id in staging twice and blow up the copilot_chats primary key during the merge.
            // The `break` itself is pinned by MeetingContext_TakesPriorityOverChat, not by this test.
            var writer = new InMemoryCopilotStagingWriter();

            await NewManager(writer).SaveSingleCopilotEventToSqlStaging(ContentWith(Meeting(), Meeting(), Meeting()), NewEvent());

            Assert.AreEqual(1, writer.TeamsRows.Count);
            Assert.AreEqual(1, writer.TotalStaged);
        }

        [TestMethod]
        public async Task CopilotEventManager_MultipleChatContexts_StagesOnlyOne()
        {
            var writer = new InMemoryCopilotStagingWriter();

            await NewManager(writer).SaveSingleCopilotEventToSqlStaging(ContentWith(Chat(), Chat(), Chat()), NewEvent());

            Assert.AreEqual(1, writer.ChatOnlyRows.Count);
            Assert.AreEqual(1, writer.TotalStaged);
        }

        [TestMethod]
        public async Task CopilotEventManager_ResolveResourceMetadataFalse_StagesEveryEventAsChatOnly_AndMakesNoGraphCall()
        {
            // Agent-metadata-only mode: every interaction becomes a chat-only row and no Graph
            // file/meeting resolution happens at all - that is the whole point, since those calls are
            // serial and network-bound on the save path.
            var writer = new InMemoryCopilotStagingWriter();
            var loader = new RecordingCopilotMetadataLoader();
            var manager = new CopilotAuditEventManager(writer, loader, NullLogger.Instance, resolveResourceMetadata: false);

            await manager.SaveSingleCopilotEventToSqlStaging(ContentWith(Meeting()), NewEvent());
            await manager.SaveSingleCopilotEventToSqlStaging(ContentWith(File()), NewEvent());
            await manager.SaveSingleCopilotEventToSqlStaging(ContentWith(Chat()), NewEvent());

            Assert.AreEqual(3, writer.ChatOnlyRows.Count, "Every interaction is staged chat-only, whatever its context.");
            Assert.AreEqual(0, writer.TeamsRows.Count);
            Assert.AreEqual(0, writer.SharePointRows.Count);
            Assert.AreEqual(0, (loader.MeetingCalls + loader.FileCalls + loader.UserIdCalls), "No Graph resolution may happen in agent-metadata-only mode.");
        }

        [TestMethod]
        public async Task CopilotEventManager_ResolveResourceMetadataTrue_DoesCallGraph()
        {
            // Counterpart to the test above, so it cannot pass merely because the loader is never used.
            var writer = new InMemoryCopilotStagingWriter();
            var loader = new RecordingCopilotMetadataLoader();
            var manager = new CopilotAuditEventManager(writer, loader, NullLogger.Instance, resolveResourceMetadata: true);

            await manager.SaveSingleCopilotEventToSqlStaging(ContentWith(Meeting()), NewEvent());

            Assert.IsTrue((loader.MeetingCalls + loader.FileCalls + loader.UserIdCalls) > 0, "With resolution enabled the meeting context must be resolved via Graph.");
        }

        [TestMethod]
        public async Task CopilotEventManager_NullAuditRecordOrBaseEvent_StagesNothing()
        {
            var writer = new InMemoryCopilotStagingWriter();
            var manager = NewManager(writer);

            await manager.SaveSingleCopilotEventToSqlStaging(null, NewEvent());
            await manager.SaveSingleCopilotEventToSqlStaging(ContentWith(Chat()), null);
            await manager.SaveSingleCopilotEventToSqlStaging(null, null);

            Assert.AreEqual(0, writer.TotalStaged, "A null record must be dropped, not staged and not thrown.");
        }

        [TestMethod]
        public async Task CopilotEventManager_NoContexts_StagesChatOnly()
        {
            var writer = new InMemoryCopilotStagingWriter();

            await NewManager(writer).SaveSingleCopilotEventToSqlStaging(ContentWith(), NewEvent());

            Assert.AreEqual(1, writer.ChatOnlyRows.Count, "An interaction with no context still carries agent metadata worth keeping.");
        }

        [TestMethod]
        public void CopilotEventManager_BothDependenciesNull_ReportsTheAdaptorFirst_AsBefore()
        {
            // A constructor initialiser runs before the constructor body, so routing through the SQL
            // writer would otherwise report "logger" where the original reported "copilotEventAdaptor".
            // Nothing depends on that ParamName today, but silently changing it is not an extraction.
            try
            {
                new CopilotAuditEventManager("Server=.;Database=x;Integrated Security=true", null, null);
                Assert.Fail("Expected an ArgumentNullException.");
            }
            catch (ArgumentNullException ex)
            {
                Assert.AreEqual("copilotEventAdaptor", ex.ParamName,
                    "Validation order must stay connection string -> adaptor -> logger.");
            }
        }

        [TestMethod]
        public void CopilotEventManager_BlankConnectionString_StillThrowsArgumentException()
        {
            foreach (var bad in new[] { null, string.Empty })
            {
                try
                {
                    new CopilotAuditEventManager(bad, new FakeCopilotMetadataLoader(), NullLogger.Instance);
                    Assert.Fail("The original constructor rejected a blank connection string; that must not change.");
                }
                catch (ArgumentException ex)
                {
                    Assert.AreEqual(typeof(ArgumentException), ex.GetType(),
                        "Must stay a plain ArgumentException - ArgumentNullException derives from it, so a bare catch would not notice the type changing.");
                    Assert.AreEqual("connectionString", ex.ParamName);
                }
            }
        }

        [TestMethod]
        public async Task CopilotEventManager_CommitAllChanges_ClearsStagedRows()
        {
            // Pins the SEAM CONTRACT - that the manager delegates the commit and does not re-stage the
            // same batch.
            //
            // It deliberately cannot pin SqlCopilotStagingWriter's own Rows.Clear() calls, and neither
            // does anything else today: the DB-backed CopilotEventManagerCommitResetsStateForNextBatch
            // stages only a chat-only row and asserts final copilot_chats counts, which stay correct
            // even if the lists were never cleared, because the common merge de-duplicates chats.
            //
            // The SQL adapter's own Rows.Clear() calls need a DB-backed guard because staging is only
            // List.Add, but committing a non-empty batch opens a connection. That separate guard covers
            // all three concrete staging batches.
            var writer = new InMemoryCopilotStagingWriter();
            var manager = NewManager(writer);

            await manager.SaveSingleCopilotEventToSqlStaging(ContentWith(Chat()), NewEvent());
            Assert.AreEqual(1, writer.TotalStaged);

            await manager.CommitAllChanges();

            Assert.AreEqual(1, writer.CommitCount);
            Assert.AreEqual(0, writer.TotalStaged, "A committed batch must not be re-staged by the next commit.");
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void CopilotEventManager_NullStagingWriter_Throws()
        {
            new CopilotAuditEventManager((ICopilotStagingWriter)null, new FakeCopilotMetadataLoader(), NullLogger.Instance);
        }

        [TestMethod]
        public async Task SqlCopilotStagingWriter_CommitAllChanges_ClearsAllThreeStagedBatches()
        {
            var logger = new Tests.UnitTests.FakeLoaderClasses.RecordingLogger();
            var meetingEventId = Guid.NewGuid();
            var fileEventId = Guid.NewGuid();
            var chatEventId = Guid.NewGuid();
            var suffix = DateTime.UtcNow.Ticks.ToString();
            var meetingUpn = "meeting-clear-" + suffix + "@contoso.onmicrosoft.com";
            var fileUpn = "file-clear-" + suffix + "@contoso.onmicrosoft.com";
            var chatUpn = "chat-clear-" + suffix + "@contoso.onmicrosoft.com";

            using (var con = new SqlConnection(_config.ConnectionStrings.DatabaseConnectionString))
            {
                con.Open();
                try
                {
                    InsertAuditEvent(con, meetingEventId, meetingUpn);
                    InsertAuditEvent(con, fileEventId, fileUpn);
                    InsertAuditEvent(con, chatEventId, chatUpn);

                    var writer = new SqlCopilotStagingWriter(_config.ConnectionStrings.DatabaseConnectionString, logger);
                    writer.StageTeams(new TeamsCopilotLogTempEntity
                    {
                        EventId = meetingEventId,
                        AppHost = "Teams",
                        MeetingId = "19:meeting-clear@thread.v2",
                        MeetingCreatedUTC = new DateTime(2026, 4, 1, 9, 30, 0, DateTimeKind.Utc),
                        MeetingName = "Contoso release sync"
                    });
                    writer.StageSharePoint(new SPCopilotLogTempEntity
                    {
                        EventId = fileEventId,
                        AppHost = "Word",
                        FileName = "Καλημέρα κόσμε",
                        FileExtension = "docx",
                        Url = "https://contoso.sharepoint.com/sites/example/Shared Documents/Καλημέρα κόσμε.docx",
                        UrlBase = "https://contoso.sharepoint.com/sites/example"
                    });
                    writer.StageChatOnly(new ChatOnlyCopilotLogTempEntity
                    {
                        EventId = chatEventId,
                        AppHost = "Teams"
                    });

                    await writer.CommitAllChanges();

                    var logEntryCountAfterFirstCommit = logger.Entries.Count;
                    var chatCountAfterFirstCommit = CountChats(con, meetingEventId, fileEventId, chatEventId);

                    await writer.CommitAllChanges();

                    var secondCommitInsertLogs = logger.Entries
                        .Skip(logEntryCountAfterFirstCommit)
                        .Where(IsInsertBatchLog)
                        .Select(e => e.Message)
                        .ToList();
                    Assert.AreEqual(0, secondCommitInsertLogs.Count,
                        "An empty second commit must not re-save any retained SharePoint, Teams or chat-only rows: "
                        + string.Join(Environment.NewLine, secondCommitInsertLogs));

                    Assert.AreEqual(chatCountAfterFirstCommit, CountChats(con, meetingEventId, fileEventId, chatEventId));
                }
                finally
                {
                    DeleteAuditEvents(con, meetingEventId, fileEventId, chatEventId, meetingUpn, fileUpn, chatUpn);
                }
            }
        }

        private static void InsertAuditEvent(SqlConnection con, Guid eventId, string upn)
        {
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM dbo.users WHERE user_name = @upn)
    INSERT INTO dbo.users (user_name, azure_ad_id) VALUES (@upn, @azureAdId);

INSERT INTO dbo.audit_events (id, time_stamp, user_id)
SELECT @eventId, @timestamp, u.id
FROM dbo.users u
WHERE u.user_name = @upn;";
                cmd.Parameters.AddWithValue("@upn", upn);
                cmd.Parameters.AddWithValue("@azureAdId", "00000000-0000-0000-0000-000000000000");
                cmd.Parameters.AddWithValue("@eventId", eventId);
                cmd.Parameters.AddWithValue("@timestamp", new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc));
                cmd.ExecuteNonQuery();
            }
        }

        private static int CountChats(SqlConnection con, Guid meetingEventId, Guid fileEventId, Guid chatEventId)
        {
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = @"
SELECT COUNT(*)
FROM dbo.copilot_chats
WHERE event_id IN (@meetingEventId, @fileEventId, @chatEventId);";
                cmd.Parameters.AddWithValue("@meetingEventId", meetingEventId);
                cmd.Parameters.AddWithValue("@fileEventId", fileEventId);
                cmd.Parameters.AddWithValue("@chatEventId", chatEventId);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        private static void DeleteAuditEvents(SqlConnection con, Guid meetingEventId, Guid fileEventId, Guid chatEventId, string meetingUpn, string fileUpn, string chatUpn)
        {
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = @"
DELETE FROM dbo.copilot_event_files WHERE copilot_chat_id IN (@meetingEventId, @fileEventId, @chatEventId);
DELETE FROM dbo.copilot_event_meetings WHERE copilot_chat_id IN (@meetingEventId, @fileEventId, @chatEventId);
DELETE FROM dbo.copilot_chats WHERE event_id IN (@meetingEventId, @fileEventId, @chatEventId);
DELETE FROM dbo.audit_events WHERE id IN (@meetingEventId, @fileEventId, @chatEventId);
DELETE FROM dbo.users WHERE user_name IN (@meetingUpn, @fileUpn, @chatUpn);";
                cmd.Parameters.AddWithValue("@meetingEventId", meetingEventId);
                cmd.Parameters.AddWithValue("@fileEventId", fileEventId);
                cmd.Parameters.AddWithValue("@chatEventId", chatEventId);
                cmd.Parameters.AddWithValue("@meetingUpn", meetingUpn);
                cmd.Parameters.AddWithValue("@fileUpn", fileUpn);
                cmd.Parameters.AddWithValue("@chatUpn", chatUpn);
                cmd.ExecuteNonQuery();
            }
        }

        private static bool IsInsertBatchLog(Tests.UnitTests.FakeLoaderClasses.RecordingLogger.Entry entry)
        {
            return entry.Level == LogLevel.Information
                && entry.Message.StartsWith("Inserting ", StringComparison.Ordinal)
                && entry.Message.Contains(" records into ");
        }
    }
}
