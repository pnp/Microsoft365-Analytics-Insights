using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Issue #699, phase 2: a Copilot Studio agent's turn counts once, however many audit records it produced.
    ///
    /// A Microsoft 365 Copilot turn with a published Copilot Studio agent is audited twice: the Copilot Studio
    /// runtime logs a record (app host 'm365copilot', no messages) and Microsoft 365 Copilot logs another a few
    /// seconds later (app host 'Office', the agent's name and the messages). They share the agent, the user and
    /// the conversation id. A Teams turn is audited by the runtime only, but can log an extra runtime record a
    /// few seconds after the first. These tests run the importer's real merge SQL (common_upsert_copilot_agents.sql
    /// and insert_copilot_dlp_events_from_staging_table.sql) against the test database, one import cycle per
    /// commit, and check that both rows are kept and the extra one is listed in copilot_chat_duplicates.
    ///
    /// Every test uses its own random agent id, user and conversation id, and synthetic payloads only.
    /// </summary>
    [TestClass]
    public class CopilotTurnPairingTests : CopilotTestBase
    {
        private const string TenantId = "00000000-0000-0000-0000-000000000000";
        private const string TitleId = "T_22222222-2222-2222-2222-222222222222";
        private const string SchemaName = "cr123_contosoHelpdesk";
        private const string DisplayName = "Contoso Helpdesk";

        private const byte RuntimeTwin = 1;
        private const byte ExtraRuntimeRecord = 2;

        #region Payloads

        /// <summary>The Copilot Studio runtime's record of a turn, on any channel.</summary>
        private static string RuntimeRecord(string agentId, string conversationId, string appHost = "m365copilot", string accessedResources = "[]")
            => $@"{{
                ""RecordType"": 261, ""Operation"": ""CopilotInteraction"", ""Workload"": ""Copilot"",
                ""AgentId"": ""{agentId}"",
                ""AgentBlueprintId"": ""00000000-0000-0000-0000-000000000000"",
                ""AgentPlatform"": ""CopilotStudio"",
                ""AppIdentity"": ""Copilot.Studio.Default-{TenantId}-{SchemaName}"",
                ""OrganizationId"": ""{TenantId}"",
                ""CopilotEventData"": {{
                    ""AppHost"": ""{appHost}"",
                    ""ConversationId"": ""{conversationId}"",
                    ""MessageIds"": [], ""AccessedResources"": {accessedResources},
                    ""PlatformAgentType"": ""CopilotStudio"",
                    ""ThreadId"": ""19:aaaa@thread.v2""
                }},
                ""CopilotLogVersion"": ""1.0.0.0""
            }}";

        /// <summary>The record Microsoft 365 Copilot logs in addition, for each turn with a published agent.</summary>
        private static string ClientRecord(string agentId, string conversationId, string accessedResources = "[]")
            => $@"{{
                ""RecordType"": 261, ""Operation"": ""CopilotInteraction"", ""Workload"": ""Copilot"",
                ""AgentId"": ""{TitleId}.{agentId}"",
                ""AgentName"": ""{DisplayName}"",
                ""AppIdentity"": ""Copilot.Studio.CustomEngine.{TitleId}"",
                ""OrganizationId"": ""{TenantId}"",
                ""CopilotEventData"": {{
                    ""AppHost"": ""Office"",
                    ""ConversationId"": ""{conversationId}"",
                    ""Messages"": [{{ ""Id"": ""1700000000001"", ""isPrompt"": true }}, {{ ""Id"": ""1700000000002"", ""isPrompt"": false }}],
                    ""TargetAgentName"": ""{DisplayName}"",
                    ""TargetPlatformAgentId"": ""{TitleId}"",
                    ""LicenseType"": ""Premium"", ""Contexts"": [], ""AccessedResources"": {accessedResources},
                    ""ThreadId"": ""19:bbbb@thread.v2""
                }},
                ""CopilotLogVersion"": ""1.0.0.0""
            }}";

        /// <summary>One web-search result, as both records of a pair have been seen to carry.</summary>
        private static string WebSearchResource(string type)
            => $@"[{{ ""Id"": ""https://www.contoso.com/search?q=1"", ""Name"": ""Contoso search result"", ""Type"": ""{type}"", ""Action"": ""Read"" }}]";

        /// <summary>A document Copilot's access to was blocked by a DLP policy.</summary>
        private static string BlockedResource()
            => @"[{
                ""Action"": ""Read"", ""Id"": ""00000000-0000-0000-0000-000000000002"",
                ""Name"": ""Καλημέρα κόσμε.docx"", ""Type"": ""docx"",
                ""SiteUrl"": ""https://contoso.sharepoint.com/sites/hr"", ""Status"": ""failure"",
                ""PolicyDetails"": [{
                    ""PolicyId"": ""00000000-0000-0000-0000-000000000004"", ""PolicyName"": ""Block Copilot on Confidential"",
                    ""Rules"": [{ ""RuleId"": ""00000000-0000-0000-0000-000000000005"", ""RuleName"": ""Confidential label"",
                                 ""Actions"": [""BlockAccess""], ""Severity"": ""High"", ""RuleMode"": ""Enforce"" }]
                }]
            }]";

        #endregion

        #region Pairs

        [TestMethod]
        public async Task Pair_InOneImportCycle_KeepsBothRowsAndCountsTheTurnOnTheClientRecord()
        {
            var s = await NewScenario();
            var runtime = s.Runtime(0);
            var client = s.Client(5);

            await CommitCycle(s, runtime, client);

            Assert.AreEqual(2, await ChatCount(runtime, client), "Both audit records are kept.");
            var duplicates = await Duplicates(runtime, client);
            Assert.AreEqual(1, duplicates.Count);
            Assert.AreEqual(client.EventId, duplicates[runtime.EventId].CountedEventId, "The turn is counted on the Microsoft 365 Copilot record.");
            Assert.AreEqual(RuntimeTwin, duplicates[runtime.EventId].Reason);
            Assert.AreEqual(s.ConversationId, await StoredConversationId(runtime));
        }

        [TestMethod]
        public async Task Pair_RuntimeRecordFirst_ClientRecordInALaterCycle_Pairs()
        {
            var s = await NewScenario();
            var runtime = s.Runtime(0);
            var client = s.Client(6);

            await CommitCycle(s, runtime);
            Assert.AreEqual(0, (await Duplicates(runtime)).Count, "With no twin yet, the runtime record counts.");

            await CommitCycle(s, client);
            AssertPaired(await Duplicates(runtime, client), runtime, client);
        }

        [TestMethod]
        public async Task Pair_ClientRecordFirst_RuntimeRecordInALaterCycle_Pairs()
        {
            var s = await NewScenario();
            var runtime = s.Runtime(0);
            var client = s.Client(4);

            await CommitCycle(s, client);
            await CommitCycle(s, runtime);

            AssertPaired(await Duplicates(runtime, client), runtime, client);
        }

        [TestMethod]
        public async Task Pair_ReImportingBothRecords_ChangesNothing()
        {
            var s = await NewScenario();
            var runtime = s.Runtime(0);
            var client = s.Client(5);

            await CommitCycle(s, runtime, client);
            await CommitCycle(s, client, runtime);
            await CommitCycle(s, runtime);

            Assert.AreEqual(2, await ChatCount(runtime, client));
            AssertPaired(await Duplicates(runtime, client), runtime, client);
        }

        [TestMethod]
        public async Task Pair_NeverAcrossConversations_OrOutsideTheWindow()
        {
            var s = await NewScenario();
            var runtime = s.Runtime(0);
            var otherConversation = s.Client(5, conversationId: Guid.NewGuid().ToString());
            var tooLate = s.Client(10 * 60);

            await CommitCycle(s, runtime, otherConversation, tooLate);

            Assert.AreEqual(0, (await Duplicates(runtime, otherConversation, tooLate)).Count,
                "A different conversation, or a client record ten minutes later, is not the runtime record's twin.");
        }

        /// <summary>
        /// A record saved by an older build has no conversation id. When the importer re-reads it in its
        /// look-back window, the id is filled in and the record can be paired.
        /// </summary>
        [TestMethod]
        public async Task Pair_RecordSavedWithoutAConversationId_IsPairedWhenReRead()
        {
            var s = await NewScenario();
            var runtime = s.Runtime(0);
            var client = s.Client(5);

            await CommitCycle(s, runtime);
            await Sql("UPDATE dbo.copilot_chats SET conversation_id = NULL WHERE event_id = {0};", runtime.EventId);

            await CommitCycle(s, client, runtime);

            Assert.AreEqual(s.ConversationId, await StoredConversationId(runtime));
            AssertPaired(await Duplicates(runtime, client), runtime, client);
        }

        #endregion

        #region Rapid prompts

        [TestMethod]
        public async Task RapidPrompts_AlternatingRecords_PairOneToOneWithTheirOwnTwins()
        {
            var s = await NewScenario();
            var r1 = s.Runtime(0);
            var c1 = s.Client(5);
            var r2 = s.Runtime(8);
            var c2 = s.Client(13);
            var r3 = s.Runtime(16);
            var c3 = s.Client(21);

            await CommitCycle(s, r1, c1, r2, c2, r3, c3);

            var duplicates = await Duplicates(r1, c1, r2, c2, r3, c3);
            Assert.AreEqual(3, duplicates.Count, "Three turns: each runtime record is one client record's twin.");
            Assert.AreEqual(c1.EventId, duplicates[r1.EventId].CountedEventId);
            Assert.AreEqual(c2.EventId, duplicates[r2.EventId].CountedEventId);
            Assert.AreEqual(c3.EventId, duplicates[r3.EventId].CountedEventId);
            Assert.IsTrue(duplicates.Values.All(d => d.Reason == RuntimeTwin));
        }

        [TestMethod]
        public async Task RapidPrompts_BothRuntimeRecordsBeforeEitherClientRecord_PairTwoForTwo()
        {
            var s = await NewScenario();
            var r1 = s.Runtime(0);
            var r2 = s.Runtime(3);
            var c1 = s.Client(5);
            var c2 = s.Client(8);

            await CommitCycle(s, r1, r2, c1, c2);

            var duplicates = await Duplicates(r1, r2, c1, c2);
            Assert.AreEqual(2, duplicates.Count, "Both runtime records are twins - never both on one client record.");
            CollectionAssert.AreEquivalent(new[] { c1.EventId, c2.EventId }, duplicates.Values.Select(d => d.CountedEventId).ToList(),
                "One-to-one: each client record counts exactly one turn.");
        }

        [TestMethod]
        public async Task RapidPrompts_SecondClientRecordInALaterCycle_ClaimsItsOwnTwin()
        {
            var s = await NewScenario();
            var r1 = s.Runtime(0);
            var c1 = s.Client(5);
            var r2 = s.Runtime(8);
            var c2 = s.Client(13);

            await CommitCycle(s, r1, c1, r2);
            await CommitCycle(s, c2);

            var duplicates = await Duplicates(r1, c1, r2, c2);
            Assert.AreEqual(2, duplicates.Count);
            Assert.AreEqual(c1.EventId, duplicates[r1.EventId].CountedEventId);
            Assert.AreEqual(c2.EventId, duplicates[r2.EventId].CountedEventId, "The later client record claims its own runtime twin.");
            Assert.AreEqual(RuntimeTwin, duplicates[r2.EventId].Reason);
        }

        #endregion

        #region Teams and the test pane

        [TestMethod]
        public async Task Teams_RuntimeRecordsWithNoTwin_AlwaysCount()
        {
            var s = await NewScenario();
            var first = s.Runtime(0, "Microsoft Teams");
            var second = s.Runtime(60, "Microsoft Teams");
            var third = s.Runtime(125, "Microsoft Teams");

            await CommitCycle(s, first, second, third);

            Assert.AreEqual(0, (await Duplicates(first, second, third)).Count, "Teams turns have no twin, so each counts.");
        }

        [TestMethod]
        public async Task Teams_ExtraRuntimeRecordAFewSecondsLater_IsCountedAsPartOfTheTurn()
        {
            var s = await NewScenario();
            var turn = s.Runtime(0, "Microsoft Teams");
            var extra = s.Runtime(3, "Microsoft Teams");
            var nextTurn = s.Runtime(45, "Microsoft Teams");

            await CommitCycle(s, turn, extra);
            await CommitCycle(s, nextTurn, extra);

            var duplicates = await Duplicates(turn, extra, nextTurn);
            Assert.AreEqual(1, duplicates.Count, "Only the extra record is left out; the next turn still counts.");
            Assert.AreEqual(turn.EventId, duplicates[extra.EventId].CountedEventId);
            Assert.AreEqual(ExtraRuntimeRecord, duplicates[extra.EventId].Reason);
        }

        [TestMethod]
        public async Task Microsoft365Copilot_ExtraRuntimeRecord_IsFoldedIntoThePairedTurn()
        {
            var s = await NewScenario();
            var runtime = s.Runtime(0);
            var client = s.Client(5);
            var extra = s.Runtime(9);

            await CommitCycle(s, runtime, client, extra);

            var duplicates = await Duplicates(runtime, client, extra);
            Assert.AreEqual(2, duplicates.Count);
            Assert.AreEqual(client.EventId, duplicates[runtime.EventId].CountedEventId);
            Assert.AreEqual(client.EventId, duplicates[extra.EventId].CountedEventId, "The turn is counted once, on the client record.");
        }

        #endregion

        #region Reports

        /// <summary>
        /// Through the real merge into the real report SQL: a pair is one interaction under one app, an extra
        /// Teams record is not a second interaction, and maker testing in the Copilot Studio test pane is left out.
        /// </summary>
        [TestMethod]
        public async Task AgentInventory_CountsEachTurnOnce_AndLeavesOutMakerTesting()
        {
            var s = await NewScenario();
            await CommitCycle(s,
                s.Runtime(0), s.Client(5),
                s.Runtime(60, "Microsoft Teams"), s.Runtime(63, "Microsoft Teams"),
                s.Runtime(120, "Copilot Studio"), s.Runtime(180, "Copilot Studio"));

            var row = await AgentInventoryRow(s);

            Assert.AreEqual(2, row.Interactions, "One Microsoft 365 Copilot turn and one Teams turn; test-pane chats are maker testing.");
            Assert.AreEqual(1, row.Users);
            Assert.AreEqual(2, row.AppsUsed, "'Office' and 'Microsoft Teams' - not 'm365copilot' as well, and not 'Copilot Studio'.");
        }

        [TestMethod]
        public async Task AgentInventory_AnAgentOnlyEverTestedInTheTestPane_IsNotListed()
        {
            var s = await NewScenario();
            await CommitCycle(s, s.Runtime(0, "Copilot Studio"), s.Runtime(30, "Copilot Studio"));

            Assert.IsNull(await AgentInventoryRow(s, required: false),
                "Maker testing is not agent use, so an agent nobody has used outside the test pane has no adoption figures.");
            Assert.AreEqual(2, await Scalar<int>(
                "SELECT COUNT(*) FROM dbo.copilot_chats WHERE user_id = {0} AND app_host = N'Copilot Studio'", s.UserId),
                "The test-pane rows are kept.");
        }

        [TestMethod]
        public async Task TopResourceTypes_AResourceBothRecordsOfAPairList_CountsOnce()
        {
            var s = await NewScenario();
            var type = "SyntheticSearch" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var runtime = s.Runtime(0, accessedResources: WebSearchResource(type));
            var client = s.Client(5, accessedResources: WebSearchResource(type));

            await CommitCycle(s, runtime, client);

            Assert.AreEqual(2, await Scalar<int>(
                @"SELECT COUNT(*) FROM dbo.copilot_event_accessed_resources AS ar
                  JOIN dbo.copilot_event_accessed_resource_types AS rt ON rt.id = ar.resource_type_id
                  WHERE rt.name = {0}", type), "Both records keep their resources.");

            using (var db = new AnalyticsEntitiesContext())
            {
                var rows = await db.Database.SqlQuery<CopilotAdoptionService.CategoryQueryRow>(
                    CopilotAdoptionSql.TopResourceTypesSql(),
                    new Microsoft.Data.SqlClient.SqlParameter("@from", s.Start.AddDays(-1)),
                    new Microsoft.Data.SqlClient.SqlParameter("@toExclusive", s.Start.AddDays(1)),
                    new Microsoft.Data.SqlClient.SqlParameter("@top", 100000)).ToListAsync();

                Assert.AreEqual(1d, rows.Single(r => r.Label == type).Value, "One access, listed on both records of the turn, counts once.");
            }
        }

        #endregion

        #region DLP

        [TestMethod]
        public async Task Dlp_TheSameBlockOnBothRecordsOfAPair_IsStoredOnce_InOneCycle()
        {
            var s = await NewScenario();
            var runtime = s.Runtime(0, accessedResources: BlockedResource());
            var client = s.Client(5, accessedResources: BlockedResource());

            await CommitCycle(s, runtime, client);
            await CommitCycle(s, runtime, client);

            Assert.AreEqual(1, await DlpEventCount(runtime, client), "One block, on one turn, is one DLP event.");
            Assert.AreEqual(1, await DlpEventCount(client), "The record the turn is counted on keeps it.");
        }

        [TestMethod]
        public async Task Dlp_TheSameBlockOnBothRecordsOfAPair_IsStoredOnce_InEitherOrder()
        {
            var s = await NewScenario();
            var runtime = s.Runtime(0, accessedResources: BlockedResource());
            var client = s.Client(5, accessedResources: BlockedResource());
            await CommitCycle(s, runtime);
            await CommitCycle(s, client);

            var t = await NewScenario();
            var runtime2 = t.Runtime(0, accessedResources: BlockedResource());
            var client2 = t.Client(5, accessedResources: BlockedResource());
            await CommitCycle(t, client2);
            await CommitCycle(t, runtime2);

            Assert.AreEqual(1, await DlpEventCount(runtime, client), "Runtime record first.");
            Assert.AreEqual(1, await DlpEventCount(runtime2, client2), "Client record first.");
        }

        [TestMethod]
        public async Task Dlp_ABlockStoredOnBothRecordsBeforeTheyWerePaired_IsReducedToOne()
        {
            var s = await NewScenario();
            var runtime = s.Runtime(0, accessedResources: BlockedResource());
            var client = s.Client(5, accessedResources: BlockedResource());

            // Both saved by a build that did not pair them: no conversation id, so no pair, and a DLP event each.
            await CommitCycle(s, runtime, client);
            await Sql("DELETE FROM dbo.copilot_chat_duplicates WHERE event_id IN ({0}, {1});", runtime.EventId, client.EventId);
            await Sql(@"INSERT INTO dbo.copilot_dlp_events (copilot_chat_id, dlp_policy_id, dlp_rule_id, dlp_action_id, resource_name_id, resource_type_id, sensitivity_label_id, is_blocked)
                        SELECT {0}, dlp_policy_id, dlp_rule_id, dlp_action_id, resource_name_id, resource_type_id, sensitivity_label_id, is_blocked
                        FROM dbo.copilot_dlp_events WHERE copilot_chat_id = {1};", runtime.EventId, client.EventId);
            await Sql("UPDATE dbo.copilot_chats SET conversation_id = NULL WHERE event_id IN ({0}, {1});", runtime.EventId, client.EventId);
            Assert.AreEqual(2, await DlpEventCount(runtime, client));

            await CommitCycle(s, runtime, client);

            AssertPaired(await Duplicates(runtime, client), runtime, client);
            Assert.AreEqual(1, await DlpEventCount(runtime, client), "Pairing them keeps the block once, on the counted record.");
            Assert.AreEqual(1, await DlpEventCount(client));
        }

        #endregion

        #region Helpers

        private sealed class Scenario
        {
            public string AgentGuid { get; } = Guid.NewGuid().ToString();
            public string ConversationId { get; } = Guid.NewGuid().ToString();
            public int UserId { get; set; }

            // Whole seconds: audit_events.time_stamp is a datetime, so no rounding can move a record across a window edge.
            public DateTime Start { get; } = new DateTime(DateTime.UtcNow.Ticks - DateTime.UtcNow.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddHours(-2);

            public Record Runtime(int seconds, string appHost = "m365copilot", string accessedResources = "[]")
                => new Record(RuntimeRecord(AgentGuid, ConversationId, appHost, accessedResources), Start.AddSeconds(seconds));

            public Record Client(int seconds, string conversationId = null, string accessedResources = "[]")
                => new Record(ClientRecord(AgentGuid, conversationId ?? ConversationId, accessedResources), Start.AddSeconds(seconds));
        }

        private sealed class Record
        {
            public Record(string json, DateTime at)
            {
                Json = json;
                At = at;
            }

            public Guid EventId { get; } = Guid.NewGuid();
            public string Json { get; }
            public DateTime At { get; }
        }

        private sealed class DuplicateRow
        {
            public Guid EventId { get; set; }
            public Guid CountedEventId { get; set; }
            public byte Reason { get; set; }
        }

        private static async Task<Scenario> NewScenario()
        {
            var scenario = new Scenario();
            using (var db = new AnalyticsEntitiesContext())
            {
                var user = new User { AzureAdId = "test", UserPrincipalName = $"turn.pairing.{Guid.NewGuid():N}@contoso.com" };
                db.users.Add(user);
                await db.SaveChangesAsync();
                scenario.UserId = user.ID;
            }
            return scenario;
        }

        /// <summary>
        /// One import cycle: each record's audit event is saved if it is new (a record already saved is being
        /// re-read, as the importer's look-back window does), then the Copilot merge, then the DLP merge, in
        /// the order SaveSession commits them.
        /// </summary>
        private async Task CommitCycle(Scenario scenario, params Record[] records)
        {
            var copilot = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var dlp = new DlpAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, _logger);

            using (var db = new AnalyticsEntitiesContext())
            {
                foreach (var record in records)
                {
                    var auditEvent = await db.AuditEventsCommon.SingleOrDefaultAsync(e => e.Id == record.EventId);
                    if (auditEvent == null)
                    {
                        auditEvent = new CommonAuditEvent
                        {
                            Id = record.EventId,
                            TimeStamp = record.At,
                            UserId = scenario.UserId,
                            Operation = new EventOperation { Name = "Copilot turn pairing test " + Guid.NewGuid() },
                        };
                        db.AuditEventsCommon.Add(auditEvent);
                        await db.SaveChangesAsync();
                    }

                    var content = CopilotAuditLogContent.FromJson(record.Json);
                    await copilot.SaveSingleCopilotEventToSqlStaging(content, auditEvent);
                    await dlp.SaveCopilotDlpMatchesToSqlStaging(content, auditEvent);
                }
            }

            await copilot.CommitAllChanges();
            await dlp.CommitAllChanges();
        }

        private static void AssertPaired(Dictionary<Guid, DuplicateRow> duplicates, Record runtime, Record client)
        {
            Assert.AreEqual(1, duplicates.Count, "Exactly one of the two records is left out of the counts.");
            Assert.IsTrue(duplicates.ContainsKey(runtime.EventId), "The runtime record is the one left out.");
            Assert.AreEqual(client.EventId, duplicates[runtime.EventId].CountedEventId);
            Assert.AreEqual(RuntimeTwin, duplicates[runtime.EventId].Reason);
        }

        private static async Task<Dictionary<Guid, DuplicateRow>> Duplicates(params Record[] records)
        {
            var ids = records.Select(r => r.EventId).ToList();
            using (var db = new AnalyticsEntitiesContext())
            {
                var rows = await db.Database.SqlQuery<DuplicateRow>(
                    "SELECT event_id AS EventId, counted_event_id AS CountedEventId, reason AS Reason FROM dbo.copilot_chat_duplicates").ToListAsync();
                return rows.Where(r => ids.Contains(r.EventId)).ToDictionary(r => r.EventId);
            }
        }

        private static async Task<int> ChatCount(params Record[] records)
        {
            var ids = records.Select(r => r.EventId).ToList();
            using (var db = new AnalyticsEntitiesContext())
            {
                return await db.CopilotChats.CountAsync(c => ids.Contains(c.AuditEvent.Id));
            }
        }

        private static async Task<int> DlpEventCount(params Record[] records)
        {
            var ids = string.Join(", ", records.Select(r => $"'{r.EventId}'"));
            return await Scalar<int>($"SELECT COUNT(*) FROM dbo.copilot_dlp_events WHERE copilot_chat_id IN ({ids})");
        }

        private static Task<string> StoredConversationId(Record record)
            => Scalar<string>("SELECT conversation_id FROM dbo.copilot_chats WHERE event_id = {0}", record.EventId);

        private static async Task<AgentUsageQueryRow> AgentInventoryRow(Scenario scenario, bool required = true)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var rows = await db.Database.SqlQuery<AgentUsageQueryRow>(
                    CopilotAdoptionSql.AgentUsageSql(new[] { -1 }),
                    new Microsoft.Data.SqlClient.SqlParameter("@historyFrom", scenario.Start.AddDays(-1)),
                    new Microsoft.Data.SqlClient.SqlParameter("@from", scenario.Start.AddDays(-1)),
                    new Microsoft.Data.SqlClient.SqlParameter("@toExclusive", scenario.Start.AddDays(1)),
                    new Microsoft.Data.SqlClient.SqlParameter("@maxRows", 100000)).ToListAsync();
                var row = rows.SingleOrDefault(r => r.AgentKey == scenario.AgentGuid);
                if (required)
                {
                    Assert.IsNotNull(row, "The agent must be in the inventory.");
                }
                return row;
            }
        }

        private static async Task<T> Scalar<T>(string sql, params object[] parameters)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                return await db.Database.SqlQuery<T>(sql, parameters).SingleAsync();
            }
        }

        private static async Task Sql(string sql, params object[] parameters)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await db.Database.ExecuteSqlCommandAsync(sql, parameters);
            }
        }

        #endregion
    }
}
