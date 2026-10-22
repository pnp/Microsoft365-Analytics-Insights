using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Persistence;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Coverage for the Copilot adaptation rules that issue #367 freed from the SQL dependency - the
    /// context-priority order, agent-metadata-only mode and the null-record guard.
    ///
    /// These run with zero SQL Server, Graph, Azure Storage or Service Bus: the manager now takes an
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

        #region Transient Graph failures during enrichment (B4)

        /// <summary>
        /// Meeting and file lookups that fail <c>FailuresLeft</c> times with the given exception, then succeed.
        /// </summary>
        private sealed class FlakyCopilotMetadataLoader : ICopilotMetadataLoader
        {
            private readonly Func<Exception> _failure;
            public int FailuresLeft;
            public int MeetingCalls;
            public int FileCalls;
            public string Subject = "Contoso weekly sync";

            public FlakyCopilotMetadataLoader(int failures, Func<Exception> failure)
            {
                FailuresLeft = failures;
                _failure = failure;
            }

            public Task<string> GetUserIdFromUpn(string userPrincipalName) => Task.FromResult("00000000-0000-0000-0000-000000000000");

            public Task<MeetingMetadata> GetMeetingInfo(string meetingId, string userGuid)
            {
                MeetingCalls++;
                if (FailuresLeft-- > 0) throw _failure();
                return Task.FromResult(new MeetingMetadata { MeetingId = meetingId, CreatedUTC = new DateTime(2026, 4, 1, 9, 30, 0, DateTimeKind.Utc), Subject = Subject });
            }

            public Task<SpoDocumentFileInfo> GetSpoFileInfo(string copilotId, string eventUpn)
            {
                FileCalls++;
                if (FailuresLeft-- > 0) throw _failure();
                return Task.FromResult(new SpoDocumentFileInfo
                {
                    Filename = "Καλημέρα κόσμε.docx",
                    Extension = "docx",
                    Url = FileContextId,
                    SiteUrl = "https://contoso.sharepoint.com/sites/example"
                });
            }
        }

        private static Exception TransientGraphFailure() => new CopilotEnrichmentTransientException("Graph returned HTTP 503.");

        private static ODataError Unavailable() => new ODataError
        {
            ResponseStatusCode = 503,
            Error = new MainError { Code = "serviceNotAvailable", Message = "Synthetic Graph error" }
        };

        private static CopilotAuditEventManager NewManagerWith(InMemoryCopilotStagingWriter writer, ICopilotMetadataLoader loader, Microsoft.Extensions.Logging.ILogger logger = null)
            => new CopilotAuditEventManager(writer, loader, logger ?? NullLogger.Instance);

        /// <summary>An interaction about the file at <paramref name="url"/>, by a user the prewarm can see.</summary>
        private static CopilotAuditLogContent ContentWithFile(string url)
        {
            var content = ContentWith(new Context { Id = url, Type = "SharePointFile" });
            content.UserId = "chris@contoso.onmicrosoft.com";
            return content;
        }

        /// <summary>
        /// Inside the save's SQL lock a meeting lookup is made once: a transient failure saves the interaction without
        /// its meeting, and counts it, rather than waiting with the lock held.
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManager_TransientMeetingLookupFailure_IsTriedOnceAndCounted()
        {
            var writer = new InMemoryCopilotStagingWriter();
            var loader = new FlakyCopilotMetadataLoader(1, TransientGraphFailure);
            var manager = NewManagerWith(writer, loader);

            await manager.SaveSingleCopilotEventToSqlStaging(ContentWith(Meeting()), NewEvent());

            Assert.AreEqual(1, loader.MeetingCalls, "No retry inside the save lock.");
            Assert.AreEqual(0, writer.TeamsRows.Count);
            Assert.AreEqual(1, writer.ChatOnlyRows.Count, "The interaction is still saved.");
            Assert.AreEqual(1, manager.UnenrichedAfterTransientFailureCount);
        }

        [TestMethod]
        public async Task CopilotEventManager_PersistentTransientFileLookupFailure_IsTriedOnceCountedAndReported()
        {
            var writer = new InMemoryCopilotStagingWriter();
            var loader = new FlakyCopilotMetadataLoader(int.MaxValue, TransientGraphFailure);
            var logger = new Tests.UnitTests.FakeLoaderClasses.RecordingLogger();
            var manager = NewManagerWith(writer, loader, logger);

            await manager.SaveSingleCopilotEventToSqlStaging(ContentWith(File()), NewEvent());

            Assert.AreEqual(1, loader.FileCalls, "No retry inside the save lock.");
            Assert.AreEqual(1, writer.ChatOnlyRows.Count, "The interaction is still saved.");
            Assert.AreEqual(1, manager.UnenrichedAfterTransientFailureCount);

            await manager.CommitAllChanges();

            var summary = logger.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.StartsWith("Copilot enrichment:")).ToList();
            Assert.AreEqual(1, summary.Count, "Interactions saved without their enrichment must be visible to operators.");
            StringAssert.Contains(summary[0].Message, "1 interaction(s)");
            Assert.AreEqual(0, manager.UnenrichedAfterTransientFailureCount, "The count is per batch.");
        }

        /// <summary>
        /// Through the real Graph loader. The save holds the process-wide SQL lock while it stages, so it must never
        /// wait: each file lookup is made once, however many fail transiently, and none of the run's retries is spent.
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManager_InTheSaveLock_TransientFileLookupsAreTriedOnceWithoutWaiting()
        {
            var graph = new Tests.UnitTests.FakeLoaderClasses.FakeSpoGraphClient { OnGetDriveItemByUrl = url => throw Unavailable() };
            var loader = new GraphFileMetadataLoader(graph, NullLogger.Instance);
            var waits = 0;
            loader.RetryDelay = delay => { waits++; return Task.CompletedTask; };
            var writer = new InMemoryCopilotStagingWriter();
            var manager = NewManagerWith(writer, loader);
            const int events = 5;

            for (var i = 0; i < events; i++)
            {
                await manager.SaveSingleCopilotEventToSqlStaging(ContentWith(File()), NewEvent());
            }

            Assert.AreEqual(0, waits, "No back-off while the save lock is held.");
            Assert.AreEqual(events, graph.GetDriveItemByUrlCalls, "One attempt per lookup.");
            Assert.AreEqual(0, loader.TransientRetriesSpent);
            Assert.AreEqual(events, manager.UnenrichedAfterTransientFailureCount);
            Assert.AreEqual(events, writer.SharePointRows.Count + writer.ChatOnlyRows.Count, "Every interaction is still saved.");
        }

        /// <summary>
        /// The retries happen in the prewarm, outside the save lock: a lookup that fails transiently once or twice still
        /// resolves there, and the save then finds the file cached, with no Graph call or wait of its own.
        /// </summary>
        [DataTestMethod]
        [DataRow(1)]
        [DataRow(2)]
        public async Task Prewarm_TransientFileLookup_IsRetriedOutsideTheLockAndTheSaveFindsItCached(int failures)
        {
            var failuresLeft = failures;
            var graph = new Tests.UnitTests.FakeLoaderClasses.FakeSpoGraphClient
            {
                OnGetDriveItemByUrl = url => failuresLeft-- > 0 ? throw Unavailable() : new Microsoft.Graph.Models.DriveItem { WebUrl = url }
            };
            var loader = new GraphFileMetadataLoader(graph, NullLogger.Instance);
            var waits = 0;
            loader.RetryDelay = delay => { waits++; return Task.CompletedTask; };
            var content = ContentWithFile(FileContextId);

            await new CopilotMetadataPrewarmer(new Tests.UnitTests.FakeLoaderClasses.FakeCopilotMetadataLoaderFactory(loader), NullLogger.Instance)
                .GetLoaderAndPrewarmAsync(new[] { content }, true);

            Assert.AreEqual(failures, waits, "The prewarm waited before each retry.");
            Assert.AreEqual(failures + 1, graph.GetDriveItemByUrlCalls);

            var writer = new InMemoryCopilotStagingWriter();
            var manager = NewManagerWith(writer, loader);
            await manager.SaveSingleCopilotEventToSqlStaging(content, NewEvent());

            Assert.AreEqual(failures + 1, graph.GetDriveItemByUrlCalls, "The save found the file cached.");
            Assert.AreEqual(failures, waits, "And did not wait.");
            Assert.AreEqual("καλημέρα.docx", writer.SharePointRows.Single().FileName);
            Assert.AreEqual(0, manager.UnenrichedAfterTransientFailureCount);
        }

        /// <summary>
        /// Under sustained throttling the retries must be bounded per RUN. Each batch has its own save session and
        /// manager, so a per-batch budget started again with every batch - hundreds a cycle at 200k-user scale. The
        /// shared loader's budget runs out once and says so once; after that every lookup is tried once.
        /// </summary>
        [TestMethod]
        public async Task Prewarm_SustainedTransientFailures_SpendAtMostTheRunsRetryBudgetAcrossBatches()
        {
            var sharesCalls = 0;
            var graph = new Tests.UnitTests.FakeLoaderClasses.FakeSpoGraphClient
            {
                OnGetDriveItemByUrl = url => { Interlocked.Increment(ref sharesCalls); throw Unavailable(); }
            };
            var logger = new Tests.UnitTests.FakeLoaderClasses.RecordingLogger();
            var loader = new GraphFileMetadataLoader(graph, logger);
            var waits = 0;
            loader.RetryDelay = delay => { Interlocked.Increment(ref waits); return Task.CompletedTask; };
            var prewarmer = new CopilotMetadataPrewarmer(new Tests.UnitTests.FakeLoaderClasses.FakeCopilotMetadataLoaderFactory(loader), NullLogger.Instance);
            const int batches = 4, filesPerBatch = 20;
            var unenriched = 0;

            for (var batch = 0; batch < batches; batch++)
            {
                var contents = Enumerable.Range(0, filesPerBatch)
                    .Select(i => ContentWithFile($"https://contoso.sharepoint.com/sites/example/Shared Documents/Καλημέρα κόσμε {batch}-{i}.docx"))
                    .ToList();
                await prewarmer.GetLoaderAndPrewarmAsync(contents, true);

                var manager = NewManagerWith(new InMemoryCopilotStagingWriter(), loader);
                foreach (var content in contents)
                {
                    await manager.SaveSingleCopilotEventToSqlStaging(content, NewEvent());
                }
                unenriched += manager.UnenrichedAfterTransientFailureCount;
                await manager.CommitAllChanges();
            }

            const int lookups = batches * filesPerBatch;
            Assert.AreEqual(GraphFileMetadataLoader.TransientRetryBudgetPerRun, waits, "Retries are bounded per run, not per batch.");
            Assert.AreEqual(GraphFileMetadataLoader.TransientRetryBudgetPerRun, loader.TransientRetriesSpent);
            Assert.AreEqual(2 * lookups + GraphFileMetadataLoader.TransientRetryBudgetPerRun, sharesCalls,
                "One attempt in the prewarm and one in the save per lookup, plus the run's retries.");
            Assert.AreEqual(lookups, unenriched, "Every interaction saved without its file is counted.");
            Assert.AreEqual(1, logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.StartsWith("Copilot file enrichment:")),
                "The spent budget is reported once.");
        }

        [TestMethod]
        public async Task CopilotEventManager_NonTransientLookupFailure_IsNotRetriedOrCountedAsTransient()
        {
            var writer = new InMemoryCopilotStagingWriter();
            var loader = new FlakyCopilotMetadataLoader(int.MaxValue, () => new InvalidOperationException("Not a transient failure"));
            var manager = NewManagerWith(writer, loader);

            await manager.SaveSingleCopilotEventToSqlStaging(ContentWith(Meeting()), NewEvent());

            Assert.AreEqual(1, loader.MeetingCalls);
            Assert.AreEqual(1, writer.ChatOnlyRows.Count);
            Assert.AreEqual(0, manager.UnenrichedAfterTransientFailureCount);
        }

        /// <summary>
        /// Kiota throws a plain ApiException, not an ODataError, when an error response has no body it can parse. A 429
        /// or a 5xx is still transient by its status code, so the interaction saved without its meeting is counted.
        /// </summary>
        [DataTestMethod]
        [DataRow(429)]
        [DataRow(503)]
        public async Task CopilotEventManager_TransientApiExceptionWithoutABody_IsCounted(int status)
        {
            var writer = new InMemoryCopilotStagingWriter();
            var loader = new FlakyCopilotMetadataLoader(int.MaxValue,
                () => new Microsoft.Kiota.Abstractions.ApiException("No error body") { ResponseStatusCode = status });
            var manager = NewManagerWith(writer, loader);

            await manager.SaveSingleCopilotEventToSqlStaging(ContentWith(Meeting()), NewEvent());

            Assert.AreEqual(1, writer.ChatOnlyRows.Count, "The interaction is still saved.");
            Assert.AreEqual(1, manager.UnenrichedAfterTransientFailureCount);
        }

        /// <summary>
        /// Through the real Graph loader: whatever the Graph error, the save makes one lookup and never waits. Only
        /// throttling and server errors count as an interaction saved without its file after a transient failure.
        /// </summary>
        [DataTestMethod]
        [DataRow(400, false)]
        [DataRow(409, false)]
        [DataRow(410, false)]
        [DataRow(429, true)]
        [DataRow(503, true)]
        public async Task CopilotEventManager_FileLookupGraphError_IsTriedOnceAndCountedOnlyWhenTransient(int status, bool transient)
        {
            var graph = new Tests.UnitTests.FakeLoaderClasses.FakeSpoGraphClient
            {
                OnGetDriveItemByUrl = url => throw new ODataError
                {
                    ResponseStatusCode = status,
                    Error = new MainError { Code = "synthetic", Message = "Synthetic Graph error" }
                }
            };
            var loader = new GraphFileMetadataLoader(graph, NullLogger.Instance);
            var waits = 0;
            loader.RetryDelay = delay => { waits++; return Task.CompletedTask; };
            var writer = new InMemoryCopilotStagingWriter();
            var manager = NewManagerWith(writer, loader);

            await manager.SaveSingleCopilotEventToSqlStaging(ContentWith(File()), NewEvent());

            Assert.AreEqual(1, graph.GetDriveItemByUrlCalls);
            Assert.AreEqual(0, waits);
            Assert.AreEqual(transient ? 1 : 0, manager.UnenrichedAfterTransientFailureCount);
            Assert.AreEqual(1, writer.SharePointRows.Count + writer.ChatOnlyRows.Count, "The interaction is saved either way.");
        }
        [TestMethod]
        public async Task CopilotEventManager_LongMeetingSubject_IsStagedWithinTheColumnWithoutSplittingASurrogatePair()
        {
            var writer = new InMemoryCopilotStagingWriter();
            // 99 characters, then an emoji (a surrogate pair) straddling online_meetings.name's 100-character limit.
            var subject = new string('Κ', 99) + "\U0001F600" + " Contoso planning review";
            var loader = new FlakyCopilotMetadataLoader(0, TransientGraphFailure) { Subject = subject };

            await NewManagerWith(writer, loader).SaveSingleCopilotEventToSqlStaging(ContentWith(Meeting()), NewEvent());

            Assert.AreEqual(new string('Κ', 99), writer.TeamsRows.Single().MeetingName);
        }

        #endregion

        #region Teams meeting merge (B1)

        /// <summary>
        /// Brings the shared test database's schema up to date before a test here seeds it with raw SQL. Nothing else in
        /// this class creates an AnalyticsEntitiesContext, so a filtered run could otherwise reach these tests with a
        /// pending migration unapplied - UniqueOnlineMeetingIds' unique index among them - or with no database at all.
        /// The context's DEBUG initializer applies pending migrations; in Release it creates a missing database. Called
        /// per SQL test rather than from a [ClassInitialize], so this class's in-memory tests still need no SQL Server.
        /// </summary>
        private static void EnsureSchemaIsCurrent()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.Initialize(force: false);
            }
        }

        [TestMethod]
        public async Task TeamsMeetingMerge_SameMeetingInTwoBatches_IsStoredOnceAndLinkedToEachInteraction()
        {
            EnsureSchemaIsCurrent();
            var meeting = NewMeeting();
            using (var con = new SqlConnection(_config.ConnectionStrings.DatabaseConnectionString))
            {
                con.Open();
                var first = InsertAuditEventForNewUser(con);
                var second = InsertAuditEventForNewUser(con);
                try
                {
                    // Each in its own commit, like two import batches. Before the fix the second stored the meeting
                    // again and its link matched both copies: a primary-key violation that failed every retry.
                    await CommitTeams(MeetingRow(first.EventId, meeting, "Contoso release sync"));
                    await CommitTeams(MeetingRow(second.EventId, meeting, "Contoso release sync"));

                    var meetingRows = MeetingRowIds(con, meeting.MeetingId);
                    Assert.AreEqual(1, meetingRows.Count, "One meeting is one online_meetings row.");
                    Assert.AreEqual(meetingRows[0], SingleLinkedMeetingRow(con, first.EventId));
                    Assert.AreEqual(meetingRows[0], SingleLinkedMeetingRow(con, second.EventId));
                }
                finally
                {
                    DeleteMeetingTestRows(con, meeting.MeetingId, first, second);
                }
            }
        }

        [TestMethod]
        public async Task TeamsMeetingMerge_SubjectLongerThanTheColumn_IsTrimmedAndLinked()
        {
            EnsureSchemaIsCurrent();
            var meeting = NewMeeting();
            var longSubject = string.Concat(Enumerable.Repeat("Καλημέρα κόσμε ", 8));    // 120 characters
            Assert.AreEqual(120, longSubject.Length);
            using (var con = new SqlConnection(_config.ConnectionStrings.DatabaseConnectionString))
            {
                con.Open();
                var interaction = InsertAuditEventForNewUser(con);
                try
                {
                    await CommitTeams(MeetingRow(interaction.EventId, meeting, longSubject));

                    var meetingRows = MeetingRowIds(con, meeting.MeetingId);
                    Assert.AreEqual(1, meetingRows.Count);
                    Assert.AreEqual(longSubject.Substring(0, 100), Scalar(con, "SELECT name FROM dbo.online_meetings WHERE id = @id", ("@id", meetingRows[0])));
                    Assert.AreEqual(meetingRows[0], SingleLinkedMeetingRow(con, interaction.EventId));
                }
                finally
                {
                    DeleteMeetingTestRows(con, meeting.MeetingId, interaction);
                }
            }
        }

        [TestMethod]
        public async Task TeamsMeetingMerge_StoredMeeting_IsNeverStoredAgain_AndARenamedOneLinksToTheStoredRow()
        {
            EnsureSchemaIsCurrent();
            var meeting = NewMeeting();
            using (var con = new SqlConnection(_config.ConnectionStrings.DatabaseConnectionString))
            {
                con.Open();
                var interaction = InsertAuditEventForNewUser(con);
                try
                {
                    // Older builds stored a meeting again on every batch. Since UniqueOnlineMeetingIds the unique index
                    // skips a second copy instead (IGNORE_DUP_KEY), so a previous-build importer can't add one either.
                    // A database that still has copies - the migration not run yet - is covered by
                    // UniqueOnlineMeetingIdsMigrationTests.TeamsMerge_BeforeTheMigration_LinksToTheOldestCopy_AndStoresNoMore.
                    for (var i = 0; i < 2; i++)
                    {
                        Execute(con, "INSERT INTO dbo.online_meetings (created, meeting_id, name) VALUES (CAST(@created AS datetime), @meetingId, @name);",
                            ("@created", meeting.Created), ("@meetingId", meeting.MeetingId), ("@name", "Contoso release sync"));
                    }
                    var seeded = MeetingRowIds(con, meeting.MeetingId);
                    Assert.AreEqual(1, seeded.Count, "A second copy of a stored meeting is skipped.");

                    // The same meeting, renamed since it was stored: it is still that meeting.
                    await CommitTeams(MeetingRow(interaction.EventId, meeting, "Contoso release sync (moved)"));

                    CollectionAssert.AreEqual(seeded, MeetingRowIds(con, meeting.MeetingId), "No further copy may be stored.");
                    Assert.AreEqual(seeded.Single(), SingleLinkedMeetingRow(con, interaction.EventId), "The interaction links to the stored row, once.");
                }
                finally
                {
                    DeleteMeetingTestRows(con, meeting.MeetingId, interaction);
                }
            }
        }

        /// <summary>
        /// The Teams merge used to run before the chat-only one, so a failure in it (meeting enrichment) kept the
        /// batch's plain interactions from being saved at all.
        /// </summary>
        [TestMethod]
        public async Task SqlCopilotStagingWriter_TeamsMergeFailure_DoesNotStopChatOnlyInteractionsBeingSaved()
        {
            EnsureSchemaIsCurrent();
            var meeting = NewMeeting();
            var logger = new Tests.UnitTests.FakeLoaderClasses.RecordingLogger();
            using (var con = new SqlConnection(_config.ConnectionStrings.DatabaseConnectionString))
            {
                con.Open();
                var chat = InsertAuditEventForNewUser(con);
                var orphanEventId = Guid.NewGuid();      // no audit event: the Teams merge fails on its foreign key
                try
                {
                    var writer = new SqlCopilotStagingWriter(_config.ConnectionStrings.DatabaseConnectionString, logger);
                    writer.StageTeams(MeetingRow(orphanEventId, meeting, "Contoso release sync"));
                    writer.StageChatOnly(new ChatOnlyCopilotLogTempEntity { EventId = chat.EventId, AppHost = "Teams" });

                    await Assert.ThrowsExceptionAsync<DataUtils.Sql.BatchSaveException>(() => writer.CommitAllChanges(),
                        "The batch must still fail, so it is retried.");

                    Assert.AreEqual(1, Convert.ToInt32(Scalar(con, "SELECT COUNT(*) FROM dbo.copilot_chats WHERE event_id = @id", ("@id", chat.EventId))),
                        "The chat-only interaction must be saved even though the Teams merge failed.");
                }
                finally
                {
                    DeleteMeetingTestRows(con, meeting.MeetingId, chat);
                }
            }
        }

        private sealed class TestMeeting
        {
            public string MeetingId;
            public DateTime Created;
        }

        private sealed class TestAuditEvent
        {
            public Guid EventId;
            public string Upn;
        }

        /// <summary>A unique meeting id, created at a time with 7 fractional digits - which datetime can't hold.</summary>
        private static TestMeeting NewMeeting() => new TestMeeting
        {
            MeetingId = "MSo" + Guid.NewGuid().ToString("N"),
            Created = new DateTime(2026, 4, 1, 9, 30, 0, DateTimeKind.Utc).AddTicks(1234567),
        };

        private static TeamsCopilotLogTempEntity MeetingRow(Guid eventId, TestMeeting meeting, string subject) => new TeamsCopilotLogTempEntity
        {
            EventId = eventId,
            AppHost = "Teams",
            MeetingId = meeting.MeetingId,
            MeetingCreatedUTC = meeting.Created,
            MeetingName = subject
        };

        private async Task CommitTeams(TeamsCopilotLogTempEntity row)
        {
            var writer = new SqlCopilotStagingWriter(_config.ConnectionStrings.DatabaseConnectionString, NullLogger.Instance);
            writer.StageTeams(row);
            await writer.CommitAllChanges();
        }

        private static TestAuditEvent InsertAuditEventForNewUser(SqlConnection con)
        {
            var interaction = new TestAuditEvent { EventId = Guid.NewGuid(), Upn = $"meeting-merge-{Guid.NewGuid():N}@contoso.onmicrosoft.com" };
            InsertAuditEvent(con, interaction.EventId, interaction.Upn);
            return interaction;
        }

        private static List<int> MeetingRowIds(SqlConnection con, string meetingId)
        {
            var ids = new List<int>();
            using (var cmd = new SqlCommand("SELECT id FROM dbo.online_meetings WHERE meeting_id = @meetingId ORDER BY id;", con))
            {
                cmd.Parameters.AddWithValue("@meetingId", meetingId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read()) ids.Add(reader.GetInt32(0));
                }
            }
            return ids;
        }

        private static int SingleLinkedMeetingRow(SqlConnection con, Guid eventId)
        {
            var ids = new List<int>();
            using (var cmd = new SqlCommand("SELECT meeting_id FROM dbo.copilot_event_meetings WHERE copilot_chat_id = @eventId;", con))
            {
                cmd.Parameters.AddWithValue("@eventId", eventId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read()) ids.Add(reader.GetInt32(0));
                }
            }
            Assert.AreEqual(1, ids.Count, "An interaction links to exactly one meeting row.");
            return ids[0];
        }

        private static object Scalar(SqlConnection con, string sql, params (string Name, object Value)[] parameters)
        {
            using (var cmd = new SqlCommand(sql, con))
            {
                foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Name, p.Value);
                return cmd.ExecuteScalar();
            }
        }

        private static void Execute(SqlConnection con, string sql, params (string Name, object Value)[] parameters)
        {
            using (var cmd = new SqlCommand(sql, con))
            {
                foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Name, p.Value);
                cmd.ExecuteNonQuery();
            }
        }

        private static void DeleteMeetingTestRows(SqlConnection con, string meetingId, params TestAuditEvent[] interactions)
        {
            foreach (var interaction in interactions)
            {
                Execute(con, @"
DELETE FROM dbo.copilot_event_meetings WHERE copilot_chat_id = @eventId;
DELETE FROM dbo.copilot_chats WHERE event_id = @eventId;
DELETE FROM dbo.audit_events WHERE id = @eventId;
DELETE FROM dbo.users WHERE user_name = @upn;", ("@eventId", interaction.EventId), ("@upn", interaction.Upn));
            }
            Execute(con, @"
DELETE l FROM dbo.copilot_event_meetings l INNER JOIN dbo.online_meetings m ON m.id = l.meeting_id WHERE m.meeting_id = @meetingId;
DELETE FROM dbo.online_meetings WHERE meeting_id = @meetingId;", ("@meetingId", meetingId));
        }

        #endregion

        [TestMethod]
        public async Task SqlCopilotStagingWriter_CommitAllChanges_ClearsAllThreeStagedBatches()
        {
            EnsureSchemaIsCurrent();
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
