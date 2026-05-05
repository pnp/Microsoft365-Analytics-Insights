using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for core save flows: events, agents, custom agent flags, and credit estimation persistence.
    /// </summary>
    [TestClass]
    public class CopilotEventManagerSaveTests : CopilotTestBase
    {
        [TestMethod]
        public async Task CopilotEventManagerSaveTest()
        {

            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                // Counts before
                var fileEventsPreCount = await db.CopilotEventMetadataFiles.CountAsync();
                var meetingEventsPreCount = await db.CopilotEventMetadataMeetings.CountAsync();
                var allCopilotEventsPreCount = await db.CopilotChats.CountAsync();

                var _ = await ExecuteCopilotEventManagerSaveFlow(new FakeCopilotMetadataLoader(), db);


                // Counts after
                var fileEventsPostCount = await db.CopilotEventMetadataFiles.CountAsync();
                var meetingEventsPostCount = await db.CopilotEventMetadataMeetings.CountAsync();
                var allCopilotEventsPostCount = await db.CopilotChats.CountAsync();

                // Assertions
                Assert.IsTrue(fileEventsPostCount == fileEventsPreCount + 1);
                Assert.IsTrue(meetingEventsPostCount == meetingEventsPreCount + 1);
                Assert.IsTrue(allCopilotEventsPostCount == allCopilotEventsPreCount + 4); //4 new events -1 meeting,1 file,1 chat,1 outlook
            }
        }

        /// <summary>
        /// When there's no permissions to read files/meetings, we should still save the chat at least
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerWithNoPermissionsSaveTest()
        {

            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                // Counts before
                var fileEventsPreCount = await db.CopilotEventMetadataFiles.CountAsync();
                var meetingEventsPreCount = await db.CopilotEventMetadataMeetings.CountAsync();
                var allCopilotEventsPreCount = await db.CopilotChats.CountAsync();

                var _ = await ExecuteCopilotEventManagerSaveFlow(new ReturnNullFilesAndMeetingsAdaptor(), db);

                // Counts after
                var fileEventsPostCount = await db.CopilotEventMetadataFiles.CountAsync();
                var meetingEventsPostCount = await db.CopilotEventMetadataMeetings.CountAsync();
                var allCopilotEventsPostCount = await db.CopilotChats.CountAsync();

                // Assertions
                Assert.IsTrue(fileEventsPostCount == fileEventsPreCount); // No file data so no new file event
                Assert.IsTrue(meetingEventsPostCount == meetingEventsPreCount); // No meeting data so no new meeting event
                Assert.IsTrue(allCopilotEventsPostCount == allCopilotEventsPreCount + 4); //4 new events -1 meeting,1 file,1 chat,1 outlook
            }
        }


        [TestMethod]
        public async Task CopilotEventManagerAgentNameUpdateSaveTest()
        {
            using (var _db = new AnalyticsEntitiesContext(_config.ConnectionStrings.SQL, true, false))
            {
                await ClearEvents(_db);

                var adaptors = new ICopilotMetadataLoader[] { new FakeCopilotMetadataLoader(), new ReturnNullFilesAndMeetingsAdaptor() };

                foreach (var adaptor in adaptors)
                {
                    // First save with initial agent name
                    var agentId = "Unit testing3 " + adaptor.GetType().Name + " " + DateTime.Now.Ticks;
                    var agentName = "Test Agent Chat " + adaptor.GetType().Name + " " + DateTime.Now.Ticks;
                    var firstChatEvents = await ExecuteCopilotEventManagerSaveFlow(adaptor, _db, Tuple.Create(agentId, agentName));

                    // Verify ALL first events saved with initial agent name
                    foreach (var evt in firstChatEvents)
                    {
                        var id = evt.Id;
                        var reloaded = await _db.CopilotChats.Include(x => x.Agent).FirstOrDefaultAsync(x => x.AuditEvent.Id == id);
                        Assert.IsNotNull(reloaded, $"CopilotChat not found for initial event {id}");
                        Assert.IsNotNull(reloaded.Agent, $"Agent navigation null for initial event {id}");
                        Assert.AreEqual(agentId, reloaded.Agent.AgentID, $"AgentID mismatch for initial event {id}");
                        Assert.AreEqual(agentName, reloaded.Agent.Name, $"Agent Name mismatch for initial event {id}");
                    }

                    // Second save with updated agent name (same agent ID)
                    var newAgentName = "Test Agent New Name " + adaptor.GetType().Name + " " + DateTime.Now.Ticks;
                    var secondChatEvents = await ExecuteCopilotEventManagerSaveFlow(adaptor, _db, Tuple.Create(agentId, newAgentName));

                    // Verify ALL second events saved and agent name updated
                    foreach (var evt in secondChatEvents)
                    {
                        var id = evt.Id;
                        var reloaded = await _db.CopilotChats.Include(x => x.Agent).FirstOrDefaultAsync(x => x.AuditEvent.Id == id);
                        if (reloaded?.Agent != null)
                        {
                            // Ensure we have fresh agent data after update
                            await _db.Entry(reloaded.Agent).ReloadAsync();
                        }
                        Assert.IsNotNull(reloaded, $"CopilotChat not found for second event {id}");
                        Assert.IsNotNull(reloaded.Agent, $"Agent navigation null for second event {id}");
                        Assert.AreEqual(agentId, reloaded.Agent.AgentID, $"AgentID mismatch for second event {id}");
                        Assert.AreEqual(newAgentName, reloaded.Agent.Name, $"Updated Agent Name mismatch for second event {id}");
                    }

                    // Assert previously created events now reflect updated agent name
                    var previouslyCreatedIds = firstChatEvents.Select(e => e.Id).ToList();
                    var previouslyCreatedChats = await _db.CopilotChats.Include(x => x.Agent)
                        .Where(x => previouslyCreatedIds.Contains(x.AuditEvent.Id)).ToListAsync();
                    foreach (var chat in previouslyCreatedChats)
                    {
                        if (chat.Agent != null)
                        {
                            await _db.Entry(chat.Agent).ReloadAsync();
                            Assert.AreEqual(newAgentName, chat.Agent.Name, "Existing event did not reflect updated agent name");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Tests that when TargetAgentName changes for the same AgentId, the DB is updated with the new name
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerTargetAgentNameUpdateSaveTest()
        {
            using (var _db = new AnalyticsEntitiesContext(_config.ConnectionStrings.SQL, true, false))
            {
                await ClearEvents(_db);

                var adaptor = new FakeCopilotMetadataLoader();

                // First save with initial TargetAgentName resolved through FromJson
                var agentId = "TargetAgentTest_" + DateTime.Now.Ticks;
                var initialTargetName = "InitialCustomEngine_" + DateTime.Now.Ticks;
                var firstChatEvents = await ExecuteCopilotEventManagerSaveFlow(adaptor, _db, Tuple.Create(agentId, initialTargetName));

                // Verify first events saved with initial agent name
                foreach (var evt in firstChatEvents)
                {
                    var id = evt.Id;
                    var reloaded = await _db.CopilotChats.Include(x => x.Agent).FirstOrDefaultAsync(x => x.AuditEvent.Id == id);
                    Assert.IsNotNull(reloaded, $"CopilotChat not found for initial event {id}");
                    Assert.IsNotNull(reloaded.Agent, $"Agent navigation null for initial event {id}");
                    Assert.AreEqual(agentId, reloaded.Agent.AgentID, $"AgentID mismatch for initial event {id}");
                    Assert.AreEqual(initialTargetName, reloaded.Agent.Name, $"Agent Name mismatch for initial event {id}");
                }

                // Second save with updated TargetAgentName (same AgentId) - simulates custom engine agent rename
                var updatedTargetName = "UpdatedCustomEngine_" + DateTime.Now.Ticks;
                var secondChatEvents = await ExecuteCopilotEventManagerSaveFlow(adaptor, _db, Tuple.Create(agentId, updatedTargetName));

                // Verify ALL second events saved and agent name updated
                foreach (var evt in secondChatEvents)
                {
                    var id = evt.Id;
                    var reloaded = await _db.CopilotChats.Include(x => x.Agent).FirstOrDefaultAsync(x => x.AuditEvent.Id == id);
                    if (reloaded?.Agent != null)
                    {
                        await _db.Entry(reloaded.Agent).ReloadAsync();
                    }
                    Assert.IsNotNull(reloaded, $"CopilotChat not found for second event {id}");
                    Assert.IsNotNull(reloaded.Agent, $"Agent navigation null for second event {id}");
                    Assert.AreEqual(agentId, reloaded.Agent.AgentID, $"AgentID mismatch for second event {id}");
                    Assert.AreEqual(updatedTargetName, reloaded.Agent.Name, $"Updated Agent Name mismatch for second event {id}");
                }

                // Assert previously created events now reflect updated agent name
                var previouslyCreatedIds = firstChatEvents.Select(e => e.Id).ToList();
                var previouslyCreatedChats = await _db.CopilotChats.Include(x => x.Agent)
                    .Where(x => previouslyCreatedIds.Contains(x.AuditEvent.Id)).ToListAsync();
                foreach (var chat in previouslyCreatedChats)
                {
                    if (chat.Agent != null)
                    {
                        await _db.Entry(chat.Agent).ReloadAsync();
                        Assert.AreEqual(updatedTargetName, chat.Agent.Name, "Existing event did not reflect updated agent name from TargetAgentName");
                    }
                }
            }
        }

        /// <summary>
        /// Tests that IsCustomAgent flag is correctly saved and persisted in the database
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerCustomAgentFlagSaveTest()
        {
            using (var db = new AnalyticsEntitiesContext(_config.ConnectionStrings.SQL, true, false))
            {
                await ClearEvents(db);

                var adaptor = new FakeCopilotMetadataLoader();
                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, adaptor, _logger);

                // Test with custom agent (IsCustomAgent = true)
                var customAgentId = "CustomAgent_" + DateTime.Now.Ticks;
                var customAgentName = "Custom Agent " + DateTime.Now.Ticks;
                var customAgentEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Custom Agent Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@customagent.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(customAgentEvent);
                await db.SaveChangesAsync();

                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:customchat@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            }
                        }
                    },
                    AgentId = customAgentId,
                    AgentName = customAgentName,
                    IsCustomAgent = true
                }, customAgentEvent);

                await copilotEventManager.CommitAllChanges();

                // Verify custom agent saved with IsCustomAgent = true
                var customChat = await db.CopilotChats.Include(x => x.Agent).FirstOrDefaultAsync(x => x.AuditEvent.Id == customAgentEvent.Id);
                Assert.IsNotNull(customChat, "Custom agent chat should be saved");
                Assert.IsNotNull(customChat.Agent, "Custom agent should exist");
                Assert.AreEqual(customAgentId, customChat.Agent.AgentID, "Custom agent ID should match");
                Assert.AreEqual(customAgentName, customChat.Agent.Name, "Custom agent name should match");
                Assert.IsTrue(customChat.Agent.IsCustomAgent.HasValue && customChat.Agent.IsCustomAgent.Value, "IsCustomAgent should be true for custom agent");

                // Test with standard agent (IsCustomAgent = null or false)
                var standardAgentId = "StandardAgent_" + DateTime.Now.Ticks;
                var standardAgentName = "Standard Agent " + DateTime.Now.Ticks;
                var standardAgentEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Standard Agent Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@standardagent.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(standardAgentEvent);
                await db.SaveChangesAsync();

                copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, adaptor, _logger);
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:standardchat@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            }
                        }
                    },
                    AgentId = standardAgentId,
                    AgentName = standardAgentName,
                    IsCustomAgent = null // or false
                }, standardAgentEvent);

                await copilotEventManager.CommitAllChanges();

                // Verify standard agent saved with IsCustomAgent = null
                var standardChat = await db.CopilotChats.Include(x => x.Agent).FirstOrDefaultAsync(x => x.AuditEvent.Id == standardAgentEvent.Id);
                Assert.IsNotNull(standardChat, "Standard agent chat should be saved");
                Assert.IsNotNull(standardChat.Agent, "Standard agent should exist");
                Assert.AreEqual(standardAgentId, standardChat.Agent.AgentID, "Standard agent ID should match");
                Assert.AreEqual(standardAgentName, standardChat.Agent.Name, "Standard agent name should match");
                Assert.IsFalse(standardChat.Agent.IsCustomAgent.HasValue && standardChat.Agent.IsCustomAgent.Value, "IsCustomAgent should be false/null for standard agent");
            }
        }

        #region Copilot Credit Estimation Save Tests

        [TestMethod]
        public async Task CopilotEventManagerCopilotCreditEstimationSaveTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test Copilot Credit Estimation Op" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@creditest.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                var creditEstimation = new CopilotCreditEstimation
                {
                    GenerativeAnswers = 2,
                    TenantGraphGroundedAnswers = 2,
                    DeepReasoningActions = 1,
                    TotalCredits = 29,
                    CreditBreakdown = new Dictionary<string, int>
                    {
                        { "Generative Answers", 4 },
                        { "Tenant Graph Grounding", 20 },
                        { "Agent Actions (Deep Reasoning)", 5 }
                    },
                    ModelsUsed = new List<string> { "DEEP_LEO" }
                };

                var auditLogContent = new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Word",
                        AccessedResources = new List<AccessedResource>
                        {
                            new AccessedResource { Type = "File", Name = "Document.docx" }
                        }
                    },
                    Cost = creditEstimation
                };

                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(auditLogContent, commonEvent);
                await copilotEventManager.CommitAllChanges();

                var savedEvent = await db.CopilotChats
                    .Where(e => e.AuditEvent.Id == commonEvent.Id)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(savedEvent, "CopilotChat event should be saved");
                Assert.AreEqual(29, savedEvent.CopilotCreditEstimateTotal, "Copilot Credit estimate total should match");
                Assert.IsNotNull(savedEvent.CopilotCreditEstimateJson, "Copilot Credit estimate JSON should not be null");

                var deserializedCreditEstimate = JsonConvert.DeserializeObject<CopilotCreditEstimation>(savedEvent.CopilotCreditEstimateJson);
                Assert.IsNotNull(deserializedCreditEstimate, "Copilot Credit estimate JSON should deserialize correctly");
                Assert.AreEqual(2, deserializedCreditEstimate.GenerativeAnswers);
                Assert.AreEqual(2, deserializedCreditEstimate.TenantGraphGroundedAnswers);
                Assert.AreEqual(1, deserializedCreditEstimate.DeepReasoningActions);
                Assert.AreEqual(29, deserializedCreditEstimate.TotalCredits);
            }
        }

        [TestMethod]
        public async Task CopilotEventManagerNullCopilotCreditEstimationTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Null Copilot Credit Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@nullcredit.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData { AppHost = "Teams" },
                    Cost = null
                }, commonEvent);

                await copilotEventManager.CommitAllChanges();

                var savedEvent = await db.CopilotChats
                    .Where(e => e.AuditEvent.Id == commonEvent.Id)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(savedEvent, "CopilotChat event should be saved");
                Assert.IsNull(savedEvent.CopilotCreditEstimateTotal, "Copilot Credit estimate total should be null");
                Assert.IsNull(savedEvent.CopilotCreditEstimateJson, "Copilot Credit estimate JSON should be null");
            }
        }

        [TestMethod]
        public async Task CopilotEventManagerCopilotCreditEstimationMultipleEventTypesTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                var adaptor = new FakeCopilotMetadataLoader();

                var fileEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "File Op" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@file.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };
                var meetingEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Meeting Op" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@meeting.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };
                var chatEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Chat Op" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@chat.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.AddRange(new[] { fileEvent, meetingEvent, chatEvent });
                await db.SaveChangesAsync();

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, adaptor, _logger);

                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Word",
                        Contexts = new List<Context>
                        {
                            new Context { Id = _config.TestCopilotDocContextIdSpSite, Type = _config.TeamSiteFileExtension }
                        }
                    },
                    Cost = new CopilotCreditEstimation { TotalCredits = 12, GenerativeAnswers = 1, TenantGraphGroundedAnswers = 1 }
                }, fileEvent);

                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:meeting_test@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING
                            }
                        }
                    },
                    Cost = new CopilotCreditEstimation { TotalCredits = 2, GenerativeAnswers = 1 }
                }, meetingEvent);

                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        Contexts = new List<Context>
                        {
                            new Context
                            {
                                Id = "https://microsoft.teams.com/threads/19:chat_test@thread.v2",
                                Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                            }
                        }
                    },
                    Cost = new CopilotCreditEstimation
                    {
                        TotalCredits = 17,
                        GenerativeAnswers = 1,
                        TenantGraphGroundedAnswers = 1,
                        DeepReasoningActions = 1
                    }
                }, chatEvent);

                await copilotEventManager.CommitAllChanges();

                var savedFileEvent = await db.CopilotChats.Where(e => e.AuditEvent.Id == fileEvent.Id).FirstOrDefaultAsync();
                var savedMeetingEvent = await db.CopilotChats.Where(e => e.AuditEvent.Id == meetingEvent.Id).FirstOrDefaultAsync();
                var savedChatEvent = await db.CopilotChats.Where(e => e.AuditEvent.Id == chatEvent.Id).FirstOrDefaultAsync();

                Assert.AreEqual(12, savedFileEvent?.CopilotCreditEstimateTotal, "File event Copilot Credits should be 12");
                Assert.AreEqual(2, savedMeetingEvent?.CopilotCreditEstimateTotal, "Meeting event Copilot Credits should be 2");
                Assert.AreEqual(17, savedChatEvent?.CopilotCreditEstimateTotal, "Chat event Copilot Credits should be 17");
            }
        }

        #endregion

        /// <summary>
        /// Tests we can load metadata from Graph
        /// </summary>
        public async Task GraphCopilotMetadataLoaderTests()
        {
            var auth = new GraphAppIndentityOAuthContext(_logger, _config.ClientID, _config.TenantGUID.ToString(), _config.ClientSecret, string.Empty, false);
            await auth.InitClientCredential();

            var loader = new GraphFileMetadataLoader(new Microsoft.Graph.GraphServiceClient(auth.Creds), _logger);

            var mySiteFileInfo = await loader.GetSpoFileInfo(_config.TestCopilotDocContextIdMySites, _config.TestCopilotEventUPN);

            Assert.IsNotNull(mySiteFileInfo);
            Assert.AreEqual(mySiteFileInfo?.Extension, _config.MySitesFileExtension);
            Assert.AreEqual(mySiteFileInfo?.Filename, _config.MySitesFileName);
            Assert.AreEqual(mySiteFileInfo?.Url, _config.MySitesFileUrl);

            var spSiteFileInfo = await loader.GetSpoFileInfo(_config.TestCopilotDocContextIdSpSite, _config.TestCopilotEventUPN);
            Assert.IsNotNull(spSiteFileInfo);
            Assert.AreEqual(spSiteFileInfo?.Extension, _config.TeamSiteFileExtension);
            Assert.AreEqual(spSiteFileInfo?.Filename, _config.TeamSitesFileName);
            Assert.AreEqual(spSiteFileInfo?.Url, _config.TeamSiteFileUrl);

            if (!string.IsNullOrEmpty(_config.TestCallThreadId))
            {
                var userId = await loader.GetUserIdFromUpn(_config.TestCopilotEventUPN);
                var meeting = await loader.GetMeetingInfo(StringUtils.GetOnlineMeetingId(_config.TestCallThreadId, userId), userId);
                Assert.IsNotNull(meeting);
            }
        }
    }
}
