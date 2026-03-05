using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
    /// Tests for AccessedResources persistence: lookup tables, junction records, SiteUrls, deduplication.
    /// </summary>
    [TestClass]
    public class CopilotEventManagerAccessedResourcesTests : CopilotTestBase
    {
        [TestMethod]
        public async Task CopilotEventManagerAccessedResourcesSaveTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {

                await ClearEvents(db);
                await ClearAccessedResources(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

                var commonEvent = new CommonAuditEvent
                {
                    TimeStamp = DateTime.Now,
                    Operation = new EventOperation { Name = "Test AccessedResources Op" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@user.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

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

                var accessedResources = await db.CopilotEventAccessedResources
                    .Include(ar => ar.ResourceId)
                    .Include(ar => ar.ResourceName)
                    .Include(ar => ar.ResourceType)
                    .Include(ar => ar.SensitivityLabel)
                    .Where(ar => ar.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(2, accessedResources.Count, "Expected 2 AccessedResource junction records");

                var firstResource = accessedResources.FirstOrDefault(ar => ar.ResourceId?.ResourceId == "resource-id-123");
                Assert.IsNotNull(firstResource, "First resource not found in junction table");
                Assert.AreEqual("TestDocument.docx", firstResource.ResourceName?.Name);
                Assert.AreEqual("Document", firstResource.ResourceType?.Name);
                Assert.AreEqual("label-456", firstResource.SensitivityLabel?.LabelId);

                var secondResource = accessedResources.FirstOrDefault(ar => ar.ResourceId?.ResourceId == "resource-id-789");
                Assert.IsNotNull(secondResource, "Second resource not found in junction table");
                Assert.AreEqual("AnotherDocument.xlsx", secondResource.ResourceName?.Name);
                Assert.AreEqual("Spreadsheet", secondResource.ResourceType?.Name);
                Assert.AreEqual("label-789", secondResource.SensitivityLabel?.LabelId);
            }
        }

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
                            }
                        }
                    }
                }, commonEvent);

                await copilotEventManager.CommitAllChanges();

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

        [TestMethod]
        public async Task CopilotEventManagerAccessedResourcesDeduplicationTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_event_accessed_resources', 'U')").FirstOrDefault().GetValueOrDefault() == 0)
                {
                    Assert.Inconclusive("AccessedResources tables do not exist. Run migration first.");
                    return;
                }

                await ClearEvents(db);
                await ClearAccessedResources(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

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

                await copilotEventManager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
                {
                    CopilotEventData = new CopilotEventData
                    {
                        AppHost = "Word",
                        AccessedResources = new List<AccessedResource> { sharedResource }
                    }
                }, commonEvent1);

                await copilotEventManager.CommitAllChanges();

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

                var resourceIds = await db.CopilotAccessedResourceIds.Where(r => r.ResourceId == "shared-resource-id").ToListAsync();
                var resourceNames = await db.CopilotAccessedResourceNames.Where(r => r.Name == "SharedDocument.docx").ToListAsync();
                var resourceTypes = await db.CopilotAccessedResourceTypes.Where(r => r.Name == "Document").ToListAsync();
                var sensitivityLabels = await db.SensitivityLabels.Where(l => l.LabelId == "shared-label").ToListAsync();

                Assert.AreEqual(1, resourceIds.Count, "Should have only 1 unique resource ID");
                Assert.AreEqual(1, resourceNames.Count, "Should have only 1 unique resource name");
                Assert.AreEqual(1, resourceTypes.Count, "Should have only 1 unique resource type");
                Assert.AreEqual(1, sensitivityLabels.Count, "Should have only 1 unique sensitivity label");

                var junctionRecords = await db.CopilotEventAccessedResources
                    .Where(ar => ar.ResourceId.ResourceId == "shared-resource-id")
                    .ToListAsync();

                Assert.AreEqual(2, junctionRecords.Count, "Should have 2 junction records (one per event)");
            }
        }

        [TestMethod]
        public async Task CopilotEventManagerAccessedResourcesSiteUrlSaveTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
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
                    Operation = new EventOperation { Name = "Test SiteUrl Op" + DateTime.Now.Ticks },
                    User = new User { AzureAdId = "test", UserPrincipalName = "test@siteurl.com" + DateTime.Now.Ticks },
                    Id = Guid.NewGuid()
                };

                db.AuditEventsCommon.Add(commonEvent);
                await db.SaveChangesAsync();

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

                var siteUrls = await db.CopilotAccessedResourceSiteUrls.ToListAsync();
                Assert.IsTrue(siteUrls.Any(s => s.SiteUrl == "https://contoso.sharepoint.com/sites/teamsite"), "First SiteUrl not saved");
                Assert.IsTrue(siteUrls.Any(s => s.SiteUrl == "https://contoso.sharepoint.com/sites/projectsite"), "Second SiteUrl not saved");

                var accessedResources = await db.CopilotEventAccessedResources
                    .Include(ar => ar.ResourceId)
                    .Include(ar => ar.ResourceName)
                    .Include(ar => ar.ResourceType)
                    .Include(ar => ar.ResourceSiteUrl)
                    .Include(ar => ar.SensitivityLabel)
                    .Where(ar => ar.ChatId == commonEvent.Id)
                    .ToListAsync();

                Assert.AreEqual(2, accessedResources.Count, "Expected 2 AccessedResource junction records");

                var firstResource = accessedResources.FirstOrDefault(ar => ar.ResourceId?.ResourceId == "resource-with-siteurl-123");
                Assert.IsNotNull(firstResource, "First resource not found in junction table");
                Assert.AreEqual("DocumentWithSite.docx", firstResource.ResourceName?.Name);
                Assert.AreEqual("Document", firstResource.ResourceType?.Name);
                Assert.AreEqual("https://contoso.sharepoint.com/sites/teamsite", firstResource.ResourceSiteUrl?.SiteUrl);
                Assert.AreEqual("label-456", firstResource.SensitivityLabel?.LabelId);

                var secondResource = accessedResources.FirstOrDefault(ar => ar.ResourceId?.ResourceId == "resource-with-siteurl-789");
                Assert.IsNotNull(secondResource, "Second resource not found in junction table");
                Assert.AreEqual("AnotherDocumentWithSite.xlsx", secondResource.ResourceName?.Name);
                Assert.AreEqual("Spreadsheet", secondResource.ResourceType?.Name);
                Assert.AreEqual("https://contoso.sharepoint.com/sites/projectsite", secondResource.ResourceSiteUrl?.SiteUrl);
                Assert.AreEqual("label-789", secondResource.SensitivityLabel?.LabelId);
            }
        }

        [TestMethod]
        public async Task CopilotEventManagerAccessedResourcesWithoutSiteUrlTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
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
                            }
                        }
                    }
                }, commonEvent);

                await copilotEventManager.CommitAllChanges();

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

        [TestMethod]
        public async Task CopilotEventManagerAccessedResourcesSiteUrlDeduplicationTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                if (db.Database.SqlQuery<int?>("SELECT OBJECT_ID('dbo.copilot_event_accessed_resource_site_urls', 'U')").FirstOrDefault().GetValueOrDefault() == 0)
                {
                    Assert.Inconclusive("AccessedResources SiteUrls table does not exist. Run migration first.");
                    return;
                }

                await ClearEvents(db);
                await ClearAccessedResources(db);

                var copilotEventManager = new CopilotAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, new FakeCopilotMetadataLoader(), _logger);

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

                var siteUrls = await db.CopilotAccessedResourceSiteUrls
                    .Where(s => s.SiteUrl == sharedSiteUrl)
                    .ToListAsync();

                Assert.AreEqual(1, siteUrls.Count, "Should have only 1 unique SiteUrl in lookup table");

                var junctionRecords = await db.CopilotEventAccessedResources
                    .Include(ar => ar.ResourceSiteUrl)
                    .Where(ar => ar.ResourceSiteUrl.SiteUrl == sharedSiteUrl)
                    .ToListAsync();

                Assert.AreEqual(2, junctionRecords.Count, "Should have 2 junction records with the shared SiteUrl");
            }
        }
    }
}
