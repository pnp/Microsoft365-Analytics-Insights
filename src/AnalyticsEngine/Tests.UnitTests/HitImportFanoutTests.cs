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
    /// <remarks>
    /// This used to fan out via a duplicate <c>urls.full_url</c>. Issue #167 made that index UNIQUE, so a
    /// duplicate URL can no longer be created - but the #165 defence is still needed, because the merge
    /// joins several dimensions and <c>sessions.ai_session_id</c> is deliberately left non-unique by
    /// <c>ImportDbHacks</c>. The fan-out is therefore driven from a duplicate session instead, which keeps
    /// the regression covered against a dimension that can still genuinely duplicate.
    /// </remarks>
    [TestClass]
    public class HitImportFanoutTests
    {
        /// <summary>
        /// A duplicate dimension row must NOT abort the day's hit import. Before the fix this threw
        /// BatchSaveException ("Cannot insert duplicate key row ... 'IX_PageRequestID'"); after the fix the
        /// single page-view is imported as exactly one hit.
        /// </summary>
        [TestMethod]
        public async Task DuplicateDimensionRow_DoesNotAbortHitImport()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Ensure schema: the unique IX_PageRequestID must exist, otherwise a real duplicate
                // page_request_id wouldn't be rejected and this test couldn't detect the regression.
                await ImportDbHacks.CleanDuplicateHitsAndCreateIX_PageRequestID(db);

                var url = "http://whatever/" + DateTime.Now.Ticks;
                var dupSessionId = Guid.NewGuid().ToString();

                var user = new User { UserPrincipalName = "fanout" + DateTime.Now.Ticks + "@example.com" };
                db.users.Add(user);
                await db.SaveChangesAsync();

                // Two session rows with the SAME ai_session_id. That column is intentionally NOT unique,
                // so the importer can (and in customer tenants does) end up with duplicate session lookups.
                db.sessions.Add(new UserSession { ai_session_id = dupSessionId, user = user });
                db.sessions.Add(new UserSession { ai_session_id = dupSessionId, user = user });
                await db.SaveChangesAsync();

                Assert.AreEqual(2, db.sessions.Count(s => s.ai_session_id == dupSessionId),
                    "The fan-out this test exercises depends on sessions.ai_session_id being non-unique.");

                var hitsPreInsert = db.hits.Count();

                // A single page-view => a single page_request_id. The duplicate session fans this one
                // staging row into two candidate hit rows that share the same page_request_id during
                // the merge - which used to break the unique index and abort the import.
                var pageRequestId = Guid.NewGuid();
                var pageViews = new PageViewCollection();
                pageViews.Rows.Add(new PageViewAppInsightsQueryResult
                {
                    Url = url,
                    CustomProperties = new PageViewCustomProps
                    {
                        PageRequestId = pageRequestId,
                        SessionId = dupSessionId
                    },
                    AppInsightsTimestamp = DateTime.Now,
                    Browser = "Whatevs",
                    DeviceModel = "Whoever",
                    Username = user.UserPrincipalName,
                    ClientOS = "Win"
                });

                // Must NOT throw, and must import the page-view exactly once despite the duplicate lookup.
                await pageViews.SaveToSQL(db, AnalyticsLogger.ConsoleOnlyTracer());

                Assert.AreEqual(1, db.hits.Count(h => h.page_request_id == pageRequestId),
                    "The page-view should be imported as exactly one hit despite the duplicate dimension lookup.");
                Assert.AreEqual(hitsPreInsert + 1, db.hits.Count(),
                    "Exactly one new hit row should have been inserted.");
            }
        }
    }
}
