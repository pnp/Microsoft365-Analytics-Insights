using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using DataUtils;
using Microsoft.Extensions.Logging;
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

        async Task ClearAccessedResources(AnalyticsEntitiesContext db)
        {
            // Clear AccessedResources data for tests
            if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_event_accessed_resources', 'U')").FirstOrDefault().GetValueOrDefault() != 0)
            {
                db.CopilotEventAccessedResources.RemoveRange(db.CopilotEventAccessedResources);
                db.CopilotAccessedResourceIds.RemoveRange(db.CopilotAccessedResourceIds);
                db.CopilotAccessedResourceNames.RemoveRange(db.CopilotAccessedResourceNames);
                
                // Clear SiteUrls if table exists
                if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_event_accessed_resource_site_urls', 'U')").FirstOrDefault().GetValueOrDefault() != 0)
                {
                    db.CopilotAccessedResourceSiteUrls.RemoveRange(db.CopilotAccessedResourceSiteUrls);
                }
                
                db.CopilotAccessedResourceTypes.RemoveRange(db.CopilotAccessedResourceTypes);
                db.SensitivityLabels.RemoveRange(db.SensitivityLabels);
                await db.SaveChangesAsync();
            }
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
            await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData
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
                },
                AgentId = chatAgentIdAndName?.Item1,
                AgentName = chatAgentIdAndName?.Item2
            }, commonEventMeeting);

            await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData
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
                },
                AgentId = chatAgentIdAndName?.Item1,
                AgentName = chatAgentIdAndName?.Item2
            }, commonEventDocEdit);

            await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData
                {
                    // Outlook event
                    AppHost = "Outlook",
                    AccessedResources = new List<AccessedResource>
                {
                    new AccessedResource{ Type = "http://schema.skype.com/HyperLink" }
                },
                },
                AgentId = chatAgentIdAndName?.Item1,
                AgentName = chatAgentIdAndName?.Item2
            }, commonOutlook);

            await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
            {
                CopilotEventData = new CopilotEventData
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
                },
                AgentId = chatAgentIdAndName?.Item1,
                AgentName = chatAgentIdAndName?.Item2
            }, commonEventChat);

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
        /// Tests that AccessedResources are correctly saved to lookup tables and junction table
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerAccessedResourcesSaveTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {

                await ClearEvents(db);
                await ClearAccessedResources(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                // Create a test event with AccessedResources
                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test AccessedResources Op" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@user.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                // Save event with AccessedResources
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Word",
                        AccessedResources = new List<AccessedResource>
                        {
                            new AccessedResource
                            {
                                Id = "resource-id-123",
                                Name = "TestDocument.docx",
                                Type = "Document",
                                SensitivityLabelId = "label-456"
                            },
                            new AccessedResource
                            {
                                Id = "resource-id-789",
                                Name = "AnotherDocument.xlsx",
                                Type = "Spreadsheet",
                                SensitivityLabelId = "label-789"
                            }
                        }
                    }
                }, commonEvent);

                await copilotEventManager.CommitAllChanges();

                // Verify lookup tables were populated
                var resourceIds = await db.CopilotAccessedResourceIds.ToListAsync();
                var resourceNames = await db.CopilotAccessedResourceNames.ToListAsync();
                var resourceTypes = await db.CopilotAccessedResourceTypes.ToListAsync();
                var sensitivityLabels = await db.SensitivityLabels.ToListAsync();

                Assert.IsTrue(resourceIds.Any(r => r.ResourceId == "resource-id-123"), "Resource ID not saved");
                Assert.IsTrue(resourceIds.Any(r => r.ResourceId == "resource-id-789"), "Resource ID not saved");
                Assert.IsTrue(resourceNames.Any(r => r.Name == "TestDocument.docx"), "Resource name not saved");
                Assert.IsTrue(resourceNames.Any(r => r.Name == "AnotherDocument.xlsx"), "Resource name not saved");
                Assert.IsTrue(resourceTypes.Any(r => r.Name == "Document"), "Resource type not saved");
                Assert.IsTrue(resourceTypes.Any(r => r.Name == "Spreadsheet"), "Resource type not saved");
                Assert.IsTrue(sensitivityLabels.Any(l => l.LabelId == "label-456"), "Sensitivity label not saved");
                Assert.IsTrue(sensitivityLabels.Any(l => l.LabelId == "label-789"), "Sensitivity label not saved");

                // Verify junction table has records
                var accessedResources = await db.CopilotEventAccessedResources
                    .Include(ar => ar.ResourceId)
                    .Include(ar => ar.ResourceName)
                    .Include(ar => ar.ResourceType)
                    .Include(ar => ar.SensitivityLabel)
                    .Where(ar => ar.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(2, accessedResources.Count, "Expected 2 AccessedResource junction records");

                // Verify first resource
                var firstResource = accessedResources.FirstOrDefault(ar => ar.ResourceId?.ResourceId == "resource-id-123");
                Assert.IsNotNull(firstResource, "First resource not found in junction table");
                Assert.AreEqual("TestDocument.docx", firstResource.ResourceName?.Name);
                Assert.AreEqual("Document", firstResource.ResourceType?.Name);
                Assert.AreEqual("label-456", firstResource.SensitivityLabel?.LabelId);

                // Verify second resource
                var secondResource = accessedResources.FirstOrDefault(ar => ar.ResourceId?.ResourceId == "resource-id-789");
                Assert.IsNotNull(secondResource, "Second resource not found in junction table");
                Assert.AreEqual("AnotherDocument.xlsx", secondResource.ResourceName?.Name);
                Assert.AreEqual("Spreadsheet", secondResource.ResourceType?.Name);
                Assert.AreEqual("label-789", secondResource.SensitivityLabel?.LabelId);
            }
        }

        /// <summary>
        /// Tests that AccessedResources with null/missing properties are handled correctly
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerAccessedResourcesPartialDataTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {

                await ClearEvents(db);
                await ClearAccessedResources(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Partial Data Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@partial.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                // Save event with partial AccessedResources (some properties null)
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        AccessedResources = new List<AccessedResource>
                        {
                            new AccessedResource
                            {
                                Id = "resource-partial-123",
                                Type = "Link"
                                // Name and SensitivityLabelId are null
                            }
                        }
                    }
                }, commonEvent);

                await copilotEventManager.CommitAllChanges();

                // Verify the resource was saved even with partial data
                var accessedResources = await db.CopilotEventAccessedResources
                    .Include(ar => ar.ResourceId)
                    .Include(ar => ar.ResourceName)
                    .Include(ar => ar.ResourceType)
                    .Include(ar => ar.SensitivityLabel)
                    .Where(ar => ar.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(1, accessedResources.Count, "Expected 1 AccessedResource with partial data");

                var resource = accessedResources.First();
                Assert.IsNotNull(resource.ResourceId, "Resource ID should be populated");
                Assert.AreEqual("resource-partial-123", resource.ResourceId.ResourceId);
                Assert.IsNull(resource.ResourceName, "Resource name should be null");
                Assert.IsNotNull(resource.ResourceType, "Resource type should be populated");
                Assert.AreEqual("Link", resource.ResourceType.Name);
                Assert.IsNull(resource.SensitivityLabel, "Sensitivity label should be null");
            }
        }

        /// <summary>
        /// Tests that duplicate AccessedResources are not created in lookup tables
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerAccessedResourcesDeduplicationTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip test if migration hasn't been run yet
                if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_event_accessed_resources', 'U')").FirstOrDefault().GetValueOrDefault() == 0)
                {
                    Assert.Inconclusive("AccessedResources tables do not exist. Run migration first.");
                    return;
                }

                await ClearEvents(db);
                await ClearAccessedResources(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                // Create two events with the same AccessedResource
                var commonEvent1 = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Dedup Test 1" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@dedup1.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                var commonEvent2 = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Dedup Test 2" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@dedup2.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.AddRange(new[] { commonEvent1, commonEvent2 });
                await db.SaveChangesAsync();

                var sharedResource = new AccessedResource
                {
                    Id = "shared-resource-id",
                    Name = "SharedDocument.docx",
                    Type = "Document",
                    SensitivityLabelId = "shared-label"
                };

                // Save first event
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Word",
                        AccessedResources = new List<AccessedResource> { sharedResource }
                    }
                }, commonEvent1);

                await copilotEventManager.CommitAllChanges();

                // Save second event with same resource
                copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Word",
                        AccessedResources = new List<AccessedResource> { sharedResource }
                    }
                }, commonEvent2);

                await copilotEventManager.CommitAllChanges();

                // Verify lookup tables only have one entry each
                var resourceIds = await db.CopilotAccessedResourceIds.Where(r => r.ResourceId == "shared-resource-id").ToListAsync();
                var resourceNames = await db.CopilotAccessedResourceNames.Where(r => r.Name == "SharedDocument.docx").ToListAsync();
                var resourceTypes = await db.CopilotAccessedResourceTypes.Where(r => r.Name == "Document").ToListAsync();
                var sensitivityLabels = await db.SensitivityLabels.Where(l => l.LabelId == "shared-label").ToListAsync();

                Assert.AreEqual(1, resourceIds.Count, "Should have only 1 unique resource ID");
                Assert.AreEqual(1, resourceNames.Count, "Should have only 1 unique resource name");
                Assert.AreEqual(1, resourceTypes.Count, "Should have only 1 unique resource type");
                Assert.AreEqual(1, sensitivityLabels.Count, "Should have only 1 unique sensitivity label");

                // But junction table should have 2 entries (one for each event)
                var junctionRecords = await db.CopilotEventAccessedResources
                    .Where(ar => ar.ResourceId.ResourceId == "shared-resource-id")
                    .ToListAsync();

                Assert.AreEqual(2, junctionRecords.Count, "Should have 2 junction records (one per event)");
            }
        }

        /// <summary>
        /// Tests that SiteUrls from AccessedResources are correctly saved to lookup table and junction table
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerAccessedResourcesSiteUrlSaveTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip test if migration hasn't been run yet
                if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_event_accessed_resource_site_urls', 'U')").FirstOrDefault().GetValueOrDefault() == 0)
                {
                    Assert.Inconclusive("AccessedResources SiteUrls table does not exist. Run migration first.");
                    return;
                }

                await ClearEvents(db);
                await ClearAccessedResources(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                // Create a test event with AccessedResources containing SiteUrls
                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test SiteUrl Op" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@siteurl.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                // Save event with AccessedResources containing SiteUrls
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Word",
                        AccessedResources = new List<AccessedResource>
                        {
                            new AccessedResource
                            {
                                Id = "resource-with-siteurl-123",
                                Name = "DocumentWithSite.docx",
                                Type = "Document",
                                SiteUrl = "https://contoso.sharepoint.com/sites/teamsite",
                                SensitivityLabelId = "label-456"
                            },
                            new AccessedResource
                            {
                                Id = "resource-with-siteurl-789",
                                Name = "AnotherDocumentWithSite.xlsx",
                                Type = "Spreadsheet",
                                SiteUrl = "https://contoso.sharepoint.com/sites/projectsite",
                                SensitivityLabelId = "label-789"
                            }
                        }
                    }
                }, commonEvent);

                await copilotEventManager.CommitAllChanges();

                // Verify SiteUrl lookup table was populated
                var siteUrls = await db.CopilotAccessedResourceSiteUrls.ToListAsync();
                Assert.IsTrue(siteUrls.Any(s => s.SiteUrl == "https://contoso.sharepoint.com/sites/teamsite"), "First SiteUrl not saved");
                Assert.IsTrue(siteUrls.Any(s => s.SiteUrl == "https://contoso.sharepoint.com/sites/projectsite"), "Second SiteUrl not saved");

                // Verify junction table has records with SiteUrls
                var accessedResources = await db.CopilotEventAccessedResources
                    .Include(ar => ar.ResourceId)
                    .Include(ar => ar.ResourceName)
                    .Include(ar => ar.ResourceType)
                    .Include(ar => ar.ResourceSiteUrl)
                    .Include(ar => ar.SensitivityLabel)
                    .Where(ar => ar.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(2, accessedResources.Count, "Expected 2 AccessedResource junction records");

                // Verify first resource with SiteUrl
                var firstResource = accessedResources.FirstOrDefault(ar => ar.ResourceId?.ResourceId == "resource-with-siteurl-123");
                Assert.IsNotNull(firstResource, "First resource not found in junction table");
                Assert.AreEqual("DocumentWithSite.docx", firstResource.ResourceName?.Name);
                Assert.AreEqual("Document", firstResource.ResourceType?.Name);
                Assert.AreEqual("https://contoso.sharepoint.com/sites/teamsite", firstResource.ResourceSiteUrl?.SiteUrl);
                Assert.AreEqual("label-456", firstResource.SensitivityLabel?.LabelId);

                // Verify second resource with SiteUrl
                var secondResource = accessedResources.FirstOrDefault(ar => ar.ResourceId?.ResourceId == "resource-with-siteurl-789");
                Assert.IsNotNull(secondResource, "Second resource not found in junction table");
                Assert.AreEqual("AnotherDocumentWithSite.xlsx", secondResource.ResourceName?.Name);
                Assert.AreEqual("Spreadsheet", secondResource.ResourceType?.Name);
                Assert.AreEqual("https://contoso.sharepoint.com/sites/projectsite", secondResource.ResourceSiteUrl?.SiteUrl);
                Assert.AreEqual("label-789", secondResource.SensitivityLabel?.LabelId);
            }
        }

        /// <summary>
        /// Tests that AccessedResources without SiteUrls are handled correctly (SiteUrl is optional)
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerAccessedResourcesWithoutSiteUrlTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip test if migration hasn't been run yet
                if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_event_accessed_resource_site_urls', 'U')").FirstOrDefault().GetValueOrDefault() == 0)
                {
                    Assert.Inconclusive("AccessedResources SiteUrls table does not exist. Run migration first.");
                    return;
                }

                await ClearEvents(db);
                await ClearAccessedResources(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "No SiteUrl Test" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@nositeurl.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                // Save event with AccessedResource that has no SiteUrl
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Teams",
                        AccessedResources = new List<AccessedResource>
                        {
                            new AccessedResource
                            {
                                Id = "resource-no-siteurl-123",
                                Name = "LinkWithoutSite.url",
                                Type = "Link"
                                // SiteUrl is null
                            }
                        }
                    }
                }, commonEvent);

                await copilotEventManager.CommitAllChanges();

                // Verify the resource was saved even without SiteUrl
                var accessedResources = await db.CopilotEventAccessedResources
                    .Include(ar => ar.ResourceId)
                    .Include(ar => ar.ResourceName)
                    .Include(ar => ar.ResourceType)
                    .Include(ar => ar.ResourceSiteUrl)
                    .Where(ar => ar.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(1, accessedResources.Count, "Expected 1 AccessedResource without SiteUrl");

                var resource = accessedResources.First();
                Assert.IsNotNull(resource.ResourceId, "Resource ID should be populated");
                Assert.AreEqual("resource-no-siteurl-123", resource.ResourceId.ResourceId);
                Assert.IsNotNull(resource.ResourceName, "Resource name should be populated");
                Assert.AreEqual("LinkWithoutSite.url", resource.ResourceName.Name);
                Assert.IsNotNull(resource.ResourceType, "Resource type should be populated");
                Assert.AreEqual("Link", resource.ResourceType.Name);
                Assert.IsNull(resource.ResourceSiteUrl, "Resource SiteUrl should be null");
            }
        }

        /// <summary>
        /// Tests that duplicate SiteUrls are deduplicated in the lookup table
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerAccessedResourcesSiteUrlDeduplicationTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Skip test if migration hasn't been run yet
                if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_event_accessed_resource_site_urls', 'U')").FirstOrDefault().GetValueOrDefault() == 0)
                {
                    Assert.Inconclusive("AccessedResources SiteUrls table does not exist. Run migration first.");
                    return;
                }

                await ClearEvents(db);
                await ClearAccessedResources(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                // Create two events with resources from the same SiteUrl
                var commonEvent1 = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "SiteUrl Dedup Test 1" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@siteurldedup1.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                var commonEvent2 = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "SiteUrl Dedup Test 2" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@siteurldedup2.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.AddRange(new[] { commonEvent1, commonEvent2 });
                await db.SaveChangesAsync();

                var sharedSiteUrl = "https://contoso.sharepoint.com/sites/shared-site";

                // Save first event with resource from shared SiteUrl
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Word",
                        AccessedResources = new List<AccessedResource>
                        {
                            new AccessedResource
                            {
                                Id = "resource-shared-site-1",
                                Name = "Document1.docx",
                                Type = "Document",
                                SiteUrl = sharedSiteUrl
                            }
                        }
                    }
                }, commonEvent1);

                await copilotEventManager.CommitAllChanges();

                // Save second event with different resource but same SiteUrl
                copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Excel",
                        AccessedResources = new List<AccessedResource>
                        {
                            new AccessedResource
                            {
                                Id = "resource-shared-site-2",
                                Name = "Spreadsheet1.xlsx",
                                Type = "Spreadsheet",
                                SiteUrl = sharedSiteUrl
                            }
                        }
                    }
                }, commonEvent2);

                await copilotEventManager.CommitAllChanges();

                // Verify lookup table only has one entry for the shared SiteUrl
                var siteUrls = await db.CopilotAccessedResourceSiteUrls
                    .Where(s => s.SiteUrl == sharedSiteUrl)
                    .ToListAsync();

                Assert.AreEqual(1, siteUrls.Count, "Should have only 1 unique SiteUrl in lookup table");

                // But junction table should have 2 entries (one for each event)
                var junctionRecords = await db.CopilotEventAccessedResources
                    .Include(ar => ar.ResourceSiteUrl)
                    .Where(ar => ar.ResourceSiteUrl.SiteUrl == sharedSiteUrl)
                    .ToListAsync();

                Assert.AreEqual(2, junctionRecords.Count, "Should have 2 junction records with the shared SiteUrl");
            }
        }

        #region Copilot Credit Estimation Tests

        /// <summary>
        /// Tests that Copilot Credit estimation data (total and JSON) is correctly saved to the database
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerCopilotCreditEstimationSaveTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                // Create a test event with Copilot Credit estimation data
                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test Copilot Credit Estimation Op" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@creditest.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

                // Create audit record with Copilot Credit estimation
                var creditEstimation = new CopilotCreditEstimation
                {
                    GenerativeAnswers = 2,
                    TenantGraphGroundedAnswers = 2,
                    DeepReasoningActions = 1,
                    TotalCredits = 29, // 2*(2+10) + 5 = 24 + 5 = 29
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

                // Save event with Copilot Credit estimation data
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(auditLogContent, commonEvent);
                await copilotEventManager.CommitAllChanges();

                // Verify Copilot Credit estimation data was saved
                var savedEvent = await db.CopilotChats
                    .Where(e => e.AuditEvent.Id == commonEvent.Id)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(savedEvent, "CopilotChat event should be saved");
                Assert.AreEqual(29, savedEvent.CopilotCreditEstimateTotal, "Copilot Credit estimate total should match");
                Assert.IsNotNull(savedEvent.CopilotCreditEstimateJson, "Copilot Credit estimate JSON should not be null");

                // Verify JSON can be deserialized back
                var deserializedCreditEstimate = JsonConvert.DeserializeObject<CopilotCreditEstimation>(savedEvent.CopilotCreditEstimateJson);
                Assert.IsNotNull(deserializedCreditEstimate, "Copilot Credit estimate JSON should deserialize correctly");
                Assert.AreEqual(2, deserializedCreditEstimate.GenerativeAnswers);
                Assert.AreEqual(2, deserializedCreditEstimate.TenantGraphGroundedAnswers);
                Assert.AreEqual(1, deserializedCreditEstimate.DeepReasoningActions);
                Assert.AreEqual(29, deserializedCreditEstimate.TotalCredits);
            }
        }

        /// <summary>
        /// Tests that null Copilot Credit estimation data is handled correctly
        /// </summary>
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

                // Save event without Copilot Credit estimation data
                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData { AppHost = "Teams" },
                    Cost = null
                }, commonEvent);

                await copilotEventManager.CommitAllChanges();

                // Verify event was saved with null Copilot Credit estimation data
                var savedEvent = await db.CopilotChats
                    .Where(e => e.AuditEvent.Id == commonEvent.Id)
                    .FirstOrDefaultAsync();

                Assert.IsNotNull(savedEvent, "CopilotChat event should be saved");
                Assert.IsNull(savedEvent.CopilotCreditEstimateTotal, "Copilot Credit estimate total should be null");
                Assert.IsNull(savedEvent.CopilotCreditEstimateJson, "Copilot Credit estimate JSON should be null");
            }
        }

        /// <summary>
        /// Tests Copilot Credit estimation tracking with different event types (file, meeting, chat)
        /// </summary>
        [TestMethod]
        public async Task CopilotEventManagerCopilotCreditEstimationMultipleEventTypesTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearEvents(db);

                var adaptor = new FakeCopilotMetadataLoader();

                // File event with Copilot Credits
                var fileEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "File Op" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@file.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                // Meeting event with Copilot Credits
                var meetingEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Meeting Op" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@meeting.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                // Chat event with Copilot Credits
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

                // File event - 12 credits (generative + tenant graph)
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

                // Meeting event - 2 credits (generative only)
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

                // Chat event - 17 credits (generative + tenant graph + deep reasoning)
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

                // Verify all events have correct Copilot Credit estimation data
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
            Assert.IsTrue(result.IsCustomAgent.HasValue && result.IsCustomAgent.Value, "IsCustomAgent should be true when extracted from AppIdentity");
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
            Assert.IsNull(result.IsCustomAgent, "IsCustomAgent should remain null when not extracted from AppIdentity");
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
            Assert.IsNull(result.IsCustomAgent, "IsCustomAgent should remain null");
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
            Assert.IsNull(result.IsCustomAgent, "IsCustomAgent should remain null when extraction fails");
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
            Assert.IsNull(result.IsCustomAgent, "IsCustomAgent should remain null when extraction fails");
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
            Assert.IsNull(result.IsCustomAgent, "IsCustomAgent should remain null when extraction fails");
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
                Assert.IsTrue(result.IsCustomAgent.HasValue && result.IsCustomAgent.Value, $"IsCustomAgent should be true for: {expectedAgentName}");
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

        /// <summary>
        /// Tests that only custom agents are charged Copilot Credits.
        /// Standard M365 Copilot should have 0 credits.
        /// </summary>
        [TestMethod]
        public void CopilotCreditEstimation_CustomAgentOnly_ChargesCopilotCredits()
        {
            // Arrange - create audit event with messages and tenant graph resources
            var json = @"{
                ""Messages"": [
                    { ""Id"": ""msg1"", ""isPrompt"": true },
                    { ""Id"": ""msg2"", ""isPrompt"": false },
                    { ""Id"": ""msg3"", ""isPrompt"": false }
                ],
                ""AccessedResources"": [
                    { ""Type"": ""File"", ""SiteUrl"": ""https://contoso.sharepoint.com/sites/team"" }
                ],
                ""ModelTransparencyDetails"": []
            }";

            // Act - analyze for custom agent
            var customAgentCost = CopilotCreditEstimation.Analyze(json, isCustomAgent: true);

            // Assert - custom agent should be charged
            Assert.AreEqual(2, customAgentCost.GenerativeAnswers, "Custom agent should have 2 generative answers");
            Assert.AreEqual(2, customAgentCost.TenantGraphGroundedAnswers, "Custom agent should have 2 tenant graph grounded answers");
            Assert.AreEqual(24, customAgentCost.TotalCredits, "Custom agent should be charged 24 credits (2 * (2 + 10))");

            // Act - analyze for non-custom (standard M365) agent
            var standardAgentCost = CopilotCreditEstimation.Analyze(json, isCustomAgent: false);

            // Assert - standard agent should NOT be charged
            Assert.AreEqual(0, standardAgentCost.GenerativeAnswers, "Standard agent should have 0 generative answers counted");
            Assert.AreEqual(0, standardAgentCost.TenantGraphGroundedAnswers, "Standard agent should have 0 tenant graph answers counted");
            Assert.AreEqual(0, standardAgentCost.TotalCredits, "Standard agent should have 0 credits");

            // Verify analytics data is still captured for standard agents
            Assert.IsTrue(standardAgentCost.ResourceTypeBreakdown.Count > 0, "Resource breakdown should still be captured for analytics");
        }

        /// <summary>
        /// Tests that deep reasoning (premium model) is only charged for custom agents
        /// </summary>
        [TestMethod]
        public void CopilotCreditEstimation_CustomAgentOnly_ChargesDeepReasoning()
        {
            // Arrange - create audit event with deep reasoning model
            var json = @"{
                ""Messages"": [
                    { ""Id"": ""msg1"", ""isPrompt"": true },
                    { ""Id"": ""msg2"", ""isPrompt"": false }
                ],
                ""AccessedResources"": [
                    { ""Type"": ""File"", ""SiteUrl"": ""https://contoso.sharepoint.com/sites/team"" }
                ],
                ""ModelTransparencyDetails"": [
                    { ""ModelName"": ""DEEP_LEO"" }
                ]
            }";

            // Act - analyze for custom agent
            var customAgentCost = CopilotCreditEstimation.Analyze(json, isCustomAgent: true);

            // Assert - custom agent should be charged for deep reasoning
            Assert.AreEqual(1, customAgentCost.DeepReasoningActions, "Custom agent should have 1 deep reasoning action");
            Assert.AreEqual(17, customAgentCost.TotalCredits, "Custom agent should be charged 17 credits (2 + 10 + 5)");
            Assert.IsTrue(customAgentCost.ModelsUsed.Contains("DEEP_LEO"), "DEEP_LEO model should be tracked for custom agent");

            // Act - analyze for non-custom (standard M365) agent
            var standardAgentCost = CopilotCreditEstimation.Analyze(json, isCustomAgent: false);

            // Assert - standard agent should NOT be charged for deep reasoning
            Assert.AreEqual(0, standardAgentCost.DeepReasoningActions, "Standard agent should have 0 deep reasoning actions counted");
            Assert.AreEqual(0, standardAgentCost.TotalCredits, "Standard agent should have 0 credits");
            
            // Verify model is still tracked for analytics even though not charged
            Assert.IsTrue(standardAgentCost.ModelsUsed.Contains("DEEP_LEO"), "DEEP_LEO model should still be tracked for analytics");
        }

        /// <summary>
        /// Tests that web-only searches (no tenant resources) still result in 0 credits for standard agents
        /// </summary>
        [TestMethod]
        public void CopilotCreditEstimation_StandardAgent_WebSearchesHaveZeroCredits()
        {
            // Arrange - create audit event with web-only resources (no tenant graph)
            var json = @"{
                ""Messages"": [
                    { ""Id"": ""msg1"", ""isPrompt"": true },
                    { ""Id"": ""msg2"", ""isPrompt"": false },
                    { ""Id"": ""msg3"", ""isPrompt"": false }
                ],
                ""AccessedResources"": [
                    { ""Type"": ""WebPage"", ""SiteUrl"": ""https://www.example.com"" }
                ],
                ""ModelTransparencyDetails"": []
            }";

            // Act - analyze for custom agent (should only charge for generative, not tenant graph)
            var customAgentCost = CopilotCreditEstimation.Analyze(json, isCustomAgent: true);

            // Assert - custom agent charged for generative only (no tenant graph)
            Assert.AreEqual(2, customAgentCost.GenerativeAnswers, "Custom agent should have 2 generative answers");
            Assert.AreEqual(0, customAgentCost.TenantGraphGroundedAnswers, "No tenant graph resources accessed");
            Assert.AreEqual(4, customAgentCost.TotalCredits, "Custom agent should be charged 4 credits (2 * 2)");

            // Act - analyze for standard agent
            var standardAgentCost = CopilotCreditEstimation.Analyze(json, isCustomAgent: false);

            // Assert - standard agent has 0 credits
            Assert.AreEqual(0, standardAgentCost.TotalCredits, "Standard agent should have 0 credits even with web searches");
        }

        /// <summary>
        /// Tests comprehensive scenario with all billing components for custom vs standard agents
        /// </summary>
        [TestMethod]
        public void CopilotCreditEstimation_ComprehensiveScenario_DifferentiatesAgentTypes()
        {
            // Arrange - complex scenario with multiple messages, tenant resources, and deep reasoning
            var json = @"{
                ""Messages"": [
                    { ""Id"": ""msg1"", ""isPrompt"": true },
                    { ""Id"": ""msg2"", ""isPrompt"": false },
                    { ""Id"": ""msg3"", ""isPrompt"": false },
                    { ""Id"": ""msg4"", ""isPrompt"": true },
                    { ""Id"": ""msg5"", ""isPrompt"": false }
                ],
                ""AccessedResources"": [
                    { ""Type"": ""docx"", ""Name"": ""Document1.docx"", ""SiteUrl"": ""https://contoso.sharepoint.com/sites/team"" },
                    { ""Type"": ""xlsx"", ""Name"": ""Spreadsheet1.xlsx"", ""SiteUrl"": ""https://contoso-my.sharepoint.com/personal/user"" },
                    { ""Type"": ""Email"", ""Name"": ""Meeting Notes"" }
                ],
                ""ModelTransparencyDetails"": [
                    { ""ModelName"": ""DEEP_LEO"" }
                ]
            }";

            // Act - analyze for custom agent
            var customAgentCost = CopilotCreditEstimation.Analyze(json, isCustomAgent: true);

            // Assert - custom agent full billing
            Assert.AreEqual(3, customAgentCost.GenerativeAnswers, "3 response messages");
            Assert.AreEqual(3, customAgentCost.TenantGraphGroundedAnswers, "All 3 responses use tenant graph");
            Assert.AreEqual(1, customAgentCost.DeepReasoningActions, "1 deep reasoning action");
            Assert.AreEqual(41, customAgentCost.TotalCredits, "Total: 3*(2+10) + 5 = 36 + 5 = 41 credits");
            Assert.AreEqual(3, customAgentCost.ResourceTypeBreakdown.Count, "3 resource types accessed");
            Assert.IsTrue(customAgentCost.CreditBreakdown.ContainsKey("Generative Answers"), "Should have generative breakdown");
            Assert.IsTrue(customAgentCost.CreditBreakdown.ContainsKey("Tenant Graph Grounding"), "Should have tenant graph breakdown");
            Assert.IsTrue(customAgentCost.CreditBreakdown.ContainsKey("Agent Actions (Deep Reasoning)"), "Should have deep reasoning breakdown");

            // Act - analyze for standard agent
            var standardAgentCost = CopilotCreditEstimation.Analyze(json, isCustomAgent: false);

            // Assert - standard agent no billing but analytics preserved
            Assert.AreEqual(0, standardAgentCost.GenerativeAnswers, "No answers counted for standard agent");
            Assert.AreEqual(0, standardAgentCost.TenantGraphGroundedAnswers, "No grounding counted for standard agent");
            Assert.AreEqual(0, standardAgentCost.DeepReasoningActions, "No actions counted for standard agent");
            Assert.AreEqual(0, standardAgentCost.TotalCredits, "Standard M365 Copilot has 0 credits");
            Assert.AreEqual(0, standardAgentCost.CreditBreakdown.Count, "No credit breakdown for standard agent");
            
            // Analytics data still captured
            Assert.AreEqual(3, standardAgentCost.ResourceTypeBreakdown.Count, "Resource analytics still captured");
            Assert.IsTrue(standardAgentCost.ModelsUsed.Contains("DEEP_LEO"), "Model analytics still captured");
        }

        /// <summary>
        /// Tests empty/null scenarios for both agent types
        /// </summary>
        [TestMethod]
        public void CopilotCreditEstimation_EmptyEvents_BothAgentTypesReturnZero()
        {
            // Arrange - empty event
            var emptyJson = @"{
                ""Messages"": [],
                ""AccessedResources"": [],
                ""ModelTransparencyDetails"": []
            }";

            // Act
            var customAgentCost = CopilotCreditEstimation.Analyze(emptyJson, isCustomAgent: true);
            var standardAgentCost = CopilotCreditEstimation.Analyze(emptyJson, isCustomAgent: false);

            // Assert - both should return 0
            Assert.AreEqual(0, customAgentCost.TotalCredits, "Empty custom agent event should have 0 credits");
            Assert.AreEqual(0, standardAgentCost.TotalCredits, "Empty standard agent event should have 0 credits");

            // Test null string
            var nullCost = CopilotCreditEstimation.Analyze((string)null, isCustomAgent: true);
            Assert.AreEqual(0, nullCost.TotalCredits, "Null event should have 0 credits");

            // Test empty string
            var emptyCost = CopilotCreditEstimation.Analyze(string.Empty, isCustomAgent: true);
            Assert.AreEqual(0, emptyCost.TotalCredits, "Empty string should have 0 credits");
        }

        /// <summary>
        /// Tests that when there's no agent information (AgentName is null/empty), Cost is set to NoCost (0 credits).
        /// This verifies the logic in CopilotAuditLogContent.FromJson() that assigns NoCost when AgentName is empty.
        /// </summary>
        [TestMethod]
        public void CopilotAuditLogContent_NoAgentName_HasNoCost()
        {
            // Arrange - JSON with Copilot event data but NO AgentName or AgentId
            // This would normally result in charges, but without an agent identifier, no cost should be assigned
            var jsonWithoutAgent = @"{
                ""CopilotEventData"": {
                    ""AppHost"": ""Teams"",
                    ""AccessedResources"": [
                        {
                            ""Id"": ""file123"",
                            ""Type"": ""File"",
                            ""SiteUrl"": ""https://contoso.sharepoint.com/sites/test""
                        }
                    ]
                },
                ""Messages"": [
                    { ""IsPrompt"": true },
                    { ""IsPrompt"": false }
                ]
            }";

            // Act - Deserialize using FromJson (which applies the cost logic)
            var auditLog = CopilotAuditLogContent.FromJson(jsonWithoutAgent);

            // Assert - When AgentName is null/empty, Cost should be NoCost (0 credits)
            Assert.IsNotNull(auditLog.Cost, "Cost should not be null");
            Assert.AreEqual(0, auditLog.Cost.TotalCredits, "When AgentName is empty, TotalCredits should be 0");
            Assert.IsTrue(string.IsNullOrEmpty(auditLog.AgentName), "AgentName should be null or empty");
            Assert.IsTrue(string.IsNullOrEmpty(auditLog.AgentId), "AgentId should be null or empty");
            Assert.IsNull(auditLog.IsCustomAgent, "IsCustomAgent should be null when agent info is not present");
        }
    }
}
