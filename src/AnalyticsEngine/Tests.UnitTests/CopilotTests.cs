using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    [TestClass]
    public class CopilotTests
    {
        protected ILogger _logger;
        protected TestsAppConfig _config;

        public CopilotTests()
        {
            _logger = new LoggerFactory().CreateLogger("CopilotTests");
            _config = new TestsAppConfig();
        }

        async Task ClearEvents(AnalyticsEntitiesContext db)
        {

            // Clear events for test
            db.CopilotEventMetadataFiles.RemoveRange(db.CopilotEventMetadataFiles);
            db.CopilotEventMetadataMeetings.RemoveRange(db.CopilotEventMetadataMeetings);
            db.CopilotChats.RemoveRange(db.CopilotChats);

            await db.SaveChangesAsync();
        }

        // Shared flow for saving Copilot events (normal + no permissions adaptor)
        // Returns list of CommonAuditEvent objects for created chat events (for further assertions if needed).
        private async Task<List<CommonAuditEvent>> ExecuteCopilotEventManagerSaveFlow(
            ICopilotMetadataLoader adaptor,
            AnalyticsEntitiesContext db,
            Tuple<string, string> chatAgentIdAndName = null)
        {
            var allCreatedChatCommonEvents = new List<CommonAuditEvent>();
            var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, adaptor, _logger);

            // Copilot events are: CommonAuditEvent + child CopilotAuditLogContent + copilot event data
            var commonEventDocEdit = new CommonAuditEvent
            {
                TimeStamp = DateTime.Now,
                Operation = new EventOperation { Name = "Document Edit" + DateTime.Now.Ticks },
                User = new User { AzureAdId = "test", UserPrincipalName = "test doc user " + DateTime.Now.Ticks },
                Id = Guid.NewGuid()
            };
            var commonEventMeeting = new CommonAuditEvent
            {
                TimeStamp = DateTime.Now,
                Operation = new EventOperation { Name = "Meeting Op" + DateTime.Now.Ticks },
                User = new User { AzureAdId = "test", UserPrincipalName = "test meeting user " + DateTime.Now.Ticks },
                Id = Guid.NewGuid()
            };
            var commonOutlook = new CommonAuditEvent
            {
                TimeStamp = DateTime.Now,
                Operation = new EventOperation { Name = "Outlook Op" + DateTime.Now.Ticks },
                User = new User { AzureAdId = "test", UserPrincipalName = "test outlook user " + DateTime.Now.Ticks },
                Id = Guid.NewGuid()
            };
            var commonEventChat = new CommonAuditEvent
            {
                TimeStamp = DateTime.Now,
                Operation = new EventOperation { Name = "Chat or something" + DateTime.Now.Ticks },
                User = new User { AzureAdId = "test", UserPrincipalName = "test chat user " + DateTime.Now.Ticks },
                Id = Guid.NewGuid()
            };

            // Persist common events for FK usage
            allCreatedChatCommonEvents.Add(commonEventMeeting);
            allCreatedChatCommonEvents.Add(commonEventDocEdit);
            allCreatedChatCommonEvents.Add(commonOutlook);
            allCreatedChatCommonEvents.Add(commonEventChat);

            db.AuditEventsCommon.AddRange(allCreatedChatCommonEvents);
            await db.SaveChangesAsync();

            // Save Copilot events - one for each type we know about
            await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent { CopilotEventData = new CopilotEventData
            {
                // Teams meeting event
                AppHost = "test",
                Contexts = new List<Context>
                {
                    new Context
                    {
                        Id = "https://microsoft.teams.com/threads/19:meeting_NDQ4MGRhYjgtMzc5MS00ZWMxLWJiZjEtOTIxZmM5Mzg3ZGFi@thread.v2", // Needs to be real
                        Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING
                    }
                }
            }, AgentId = chatAgentIdAndName?.Item1, AgentName = chatAgentIdAndName?.Item2 }, commonEventMeeting);

            await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent { CopilotEventData = new CopilotEventData
            {
                // Document event
                AppHost = "Word",
                Contexts = new List<Context>
                {
                    new Context
                    {
                        Id = _config.TestCopilotDocContextIdSpSite,
                        Type = _config.TeamSiteFileExtension
                    }
                }
            }, AgentId = chatAgentIdAndName?.Item1, AgentName = chatAgentIdAndName?.Item2 }, commonEventDocEdit);

            await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent { CopilotEventData = new CopilotEventData
            {
                // Outlook event
                AppHost = "Outlook",
                AccessedResources = new List<AccessedResource>
                {
                    new AccessedResource{ Type = "http://schema.skype.com/HyperLink" }
                },
            }, AgentId = chatAgentIdAndName?.Item1, AgentName = chatAgentIdAndName?.Item2 }, commonOutlook);

            await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent { CopilotEventData = new CopilotEventData
            {
                // Chat event
                AppHost = "Teams",
                Contexts = new List<Context>
                {
                    new Context
                    {
                        Id = "https://microsoft.teams.com/threads/19:somechatthread@thread.v2",
                        Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                    }
                }
            }, AgentId = chatAgentIdAndName?.Item1, AgentName = chatAgentIdAndName?.Item2 }, commonEventChat);

            await copilotEventManager.CommitAllChanges();

            return allCreatedChatCommonEvents;
        }

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
        /// Tests we can load metadata from Graph
        /// </summary>
