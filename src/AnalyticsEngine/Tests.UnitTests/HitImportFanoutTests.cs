using Common.Entities;
using Common.Entities.Migrations;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Entity;
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
    /// migration <c>RetireImportDbHacks</c>. The fan-out is therefore driven from a duplicate session
    /// instead, which keeps the regression covered against a dimension that can still genuinely duplicate.
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
                // It is created by migration RetireImportDbHacks, which is idempotent, so running its SQL
                // here is a metadata-only no-op once the migration has been applied.
                await db.Database.ExecuteSqlCommandAsync(
                    TransactionalBehavior.DoNotEnsureTransaction, RetireImportDbHacks.Up_Sql);

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

        /// <summary>
        /// The tracker session id is a browser session cookie, so two people using the same browser profile share
        /// it. Sessions used to be created only for tracker ids never seen before, while hits and searches resolve
        /// their session by (tracker id, user) - so once the first person's session existed, everything the second
        /// person did in that browser session was silently dropped.
        /// </summary>
        [TestMethod]
        public async Task SecondPersonOnAnExistingTrackerSession_KeepsTheirHitsAndSearches()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var token = Guid.NewGuid().ToString("N");
                var firstPerson = new User { UserPrincipalName = $"first-{token}@contoso.com" };
                var secondPerson = new User { UserPrincipalName = $"second-{token}@contoso.com" };
                db.users.Add(firstPerson);
                db.users.Add(secondPerson);
                var sharedTrackerSession = Guid.NewGuid().ToString();
                db.sessions.Add(new UserSession { ai_session_id = sharedTrackerSession, user = firstPerson });
                await db.SaveChangesAsync();

                var pageRequestId = Guid.NewGuid();
                var pageViews = new PageViewCollection();
                pageViews.Rows.Add(new PageViewAppInsightsQueryResult
                {
                    Url = "https://contoso.sharepoint.com/sites/example/" + token + "/Καλημέρα.aspx",
                    CustomProperties = new PageViewCustomProps { PageRequestId = pageRequestId, SessionId = sharedTrackerSession },
                    AppInsightsTimestamp = DateTime.UtcNow,
                    Browser = "Edge",
                    DeviceModel = "PC",
                    Username = secondPerson.UserPrincipalName,
                    ClientOS = "Windows"
                });

                await pageViews.SaveToSQL(db, AnalyticsLogger.ConsoleOnlyTracer());

                var hitOwner = await db.hits.Where(h => h.page_request_id == pageRequestId).Select(h => (int?)h.session.user.ID).SingleOrDefaultAsync();
                Assert.AreEqual(secondPerson.ID, hitOwner, "The second person's page view must be stored, against their own session.");
                Assert.AreEqual(2, await db.sessions.CountAsync(s => s.ai_session_id == sharedTrackerSession));

                var searchTerm = "Καλημέρα κόσμε " + token;
                var search = new SearchEventAppInsightsQueryResult { AppInsightsTimestamp = DateTime.UtcNow, Username = secondPerson.UserPrincipalName };
                search.CustomProperties.SearchText = searchTerm;
                search.CustomProperties.SessionId = sharedTrackerSession;
                var searches = new CustomEventsResultCollection();
                searches.Rows.Add(search);

                await searches.SaveSearchesToSQL(AnalyticsLogger.ConsoleOnlyTracer(), db);

                var searchOwners = await db.searches.Where(s => s.search_term.search_term == searchTerm).Select(s => s.session.user.ID).ToListAsync();
                CollectionAssert.AreEqual(new[] { secondPerson.ID }, searchOwners, "The second person's search must be stored, once, against their own session.");

                // Saving the same page view again neither duplicates the session nor the hit.
                await pageViews.SaveToSQL(db, AnalyticsLogger.ConsoleOnlyTracer());
                Assert.AreEqual(2, await db.sessions.CountAsync(s => s.ai_session_id == sharedTrackerSession));
                Assert.AreEqual(1, await db.hits.CountAsync(h => h.page_request_id == pageRequestId));
            }
        }

        /// <summary>
        /// dbo.sessions.ai_session_id is varchar(50). A tracker id longer than that used to reach the merge, where the
        /// session insert failed on truncation and took every other page view in the batch down with it.
        /// </summary>
        [TestMethod]
        public async Task OverLongTrackerSessionId_IsSkippedWithoutFailingTheRestOfTheBatch()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var token = Guid.NewGuid().ToString("N");
                var normal = Guid.NewGuid();
                var overLong = Guid.NewGuid();
                var pageViews = new PageViewCollection();
                foreach (var (pageRequestId, sessionId) in new[] { (normal, Guid.NewGuid().ToString()), (overLong, new string('a', 51)) })
                {
                    pageViews.Rows.Add(new PageViewAppInsightsQueryResult
                    {
                        Url = "https://contoso.sharepoint.com/sites/example/" + token + "/Καλημέρα.aspx",
                        CustomProperties = new PageViewCustomProps { PageRequestId = pageRequestId, SessionId = sessionId },
                        AppInsightsTimestamp = DateTime.UtcNow,
                        Browser = "Edge",
                        DeviceModel = "PC",
                        Username = $"person-{token}@contoso.com",
                        ClientOS = "Windows"
                    });
                }

                await pageViews.SaveToSQL(db, AnalyticsLogger.ConsoleOnlyTracer());

                Assert.AreEqual(1, await db.hits.CountAsync(h => h.page_request_id == normal), "The rest of the batch is imported.");
                Assert.AreEqual(0, await db.hits.CountAsync(h => h.page_request_id == overLong));
            }
        }
    }
}
