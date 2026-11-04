using Common.Entities;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The audit import's SharePoint web lookup: one read of <c>webs</c> and <c>sites</c> per import cycle, shared by every
    /// save batch, answering with the whole-path-segment rules of #819.
    /// </summary>
    [TestClass]
    public class SharePointWebIndexTests
    {
        private const string Root = "https://contoso.sharepoint.com";
        private const string MySite = "https://contoso-my.sharepoint.com";

        #region The lookup answers as GetClosest does

        /// <summary>
        /// The dictionary walk must find exactly what #819's linear scan finds, for every candidate shape and every boundary a
        /// caller passes - whole segments, any case, trailing slashes, query strings, managed paths, non-Latin paths.
        /// </summary>
        [TestMethod]
        public void UrlPrefixIndex_FindsWhatGetClosestFinds()
        {
            var candidates = new List<Url>();
            var index = new UrlPrefixIndex<Url>();
            foreach (var candidate in new[]
            {
                Root, Root + "/sites/hr", Root + "/sites/hr/team/", Root + "/sites/hr/team/projects", Root + "/sites/finance/",
                Root + "/teams/sales", Root + "/Sites/Καλημέρα", Root + "/sites/hr?web=1", "https://server/SITE1", "https://server/site1/sub1",
                MySite, MySite + "/personal/alex_contoso_com", MySite + "/personal/alex_contoso_com/Documents", "https://contoso.sharepoint.com.example",
            })
            {
                var url = new Url { FullUrl = candidate };
                candidates.Add(url);
                Assert.IsTrue(index.TryAdd(candidate, url), candidate);
            }

            var lookups = new[]
            {
                Root, Root + "/", Root + "/Shared Documents/a.docx", Root + "/sites/hr", Root + "/SITES/HR/", Root + "/sites/hrdocs/a.docx",
                Root + "/sites/hr/team/Shared Documents/Καλημέρα κόσμε.docx", Root + "/sites/hr/teamwork", Root + "/sites/hr/team",
                Root + "/sites/hr/team/projects/x?y=1/2#z", Root + "/sites/finance", Root + "/sites/finance//a", Root + "/teams/sales/General",
                Root + "/sites/Καλημέρα/Shared Documents", Root + "/sites/hr?web=1", Root + "/sites/hr?web=1/a", Root + "/sites/hr#top",
                "https://server/site1/doc.aspx", "https://server/site1/sub1/doc.aspx", "https://server/site3/doc.aspx",
                MySite + "/personal/alex_contoso_com/Documents/a.docx", MySite + "/personal/sam_contoso_com/Documents/a.docx",
                "https://contoso.sharepoint.com.example/sites/hr", "https://other.example/", "/", "//", "relative/path", "https:",
            };

            foreach (var lookup in lookups)
            {
                foreach (var shortestMatch in new[] { 0, 1, SPWebResolver.SiteCollectionRootLength(lookup), SPWebResolver.SiteCollectionRootLength(UrlPrefixIndex<Url>.KeyFor(lookup)), lookup.Length })
                {
                    var expected = candidates.GetClosest(lookup, shortestMatch);
                    index.TryGetClosest(lookup, shortestMatch, out var actual);
                    Assert.AreSame(expected, actual, $"{lookup} (shortest {shortestMatch}): expected {expected?.FullUrl ?? "none"}, found {actual?.FullUrl ?? "none"}");
                }
            }
        }

        [TestMethod]
        public void UrlPrefixIndex_ExactMatchIgnoresCaseAndTrailingSlashes_AndTheFirstRowForAUrlKeepsIt()
        {
            var index = new UrlPrefixIndex<int>();
            Assert.IsTrue(index.TryAdd(Root + "/sites/hr/", 1));
            Assert.IsFalse(index.TryAdd(Root + "/SITES/HR", 2), "The same address in another case is the same key.");

            Assert.IsTrue(index.TryGetExact(Root + "/Sites/Hr//", out var found));
            Assert.AreEqual(1, found);
            Assert.IsFalse(index.TryGetExact(Root + "/sites/hrdocs", out _));
        }

        #endregion

        #region The shared index

        [TestMethod]
        public async Task SharedIndex_ReadsEverythingOnce_ThenOnlyRowsAddedSince_AndEverythingAgainAfterAFailedSave()
        {
            var source = new ListRowSource();
            source.AddSite(10, Root + "/sites/hr/");
            source.AddWeb(20, 10, Root + "/sites/hr");
            var index = new SharePointWebIndex();

            await index.RefreshAsync(source);
            CollectionAssert.AreEqual(new[] { 0 }, source.WebReads);
            CollectionAssert.AreEqual(new[] { 0 }, source.SiteReads);

            source.AddSite(11, Root + "/sites/finance/");
            await index.RefreshAsync(source);
            CollectionAssert.AreEqual(new[] { 0, 20 }, source.WebReads, "Only webs above the highest id already read.");
            CollectionAssert.AreEqual(new[] { 0, 10 }, source.SiteReads);
            Assert.IsTrue(index.TryGetClosestSite(Root + "/sites/finance/Shared Documents", 0, out var finance));
            Assert.AreEqual(11, finance.Id, "A site added since the first read is found.");
            Assert.AreEqual(1, index.FullLoads);

            index.Invalidate();
            await index.RefreshAsync(source);
            CollectionAssert.AreEqual(new[] { 0, 20, 0 }, source.WebReads, "After a failed save, everything is read again.");
            Assert.AreEqual(2, index.FullLoads);
            Assert.AreEqual(2, index.SiteCount);
        }

        /// <summary>
        /// Several save batches at once: the refresh and the lookups share the index without corrupting it or losing a row.
        /// </summary>
        [TestMethod]
        public async Task SharedIndex_ConcurrentBatches_RefreshAndLookUpSafely()
        {
            var source = new ListRowSource();
            var index = new SharePointWebIndex();
            const int Writers = 8, SitesPerWriter = 250;

            var batches = Enumerable.Range(0, Writers).Select(writer => Task.Run(async () =>
            {
                for (var i = 0; i < SitesPerWriter; i++)
                {
                    var url = $"{MySite}/personal/user{writer}_{i}_contoso_com";
                    var id = source.AddSiteWithNextId(url);
                    await index.RefreshAsync(source);
                    Assert.IsTrue(index.TryGetClosestSite(url + "/Documents/a.docx", SPWebResolver.SiteCollectionRootLength(url + "/Documents/a.docx"), out var site),
                        "A batch finds the site that was added before it refreshed.");
                    Assert.AreEqual(id, site.Id);
                }
            })).ToArray();
            await Task.WhenAll(batches);

            Assert.AreEqual(Writers * SitesPerWriter, index.SiteCount);
            Assert.AreEqual(1, index.FullLoads);
        }

        #endregion

        #region Batches against SQL

        /// <summary>
        /// Two save batches, each with its own context, sharing one index: a web or site already in SQL is attached as an
        /// unchanged row - never inserted twice, never updated - a new web joins the nearest site collection in SQL, and the
        /// second batch sees the first batch's webs and the site its own staging merge has just inserted without reading
        /// every row again.
        /// </summary>
        [TestMethod]
        public async Task SharedIndex_AcrossBatches_AttachesRowsInSql_AndInsertsOnlyNewWebs()
        {
            using (var scratch = CreateWebsAndSitesDatabase("WebIndexBatches"))
            {
                // As the staging merge writes them: each audit SiteUrl as it came, trailing slash included.
                scratch.Execute($"INSERT INTO dbo.sites (url_base) VALUES (N'{Root}/sites/hr/'), (N'{MySite}/personal/alex_contoso_com/');");
                var hrSiteId = Convert.ToInt32(scratch.Scalar($"SELECT id FROM dbo.sites WHERE url_base = N'{Root}/sites/hr/'"));
                var index = new SharePointWebIndex();

                using (var db = new AnalyticsEntitiesContext(scratch.ConnectionString, true, false))
                {
                    db.Configuration.AutoDetectChangesEnabled = false;
                    var lookups = new SharePointLookupManager(db, index);

                    var hr = await lookups.GetWebOrCreateWebPlusSite(Root + "/sites/hr/", true);
                    var team = await lookups.GetWebOrCreateWebPlusSite(Root + "/sites/hr/team/", true);
                    var oneDrive = await lookups.GetWebOrCreateWebPlusSite(MySite + "/personal/alex_contoso_com/", true);

                    Assert.AreEqual(hrSiteId, hr.site.ID, "A new web joins the site collection the staging merge inserted.");
                    Assert.AreSame(hr.site, team.site, "A subsite joins its parent's site collection, as one object.");
                    Assert.AreEqual(EntityState.Unchanged, db.Entry(hr.site).State, "A site in SQL is attached, not added.");
                    Assert.AreNotSame(hr.site, oneDrive.site);

                    SaveAsTheAuditImportDoes(db, hr, team, oneDrive);
                }

                Assert.AreEqual(3, ScalarInt(scratch, "SELECT COUNT(*) FROM dbo.webs"));
                Assert.AreEqual(2, ScalarInt(scratch, "SELECT COUNT(*) FROM dbo.sites"), "No site collection was inserted twice.");
                Assert.AreEqual(2, ScalarInt(scratch, $"SELECT COUNT(*) FROM dbo.webs WHERE site_id = {hrSiteId}"));

                // The next batch's staging merge inserts the site collection of a SiteUrl it has not seen before.
                scratch.Execute($"INSERT INTO dbo.sites (url_base) VALUES (N'{Root}/sites/finance/');");
                var financeSiteId = ScalarInt(scratch, $"SELECT id FROM dbo.sites WHERE url_base = N'{Root}/sites/finance/'");

                using (var db = new AnalyticsEntitiesContext(scratch.ConnectionString, true, false))
                {
                    db.Configuration.AutoDetectChangesEnabled = false;
                    var lookups = new SharePointLookupManager(db, index);

                    var hr = await lookups.GetWebOrCreateWebPlusSite(Root + "/SITES/HR", true);
                    var finance = await lookups.GetWebOrCreateWebPlusSite(Root + "/sites/finance/", true);
                    var file = await lookups.GetWebOrCreateWebPlusSite(Root + "/sites/hr/team/Shared Documents/Καλημέρα κόσμε.docx", false);

                    Assert.AreNotEqual(0, hr.ID, "The web the first batch saved is found.");
                    Assert.AreEqual(EntityState.Unchanged, db.Entry(hr).State);
                    Assert.AreEqual(financeSiteId, finance.site.ID, "The site this batch's merge inserted is found without a full read.");
                    Assert.AreEqual(Root + "/sites/hr/team", file.url_base, "A file is in its subsite's web.");
                    Assert.AreSame(file, await lookups.GetWebOrCreateWebPlusSite(Root + "/sites/hr/team", true), "One object per web in a batch.");

                    SaveAsTheAuditImportDoes(db, hr, finance, file);
                }

                Assert.AreEqual(4, ScalarInt(scratch, "SELECT COUNT(*) FROM dbo.webs"), "Only the finance web was new.");
                Assert.AreEqual(1, ScalarInt(scratch, $"SELECT COUNT(*) FROM dbo.webs WHERE site_id = {financeSiteId}"));
                Assert.AreEqual(1, index.FullLoads, "The second batch read only the rows added since the first.");
            }
        }

        /// <summary>
        /// A row another writer commits late, with a lower id than one already read, is the one thing an incremental read
        /// misses. A batch that then inserts it again fails on the unique index; the failed save makes the retry read every
        /// row, and the retry finds it.
        /// </summary>
        [TestMethod]
        public async Task SharedIndex_ARowCommittedBelowTheHighestIdRead_IsFoundAfterAFailedSave()
        {
            using (var scratch = CreateWebsAndSitesDatabase("WebIndexGap"))
            {
                scratch.Execute("DBCC CHECKIDENT ('dbo.sites', RESEED, 100); DBCC CHECKIDENT ('dbo.webs', RESEED, 100);");
                scratch.Execute($"INSERT INTO dbo.sites (url_base) VALUES (N'{Root}/sites/hr/'); INSERT INTO dbo.webs (url_base, site_id) SELECT N'{Root}/sites/hr', id FROM dbo.sites;");
                var index = new SharePointWebIndex();
                using (var db = new AnalyticsEntitiesContext(scratch.ConnectionString, true, false))
                {
                    await index.RefreshAsync(db);
                }
                Assert.AreEqual(1, index.WebCount);

                scratch.Execute(
                    $"SET IDENTITY_INSERT dbo.sites ON; INSERT INTO dbo.sites (id, url_base) VALUES (5, N'{Root}/sites/late/'); SET IDENTITY_INSERT dbo.sites OFF;" +
                    $"SET IDENTITY_INSERT dbo.webs ON; INSERT INTO dbo.webs (id, url_base, site_id) VALUES (5, N'{Root}/sites/late', 5); SET IDENTITY_INSERT dbo.webs OFF;");

                using (var db = new AnalyticsEntitiesContext(scratch.ConnectionString, true, false))
                {
                    db.Configuration.AutoDetectChangesEnabled = false;
                    var lookups = new SharePointLookupManager(db, index);
                    var late = await lookups.GetWebOrCreateWebPlusSite(Root + "/sites/late/", true);
                    Assert.AreEqual(0, late.ID, "The incremental read missed the late row, so the batch thinks the web is new.");

                    await Assert.ThrowsExceptionAsync<System.Data.Entity.Infrastructure.DbUpdateException>(() =>
                    {
                        SaveAsTheAuditImportDoes(db, late);
                        return Task.CompletedTask;
                    }, "The unique index refuses the duplicate.");
                    lookups.ForgetWebIndexAfterFailedSave();
                }

                using (var db = new AnalyticsEntitiesContext(scratch.ConnectionString, true, false))
                {
                    var lookups = new SharePointLookupManager(db, index);
                    var late = await lookups.GetWebOrCreateWebPlusSite(Root + "/sites/late/", true);
                    Assert.AreEqual(5, late.ID, "The retry read every row again.");
                    Assert.AreEqual(2, index.FullLoads);
                }
            }
        }

        #endregion

        #region Opt-in scale measurement

        /// <summary>
        /// Opt-in (<c>SP_WEB_INDEX_SCALE=1</c>): one save batch's web lookups against <c>webs</c> and <c>sites</c> at the
        /// 200,000-user baseline - a site collection and a web per OneDrive, plus SharePoint sites - the way every batch used
        /// to do them (read every row, tracked; scan every site for each new address) and the way they are done now. Nothing
        /// is asserted about time; the timings go to the test output.
        /// </summary>
        [TestMethod]
        [TestCategory("Performance")]
        public async Task WebLookups_PerBatch_AtSyntheticScale()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("SP_WEB_INDEX_SCALE"), "1", StringComparison.Ordinal))
            {
                Assert.Inconclusive("Set SP_WEB_INDEX_SCALE=1 (and optionally SP_WEB_INDEX_SCALE_ONEDRIVES) to run the synthetic-scale measurement.");
            }

            var oneDrives = int.TryParse(Environment.GetEnvironmentVariable("SP_WEB_INDEX_SCALE_ONEDRIVES"), out var configured) && configured > 0
                ? configured
                : 200000;
            var sharePointSites = oneDrives / 10;
            const int Batches = 5, ExistingPerBatch = 400, NewPerBatch = 100, FilesPerBatch = 100;

            using (var scratch = CreateWebsAndSitesDatabase("WebIndexScale"))
            {
                var seed = Stopwatch.StartNew();
                scratch.Execute($@"
WITH n AS (SELECT TOP ({oneDrives + sharePointSites}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
           FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c)
INSERT INTO dbo.sites (url_base)
SELECT CASE WHEN i <= {oneDrives} THEN N'{MySite}/personal/user' + CAST(i AS nvarchar(10)) + N'_contoso_com/'
            ELSE N'{Root}/sites/site' + CAST(i AS nvarchar(10)) + N'/' END
FROM n;
INSERT INTO dbo.webs (url_base, site_id)
SELECT LEFT(url_base, LEN(url_base) - 1), id FROM dbo.sites;");
                Console.WriteLine("Seeded {0:N0} site collections and {0:N0} webs in {1:N0} ms.", oneDrives + sharePointSites, seed.ElapsedMilliseconds);

                var random = new Random(42);
                var batches = Enumerable.Range(0, Batches).Select(b => new
                {
                    Existing = Enumerable.Range(0, ExistingPerBatch).Select(_ => $"{MySite}/personal/user{random.Next(1, oneDrives + 1)}_contoso_com/").ToList(),
                    New = Enumerable.Range(0, NewPerBatch).Select(i => $"{Root}/sites/new{b}_{i}/").ToList(),
                    Files = Enumerable.Range(0, FilesPerBatch).Select(_ => $"{MySite}/personal/user{random.Next(1, oneDrives + 1)}_contoso_com/Documents/a.docx").ToList(),
                }).ToList();

                // Before: every batch read every row into its context, tracked, and scanned every site for each new address.
                var before = new List<long>();
                foreach (var batch in batches)
                {
                    var watch = Stopwatch.StartNew();
                    using (var db = new AnalyticsEntitiesContext(scratch.ConnectionString, true, false))
                    {
                        db.Configuration.AutoDetectChangesEnabled = false;
                        var legacy = new LegacyResolver();
                        await legacy.PopulateCaches(db);
                        foreach (var url in batch.Existing.Concat(batch.New)) legacy.GetWeb(url, true);
                        foreach (var url in batch.Files) legacy.GetWeb(url, false);
                        db.ChangeTracker.DetectChanges();
                    }
                    before.Add(watch.ElapsedMilliseconds);
                }

                // After: one index for the cycle; each batch reads only rows added since, and probes per path segment.
                var after = new List<long>();
                var index = new SharePointWebIndex();
                foreach (var batch in batches)
                {
                    var watch = Stopwatch.StartNew();
                    using (var db = new AnalyticsEntitiesContext(scratch.ConnectionString, true, false))
                    {
                        db.Configuration.AutoDetectChangesEnabled = false;
                        var lookups = new SharePointLookupManager(db, index);
                        foreach (var url in batch.Existing.Concat(batch.New)) await lookups.GetWebOrCreateWebPlusSite(url, true);
                        foreach (var url in batch.Files) Assert.IsNotNull(await lookups.GetWebOrCreateWebPlusSite(url, false));
                        db.ChangeTracker.DetectChanges();
                    }
                    after.Add(watch.ElapsedMilliseconds);
                }

                Console.WriteLine("Per batch ({0:N0} distinct site URLs, {1:N0} new; {2:N0} file URLs), ms:", ExistingPerBatch + NewPerBatch, NewPerBatch, FilesPerBatch);
                Console.WriteLine("  before: {0}  (total {1:N0})", string.Join(", ", before.Select(ms => ms.ToString("N0", CultureInfo.InvariantCulture))), before.Sum());
                Console.WriteLine("  after:  {0}  (total {1:N0}; the first batch includes the cycle's one full read)", string.Join(", ", after.Select(ms => ms.ToString("N0", CultureInfo.InvariantCulture))), after.Sum());
                Console.WriteLine("  index: {0:N0} webs, {1:N0} site collections, {2} full read(s).", index.WebCount, index.SiteCount, index.FullLoads);
                Assert.AreEqual(1, index.FullLoads);
            }
        }

        /// <summary>The lookup as every batch did it before the shared index, for the scale measurement's "before".</summary>
        private sealed class LegacyResolver
        {
            private readonly Dictionary<string, Site> _sites = new Dictionary<string, Site>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, Web> _webs = new Dictionary<string, Web>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, Web> _containing = new Dictionary<string, Web>(StringComparer.OrdinalIgnoreCase);

            public async Task PopulateCaches(AnalyticsEntitiesContext db)
            {
                foreach (var web in await db.webs.ToListAsync()) _webs[StringUtils.RemoveTrailingSlash(web.url_base)] = web;
                foreach (var site in await db.sites.ToListAsync()) _sites[StringUtils.RemoveTrailingSlash(site.UrlBase)] = site;
            }

            public Web GetWeb(string url, bool create)
            {
                var key = StringUtils.RemoveTrailingSlash(url);
                var root = SPWebResolver.SiteCollectionRootLength(key);
                if (!create)
                {
                    if (!_containing.TryGetValue(key, out var containing))
                    {
                        containing = _webs.Values.GetClosest(key, root);
                        _containing.Add(key, containing);
                    }
                    return containing;
                }
                if (!_webs.TryGetValue(key, out var web))
                {
                    var site = _sites.Values.GetClosest(key, root) ?? new Site { UrlBase = url };
                    web = new Web { url_base = key.ToLowerInvariant(), site = site };
                    _webs.Add(key, web);
                    _containing.Clear();
                }
                return web;
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// <c>sites</c> and <c>webs</c> as the migrations create them - created before the context first opens the database,
        /// or EF's initializer takes the empty database for one to create.
        /// </summary>
        private static ScratchDatabase CreateWebsAndSitesDatabase(string purpose)
        {
            var scratch = ScratchDatabase.Create(purpose);
            scratch.Execute(@"
CREATE TABLE dbo.sites (id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_sites PRIMARY KEY, url_base nvarchar(500) NULL, site_id nvarchar(100) NULL);
CREATE UNIQUE INDEX IX_url_base ON dbo.sites (url_base);
CREATE TABLE dbo.webs (id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_webs PRIMARY KEY, url_base nvarchar(500) NULL, title nvarchar(max) NULL,
    site_id int NOT NULL CONSTRAINT FK_webs_sites FOREIGN KEY REFERENCES dbo.sites (id) ON DELETE CASCADE);
CREATE UNIQUE INDEX IX_url_base ON dbo.webs (url_base);
CREATE INDEX IX_site_id ON dbo.webs (site_id);");
            return scratch;
        }

        /// <summary>
        /// The audit import reaches a new web through <c>event_meta_sharepoint.related_web</c> when it detects changes; these
        /// tests have no event table, so a web the context does not track yet is added the same way.
        /// </summary>
        private static void SaveAsTheAuditImportDoes(AnalyticsEntitiesContext db, params Web[] webs)
        {
            foreach (var web in webs.Where(w => db.Entry(w).State == EntityState.Detached))
            {
                db.webs.Add(web);
            }
            db.ChangeTracker.DetectChanges();
            db.SaveChanges();
        }

        private static int ScalarInt(ScratchDatabase scratch, string sql) => Convert.ToInt32(scratch.Scalar(sql), CultureInfo.InvariantCulture);

        private sealed class ListRowSource : SharePointWebIndex.IRowSource
        {
            private readonly object _sync = new object();
            private readonly List<SharePointWebIndex.WebRow> _webs = new List<SharePointWebIndex.WebRow>();
            private readonly List<SharePointWebIndex.SiteRow> _sites = new List<SharePointWebIndex.SiteRow>();
            private int _nextSiteId = 1;

            public List<int> WebReads { get; } = new List<int>();
            public List<int> SiteReads { get; } = new List<int>();

            public void AddWeb(int id, int siteId, string url)
            {
                lock (_sync) _webs.Add(new SharePointWebIndex.WebRow { Id = id, SiteId = siteId, Url = url });
            }

            public void AddSite(int id, string url)
            {
                lock (_sync) _sites.Add(new SharePointWebIndex.SiteRow { Id = id, Url = url });
            }

            public int AddSiteWithNextId(string url)
            {
                lock (_sync)
                {
                    var id = _nextSiteId++;
                    _sites.Add(new SharePointWebIndex.SiteRow { Id = id, Url = url });
                    return id;
                }
            }

            public Task<List<SharePointWebIndex.WebRow>> WebsAfterAsync(int afterId)
            {
                lock (_sync)
                {
                    WebReads.Add(afterId);
                    return Task.FromResult(_webs.Where(w => w.Id > afterId).OrderBy(w => w.Id).ToList());
                }
            }

            public Task<List<SharePointWebIndex.SiteRow>> SitesAfterAsync(int afterId)
            {
                lock (_sync)
                {
                    SiteReads.Add(afterId);
                    return Task.FromResult(_sites.Where(s => s.Id > afterId).OrderBy(s => s.Id).ToList());
                }
            }
        }

        #endregion
    }
}
