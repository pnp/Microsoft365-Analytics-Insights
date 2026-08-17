using Common.Entities;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Sql;

namespace Tests.UnitTests
{
    /// <summary>
    /// Regression tests for issue #165: a duplicate row in a dimension table the hits merge joins to
    /// (urls, sessions, page_titles, ...) used to fan a single page-view out into several candidate
    /// rows sharing one page_request_id. SELECT DISTINCT couldn't collapse them (the lookup ids
    /// differ), so the INSERT violated the unique IX_PageRequestID and aborted the whole day's hit
    /// import with a BatchSaveException. The merge now keeps exactly one row per page_request_id.
    /// </summary>
    [TestClass]
    public class HitImportFanoutTests
    {
        /// <summary>
        /// A duplicate urls.full_url (urls.full_url has only a NON-unique index, so customer DBs do
        /// accumulate duplicate URL lookups) must NOT abort the day's hit import. Before the fix this
        /// threw BatchSaveException ("Cannot insert duplicate key row ... 'IX_PageRequestID'"); after
        /// the fix the single page-view is imported as exactly one hit.
        /// </summary>
        [TestMethod]
        public async Task DuplicateDimensionRow_DoesNotAbortHitImport()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Ensure schema: the unique IX_PageRequestID must exist, otherwise a real duplicate
                // page_request_id wouldn't be rejected and this test couldn't detect the regression.
                await ImportDbHacks.CleanDuplicateHitsAndCreateIX_PageRequestID(db);

                var dupUrl = "http://whatever/" + DateTime.Now.Ticks;

                // Two url rows with the SAME full_url. urls.full_url is not unique, so the importer
                // can (and in customer tenants does) end up with duplicate URL lookups.
                db.urls.Add(new Url { FullUrl = dupUrl });
                db.urls.Add(new Url { FullUrl = dupUrl, MetadataLastRefreshed = DateTime.Now });
                await db.SaveChangesAsync();

                var hitsPreInsert = db.hits.Count();

                // A single page-view => a single page_request_id. The duplicate URL fans this one
                // staging row into two candidate hit rows that share the same page_request_id during
                // the merge - which used to break the unique index and abort the import.
                var pageRequestId = Guid.NewGuid();
                var pageViews = new PageViewCollection();
                pageViews.Rows.Add(new PageViewAppInsightsQueryResult
                {
                    Url = dupUrl,
                    CustomProperties = new PageViewCustomProps
                    {
                        PageRequestId = pageRequestId,
                        SessionId = Guid.NewGuid().ToString()
                    },
                    AppInsightsTimestamp = DateTime.Now,
                    Browser = "Whatevs",
                    DeviceModel = "Whoever",
                    Username = "bob",
                    ClientOS = "Win"
                });

                // Must NOT throw, and must import the page-view exactly once despite the duplicate URL.
                await pageViews.SaveToSQL(db, AnalyticsLogger.ConsoleOnlyTracer());

                Assert.AreEqual(1, db.hits.Count(h => h.page_request_id == pageRequestId),
                    "The page-view should be imported as exactly one hit despite the duplicate URL lookup.");
                Assert.AreEqual(hitsPreInsert + 1, db.hits.Count(),
                    "Exactly one new hit row should have been inserted.");
            }
        }
    }
}
