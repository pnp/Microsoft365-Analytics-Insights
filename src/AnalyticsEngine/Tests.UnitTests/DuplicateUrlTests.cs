using Common.Entities;
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
        public async Task DupUrlTests()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Make sure we have the right DB schema
                ImportDbHacks.CleanDuplicateHitsAndCreateIX_PageRequestID();


                var URL_DUP = "http://whatever/" + DateTime.Now.Ticks;

                // Insert duplicate URLs
                var urlNeedingUpdate = new Url() { FullUrl = URL_DUP };
                var urlNotNeedingUpdate = new Url() { FullUrl = URL_DUP, MetadataLastRefreshed = DateTime.Now };
                db.urls.Add(urlNeedingUpdate);
                db.urls.Add(urlNotNeedingUpdate);
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

                var hitsPostInsert = db.hits.Count();

                // We should have only 1 extra hit as they both share same req ID
                Assert.IsTrue(hitsPreInsert == hitsPostInsert - 1);

            }
        }

    }
}
