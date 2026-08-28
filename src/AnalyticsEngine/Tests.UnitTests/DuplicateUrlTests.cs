using Common.Entities;
using Common.Entities.Entities;
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
    /// <summary>
    /// Asserts the invariant introduced by issue #167: <c>dbo.urls.full_url</c> is UNIQUE, so duplicate URL
    /// lookups can no longer be created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class previously inserted two <c>urls</c> rows with the same <c>full_url</c> and asserted that
    /// <c>ImportDbHacks.CleanDuplicateUrls</c> consolidated them. That premise is now false by design - the
    /// unique index rejects the second row - so the test has been turned around to prove the constraint
    /// rather than the clean-up.
    /// </para>
    /// <para>
    /// The de-duplication behaviour itself is not lost: it is covered far more thoroughly by
    /// <see cref="UniqueUrlsFullUrlIndexMigrationTests"/>, which runs the migration's real SQL from a
    /// pre-migration (non-unique) state, including reference repointing, collision pruning and non-ASCII
    /// URLs. <c>CleanDuplicateUrls</c> has no production caller and is now only a one-time safeguard for
    /// databases that predate the migration.
    /// </para>
    /// </remarks>
    [TestClass]
    public class DuplicateUrlTests
    {
        /// <summary>
        /// The index is UNIQUE, so a second row with the same full_url must not be created. Because it is
        /// built WITH (IGNORE_DUP_KEY = ON), SQL Server skips the duplicate instead of aborting the whole
        /// statement - which is the point: a concurrent check-then-insert race in the importer loses only
        /// the duplicate, not every other new URL in the same batch.
        /// </summary>
        [TestMethod]
        public async Task DuplicateUrl_CannotBeCreated()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var url = "http://whatever/" + DateTime.Now.Ticks;

                db.urls.Add(new Url { FullUrl = url });
                await db.SaveChangesAsync();

                Assert.AreEqual(1, db.urls.Count(u => u.FullUrl == url));

                // A second insert of the same URL, issued the way the importer's staging merge does (raw
                // SQL, several rows in one statement). The duplicate is skipped; the other row still lands.
                var brandNew = "http://whatever/other/" + DateTime.Now.Ticks;
                await db.Database.ExecuteSqlCommandAsync(
                    System.Data.Entity.TransactionalBehavior.DoNotEnsureTransaction,
                    @"INSERT INTO dbo.urls (full_url) SELECT @p0 UNION ALL SELECT @p1;", url, brandNew);

                Assert.AreEqual(1, db.urls.Count(u => u.FullUrl == url),
                    "The unique index must have prevented a second row for the same URL.");
                Assert.AreEqual(1, db.urls.Count(u => u.FullUrl == brandNew),
                    "IGNORE_DUP_KEY must let the rest of the statement succeed - otherwise a single racing "
                    + "duplicate would lose every other new URL in the same import batch.");
            }
        }

        /// <summary>
        /// The URL lookup is case-insensitive under the database collation, which is what the unique index
        /// enforces and what the staging merges already assume when they join on
        /// <c>urls.full_url = imports.url</c>.
        /// </summary>
        [TestMethod]
        public async Task UrlsDifferingOnlyByCase_AreTheSameUrl()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var url = "http://whatever/CaseTest" + DateTime.Now.Ticks;

                db.urls.Add(new Url { FullUrl = url });
                await db.SaveChangesAsync();

                await db.Database.ExecuteSqlCommandAsync(
                    System.Data.Entity.TransactionalBehavior.DoNotEnsureTransaction,
                    @"INSERT INTO dbo.urls (full_url) VALUES (@p0);", url.ToUpperInvariant());

                Assert.AreEqual(1, db.urls.Count(u => u.FullUrl == url),
                    "A case-only variant is the same URL under this collation, so it must not create a row.");
            }
        }

        /// <summary>
        /// The end-to-end consequence of #167 for the hits import: with duplicate URLs impossible, two
        /// page-views for the same URL import as two hits against ONE url row.
        /// </summary>
        [TestMethod]
        public async Task TwoPageViewsForOneUrl_ImportAgainstASingleUrlRow()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await ImportDbHacks.CleanDuplicateHitsAndCreateIX_PageRequestID(db);

                var url = "http://whatever/" + DateTime.Now.Ticks;
                var hitsPreInsert = db.hits.Count();

                var pageViews = new PageViewCollection();
                for (var i = 0; i < 2; i++)
                {
                    pageViews.Rows.Add(new PageViewAppInsightsQueryResult
                    {
                        Url = url,
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
                }

                await pageViews.SaveToSQL(db, AnalyticsLogger.ConsoleOnlyTracer());

                Assert.AreEqual(1, db.urls.Count(u => u.FullUrl == url),
                    "The importer must create exactly one url lookup for the URL.");
                Assert.AreEqual(hitsPreInsert + 2, db.hits.Count(),
                    "Both page-views should be imported.");
            }
        }
    }
}
