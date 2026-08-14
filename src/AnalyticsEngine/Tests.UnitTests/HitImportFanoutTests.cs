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
    /// (sessions, page_titles, ...) used to fan a single page-view out into several candidate
    /// rows sharing one page_request_id. SELECT DISTINCT couldn't collapse them (the lookup ids
    /// differ), so the INSERT violated the unique IX_PageRequestID and aborted the whole day's hit
    /// import with a BatchSaveException. The merge now keeps exactly one row per page_request_id.
    /// </summary>
    [TestClass]
    public class HitImportFanoutTests
    {
        /// <summary>
        /// Duplicate sessions remain possible because ai_session_id is intentionally non-unique.
        /// They must not fan one page-view into duplicate hits or abort the day's import.
        /// </summary>
        [TestMethod]
        public async Task DuplicateDimensionRow_DoesNotAbortHitImport()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Ensure schema: the unique IX_PageRequestID must exist, otherwise a real duplicate
                // page_request_id wouldn't be rejected and this test couldn't detect the regression.
                await ImportDbHacks.CleanDuplicateHitsAndCreateIX_PageRequestID(db);

                var suffix = DateTime.Now.Ticks;
                var userName = "fanout-" + suffix + "@contoso.com";
                var sessionId = Guid.NewGuid().ToString();
                var user = new User { UserPrincipalName = userName };
                db.sessions.Add(new UserSession { ai_session_id = sessionId, user = user });
                db.sessions.Add(new UserSession { ai_session_id = sessionId, user = user });
                await db.SaveChangesAsync();

                var hitsPreInsert = db.hits.Count();

                // A single page-view => a single page_request_id. The duplicate session fans this one
                // staging row into two candidate hit rows that share the same page_request_id during
                // the merge - which used to break the unique index and abort the import.
                var pageRequestId = Guid.NewGuid();
                var pageViews = new PageViewCollection();
                pageViews.Rows.Add(new PageViewAppInsightsQueryResult
                {
                    Url = "https://contoso.sharepoint.com/sites/fanout-" + suffix,
                    CustomProperties = new PageViewCustomProps
                    {
                        PageRequestId = pageRequestId,
                        SessionId = sessionId
                    },
                    AppInsightsTimestamp = DateTime.Now,
                    Browser = "Whatevs",
                    DeviceModel = "Whoever",
                    Username = userName,
                    ClientOS = "Win"
                });

                // Must NOT throw, and must import the page-view exactly once despite duplicate sessions.
                await pageViews.SaveToSQL(db, AnalyticsLogger.ConsoleOnlyTracer());

                Assert.AreEqual(1, db.hits.Count(h => h.page_request_id == pageRequestId),
                    "The page-view should be imported as exactly one hit despite duplicate sessions.");
                Assert.AreEqual(hitsPreInsert + 1, db.hits.Count(),
                    "Exactly one new hit row should have been inserted.");
            }
        }
    }
}
