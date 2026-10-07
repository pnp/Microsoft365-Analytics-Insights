using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Common.Entities.Entities.AuditLog;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.CostEstimate;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Issue #699: a Copilot Studio agent must be ONE copilot_agents row with a name, whichever channel it is
    /// used in, and the agents upsert must never lose a name or a flag, or create a second row.
    ///
    /// Copilot Studio's runtime writes a CopilotInteraction record for every turn on every channel (the test
    /// pane, Teams and Microsoft 365 Copilot). It is keyed on the agent's Entra Agent ID and carries no
    /// AgentName. Microsoft 365 Copilot also writes a record for each turn, keyed on "T_{titleId}.{agentId}"
    /// with the agent's display name. The payloads below have both shapes, with synthetic IDs and names only.
    ///
    /// The upsert tests run the real merge SQL (common_upsert_copilot_agents.sql) against the test database.
    /// Each uses its own random agent id, so they don't depend on what else is in copilot_agents.
    /// </summary>
    [TestClass]
    public class CopilotAgentIdentityTests : CopilotTestBase
    {
        private const string TenantId = "00000000-0000-0000-0000-000000000000";
        private const string AgentGuid = "11111111-1111-1111-1111-111111111111";
        private const string TitleId = "T_22222222-2222-2222-2222-222222222222";
        private const string BotId = "33333333-3333-3333-3333-333333333333";
        private const string NonDefaultEnvironmentId = "55555555-5555-5555-5555-555555555555";
        private const string SchemaName = "cr123_contosoHelpdesk";
        private const string DisplayName = "Contoso Helpdesk";

        // "Καλημέρα κόσμε": a synthetic non-ASCII display name, because copilot_agents.name must be Unicode-safe.
        private const string Greek = "\u039A\u03B1\u03BB\u03B7\u03BC\u03AD\u03C1\u03B1 \u03BA\u03CC\u03C3\u03BC\u03B5";

        #region Payloads

        /// <summary>The Copilot Studio runtime's record, as logged for a turn on any channel.</summary>
        private static string RuntimeRecord(string agentId = AgentGuid, string environmentId = "Default-" + TenantId, string appHost = "m365copilot")
            => $@"{{
                ""RecordType"": 261, ""Operation"": ""CopilotInteraction"", ""Workload"": ""Copilot"",
                ""AgentId"": ""{agentId}"",
                ""AgentBlueprintId"": ""25664c89-cea5-4ab6-b924-a54fd8a19ae0"",
                ""AgentPlatform"": ""CopilotStudio"",
                ""AppIdentity"": ""Copilot.Studio.{environmentId}-{SchemaName}"",
                ""PlatformAgentId"": ""{environmentId}_{BotId}"",
                ""OrganizationId"": ""{TenantId}"",
                ""CopilotEventData"": {{
                    ""AppHost"": ""{appHost}"",
                    ""ConversationId"": ""44444444-4444-4444-4444-444444444444"",
                    ""MessageIds"": [], ""AccessedResources"": [],
                    ""PlatformAgentId"": ""{environmentId}_{BotId}"",
                    ""PlatformAgentType"": ""CopilotStudio"",
                    ""ThreadId"": ""19:aaaa@thread.v2""
                }},
                ""CopilotLogVersion"": ""1.0.0.0""
            }}";

        /// <summary>The record Microsoft 365 Copilot logs in addition, for each turn with a published agent.</summary>
        private static string Microsoft365CopilotRecord(string agentId = AgentGuid, string displayName = DisplayName)
            => $@"{{
                ""RecordType"": 261, ""Operation"": ""CopilotInteraction"", ""Workload"": ""Copilot"",
                ""AgentId"": ""{TitleId}.{agentId}"",
                ""AgentName"": ""{displayName}"",
                ""AppIdentity"": ""Copilot.Studio.CustomEngine.{TitleId}"",
                ""OrganizationId"": ""{TenantId}"",
                ""CopilotEventData"": {{
                    ""AppHost"": ""Office"",
                    ""ConversationId"": ""44444444-4444-4444-4444-444444444444"",
                    ""Messages"": [{{ ""Id"": ""1700000000001"", ""isPrompt"": true }}, {{ ""Id"": ""1700000000002"", ""isPrompt"": false }}],
                    ""TargetAgentName"": ""{displayName}"",
                    ""TargetPlatformAgentId"": ""{TitleId}"",
                    ""LicenseType"": ""Premium"", ""Contexts"": [], ""AccessedResources"": [],
                    ""ThreadId"": ""19:bbbb@thread.v2""
                }},
                ""CopilotLogVersion"": ""1.0.0.0""
            }}";

        #endregion

        #region FromJson

        [TestMethod]
        public void FromJson_RuntimeAndMicrosoft365CopilotRecords_ResolveToTheSameAgentId()
        {
            var runtime = CopilotAuditLogContent.FromJson(RuntimeRecord());
            var m365Copilot = CopilotAuditLogContent.FromJson(Microsoft365CopilotRecord());

            Assert.AreEqual(AgentGuid, runtime.AgentId, "The runtime record is keyed on the agent's Entra Agent ID.");
            Assert.AreEqual(AgentGuid, m365Copilot.AgentId, "T_{titleId}.{agentId} must resolve to the runtime record's agent id.");
        }

        [TestMethod]
        public void FromJson_RuntimeRecord_GetsItsSchemaNameAsAFallbackOnly()
        {
            var runtime = CopilotAuditLogContent.FromJson(RuntimeRecord());

            Assert.IsNull(runtime.AgentName, "The runtime record carries no display name, and none is made up for it.");
            Assert.AreEqual(SchemaName, runtime.AgentFallbackName);
            Assert.AreEqual(0, runtime.Cost.TotalCredits, "The runtime record carries no Messages, so it is never priced (#639).");
            Assert.AreEqual(CopilotAgentCreditBasis.NoMessages, runtime.Cost.AgentCreditBasis);
        }

        [TestMethod]
        public void FromJson_Microsoft365CopilotRecord_KeepsItsDisplayName()
        {
            var m365Copilot = CopilotAuditLogContent.FromJson(Microsoft365CopilotRecord(displayName: Greek));

            Assert.AreEqual(Greek, m365Copilot.AgentName);
            Assert.IsNull(m365Copilot.AgentFallbackName, "A record that has a display name needs no fallback.");
        }

        [TestMethod]
        public void FromJson_RuntimeRecordFromANonDefaultEnvironment_GetsTheFallbackName()
        {
            // Every environment but the default one has a bare GUID id, with no tenant id in it.
            var runtime = CopilotAuditLogContent.FromJson(RuntimeRecord(environmentId: NonDefaultEnvironmentId));

            Assert.AreEqual(AgentGuid, runtime.AgentId);
            Assert.AreEqual(SchemaName, runtime.AgentFallbackName);
        }

        [TestMethod]
        public void FromJson_RecordWithoutAnAgentIdFromANonDefaultEnvironment_GetsAKeyAndAName()
        {
            var appIdentity = $"Copilot.Studio.{NonDefaultEnvironmentId}-{SchemaName}";
            var json = $@"{{
                ""OrganizationId"": ""{TenantId}"",
                ""AppIdentity"": ""{appIdentity}"",
                ""CopilotEventData"": {{ ""AppHost"": ""Teams"", ""AccessedResources"": [], ""Contexts"": [] }}
            }}";

            var result = CopilotAuditLogContent.FromJson(json);

            Assert.AreEqual(appIdentity, result.AgentId, "Keyed on AppIdentity, as the default environment's records already are.");
            Assert.AreEqual(SchemaName, result.AgentName);
        }

        [DataTestMethod]
        [DataRow("Copilot.Studio.CustomEngine.T_22222222-2222-2222-2222-222222222222")]
        [DataRow("Copilot.M365Copilot.CoworkChat")]
        [DataRow("Copilot.Studio.Default-00000000-0000-0000-0000-000000000000")]
        [DataRow("SomeOtherFormat-00000000-0000-0000-0000-000000000000-cr123_contosoHelpdesk")]
        public void FromJson_RecordWithAnAgentIdButNoName_GetsNoFallbackFromAnyOtherAppIdentity(string appIdentity)
        {
            var json = $@"{{
                ""AgentId"": ""{AgentGuid}"",
                ""AppIdentity"": ""{appIdentity}"",
                ""OrganizationId"": ""{TenantId}"",
                ""CopilotEventData"": {{ ""AppHost"": ""Office"", ""AccessedResources"": [], ""Contexts"": [] }}
            }}";

            var result = CopilotAuditLogContent.FromJson(json);

            Assert.AreEqual(AgentGuid, result.AgentId);
            Assert.IsNull(result.AgentFallbackName);
        }

        [DataTestMethod]
        [DataRow("Copilot.M365Copilot.CoworkChat")]
        [DataRow("CopilotStudio.Declarative.T_22222222-2222-2222-2222-222222222222.11111111-1111-1111-1111-111111111111")]
        [DataRow("CopilotStudio.CustomEngine.T_22222222-2222-2222-2222-222222222222")]
        [DataRow("SPO_M2U2ZGNkExampleItemId_01ABCDEF")]
        [DataRow("Contoso.Unknown.Agent.Id")]
        [DataRow("T_22222222-2222-2222-2222-222222222222")]
        [DataRow("P_22222222-2222-2222-2222-222222222222.11111111-1111-1111-1111-111111111111")]
        [DataRow("T_22222222-2222-2222-2222-222222222222.not-a-guid")]
        [DataRow("T_22222222-2222-2222-2222-222222222222.11111111-1111-1111-1111-111111111111.extra")]
        [DataRow("XT_22222222-2222-2222-2222-222222222222.11111111-1111-1111-1111-111111111111")]
        public void FromJson_OtherAgentIdShapes_AreNotRewritten(string agentId)
        {
            var json = $@"{{
                ""AgentId"": ""{agentId}"",
                ""AgentName"": ""Contoso Agent"",
                ""CopilotEventData"": {{ ""AppHost"": ""Office"", ""AccessedResources"": [], ""Contexts"": [] }}
            }}";

            Assert.AreEqual(agentId, CopilotAuditLogContent.FromJson(json).AgentId);
        }

        #endregion

        #region Upsert

        /// <summary>Probe (c) in #699: one batch, one new agent_id, an unnamed record and a named one.</summary>
        [TestMethod]
        public async Task Upsert_OneBatchWithAnUnnamedAndANamedRecord_InsertsOneRowWithTheName()
        {
            var agentId = NewAgentId();

            await CommitBatch(Record(agentId), Record(agentId, name: DisplayName));

            var rows = await AgentRows(agentId);
            Assert.AreEqual(1, rows.Count, "One agent_id must never get two copilot_agents rows.");
            Assert.AreEqual(DisplayName, rows[0].Name);
        }

        [TestMethod]
        public async Task Upsert_OneBatchWithAFallbackAndADisplayName_InsertsOneRowWithTheDisplayName()
        {
            var agentId = NewAgentId();

            await CommitBatch(Record(agentId, fallbackName: SchemaName), Record(agentId, name: DisplayName));

            var rows = await AgentRows(agentId);
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(DisplayName, rows[0].Name);
        }

        /// <summary>Probe (a) in #699: a later named record must fill in an agent first seen without a name.</summary>
        [TestMethod]
        public async Task Upsert_LaterNamedRecord_FillsInANullName()
        {
            var agentId = NewAgentId();

            await CommitBatch(Record(agentId));
            Assert.IsNull((await SingleAgentRow(agentId)).Name);

            await CommitBatch(Record(agentId, name: DisplayName));
            Assert.AreEqual(DisplayName, (await SingleAgentRow(agentId)).Name);
        }

        [TestMethod]
        public async Task Upsert_FallbackName_FillsANullNameButNeverReplacesADisplayName()
        {
            var agentId = NewAgentId();

            await CommitBatch(Record(agentId, fallbackName: SchemaName));
            Assert.AreEqual(SchemaName, (await SingleAgentRow(agentId)).Name, "With nothing better known, the schema name is shown.");

            await CommitBatch(Record(agentId, name: DisplayName));
            Assert.AreEqual(DisplayName, (await SingleAgentRow(agentId)).Name, "A display name replaces the fallback.");

            await CommitBatch(Record(agentId, fallbackName: SchemaName));
            Assert.AreEqual(DisplayName, (await SingleAgentRow(agentId)).Name, "The fallback must never replace a display name.");
        }

        /// <summary>Probe (b) in #699: a change to is_custom_agent alone must not wipe the name.</summary>
        [TestMethod]
        public async Task Upsert_FlagChange_DoesNotWipeTheName()
        {
            var agentId = NewAgentId();

            await CommitBatch(Record(agentId, name: DisplayName));
            await CommitBatch(Record(agentId, name: DisplayName, isCustomAgent: true));

            var row = await SingleAgentRow(agentId);
            Assert.AreEqual(DisplayName, row.Name);
            Assert.AreEqual(true, row.IsCustomAgent);

            await CommitBatch(Record(agentId, isCustomAgent: false));

            row = await SingleAgentRow(agentId);
            Assert.AreEqual(DisplayName, row.Name, "A batch that carries only a flag must leave the name alone.");
            Assert.AreEqual(false, row.IsCustomAgent);
        }

        /// <summary>Probe (d) in #699: a rename in a batch that carries no flag must not wipe the flag.</summary>
        [TestMethod]
        public async Task Upsert_RenameWithoutAFlag_DoesNotWipeTheFlag()
        {
            var agentId = NewAgentId();

            await CommitBatch(Record(agentId, name: DisplayName, isCustomAgent: true));
            await CommitBatch(Record(agentId, name: Greek));

            var row = await SingleAgentRow(agentId);
            Assert.AreEqual(Greek, row.Name, "The rename is applied, and the non-ASCII name survives.");
            Assert.AreEqual(true, row.IsCustomAgent);
        }

        [TestMethod]
        public async Task Upsert_BatchThatDisagreesOnTheName_KeepsTheStoredName()
        {
            var agentId = NewAgentId();
            const string renamed = "Contoso IT Helpdesk";

            await CommitBatch(Record(agentId, name: DisplayName));
            await CommitBatch(Record(agentId, name: renamed), Record(agentId, name: DisplayName));
            Assert.AreEqual(DisplayName, (await SingleAgentRow(agentId)).Name, "Records that disagree must not flip the name.");

            await CommitBatch(Record(agentId, name: renamed));
            Assert.AreEqual(renamed, (await SingleAgentRow(agentId)).Name, "Once the batch agrees, the rename is applied.");
        }

        [TestMethod]
        public async Task Upsert_NameLongerThanTheColumn_IsTrimmedRatherThanFailingTheBatch()
        {
            var agentId = NewAgentId();
            var longName = string.Concat(Enumerable.Repeat(Greek + " ", 12));
            Assert.IsTrue(longName.Length > 100);

            var eventIds = await CommitBatch(Record(agentId, name: longName));

            var row = await SingleAgentRow(agentId);
            Assert.AreEqual(longName.Substring(0, 100), row.Name);
            using (var db = new AnalyticsEntitiesContext())
            {
                var chatEventId = eventIds.Single();
                var chat = await db.CopilotChats.AsNoTracking().SingleAsync(c => c.AuditEvent.Id == chatEventId);
                Assert.AreEqual(row.ID, chat.AgentId, "The interaction must still be saved against its agent.");
            }
        }

        /// <summary>
        /// The whole path, from the two audit payloads to the tables: one Copilot Studio agent used in Microsoft
        /// 365 Copilot and in Teams is one copilot_agents row. It is named from its schema name until a Microsoft
        /// 365 Copilot record brings the display name, and every interaction points at that row.
        /// </summary>
        [TestMethod]
        public async Task EndToEnd_RuntimeAndMicrosoft365CopilotRecords_ShareOneNamedAgentRow()
        {
            var agentGuid = Guid.NewGuid().ToString();

            var firstBatch = await CommitBatch(
                CopilotAuditLogContent.FromJson(RuntimeRecord(agentGuid, appHost: "Microsoft Teams")));
            Assert.AreEqual(SchemaName, (await SingleAgentRow(agentGuid)).Name, "Teams-only use is named from the schema name.");

            var secondBatch = await CommitBatch(
                CopilotAuditLogContent.FromJson(RuntimeRecord(agentGuid, appHost: "m365copilot")),
                CopilotAuditLogContent.FromJson(Microsoft365CopilotRecord(agentGuid, Greek)));

            var agent = await SingleAgentRow(agentGuid);
            Assert.AreEqual(Greek, agent.Name, "The display name replaces the schema name.");

            using (var db = new AnalyticsEntitiesContext())
            {
                Assert.AreEqual(0, await db.CopilotAgents.CountAsync(a => a.AgentID.EndsWith(agentGuid) && a.AgentID != agentGuid),
                    "No T_{titleId}.{agentId} row may be created alongside the agent's own row.");

                var eventIds = firstBatch.Concat(secondBatch).ToList();
                var chatAgentIds = await db.CopilotChats.AsNoTracking()
                    .Where(c => eventIds.Contains(c.AuditEvent.Id))
                    .Select(c => c.AgentId)
                    .ToListAsync();
                Assert.AreEqual(3, chatAgentIds.Count);
                Assert.IsTrue(chatAgentIds.All(id => id == agent.ID), "Every interaction must point at the one agent row.");
            }
        }

        /// <summary>
        /// Before #699, the upsert could leave an agent_id with two rows. New interactions with such an agent must
        /// always land on the same (oldest) row.
        /// </summary>
        [TestMethod]
        public async Task Upsert_AgentIdThatAlreadyHasTwoRows_SendsInteractionsToTheOldestRow()
        {
            var agentId = NewAgentId();
            await AddDuplicateAgentRows(agentId);
            var oldestRowId = (await AgentRows(agentId)).Min(r => r.ID);

            var eventIds = await CommitBatch(Record(agentId, name: DisplayName), Record(agentId, name: DisplayName));

            using (var db = new AnalyticsEntitiesContext())
            {
                var chatAgentIds = await db.CopilotChats.AsNoTracking()
                    .Where(c => eventIds.Contains(c.AuditEvent.Id))
                    .Select(c => c.AgentId)
                    .ToListAsync();
                Assert.AreEqual(2, chatAgentIds.Count);
                Assert.IsTrue(chatAgentIds.All(id => id == oldestRowId));
            }
        }

        /// <summary>
        /// The Teams meeting merge joined copilot_agents without using it, so an agent_id with two rows gave a
        /// meeting interaction two copilot_event_meetings rows, which violates that table's primary key.
        /// </summary>
        [TestMethod]
        public async Task TeamsMeetingInteraction_ForAnAgentIdThatAlreadyHasTwoRows_IsSavedOnce()
        {
            var agentId = NewAgentId();
            await AddDuplicateAgentRows(agentId);

            var meetingRecord = Record(agentId, name: DisplayName);
            meetingRecord.CopilotEventData.AppHost = "Teams";
            meetingRecord.CopilotEventData.Contexts = new List<Context>
            {
                new Context
                {
                    Id = "https://microsoft.teams.com/threads/19:meeting_MDAwMDAwMDAtMDAwMC0wMDAwLTAwMDAtMDAwMDAwMDAwMDAw@thread.v2",
                    Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING,
                },
            };

            var eventId = (await CommitBatch(meetingRecord)).Single();

            using (var db = new AnalyticsEntitiesContext())
            {
                Assert.AreEqual(1, await db.CopilotEventMetadataMeetings.CountAsync(m => m.ChatId == eventId));
            }
        }

        #endregion

        #region Repair of existing installations

        /// <summary>
        /// The headline case for a database an older importer has already filled: a Copilot Studio agent split into
        /// its runtime row (keyed on the agent id, unnamed) and its Microsoft 365 Copilot row (T_..., named).
        /// </summary>
        [TestMethod]
        public async Task Repair_SplitCopilotStudioAgent_IsMergedIntoTheRowKeyedOnItsAgentId()
        {
            var agentGuid = NewAgentId();
            var titleScopedId = $"T_{Guid.NewGuid()}.{agentGuid}";
            var runtimeRowId = await AddLegacyAgentRow(agentGuid);
            var m365CopilotRowId = await AddLegacyAgentRow(titleScopedId, Greek, isCustomAgent: true);
            var runtimeChats = await CommitBatch(Record(agentGuid), Record(agentGuid));
            var m365CopilotChats = await CommitBatch(Record(titleScopedId));

            var result = await RunRepair();

            Assert.IsTrue(result.MergedAgentRows >= 1);
            Assert.IsTrue(result.MovedInteractions >= 1);
            var kept = await SingleAgentRow(agentGuid);
            Assert.AreEqual(runtimeRowId, kept.ID, "The row already keyed on the agent id is the one kept.");
            Assert.AreEqual(Greek, kept.Name, "It takes the display name Microsoft 365 Copilot logged.");
            Assert.AreEqual(true, kept.IsCustomAgent);
            Assert.IsNull(await AgentRowById(m365CopilotRowId), "The T_ row is deleted once its interactions have moved.");
            CollectionAssert.AreEqual(new List<int?> { kept.ID, kept.ID, kept.ID },
                await ChatAgentIds(runtimeChats.Concat(m365CopilotChats)), "Every interaction is on the kept row.");

            // The importer's next Microsoft 365 Copilot record for the agent lands on the same row.
            var next = await CommitBatch(CopilotAuditLogContent.FromJson(Microsoft365CopilotRecord(agentGuid, Greek)));
            Assert.AreEqual(kept.ID, (await ChatAgentIds(next)).Single());
            Assert.AreEqual(kept.ID, (await SingleAgentRow(agentGuid)).ID);
        }

        [TestMethod]
        public async Task Repair_TitleScopedRowWithNoAgentIdRow_IsRekeyedInPlace()
        {
            var agentGuid = NewAgentId();
            var titleScopedId = $"T_{Guid.NewGuid()}.{agentGuid}";
            var rowId = await AddLegacyAgentRow(titleScopedId, DisplayName);
            var chats = await CommitBatch(Record(titleScopedId));

            var result = await RunRepair();

            Assert.IsTrue(result.RekeyedAgentRows >= 1);
            var kept = await SingleAgentRow(agentGuid);
            Assert.AreEqual(rowId, kept.ID, "The row keeps its id, so nothing that points at it has to move.");
            Assert.AreEqual(DisplayName, kept.Name);
            Assert.AreEqual(rowId, (await ChatAgentIds(chats)).Single());
        }

        [TestMethod]
        public async Task Repair_DuplicateRowsForOneAgentId_AreMergedIntoTheOldest()
        {
            var agentId = NewAgentId();
            var oldestRowId = await AddLegacyAgentRow(agentId);
            var newerRowId = await AddLegacyAgentRow(agentId, DisplayName, isCustomAgent: true);
            var chats = await CommitBatch(Record(agentId), Record(agentId));
            await MoveChat(chats[1], newerRowId);   // the old upsert could send an interaction to either row

            await RunRepair();

            var kept = await SingleAgentRow(agentId);
            Assert.AreEqual(oldestRowId, kept.ID);
            Assert.AreEqual(DisplayName, kept.Name, "A NULL name is filled from the duplicate.");
            Assert.AreEqual(true, kept.IsCustomAgent);
            CollectionAssert.AreEqual(new List<int?> { oldestRowId, oldestRowId }, await ChatAgentIds(chats));
        }

        /// <summary>
        /// The importer has collapsed "SharePointAgents.Declarative.SPO_..." to "SPO_..." since July 2026, but nothing
        /// merged the rows created before that.
        /// </summary>
        [TestMethod]
        public async Task Repair_SharePointWrapperRow_IsMergedIntoTheBareSpoRow()
        {
            var spoId = "SPO_" + Guid.NewGuid().ToString("N");
            var wrapperId = "SharePointAgents.Declarative." + spoId;
            var wrapperRowId = await AddLegacyAgentRow(wrapperId, "Contoso Proposals Agent");
            var bareRowId = await AddLegacyAgentRow(spoId, "Contoso Proposals Agent");
            var wrapperChats = await CommitBatch(Record(wrapperId));

            await RunRepair();

            var kept = await SingleAgentRow(spoId);
            Assert.AreEqual(bareRowId, kept.ID);
            Assert.IsNull(await AgentRowById(wrapperRowId));
            Assert.AreEqual(bareRowId, (await ChatAgentIds(wrapperChats)).Single());
        }

        [TestMethod]
        public async Task Repair_PrefersTheMicrosoft365CopilotDisplayNameOverASchemaName()
        {
            var agentGuid = NewAgentId();
            await AddLegacyAgentRow($"T_{Guid.NewGuid()}.{agentGuid}", DisplayName);   // the older row
            var runtimeRowId = await AddLegacyAgentRow(agentGuid, SchemaName);

            await RunRepair();

            var kept = await SingleAgentRow(agentGuid);
            Assert.AreEqual(runtimeRowId, kept.ID, "The row already keyed on the agent id is kept, even when it is newer.");
            Assert.AreEqual(DisplayName, kept.Name);
        }

        [TestMethod]
        public async Task Repair_LeavesEveryOtherIdShapeAlone()
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var agentIds = new[]
            {
                $"CopilotStudio.Declarative.T_{a}.{b}",
                $"T_{a}",
                $"P_{a}.{b}",
                $"T_{a}.{b}.extra",
                $"Copilot.M365Copilot.{a:N}",
                $"Contoso.Unknown.{a:N}",
                b.ToString(),
            };
            var rowIds = new List<int>();
            foreach (var agentId in agentIds)
            {
                rowIds.Add(await AddLegacyAgentRow(agentId, DisplayName));
            }

            await RunRepair();

            for (var i = 0; i < agentIds.Length; i++)
            {
                var row = await AgentRowById(rowIds[i]);
                Assert.IsNotNull(row, $"{agentIds[i]} must not be merged away.");
                Assert.AreEqual(agentIds[i], row.AgentID);
                Assert.AreEqual(DisplayName, row.Name);
            }
        }

        [TestMethod]
        public async Task Repair_RunAgain_DoesNothing()
        {
            var agentGuid = NewAgentId();
            var titleScopedId = $"T_{Guid.NewGuid()}.{agentGuid}";
            await AddLegacyAgentRow(agentGuid);
            await AddLegacyAgentRow(titleScopedId, DisplayName);
            await CommitBatch(Record(titleScopedId));

            await RunRepair();
            var second = await RunRepair();

            Assert.AreEqual(0, second.MergedAgentRows);
            Assert.AreEqual(0, second.RekeyedAgentRows);
            Assert.AreEqual(0L, second.MovedInteractions);
            Assert.AreEqual(0, second.PendingAgentRows);
        }

        #endregion

        #region Helpers

        private static string NewAgentId() => Guid.NewGuid().ToString();

        private static CopilotAuditLogContent Record(string agentId, string name = null, string fallbackName = null, bool? isCustomAgent = null)
            => new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData { AppHost = "m365copilot" },
                AgentId = agentId,
                AgentName = name,
                AgentFallbackName = fallbackName,
                IsCustomAgent = isCustomAgent,
            };

        /// <summary>Stages each record against its own new audit event, and commits them all as ONE batch.</summary>
        private async Task<List<Guid>> CommitBatch(params CopilotAuditLogContent[] records)
        {
            var manager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
            var eventIds = new List<Guid>();
            using (var db = new AnalyticsEntitiesContext())
            {
                foreach (var record in records)
                {
                    var commonEvent = new CommonAuditEvent
                    {
                        TimeStamp = DateTime.UtcNow,
                        Operation = new EventOperation { Name = "Copilot agent identity test " + Guid.NewGuid() },
                        User = new User { AzureAdId = "test", UserPrincipalName = $"agent.identity.{Guid.NewGuid():N}@contoso.com" },
                        Id = Guid.NewGuid(),
                    };
                    db.AuditEventsCommon.Add(commonEvent);
                    await db.SaveChangesAsync();

                    await manager.SaveSingleCopilotEventToSqlStaging(record, commonEvent);
                    eventIds.Add(commonEvent.Id);
                }
            }
            await manager.CommitAllChanges();
            return eventIds;
        }

        private static async Task<List<CopilotAgent>> AgentRows(string agentId)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                return await db.CopilotAgents.AsNoTracking().Where(a => a.AgentID == agentId).OrderBy(a => a.ID).ToListAsync();
            }
        }

        private static async Task<CopilotAgent> SingleAgentRow(string agentId)
        {
            var rows = await AgentRows(agentId);
            Assert.AreEqual(1, rows.Count, $"Expected exactly one copilot_agents row for agent id {agentId}.");
            return rows[0];
        }

        /// <summary>Two rows for one agent_id, as the upsert could leave behind before #699.</summary>
        private static async Task AddDuplicateAgentRows(string agentId)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await db.Database.ExecuteSqlCommandAsync(
                    "INSERT INTO dbo.copilot_agents ([name], agent_id) VALUES ({0}, {1}); " +
                    "INSERT INTO dbo.copilot_agents ([name], agent_id) VALUES (NULL, {1});",
                    DisplayName, agentId);
            }
        }

        /// <summary>A copilot_agents row exactly as an older importer could have left it. Returns its id.</summary>
        private static async Task<int> AddLegacyAgentRow(string agentId, string name = null, bool? isCustomAgent = null)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                return await db.Database.SqlQuery<int>(
                    "INSERT INTO dbo.copilot_agents ([name], agent_id, is_custom_agent) OUTPUT INSERTED.id VALUES ({0}, {1}, {2});",
                    name, agentId, isCustomAgent).SingleAsync();
            }
        }

        private static async Task<CopilotAgent> AgentRowById(int id)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                return await db.CopilotAgents.AsNoTracking().SingleOrDefaultAsync(a => a.ID == id);
            }
        }

        /// <summary>The agent row each interaction points at, in the order the events are given.</summary>
        private static async Task<List<int?>> ChatAgentIds(IEnumerable<Guid> eventIds)
        {
            var ids = eventIds.ToList();
            using (var db = new AnalyticsEntitiesContext())
            {
                var byEvent = await db.CopilotChats.AsNoTracking()
                    .Where(c => ids.Contains(c.AuditEvent.Id))
                    .Select(c => new { EventId = c.AuditEvent.Id, c.AgentId })
                    .ToListAsync();
                return ids.Select(id => byEvent.Single(c => c.EventId == id).AgentId).ToList();
            }
        }

        private static async Task MoveChat(Guid eventId, int agentRowId)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await db.Database.ExecuteSqlCommandAsync(
                    "UPDATE dbo.copilot_chats SET agent_id = {0} WHERE event_id = {1};", agentRowId, eventId);
            }
        }

        private Task<CopilotAgentRepairResult> RunRepair()
            => CopilotAuditEventManager.RunSplitAgentRepairAsync(_config.ConnectionStrings.DatabaseConnectionString);

        #endregion
    }
}
