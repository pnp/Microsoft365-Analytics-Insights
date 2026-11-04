using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAuditBackfill;
using Common.Entities.State;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Migrations;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;
using WebJob.Office365ActivityImporter.Engine.Graph.Copilot.AuditBackfill;
using Configuration = Common.Entities.Migrations.Configuration;

namespace Tests.UnitTests
{
    /// <summary>
    /// The shared Copilot merge (<c>common_upsert_copilot_agents.sql</c>) and the backfill's completeness check, run for real
    /// against a throwaway database with every migration applied. The staging tables are sharded per writer, so nothing here
    /// touches another test's tables. Synthetic contoso.com users and zeroed-style GUIDs only.
    /// </summary>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class CopilotMergeScratchSqlTests
    {
        private static readonly ILogger Logger = NullLogger.Instance;
        private static readonly DateTime Now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        private static ScratchDatabase _database;

        [ClassInitialize]
        public static void CreateMigratedDatabase(TestContext context)
        {
            _database = ScratchDatabase.Create("CopilotMerge");
            new DbMigrator(new Configuration
            {
                TargetDatabase = new DbConnectionInfo(_database.ConnectionString, "Microsoft.Data.SqlClient"),
            }).Update();
        }

        [ClassCleanup]
        public static void DropDatabase()
        {
            SqlConnection.ClearAllPools();
            _database?.Dispose();
        }

        #region X1-N3: optional values longer than their column

        /// <summary>
        /// A message id longer than copilot_event_messages.message_id, or a model name longer than copilot_ai_models.name,
        /// used to fail the shared merge - and so every interaction in the batch - on every retry.
        /// </summary>
        [TestMethod]
        public async Task CopilotMerge_OverlongMessageIdsAndModelName_SaveTheInteraction_AndRetryCleanly()
        {
            var id = Guid.NewGuid();
            var upn = NewUpn();
            // Two ids that share their first 520 characters must stay two messages.
            var longIdA = new string('x', 520) + "-A";
            var longIdB = new string('x', 520) + "-B";
            var longModel = "Contoso-Καλημέρα-" + new string('m', 120);
            var interaction = Interaction(id, upn,
                "\"Messages\":[{\"Id\":\"" + longIdA + "\",\"isPrompt\":true},{\"Id\":\"" + longIdB + "\",\"isPrompt\":false},{\"Id\":\"short-1\",\"isPrompt\":false}]," +
                "\"ModelTransparencyDetails\":[{\"ModelName\":\"" + longModel + "\",\"ModelProviderName\":\"Contoso\",\"ModelVersion\":\"1\"}]");

            await MergeAsync(interaction);
            await MergeAsync(interaction);      // the retry of a batch whose merge already ran

            var messageIds = Strings("SELECT message_id FROM dbo.copilot_event_messages WHERE copilot_chat_id = @id", id);
            Assert.AreEqual(3, messageIds.Count, "Three distinct messages, each stored once.");
            Assert.IsTrue(messageIds.All(m => m.Length <= AuditStagingText.CopilotMessageIdMaxLength));
            CollectionAssert.Contains(messageIds, "short-1", "An id that fits is stored as it is.");
            Assert.AreEqual(2, messageIds.Count(m => m.StartsWith(new string('x', 100), StringComparison.Ordinal)), "The two long ids stay apart.");

            var models = Strings(@"SELECT m.[name] FROM dbo.copilot_event_ai_models l
                                   JOIN dbo.copilot_ai_models m ON m.id = l.model_id WHERE l.copilot_chat_id = @id", id);
            Assert.AreEqual(1, models.Count, "The model is linked once.");
            Assert.AreEqual(longModel.Substring(0, AuditStagingText.CopilotAiModelNameMaxLength), models[0], "Trimmed to the column, not dropped.");
        }

        #endregion

        #region R1-3: a backfill commit that failed partway

        /// <summary>
        /// The chat row is not enough: an interaction is complete when every kind of child row its payload implies is there.
        /// One whose merge stopped after copilot_chats used to count as complete, so a retried backfill never wrote the rest.
        /// </summary>
        [TestMethod]
        public async Task CompletenessFilter_ChatWithAMissingChildKind_IsNotComplete()
        {
            var upn = NewUpn();
            var complete = Interaction(Guid.NewGuid(), upn, ResourcesAndMessages);
            var messagesMissing = Interaction(Guid.NewGuid(), upn, ResourcesAndMessages);
            var nothingToWrite = Interaction(Guid.NewGuid(), upn, "\"Messages\":[]");
            var notSaved = Interaction(Guid.NewGuid(), upn, ResourcesAndMessages);
            await MergeAsync(complete, nothingToWrite);
            await MergeWithMessagesFailingAsync(messagesMissing);

            var found = await new SqlCopilotAuditBackfillExistingEventFilter(NewContext)
                .GetCompletelySavedIdsAsync(new AbstractAuditLogContent[] { complete, messagesMissing, nothingToWrite, notSaved });

            CollectionAssert.AreEquivalent(new[] { complete.Id, nothingToWrite.Id }, found.ToList());
        }

        /// <summary>
        /// End to end: the backfill's first commit stops after the interaction's chat and accessed resources; the slice is
        /// retried, and the retry writes the messages instead of skipping the interaction because its chat row exists. An
        /// interaction that was already saved completely is still dropped.
        /// </summary>
        [TestMethod]
        public async Task Backfill_CommitThatFailedPartway_WritesTheMissingRowsOnRetry()
        {
            var upn = NewUpn();
            var partial = Interaction(Guid.NewGuid(), upn, ResourcesAndMessages);
            var alreadySaved = Interaction(Guid.NewGuid(), upn, ResourcesAndMessages);
            await MergeAsync(alreadySaved);

            var state = new CopilotAuditBackfillStateStore(new InMemoryKeyValueStore(), false, () => Now);
            await state.CreateAsync(Now.Date.AddDays(-1), Now.Date, "admin@contoso.com");
            var source = new OnePageSource(Record(partial.Id, upn, ResourcesAndMessages), Record(alreadySaved.Id, upn, ResourcesAndMessages));
            var persistence = new ScratchCopilotPersistence { FailMessagesOnCommit = 1 };
            var importer = new CopilotAuditBackfillImporter(state, source, persistence,
                new AppConfig { ImportJobSettings = new ImportTaskSettings { Copilot = true } }, Logger,
                () => Now, _ => Task.CompletedTask, new SqlCopilotAuditBackfillExistingEventFilter(NewContext), () => TimeSpan.Zero);

            CopilotAuditBackfillJob job = null;
            for (var cycle = 0; cycle < 5 && (job == null || job.IsActive); cycle++)
            {
                job = await importer.AdvanceLatestAsync();
            }

            Assert.AreEqual(2, persistence.Commits, "The retried slice must commit the half-saved interaction again, not drop it as already present.");
            Assert.IsTrue(persistence.PartialStateSeen, "The first commit left the interaction's chat saved without its messages.");
            Assert.AreEqual(CopilotAuditBackfillStates.Completed, job.State);
            Assert.AreEqual(0, job.Gaps.Count);
            Assert.AreEqual(1, Count("SELECT COUNT(*) FROM dbo.copilot_chats WHERE event_id = @id", partial.Id));
            Assert.AreEqual(1, Count("SELECT COUNT(*) FROM dbo.copilot_event_accessed_resources WHERE copilot_chat_id = @id", partial.Id),
                "The accessed resource the failed attempt wrote is not written twice.");
            Assert.AreEqual(2, Count("SELECT COUNT(*) FROM dbo.copilot_event_messages WHERE copilot_chat_id = @id", partial.Id),
                "The retry writes the messages the failed attempt did not.");
            Assert.AreEqual(1, job.RecordsImported);
            Assert.AreEqual(1, job.RecordsAlreadyPresent, "A completely saved interaction is still dropped before the commit.");
        }

        #endregion

        #region Helpers

        private const string ResourcesAndMessages =
            "\"AccessedResources\":[{\"Id\":\"https://contoso.sharepoint.com/sites/example/Shared Documents/Καλημέρα κόσμε.docx\",\"Name\":\"Καλημέρα κόσμε.docx\",\"Type\":\"File\"}]," +
            "\"Messages\":[{\"Id\":\"prompt-1\",\"isPrompt\":true},{\"Id\":\"response-1\",\"isPrompt\":false}]";

        private static AnalyticsEntitiesContext NewContext() => new AnalyticsEntitiesContext(_database.ConnectionString, true, false);

        private static string NewUpn() => $"copilot-merge-{Guid.NewGuid():N}@contoso.com";

        private static CopilotAuditSearchRecord Record(Guid id, string upn, string copilotEventData) => new CopilotAuditSearchRecord
        {
            Id = id.ToString(),
            CreatedDateTime = Now.AddDays(-1),
            AuditLogRecordType = "microsoft.graph.security.auditLogRecordType",
            Operation = "CopilotInteraction",
            Service = "Copilot",
            UserPrincipalName = upn,
            AuditData = JObject.Parse("{\"@odata.type\":\"#microsoft.graph.security.auditData\",\"Id\":\"" + id + "\",\"RecordType\":261," +
                "\"CreationTime\":\"" + Now.AddDays(-1).ToString("s") + "Z\",\"Operation\":\"CopilotInteraction\",\"Workload\":\"Copilot\"," +
                "\"UserId\":\"" + upn + "\",\"OrganizationId\":\"00000000-0000-0000-0000-000000000000\"," +
                "\"CopilotEventData\":{\"AppHost\":\"Word\",\"Contexts\":[]," + copilotEventData + "}}"),
        };

        private static CopilotAuditLogContent Interaction(Guid id, string upn, string copilotEventData)
        {
            var mapped = CopilotAuditSearchRecordMapper.Map(Record(id, upn, copilotEventData), Logger);
            Assert.IsNotNull(mapped.Content, "The synthetic record must map.");
            return (CopilotAuditLogContent)mapped.Content;
        }

        /// <summary>The core merge's audit_events row, which copilot_chats has a foreign key to.</summary>
        private static void SeedAuditEvent(AbstractAuditLogContent interaction)
        {
            Execute(@"
DECLARE @operationId int = (SELECT id FROM dbo.event_operations WHERE operation_name = N'CopilotInteraction');
IF @operationId IS NULL
BEGIN
    INSERT INTO dbo.event_operations (operation_name) VALUES (N'CopilotInteraction');
    SET @operationId = SCOPE_IDENTITY();
END
DECLARE @userId int = (SELECT id FROM dbo.users WHERE user_name = @upn);
IF @userId IS NULL
BEGIN
    INSERT INTO dbo.users (user_name) VALUES (@upn);
    SET @userId = SCOPE_IDENTITY();
END
IF NOT EXISTS (SELECT 1 FROM dbo.audit_events WHERE id = @id)
    INSERT INTO dbo.audit_events (id, time_stamp, operation_id, user_id) VALUES (@id, @time, @operationId, @userId);",
                ("@id", interaction.Id), ("@upn", interaction.UserId), ("@time", interaction.CreationTime));
        }

        /// <summary>The Copilot half of the save path, as the live import and the backfill run it, on sharded staging tables.</summary>
        private static async Task MergeAsync(params AbstractAuditLogContent[] interactions)
        {
            foreach (var interaction in interactions) SeedAuditEvent(interaction);

            var manager = new CopilotAuditEventManager(
                new SqlCopilotStagingWriter(_database.ConnectionString, Logger, isolateStagingTables: true),
                new NoGraphLoader(), Logger, resolveResourceMetadata: false);
            foreach (CopilotAuditLogContent interaction in interactions)
            {
                await manager.SaveSingleCopilotEventToSqlStaging(interaction, new CommonAuditEvent { Id = interaction.Id, TimeStamp = interaction.CreationTime });
            }
            await manager.CommitAllChanges();
        }

        /// <summary>
        /// Runs the merge with every insert into copilot_event_messages failing, so it stops after copilot_chats and the
        /// accessed resources - the state a deadlock, timeout or failover at that point leaves behind.
        /// </summary>
        private static async Task MergeWithMessagesFailingAsync(params AbstractAuditLogContent[] interactions)
        {
            Execute(@"CREATE TRIGGER dbo.tr_test_fail_copilot_messages ON dbo.copilot_event_messages AFTER INSERT AS
                      BEGIN
                          THROW 50000, 'Synthetic failure after the interaction''s chat was saved.', 1;
                      END");
            try
            {
                await Assert.ThrowsExceptionAsync<DataUtils.Sql.BatchSaveException>(() => MergeAsync(interactions));
            }
            finally
            {
                Execute("DROP TRIGGER dbo.tr_test_fail_copilot_messages");
            }
        }

        private static void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using (var con = new SqlConnection(_database.ConnectionString))
            using (var cmd = new SqlCommand(sql, con))
            {
                foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Name, p.Value);
                con.Open();
                cmd.ExecuteNonQuery();
            }
        }

        private static int Count(string sql, Guid id)
        {
            using (var con = new SqlConnection(_database.ConnectionString))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.AddWithValue("@id", id);
                con.Open();
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        private static List<string> Strings(string sql, Guid id)
        {
            var values = new List<string>();
            using (var con = new SqlConnection(_database.ConnectionString))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.AddWithValue("@id", id);
                con.Open();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read()) values.Add(reader.GetString(0));
                }
            }
            return values;
        }

        /// <summary>Resource resolution is off in these tests, so Graph is never asked.</summary>
        private sealed class NoGraphLoader : ICopilotMetadataLoader
        {
            public Task<SpoDocumentFileInfo> GetSpoFileInfo(string copilotId, string eventUpn) => throw new AssertFailedException("No Graph lookups expected.");
            public Task<MeetingMetadata> GetMeetingInfo(string threadId, string userGuid) => throw new AssertFailedException("No Graph lookups expected.");
            public Task<string> GetUserIdFromUpn(string userPrincipalName) => throw new AssertFailedException("No Graph lookups expected.");
        }

        /// <summary>The backfill's persistence, reduced to what these tests need: the audit row and the real Copilot merge.</summary>
        private sealed class ScratchCopilotPersistence : IActivityReportPersistenceManager
        {
            /// <summary>The commit (1-based) whose message inserts fail.</summary>
            public int FailMessagesOnCommit;
            public int Commits;

            /// <summary>Set once the failed commit is seen to have left the interactions' chats without their messages.</summary>
            public bool PartialStateSeen;

            public async Task<ImportStat> CommitAll(ActivityReportSet activities)
            {
                Commits++;
                if (Commits == FailMessagesOnCommit)
                {
                    await MergeWithMessagesFailingAsync(activities.ToArray());
                    PartialStateSeen = activities.All(a => Count("SELECT COUNT(*) FROM dbo.copilot_chats WHERE event_id = @id", a.Id) == 1
                        && Count("SELECT COUNT(*) FROM dbo.copilot_event_messages WHERE copilot_chat_id = @id", a.Id) == 0);
                    throw new InvalidOperationException("The synthetic partial commit failed, as the real save would.");
                }

                await MergeAsync(activities.ToArray());
                return new ImportStat { Imported = activities.Count, Total = activities.Count };
            }
        }

        /// <summary>An Audit Search that succeeds at once and returns the same page every time its query is read.</summary>
        private sealed class OnePageSource : ICopilotAuditSearchSource
        {
            private readonly CopilotAuditSearchRecord[] _records;
            private int _submits;

            public OnePageSource(params CopilotAuditSearchRecord[] records) => _records = records;

            public Task<AppTokenPermissionAccess> GetPermissionAccessAsync() => Task.FromResult(AppTokenPermissionAccess.Granted);

            public Task<CopilotAuditSearchQuery> SubmitQueryAsync(CopilotAuditBackfillSlice slice)
                => Task.FromResult(new CopilotAuditSearchQuery { Id = "query-" + (++_submits), Status = "running" });

            public Task<CopilotAuditSearchQuery> GetQueryAsync(string queryId)
                => Task.FromResult(new CopilotAuditSearchQuery { Id = queryId, Status = "succeeded" });

            public Task<CopilotAuditSearchRecordPage> GetRecordsAsync(string queryId, string nextLink = null)
                => Task.FromResult(new CopilotAuditSearchRecordPage { Records = _records.ToList() });
        }

        #endregion
    }
}