#if DEBUG
        [TestMethod]
#endif
        public async Task GraphCopilotMetadataLoaderTests()
        {
            var auth = new GraphAppIndentityOAuthContext(_logger, _config.ClientID, _config.TenantGUID.ToString(), _config.ClientSecret, string.Empty, false);
            await auth.InitClientCredential();

            var loader = new GraphFileMetadataLoader(new Microsoft.Graph.GraphServiceClient(auth.Creds), _logger);

            // Test a file from users OneDrive (my site)
            var mySiteFileInfo = await loader.GetSpoFileInfo(_config.TestCopilotDocContextIdMySites, _config.TestCopilotEventUPN);

            Assert.IsNotNull(mySiteFileInfo);
            Assert.AreEqual(mySiteFileInfo?.Extension, _config.MySitesFileExtension);
            Assert.AreEqual(mySiteFileInfo?.Filename, _config.MySitesFileName);
            Assert.AreEqual(mySiteFileInfo?.Url, _config.MySitesFileUrl);

            // Test a file from a team site
            var spSiteFileInfo = await loader.GetSpoFileInfo(_config.TestCopilotDocContextIdSpSite, _config.TestCopilotEventUPN);
            Assert.IsNotNull(spSiteFileInfo);
            Assert.AreEqual(spSiteFileInfo?.Extension, _config.TeamSiteFileExtension);
            Assert.AreEqual(spSiteFileInfo?.Filename, _config.TeamSitesFileName);
            Assert.AreEqual(spSiteFileInfo?.Url, _config.TeamSiteFileUrl);

            // Test a call
            if (!string.IsNullOrEmpty(_config.TestCallThreadId))
            {
                var userId = await loader.GetUserIdFromUpn(_config.TestCopilotEventUPN);
                var meeting = await loader.GetMeetingInfo(StringUtils.GetOnlineMeetingId(_config.TestCallThreadId, userId), userId);
                Assert.IsNotNull(meeting);
            }
        }

        /// <summary>
        /// Tests that AgentName and AgentId are correctly extracted from AppIdentity when they are not directly provided
        /// </summary>
        [TestMethod]
        public void CopilotAuditLogContent_FromJson_ExtractsAgentFromAppIdentity()
        {
            // Arrange
            var organizationId = "873ca9a3-4805-48f2-b419-fabf868641da";
            var expectedAgentName = "contoso_itAssistant";
            var appIdentity = $"Copilot.Studio.Default-{organizationId}-{expectedAgentName}";
            
            var json = $@"{{
                ""OrganizationId"": ""{organizationId}"",
                ""AppIdentity"": ""{appIdentity}"",
                ""CopilotEventData"": {{
                    ""AppHost"": ""Teams"",
                    ""AccessedResources"": [],
                    ""Contexts"": []
                }}
            }}";

            // Act
            var result = CopilotAuditLogContent.FromJson(json);

            // Assert
            Assert.IsNotNull(result, "Result should not be null");
            Assert.AreEqual(expectedAgentName, result.AgentName, "AgentName should be extracted from AppIdentity");
            Assert.AreEqual(appIdentity, result.AgentId, "AgentId should be set to AppIdentity value");
            Assert.AreEqual(appIdentity, result.AppIdentity, "AppIdentity should be preserved");
            Assert.AreEqual(organizationId, result.OrganizationId, "OrganizationId should be preserved");
        }

        /// <summary>
        /// Tests that existing AgentName and AgentId values are not overwritten when they are already present
        /// </summary>
        [TestMethod]
        public void CopilotAuditLogContent_FromJson_PreservesExistingAgentValues()
        {
            // Arrange
            var organizationId = "873ca9a3-4805-48f2-b419-fabf868641da";
            var existingAgentName = "ExistingAgent";
            var existingAgentId = "existing-agent-id-123";
            var appIdentity = $"Copilot.Studio.Default-{organizationId}-contoso_itAssistant";
            
            var json = $@"{{
                ""OrganizationId"": ""{organizationId}"",
                ""AppIdentity"": ""{appIdentity}"",
                ""AgentName"": ""{existingAgentName}"",
                ""AgentId"": ""{existingAgentId}"",
                ""CopilotEventData"": {{
                    ""AppHost"": ""Teams"",
                    ""AccessedResources"": [],
                    ""Contexts"": []
                }}
            }}";

            // Act
            var result = CopilotAuditLogContent.FromJson(json);

            // Assert
            Assert.IsNotNull(result, "Result should not be null");
            Assert.AreEqual(existingAgentName, result.AgentName, "Existing AgentName should be preserved");
            Assert.AreEqual(existingAgentId, result.AgentId, "Existing AgentId should be preserved");
        }

        /// <summary>
        /// Tests that extraction doesn't fail when AppIdentity is missing
        /// </summary>
        [TestMethod]
        public void CopilotAuditLogContent_FromJson_HandlesNullAppIdentity()
        {
            // Arrange
            var json = @"{
                ""OrganizationId"": ""873ca9a3-4805-48f2-b419-fabf868641da"",
                ""CopilotEventData"": {
                    ""AppHost"": ""Teams"",
                    ""AccessedResources"": [],
                    ""Contexts"": []
                }
            }";

            // Act
            var result = CopilotAuditLogContent.FromJson(json);

            // Assert
            Assert.IsNotNull(result, "Result should not be null");
            Assert.IsNull(result.AgentName, "AgentName should remain null");
            Assert.IsNull(result.AgentId, "AgentId should remain null");
        }

        /// <summary>
        /// Tests that extraction doesn't fail when OrganizationId is missing
        /// </summary>
        [TestMethod]
        public void CopilotAuditLogContent_FromJson_HandlesNullOrganizationId()
        {
            // Arrange
            var appIdentity = "Copilot.Studio.Default-873ca9a3-4805-48f2-b419-fabf868641da-contoso_itAssistant";
            
            var json = $@"{{
                ""AppIdentity"": ""{appIdentity}"",
                ""CopilotEventData"": {{
                    ""AppHost"": ""Teams"",
                    ""AccessedResources"": [],
                    ""Contexts"": []
                }}
            }}";

            // Act
            var result = CopilotAuditLogContent.FromJson(json);

            // Assert
            Assert.IsNotNull(result, "Result should not be null");
            Assert.IsNull(result.AgentName, "AgentName should remain null when OrganizationId is missing");
            Assert.IsNull(result.AgentId, "AgentId should remain null when OrganizationId is missing");
        }

        /// <summary>
        /// Tests that extraction handles AppIdentity that doesn't contain the OrganizationId
        /// </summary>
        [TestMethod]
        public void CopilotAuditLogContent_FromJson_HandlesAppIdentityWithoutOrgId()
        {
            // Arrange
            var organizationId = "873ca9a3-4805-48f2-b419-fabf868641da";
            var appIdentity = "SomeOtherFormat-12345-agentName";
            
            var json = $@"{{
                ""OrganizationId"": ""{organizationId}"",
                ""AppIdentity"": ""{appIdentity}"",
                ""CopilotEventData"": {{
                    ""AppHost"": ""Teams"",
                    ""AccessedResources"": [],
                    ""Contexts"": []
                }}
            }}";

            // Act
            var result = CopilotAuditLogContent.FromJson(json);

            // Assert
            Assert.IsNotNull(result, "Result should not be null");
            Assert.IsNull(result.AgentName, "AgentName should remain null when OrganizationId not found in AppIdentity");
            Assert.IsNull(result.AgentId, "AgentId should remain null when OrganizationId not found in AppIdentity");
        }

        /// <summary>
        /// Tests edge case where AppIdentity ends with OrganizationId (no agent name after)
        /// </summary>
        [TestMethod]
        public void CopilotAuditLogContent_FromJson_HandlesAppIdentityEndingWithOrgId()
        {
            // Arrange
            var organizationId = "873ca9a3-4805-48f2-b419-fabf868641da";
            var appIdentity = $"Copilot.Studio.Default-{organizationId}";
            
            var json = $@"{{
                ""OrganizationId"": ""{organizationId}"",
                ""AppIdentity"": ""{appIdentity}"",
                ""CopilotEventData"": {{
                    ""AppHost"": ""Teams"",
                    ""AccessedResources"": [],
                    ""Contexts"": []
                }}
            }}";

            // Act
            var result = CopilotAuditLogContent.FromJson(json);

            // Assert
            Assert.IsNotNull(result, "Result should not be null");
            Assert.IsNull(result.AgentName, "AgentName should remain null when no content after OrganizationId");
            Assert.IsNull(result.AgentId, "AgentId should remain null when no content after OrganizationId");
        }

        /// <summary>
        /// Tests various agent name formats including special characters
        /// </summary>
        [TestMethod]
        public void CopilotAuditLogContent_FromJson_HandlesVariousAgentNameFormats()
        {
            // Arrange
            var organizationId = "873ca9a3-4805-48f2-b419-fabf868641da";
            var testCases = new[]
            {
                "contoso_itAssistant",
                "agent-with-dashes",
                "AgentWithCamelCase",
                "agent.with.dots",
                "123numericAgent",
                "agent_with_multiple_underscores"
            };

            foreach (var expectedAgentName in testCases)
            {
                var appIdentity = $"Copilot.Studio.Default-{organizationId}-{expectedAgentName}";
                var json = $@"{{
                    ""OrganizationId"": ""{organizationId}"",
                    ""AppIdentity"": ""{appIdentity}"",
                    ""CopilotEventData"": {{
                        ""AppHost"": ""Teams"",
                        ""AccessedResources"": [],
                        ""Contexts"": []
                    }}
                }}";

                // Act
                var result = CopilotAuditLogContent.FromJson(json);

                // Assert
                Assert.IsNotNull(result, $"Result should not be null for agent name: {expectedAgentName}");
                Assert.AreEqual(expectedAgentName, result.AgentName, $"AgentName should be correctly extracted for: {expectedAgentName}");
                Assert.AreEqual(appIdentity, result.AgentId, $"AgentId should be set to AppIdentity for: {expectedAgentName}");
            }
        }

    }
}
