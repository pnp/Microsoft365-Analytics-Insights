using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Entities.AuditLog;
using DataUtils;
using DataUtils.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Sql;

namespace Tests.UnitTests
{
    [TestClass]
    public class DuplicateUrlTests
    {

        [TestMethod]
        public async Task InsertAndCleanDuplicateUrlTests()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Make sure we have the right DB schema
                await ImportDbHacks.CleanDuplicateHitsAndCreateIX_PageRequestID(db);


                var URL_DUP = "http://whatever/" + DateTime.Now.Ticks;

                // Insert x2 duplicate URLs
                var urlDup1 = new Url() { FullUrl = URL_DUP };
                var urlDup2 = new Url() { FullUrl = URL_DUP, MetadataLastRefreshed = DateTime.Now };
                db.urls.Add(urlDup1);
                await db.SaveChangesAsync();

                db.urls.Add(urlDup2);
                await db.SaveChangesAsync();
                Console.WriteLine($"Inserted duplicate URLs: {urlDup1.ID} and {urlDup2.ID}");

                var randoUser = new User 
                {
                    UserPrincipalName = "user" + DateTime.Now.Ticks + "@example.com",
                };

                // Insert linked data to the URLs. These should be all the entities that reference URLs in the database.
                var spEvent = new SharePointEventMetadata
                {
                    url = urlDup2,
                    Event = new Office365Event
                    {
                        Id = Guid.NewGuid(),
                        Operation = new EventOperation
                        {
                            Name = "Op" + DateTime.Now.Ticks
                        },
                        User = randoUser,
                        TimeStamp = DateTime.Now,
                    },
                };
                db.sharepoint_events.Add(spEvent);

                var copilotEvent = new CopilotEventMetadataFile
                {
                    FileExtension = new SPEventFileExtension { extension_name = "Ext" + DateTime.Now.Ticks },
                    FileName = new SPEventFileName { Name = "File" + DateTime.Now.Ticks },
                    Url = urlDup2,
                    Site = new Site { UrlBase = "http://site" + DateTime.Now.Ticks },
                    RelatedChat = new CopilotChat
                    {
                        AppHost = "AppHost" + DateTime.Now.Ticks,
                        Event = new Office365Event
                        {
                            Id = Guid.NewGuid(),
                            Operation = new EventOperation
                            {
                                Name = "Op" + DateTime.Now.Ticks
                            },
                            User = randoUser,
                            TimeStamp = DateTime.Now,
                        }
                    }
                };
                db.CopilotEventMetadataFiles.Add(copilotEvent);

                var pageComment = new PageComment
                {
                    Url = urlDup2,
                    Comment = "Comment" + DateTime.Now.Ticks,
                    User = randoUser,
                    Created = DateTime.Now,
                };
                db.UrlComments.Add(pageComment);

                var pageLike = new PageLike
                {
                    Url = urlDup2,
                    User = randoUser,
                    Created = DateTime.Now,
                };
                db.UrlLikes.Add(pageLike);

                var pagePropVal = new FileMetadataPropertyValue
                {
                    Url = urlDup2,
                    Field = new FileMetadataFieldName { Name = "Prop" + DateTime.Now.Ticks },
                    Updated = DateTime.Now,
                };
                db.FileMetadataPropertyValues.Add(pagePropVal);
                await db.SaveChangesAsync();

                var duplicateHitsInSingleBatch = new PageViewCollection();

                var hitsPreInsert = db.hits.Count();

                // Add 2 hits with same URL but different request IDs
                duplicateHitsInSingleBatch.Rows.Add(new PageViewAppInsightsQueryResult
                {
                    Url = URL_DUP,
                    CustomProperties = new PageViewCustomProps
                    {
                        PageRequestId = Guid.NewGuid(),
                        SessionId = Guid.NewGuid().ToString()
                    },
                    AppInsightsTimestamp = DateTime.Now,
                    Browser = "Whatevs",
                    DeviceModel = "Whoever",
                    Username = "bob",
                    ClientOS = "Win"
                });
                duplicateHitsInSingleBatch.Rows.Add(new PageViewAppInsightsQueryResult
                {
                    Url = URL_DUP,
                    CustomProperties = new PageViewCustomProps
                    {
                        PageRequestId = Guid.NewGuid(),
                        SessionId = Guid.NewGuid().ToString()
                    },
                    AppInsightsTimestamp = DateTime.Now,
                    Browser = "Whatevs",
                    DeviceModel = "Whoever",
                    Username = "bob",
                    ClientOS = "Win"
                });

                await Assert.ThrowsAsync<BatchSaveException>(async () =>
                    await duplicateHitsInSingleBatch.SaveToSQL(db, AnalyticsLogger.ConsoleOnlyTracer())
                );

                // Cleanup duplicate URLs
                await ImportDbHacks.CleanDuplicateUrls(db);

                // Save hits again after cleanup. Should succeed now
                await duplicateHitsInSingleBatch.SaveToSQL(db, AnalyticsLogger.ConsoleOnlyTracer());

                var hitsPostInsert = db.hits.Count();

                // Find the resources we created above to make sure they still exist (or not)
                var urlDup1Check = db.urls.FirstOrDefault(u => u.ID == urlDup1.ID);
                var urlDup2Check = db.urls.FirstOrDefault(u => u.ID == urlDup2.ID);
                Assert.IsNotNull(urlDup1Check, "URL 1 should still exist after cleanup");
                Assert.IsNull(urlDup2Check, "URL 2 should not exist after cleanup");

                var spEventCheck = db.sharepoint_events.FirstOrDefault(e => e.Event.Id == spEvent.Event.Id);
                Assert.IsNotNull(spEventCheck, "SharePoint event should exist after cleanup");

                var copilotEventCheck = db.CopilotEventMetadataFiles.FirstOrDefault(e => e.RelatedChat.Event.Id == copilotEvent.RelatedChat.Event.Id);
                Assert.IsNotNull(copilotEventCheck, "Copilot event should exist after cleanup");

                var pageCommentCheck = db.UrlComments.FirstOrDefault(c => c.ID == pageComment.ID);
                Assert.IsNotNull(pageCommentCheck, "Page comment should exist after cleanup");

                var pageLikeCheck = db.UrlLikes.FirstOrDefault(l => l.ID == pageLike.ID);
                Assert.IsNotNull(pageLikeCheck, "Page like should exist after cleanup");

                var pagePropValCheck = db.FileMetadataPropertyValues.FirstOrDefault(p => p.ID == pagePropVal.ID);
                Assert.IsNotNull(pagePropValCheck, "Page property value should exist after cleanup");

                Assert.IsTrue(hitsPostInsert == hitsPreInsert + duplicateHitsInSingleBatch.Rows.Count, 
                    "Hits count should increase by " + duplicateHitsInSingleBatch.Rows.Count + " after saving duplicate hits");
            }
        }
    }
}
