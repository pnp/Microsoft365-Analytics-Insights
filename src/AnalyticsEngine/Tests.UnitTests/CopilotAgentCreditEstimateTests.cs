using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Common.Entities.Copilot;
using Common.Entities.Entities.AuditLog;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Issue #639, from the audit payload to the stored row. From Stable build 1552 <c>FromJson</c> never
    /// classified an agent, so every agent interaction was estimated at 0 credits and every new agent was
    /// stored with <c>is_custom_agent</c> NULL. The estimator's own tests all called it directly with
    /// "custom agent: true", so none of them went through the path that was broken. These do.
    ///
    /// Synthetic payloads only: zeroed or repeated-digit GUIDs and "Contoso" names, in the record shapes
    /// documented by Purview and observed in #699.
    /// </summary>
    [TestClass]
    public class CopilotAgentCreditEstimateTests : CopilotTestBase
    {
        private const string TenantId = "00000000-0000-0000-0000-000000000000";
        private const string AgentGuid = "11111111-1111-1111-1111-111111111111";
        private const string TitleId = "T_22222222-2222-2222-2222-222222222222";
        private const string SchemaName = "cr123_contosoHelpdesk";
        private const string DisplayName = "Contoso Helpdesk";
        private const string OneResponse = @"""Messages"": [ { ""Id"": ""1700000000001"", ""isPrompt"": true }, { ""Id"": ""1700000000002"", ""isPrompt"": false } ]";
        private const string DeepReasoning = @"""ModelTransparencyDetails"": [ { ""ModelName"": ""DEEP_LEO"" } ]";

        #region Payloads

        /// <summary>A named agent's record, with whatever CopilotEventData members the test needs.</summary>
        private static string AgentRecord(string agentId, string agentName = DisplayName, string appIdentity = null, string eventData = OneResponse)
            => $@"{{
                ""RecordType"": 261, ""Operation"": ""CopilotInteraction"", ""Workload"": ""Copilot"",
                ""AgentId"": {Json(agentId)},
                ""AgentName"": {Json(agentName)},
                ""AppIdentity"": {Json(appIdentity)},
                ""OrganizationId"": ""{TenantId}"",
                ""CopilotEventData"": {{ ""AppHost"": ""Office"", ""AccessedResources"": [], ""Contexts"": [], {eventData} }}
            }}";

        /// <summary>Copilot Studio's runtime record for one turn (#699): the agent's id, no name, no Messages.</summary>
        private static string RuntimeRecord(string agentGuid = AgentGuid, string appHost = "m365copilot", string extraEventData = null)
            => $@"{{
                ""RecordType"": 261, ""Operation"": ""CopilotInteraction"", ""Workload"": ""Copilot"",
                ""AgentId"": ""{agentGuid}"",
                ""AgentBlueprintId"": ""00000000-0000-0000-0000-000000000000"",
                ""AgentPlatform"": ""CopilotStudio"",
                ""AppIdentity"": ""Copilot.Studio.Default-{TenantId}-{SchemaName}"",
                ""OrganizationId"": ""{TenantId}"",
                ""CopilotEventData"": {{
                    ""AppHost"": ""{appHost}"",
                    ""ConversationId"": ""44444444-4444-4444-4444-444444444444"",
                    ""MessageIds"": [], ""AccessedResources"": []{(extraEventData == null ? string.Empty : ", " + extraEventData)},
                    ""ThreadId"": ""19:aaaa@thread.v2""
                }},
                ""CopilotLogVersion"": ""1.0.0.0""
            }}";

        /// <summary>The record Microsoft 365 Copilot logs as well, for the same turn (#699): name and Messages.</summary>
        private static string Microsoft365CopilotRecord(string agentGuid = AgentGuid, string extraEventData = null)
            => $@"{{
                ""RecordType"": 261, ""Operation"": ""CopilotInteraction"", ""Workload"": ""Copilot"",
                ""AgentId"": ""{TitleId}.{agentGuid}"",
                ""AgentName"": ""{DisplayName}"",
                ""AppIdentity"": ""Copilot.Studio.CustomEngine.{TitleId}"",
                ""OrganizationId"": ""{TenantId}"",
                ""CopilotEventData"": {{
                    ""AppHost"": ""Office"",
                    ""ConversationId"": ""44444444-4444-4444-4444-444444444444"",
                    {OneResponse},
                    ""TargetAgentName"": ""{DisplayName}"",
                    ""TargetPlatformAgentId"": ""{TitleId}"",
                    ""LicenseType"": ""Premium"", ""Contexts"": [], ""AccessedResources"": []{(extraEventData == null ? string.Empty : ", " + extraEventData)},
                    ""ThreadId"": ""19:bbbb@thread.v2""
                }},
                ""CopilotLogVersion"": ""1.0.0.0""
            }}";

        private static string Json(string value) => value == null ? "null" : JsonConvert.ToString(value);

        #endregion

        #region FromJson

        /// <summary>Acceptance criterion 1 of #639, first half.</summary>
        [TestMethod]
        public void FromJson_CustomEngineAgentWithOneResponse_IsPriced()
        {
            var record = CopilotAuditLogContent.FromJson(AgentRecord("CopilotStudio.CustomEngine." + TenantId));

            Assert.AreEqual(CopilotAgentKind.CustomEngine, record.AgentKind);
            Assert.AreEqual(CopilotAgentOrigin.CustomerBuilt, record.AgentOrigin);
            Assert.AreEqual(true, record.IsCustomAgent);
            Assert.IsTrue(record.Cost.TotalCredits > 0, "A custom-engine agent's response is priced.");
            Assert.AreEqual(2, record.Cost.TotalCredits, "One generative answer, nothing accessed: 2 credits.");
            Assert.AreEqual(CopilotAgentCreditBasis.CustomEngine, record.Cost.AgentCreditBasis);
        }

        /// <summary>Acceptance criterion 1 of #639, second half: a 0 that says it was not assessed.</summary>
        [TestMethod]
        public void FromJson_UnrecognisedAgent_IsZeroAndRecordedAsNotAssessed()
        {
            var record = CopilotAuditLogContent.FromJson(AgentRecord("Contoso.Unrecognised.Agent"));

            Assert.AreEqual(CopilotAgentKind.Unknown, record.AgentKind);
            Assert.AreEqual(CopilotAgentOrigin.Unknown, record.AgentOrigin);
            Assert.IsNull(record.IsCustomAgent, "An unrecognised agent is stored with is_custom_agent NULL, not guessed.");
            Assert.AreEqual(0, record.Cost.TotalCredits);
            Assert.AreEqual(CopilotAgentCreditBasis.NotAssessed, record.Cost.AgentCreditBasis,
                "The 0 must be distinguishable from a genuine 0.");

            var stored = JsonConvert.SerializeObject(record.Cost);
            StringAssert.Contains(stored, "\"AgentCreditBasis\":\"NotAssessed\"", "The basis is persisted in copilot_credit_estimate_json.");
            StringAssert.Contains(stored, "\"CostModelVersion\":\"1.2.0.0\"", "...under a model version that says the estimate is post-#639.");
        }

        [TestMethod]
        public void FromJson_DeclarativeAgent_IsAGenuineZero()
        {
            var record = CopilotAuditLogContent.FromJson(AgentRecord("CopilotStudio.Declarative." + TenantId));

            Assert.AreEqual(true, record.IsCustomAgent, "Declarative and customer-built.");
            Assert.AreEqual(0, record.Cost.TotalCredits);
            Assert.AreEqual(CopilotAgentCreditBasis.Declarative, record.Cost.AgentCreditBasis);
        }

        [TestMethod]
        public void FromJson_MicrosoftAgent_IsAGenuineZero()
        {
            var record = CopilotAuditLogContent.FromJson(AgentRecord(null, "Copilot Cowork", "Copilot.M365Copilot.CoworkChat"));

            Assert.AreEqual("Copilot.M365Copilot.CoworkChat", record.AgentId);
            Assert.AreEqual(CopilotAgentOrigin.Microsoft, record.AgentOrigin);
            Assert.AreEqual(false, record.IsCustomAgent, "is_custom_agent = 0 now means Microsoft's.");
            Assert.AreEqual(0, record.Cost.TotalCredits);
            Assert.AreEqual(CopilotAgentCreditBasis.MicrosoftAgent, record.Cost.AgentCreditBasis);
        }

        [TestMethod]
        public void FromJson_SharePointAgent_IsDeclarativeOfUnknownOrigin()
        {
            var record = CopilotAuditLogContent.FromJson(AgentRecord("SharePointAgents.Declarative.SPO_ContosoExampleItemId_01", "Contoso Proposals Agent"));

            Assert.AreEqual("SPO_ContosoExampleItemId_01", record.AgentId);
            Assert.AreEqual(CopilotAgentKind.Declarative, record.AgentKind);
            Assert.IsNull(record.IsCustomAgent, "A user's SharePoint agent and a site's ready-made one may share the prefix.");
            Assert.AreEqual(CopilotAgentCreditBasis.Declarative, record.Cost.AgentCreditBasis);
        }

        [TestMethod]
        public void FromJson_RecordWithNoAgent_IsNotAnAgentInteraction()
        {
            var record = CopilotAuditLogContent.FromJson(AgentRecord(null, null));

            Assert.IsNull(record.AgentId);
            Assert.IsNull(record.IsCustomAgent);
            Assert.AreSame(CopilotCreditEstimation.NoCost, record.Cost);
            Assert.AreEqual(CopilotAgentCreditBasis.NoAgent, record.Cost.AgentCreditBasis);
        }

        [TestMethod]
        public void FromJson_PayloadIsCustomAgentProperty_IsIgnored()
        {
            // Microsoft documents no IsCustomAgent property, but one would bind during deserialisation. The
            // classifier decides, so an unexpected payload property cannot mislabel the agent.
            var json = AgentRecord("Contoso.Unrecognised.Agent").Replace(@"""RecordType"": 261,", @"""RecordType"": 261, ""IsCustomAgent"": true,");

            Assert.IsNull(CopilotAuditLogContent.FromJson(json).IsCustomAgent);
        }

        /// <summary>
        /// The check #639 asked for: one Microsoft 365 Copilot turn with a Copilot Studio agent is logged twice
        /// (#699) - by the Copilot Studio runtime (no Messages) and by Microsoft 365 Copilot (with Messages) -
        /// and must be priced on one of them only.
        /// </summary>
        [TestMethod]
        public void FromJson_Microsoft365CopilotTurn_IsPricedOnOneTwinOnly()
        {
            var runtime = CopilotAuditLogContent.FromJson(RuntimeRecord());
            var m365Copilot = CopilotAuditLogContent.FromJson(Microsoft365CopilotRecord());

            Assert.AreEqual(runtime.AgentId, m365Copilot.AgentId, "Both twins are the same agent (#699).");
            Assert.AreEqual(true, runtime.IsCustomAgent, "AgentPlatform CopilotStudio: customer-built.");
            Assert.AreEqual(true, m365Copilot.IsCustomAgent, "Copilot.Studio.CustomEngine AppIdentity: customer-built.");

            Assert.AreEqual(0, runtime.Cost.TotalCredits);
            Assert.AreEqual(CopilotAgentCreditBasis.NoMessages, runtime.Cost.AgentCreditBasis);
            Assert.AreEqual(2, m365Copilot.Cost.TotalCredits);
            Assert.AreEqual(CopilotAgentCreditBasis.CustomEngine, m365Copilot.Cost.AgentCreditBasis);
        }

        /// <summary>
        /// Deep reasoning is priced per record, not per response, so it is the one charge that could still land
        /// on both twins if the runtime record ever listed the model. It cannot: a record with no Messages is
        /// never priced.
        /// </summary>
        [TestMethod]
        public void FromJson_DeepReasoningOnBothTwins_IsPricedOnce()
        {
            var runtime = CopilotAuditLogContent.FromJson(RuntimeRecord(extraEventData: DeepReasoning));
            var m365Copilot = CopilotAuditLogContent.FromJson(Microsoft365CopilotRecord(extraEventData: DeepReasoning));

            Assert.AreEqual(0, runtime.Cost.TotalCredits);
            Assert.AreEqual(1, runtime.Cost.ModelsUsed.Count, "The model is still recorded for analytics.");
            Assert.AreEqual(7, m365Copilot.Cost.TotalCredits, "2 for the answer and 5 for deep reasoning, once.");
        }

        [TestMethod]
        public void FromJson_TeamsTurnWithACopilotStudioAgent_IsNotPricedAndSaysWhy()
        {
            // Teams logs only the runtime record, which has nothing to price. A known gap (#699), recorded as such.
            var teams = CopilotAuditLogContent.FromJson(RuntimeRecord(appHost: "Microsoft Teams"));

            Assert.AreEqual(0, teams.Cost.TotalCredits);
            Assert.AreEqual(CopilotAgentCreditBasis.NoMessages, teams.Cost.AgentCreditBasis);
        }

        #endregion

        #region Through the agents upsert

        /// <summary>
        /// The classification reaches copilot_agents.is_custom_agent through the real merge SQL, and the estimate
        /// reaches copilot_chats: 1 for customer-built, 0 for Microsoft's, NULL when unknown.
        /// </summary>
        [TestMethod]
        public async Task Commit_StoresTheOriginAndTheEstimate()
        {
            var copilotStudioAgent = Guid.NewGuid().ToString();
            var unrecognised = "Contoso.Unrecognised." + Guid.NewGuid().ToString("N");
            var firstParty = "Copilot.M365Copilot.ContosoTest" + Guid.NewGuid().ToString("N");

            var eventIds = await CommitBatch(
                CopilotAuditLogContent.FromJson(RuntimeRecord(copilotStudioAgent)),
                CopilotAuditLogContent.FromJson(Microsoft365CopilotRecord(copilotStudioAgent)),
                CopilotAuditLogContent.FromJson(AgentRecord(unrecognised)),
                CopilotAuditLogContent.FromJson(AgentRecord(null, "Contoso first-party agent", firstParty)));

            Assert.AreEqual(true, (await SingleAgentRow(copilotStudioAgent)).IsCustomAgent);
            Assert.IsNull((await SingleAgentRow(unrecognised)).IsCustomAgent);
            Assert.AreEqual(false, (await SingleAgentRow(firstParty)).IsCustomAgent);

            using (var db = new AnalyticsEntitiesContext())
            {
                var chats = await db.CopilotChats.AsNoTracking()
                    .Where(c => eventIds.Contains(c.AuditEvent.Id))
                    .Select(c => new { EventId = c.AuditEvent.Id, c.CopilotCreditEstimateTotal, c.CopilotCreditEstimateJson })
                    .ToListAsync();
                var byEvent = eventIds.Select(id => chats.Single(c => c.EventId == id)).ToList();

                Assert.AreEqual(0, byEvent[0].CopilotCreditEstimateTotal, "Runtime twin: not priced.");
                Assert.AreEqual(2, byEvent[1].CopilotCreditEstimateTotal, "Microsoft 365 Copilot twin: priced.");
                Assert.AreEqual(0, byEvent[2].CopilotCreditEstimateTotal);
                Assert.AreEqual(CopilotAgentCreditBasis.NotAssessed,
                    JsonConvert.DeserializeObject<CopilotCreditEstimation>(byEvent[2].CopilotCreditEstimateJson).AgentCreditBasis);
            }
        }

        /// <summary>
        /// A record the classifier cannot place (NULL) must never undo what an earlier record established, and
        /// only a record with a contrary classification may change it.
        /// </summary>
        [TestMethod]
        public async Task Commit_UnrecognisedRecordNeverWipesAStoredOrigin()
        {
            var agentGuid = Guid.NewGuid().ToString();

            await CommitBatch(CopilotAuditLogContent.FromJson(Microsoft365CopilotRecord(agentGuid)));
            Assert.AreEqual(true, (await SingleAgentRow(agentGuid)).IsCustomAgent);

            // The same agent id, with nothing to say what it is: a bare GUID and a display name.
            var bare = CopilotAuditLogContent.FromJson(AgentRecord(agentGuid));
            Assert.IsNull(bare.IsCustomAgent, "Precondition: the classifier cannot place a bare GUID on its own.");
            await CommitBatch(bare);

            var row = await SingleAgentRow(agentGuid);
            Assert.AreEqual(true, row.IsCustomAgent, "NULL in a batch keeps the stored value.");
            Assert.AreEqual(DisplayName, row.Name, "...and the flag change path never touches the name (#699).");
        }

        #endregion

        #region Helpers

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
                        Operation = new EventOperation { Name = "Copilot agent origin test " + Guid.NewGuid() },
                        User = new User { AzureAdId = "test", UserPrincipalName = $"agent.origin.{Guid.NewGuid():N}@contoso.com" },
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

        private static async Task<CopilotAgent> SingleAgentRow(string agentId)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var rows = await db.CopilotAgents.AsNoTracking().Where(a => a.AgentID == agentId).ToListAsync();
                Assert.AreEqual(1, rows.Count, $"Expected exactly one copilot_agents row for agent id {agentId}.");
                return rows[0];
            }
        }

        #endregion
    }
}
