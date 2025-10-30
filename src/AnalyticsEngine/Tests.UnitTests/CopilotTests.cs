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
        // chatAgents: list of (AgentId, AgentName) pairs to create chat events for. If null, a single chat event without agent info is created.
        // includeNonChatEvents: if true, meeting/file/outlook events are also generated.
        // connectionStringOverride: allows using a different connection string for CopilotAuditEventManager.
        // Returns list of CommonAuditEvent objects for created chat events (for further assertions if needed).
        private async Task<List<CommonAuditEvent>> ExecuteCopilotEventManagerSaveFlow(
            ICopilotMetadataLoader adaptor,
            AnalyticsEntitiesContext db,
            IList<Tuple<string, string>> chatAgents = null,
            bool includeNonChatEvents = true,
            string connectionStringOverride = null)
        {

            var copilotEventManager = new CopilotAuditEventManager(connectionStringOverride ?? _config.ConnectionStrings.DatabaseConnectionString, adaptor, _logger);

            var createdChatCommonEvents = new List<CommonAuditEvent>();

            // Non-chat events
            CommonAuditEvent commonEventDocEdit = null;
            CommonAuditEvent commonEventMeeting = null;
            CommonAuditEvent commonOutlook = null;

            CopilotEventData meeting = null;
            CopilotEventData docEvent = null;
            CopilotEventData outlook = null;

            if (includeNonChatEvents)
            {
                commonEventDocEdit = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Document Edit" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test doc user " + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };
                commonEventMeeting = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Meeting Op" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test meeting user " + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };
                commonOutlook = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Outlook Op" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test outlook user " + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                meeting = new CopilotEventData
                {
                    AppHost = "test",
                    Contexts = new List<Context>
                    {
                        new Context
                        {
                            Id = "https://microsoft.teams.com/threads/19:meeting_NDQ4MGRhYjgtMzc5MS00ZWMxLWJiZjEtOTIxZmM5Mzg3ZGFi@thread.v2", // Needs to be real
                            Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING
                        }
                    }
                };
                docEvent = new CopilotEventData
                {
                    AppHost = "Word",
                    Contexts = new List<Context>
                    {
                        new Context
                        {
                            Id = _config.TestCopilotDocContextIdSpSite,
                            Type = _config.TeamSiteFileExtension
                        }
                    }
                };
                outlook = new CopilotEventData
                {
                    AppHost = "Outlook",
                    AccessedResources = new List<AccessedResource>
                    {
                        new AccessedResource{ Type = "http://schema.skype.com/HyperLink" }
                    },
                };
            }

            // Chat events (can be multiple with agent settings)
            var teamsChatTemplate = new CopilotEventData
            {
                AppHost = "Teams",
                Contexts = new List<Context>
                {
                    new Context
                    {
                        Id = "https://microsoft.teams.com/threads/19:somechatthread@thread.v2",
                        Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT
                    }
                }
            };

            if (chatAgents == null)
            {
                var commonEventChat = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Chat or something" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test chat user " + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };
                createdChatCommonEvents.Add(commonEventChat);
                db.AuditEventsCommon.Add(commonEventChat);
            }
            else
            {
                foreach (var agent in chatAgents)
                {
                    var commonEventChat = new CommonAuditEvent
                    {
                        TimeStamp = DateTime.Now,
                        Operation = new EventOperation { Name = "Chat op " + DateTime.Now.Ticks },
                        User = new User { AzureAdId = "test", UserPrincipalName = "test chat user " + DateTime.Now.Ticks },
                        Id = Guid.NewGuid()
                    };
                    createdChatCommonEvents.Add(commonEventChat);
                    db.AuditEventsCommon.Add(commonEventChat);
                }
            }

            // Persist common events for FK usage
            if (includeNonChatEvents)
            {
                db.AuditEventsCommon.Add(commonEventDocEdit);
                db.AuditEventsCommon.Add(commonEventMeeting);
                db.AuditEventsCommon.Add(commonOutlook);
            }
            await db.SaveChangesAsync();

            // Save Copilot events
            if (includeNonChatEvents)
            {
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent { CopilotEventData = meeting }, commonEventMeeting);
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent { CopilotEventData = docEvent }, commonEventDocEdit);
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent { CopilotEventData = outlook }, commonOutlook);
            }

            if (chatAgents == null)
            {
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent { CopilotEventData = teamsChatTemplate }, createdChatCommonEvents[0]);
            }
            else
            {
                for (int i =0; i < chatAgents.Count; i++)
                {
                    var agentTuple = chatAgents[i];
                    await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                    {
                        CopilotEventData = teamsChatTemplate,
                        AgentId = agentTuple.Item1,
                        AgentName = agentTuple.Item2
                    }, createdChatCommonEvents[i]);
                }
            }

            await copilotEventManager.CommitAllChanges();
            return createdChatCommonEvents;
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
                Assert.IsTrue(fileEventsPostCount == fileEventsPreCount +1);
                Assert.IsTrue(meetingEventsPostCount == meetingEventsPreCount +1);
                Assert.IsTrue(allCopilotEventsPostCount == allCopilotEventsPreCount +4); //4 new events -1 meeting,1 file,1 chat,1 outlook
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
                Assert.IsTrue(allCopilotEventsPostCount == allCopilotEventsPreCount +4); //4 new events -1 meeting,1 file,1 chat,1 outlook
            }
        }


        [TestMethod]
        public async Task CopilotEventManagerAgentNameUpdateSaveTest()
        {
            using (var _db = new AnalyticsEntitiesContext(_config.ConnectionStrings.SQL, true, false))
            {
                await ClearEvents(_db);

                // First save with initial agent name
                var agentId = "Unit testing3 " + DateTime.Now.Ticks;
                var agentName = "Test Agent Chat " + DateTime.Now.Ticks;
                var firstChatEvents = await ExecuteCopilotEventManagerSaveFlow(new FakeCopilotMetadataLoader(), _db,
                    new List<Tuple<string, string>> { Tuple.Create(agentId, agentName) }, includeNonChatEvents: false, connectionStringOverride: _config.ConnectionStrings.SQL);

                // Capture id outside the LINQ expression to avoid translation issues
                var firstChatEventId = firstChatEvents[0].Id;

                // Verify first chat saved with initial agent name
                var firstChatReloaded = await _db.CopilotChats
                    .Include(x => x.Agent)
                    .FirstOrDefaultAsync(x => x.AuditEvent.Id == firstChatEventId);
                Assert.IsNotNull(firstChatReloaded);
                Assert.IsTrue(firstChatReloaded.Agent.AgentID == agentId);
                Assert.IsTrue(firstChatReloaded.Agent.Name == agentName);

                // Second save with updated agent name (same agent ID)
                var newAgentName = "Test Agent New Name " + DateTime.Now.Ticks;
                var secondChatEvents = await ExecuteCopilotEventManagerSaveFlow(new FakeCopilotMetadataLoader(), _db,
                    new List<Tuple<string, string>> { Tuple.Create(agentId, newAgentName) }, includeNonChatEvents: false, connectionStringOverride: _config.ConnectionStrings.SQL);

                var secondChatEventId = secondChatEvents[0].Id;

                // Verify second chat saved and agent name updated
                var secondChatReloaded = await _db.CopilotChats.Include(x => x.Agent)
                    .FirstOrDefaultAsync(x => x.AuditEvent.Id == secondChatEventId);
                if (secondChatReloaded?.Agent != null)
                {
                    await _db.Entry(secondChatReloaded.Agent).ReloadAsync();
                }
                Assert.IsNotNull(secondChatReloaded);
                Assert.IsTrue(secondChatReloaded.Agent.AgentID == agentId);
                Assert.IsTrue(secondChatReloaded.Agent.Name == newAgentName);
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

    }
}
