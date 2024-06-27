using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
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

        // https://learn.microsoft.com/en-us/office/office-365-management-api/copilot-schema
        [TestMethod]
        public async Task CopilotEventManagerSaveTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Clear events for test
                db.CopilotEventMetadataFiles.RemoveRange(db.CopilotEventMetadataFiles);
                db.CopilotEventMetadataMeetings.RemoveRange(db.CopilotEventMetadataMeetings);
                await db.SaveChangesAsync();

                var copilotEventAdaptor = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotEventAdaptor(), _logger);

                var commonEventDocEdit = new Office365Event
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Document Edit" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test doc user " + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };
                var commonEventChat = new Office365Event
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Chat or something" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test chat user " + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };
                var commonEventMeeting = new Office365Event
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Meeting Op" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test meeting user " + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };
                var commonOutlook = new Office365Event
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Outlook Op" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test outlook user " + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                // Audit metadata for our tests
                var meeting = new CopilotEventData
                {
                    AppHost = "test",
                    Contexts = new List<Context>
                    {
                        new Context
                        {
                            Id = "https://microsoft.teams.com/threads/19:meeting_NDQ4MGRhYjgtMzc5MS00ZWMxLWJiZjEtOTIxZmM5Mzg3ZGFi@thread.v2",   // Needs to be real
                            Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING
                        }
                    }
                };
                var docEvent = new CopilotEventData
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
                var teamsChat = new CopilotEventData
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

                var outlook = new CopilotEventData
                {
                    AppHost = "Outlook",
                    AccessedResources = new List<AccessedResource>
                    {
                        new AccessedResource{ Type = "http://schema.skype.com/HyperLink" }
                    },
                };


                // Check counts before and after
                var fileEventsPreCount = await db.CopilotEventMetadataFiles.CountAsync();
                var meetingEventsPreCount = await db.CopilotEventMetadataMeetings.CountAsync();
                var allCopilotEventsPreCount = await db.CopilotChats.CountAsync();

                // Save common events as they are required for the foreign key - the common event is saved before CopilotAuditEventManager runs on the metadata
                db.AuditEventsCommon.Add(commonEventDocEdit);
                db.AuditEventsCommon.Add(commonEventMeeting);
                db.AuditEventsCommon.Add(commonEventChat);
                db.AuditEventsCommon.Add(commonOutlook);
                await db.SaveChangesAsync();

                // Save events
                await copilotEventAdaptor.SaveSingleCopilotEventToSql(meeting, commonEventMeeting);
                await copilotEventAdaptor.SaveSingleCopilotEventToSql(docEvent, commonEventDocEdit);
                await copilotEventAdaptor.SaveSingleCopilotEventToSql(teamsChat, commonEventChat);
                await copilotEventAdaptor.SaveSingleCopilotEventToSql(outlook, commonOutlook);
                await copilotEventAdaptor.CommitAllChanges();

                // Verify counts have increased
                var fileEventsPostCount = await db.CopilotEventMetadataFiles.CountAsync();
                var meetingEventsPostCount = await db.CopilotEventMetadataMeetings.CountAsync();
                var allCopilotEventsPostCount = await db.CopilotChats.CountAsync();

                Assert.IsTrue(fileEventsPostCount == fileEventsPreCount + 1);
                Assert.IsTrue(meetingEventsPostCount == meetingEventsPreCount + 1);
                Assert.IsTrue(allCopilotEventsPostCount == allCopilotEventsPreCount + 4); // 4 new events - 1 meeting, 1 file, 1 chat, 1 outlook
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
            Assert.AreEqual(mySiteFileInfo?.Extension, _config.MySitesFileExtension);
            Assert.AreEqual(mySiteFileInfo?.Filename, _config.MySitesFileName);
            Assert.AreEqual(mySiteFileInfo?.Url, _config.MySitesFileUrl);

            // Test a file from a team site
            var spSiteFileInfo = await loader.GetSpoFileInfo(_config.TestCopilotDocContextIdSpSite, _config.TestCopilotEventUPN);
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

        [TestMethod]
        public void GetMeetingIdFragmentFromMeetingThreadUrl()
        {
            Assert.AreEqual("19:meeting_NDQ4MGRhYjgtMzc5MS00ZWMxLWJiZjEtOTIxZmM5Mzg3ZGFi@thread.v2",
                StringUtils.GetMeetingIdFragmentFromMeetingThreadUrl("https://microsoft.teams.com/threads/19:meeting_NDQ4MGRhYjgtMzc5MS00ZWMxLWJiZjEtOTIxZmM5Mzg3ZGFi@thread.v2"));
            Assert.IsNull(StringUtils.GetMeetingIdFragmentFromMeetingThreadUrl("https://microsoft.teams.com/"));
        }

        [TestMethod]
        public void GetSiteUrl()
        {
            // My Site
            Assert.AreEqual("https://test.sharepoint.com/sites/test",
                StringUtils.GetSiteUrl("https://test.sharepoint.com/sites/test/Shared%20Documents/General/test.docx"));

            Assert.AreEqual("https://test.sharepoint.com/sites/test",
                StringUtils.GetSiteUrl("https://test.sharepoint.com/sites/test"));

            // If we're not passing a doc in the root-site, we should get the root site back
            Assert.IsNull(StringUtils.GetSiteUrl("https://test.sharepoint.com"));
            Assert.IsNull(StringUtils.GetSiteUrl("https://test.sharepoint.com/"));

            Assert.AreEqual("https://m365cp123890-my.sharepoint.com/personal/sambetts_m365cp123890_onmicrosoft_com",
                StringUtils.GetSiteUrl(
                "https://m365cp123890-my.sharepoint.com/personal/sambetts_m365cp123890_onmicrosoft_com/_layouts/15/Doc.aspx?sourcedoc=%7B0D86F64F-8435-430C-8979-FF46C00F7ACB%7D&file=Presentation.pptx&action=edit&mobileredirect=true")
                );

            // Root site doc
            Assert.AreEqual("https://m365cp123890.sharepoint.com",
                StringUtils.GetSiteUrl(
                "https://m365cp123890.sharepoint.com/_layouts/15/Doc.aspx?sourcedoc=%7B0D86F64F-8435-430C-8979-FF46C00F7ACB%7D&file=Presentation.pptx&action=edit&mobileredirect=true")
                );
            Assert.AreEqual("https://m365cp123890.sharepoint.com",
                StringUtils.GetSiteUrl("https://m365cp123890.sharepoint.com/Doc.docx"));
        }

        [TestMethod]
        public void GetHostAndSiteRelativeUrl()
        {
            var subSiteResult = StringUtils.GetHostAndSiteRelativeUrl("https://test.sharepoint.com/sites/test");
            Assert.AreEqual("test.sharepoint.com:/sites/test", subSiteResult);

            var rootSiteResult = StringUtils.GetHostAndSiteRelativeUrl("https://test.sharepoint.com/");
            Assert.AreEqual("root", rootSiteResult);

            Assert.IsNull(StringUtils.GetHostAndSiteRelativeUrl("https://test.com/"));
        }

        [TestMethod]
        public void GetDriveItemId()
        {
            Assert.IsNull(StringUtils.GetDriveItemId("https://test.sharepoint.com/sites/test"));
            Assert.AreEqual(StringUtils.GetDriveItemId(
                "https://m365cp123890-my.sharepoint.com/personal/sambetts_m365cp123890_onmicrosoft_com/_layouts/15/Doc.aspx?sourcedoc=%7B0D86F64F-8435-430C-8979-FF46C00F7ACB%7D&file=Presentation.pptx&action=edit&mobileredirect=true"),
                "0D86F64F-8435-430C-8979-FF46C00F7ACB");
        }
    }
}
