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

                // First save with initial agent name
                var agentId = "Unit testing3 " + DateTime.Now.Ticks;
                var agentName = "Test Agent Chat " + DateTime.Now.Ticks;
                var firstChatEvents = await ExecuteCopilotEventManagerSaveFlow(new FakeCopilotMetadataLoader(), _db, Tuple.Create(agentId, agentName));

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
                var newAgentName = "Test Agent New Name " + DateTime.Now.Ticks;
                var secondChatEvents = await ExecuteCopilotEventManagerSaveFlow(new FakeCopilotMetadataLoader(), _db, Tuple.Create(agentId, newAgentName));

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

                // Optionally also assert that previously created events now reflect updated agent name (if agent entity updated globally)
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
