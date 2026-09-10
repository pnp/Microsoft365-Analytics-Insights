using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate;

namespace Tests.UnitTests
{
    /// <summary>
    /// The weekly SharePoint site-usage save loop, driven entirely through the
    /// <c>ISharePointSiteUsageStore</c> port extracted by issue #375. Zero SQL Server, zero Graph.
    ///
    /// <para>
    /// The same guarantees are also covered end-to-end against a real database in
    /// <c>GraphUsageReportImportTests</c>; these run without one, and additionally pin the two things a
    /// row-count assertion cannot see - that storage is read ONCE rather than once per site, and that a
    /// site row with no URL is never used as a key.
    /// </para>
    /// </summary>
    [TestClass]
    public class SharePointSiteUsageStoreTests
    {
        // 2024-02-25 is a Sunday; 2024-02-18 the Sunday before.
        private static readonly DateTime ThisSunday = new DateTime(2024, 2, 25);
        private static readonly DateTime LastSunday = new DateTime(2024, 2, 18);

        private static SharePointSitesWeeklyUsageReportLoader LoaderOver(InMemorySharePointSiteUsageStore store)
            => new SharePointSitesWeeklyUsageReportLoader(store, null, NullLogger.Instance, null);

        private static SharePointSiteUsageDetail Report(string url, DateTime refreshDate, int fileCount = 0)
            => new SharePointSiteUsageDetail { SiteUrl = url, FileCount = fileCount, ReportRefreshDate = refreshDate };

        [TestMethod]
        public async Task SharePointSitesUsage_SavesStaleAndNewSites_SkipsAlreadyCurrent_WithoutSql()
        {
            var store = new InMemorySharePointSiteUsageStore();
            var stale = store.SeedSite("https://contoso.sharepoint.com/sites/stale");
            var current = store.SeedSite("https://contoso.sharepoint.com/sites/current");
            store.SeedWeek(stale, LastSunday).SeedWeek(current, ThisSunday);

            var loader = LoaderOver(store);
            var saved = await loader.SaveLoadedReportsIfRefreshOnDay(DayOfWeek.Sunday, new List<SharePointSiteUsageDetail>
            {
                Report("https://contoso.sharepoint.com/sites/stale", ThisSunday, fileCount: 10),
                Report("https://contoso.sharepoint.com/sites/current", ThisSunday, fileCount: 20),
                Report("https://contoso.sharepoint.com/sites/brand-new", ThisSunday, fileCount: 30),
                // Saturday - outside the refresh day, so it must be ignored entirely.
                Report("https://contoso.sharepoint.com/sites/brand-new", new DateTime(2024, 2, 24), fileCount: 99),
            });

            Assert.AreEqual(2, saved, "The stale existing site and the brand-new site; not the already-current one, not the Saturday row.");
            Assert.AreEqual(1, store.CommitCount, "One commit for the whole run, not one per site.");

            // The stale site's new week must reuse the EXISTING site key, not create a second site row.
            var staleRows = store.WeeklyStats.Where(s => s.SiteId == stale.ID).ToList();
            Assert.AreEqual(2, staleRows.Count, "The stale site should now hold the old and the new week.");
            Assert.AreEqual(10, staleRows.Single(r => r.ForWeekEnding == ThisSunday).FileCount);

            Assert.AreEqual(1, store.WeeklyStats.Count(s => s.SiteId == current.ID),
                "A site already stored for this week must not get a duplicate row.");

            var newSite = store.Sites.SingleOrDefault(s => s.UrlBase == "https://contoso.sharepoint.com/sites/brand-new");
            Assert.IsNotNull(newSite, "An unknown site URL should create the site.");
            var newRows = store.WeeklyStats.Where(s => s.SiteId == newSite.ID).ToList();
            Assert.AreEqual(1, newRows.Count, "The Saturday row must not have produced a second week.");
            Assert.AreEqual(30, newRows[0].FileCount);
        }

        [TestMethod]
        public async Task SharePointSitesUsage_MatchesExistingSiteUrlCaseInsensitively_WithoutSql()
        {
            // Graph can return the site URL in a different case. SQL Server's collation is case-insensitive,
            // so treating the two as different sites would insert a duplicate the database then sees as one.
            var storedUrl = "https://contoso.sharepoint.com/sites/CaseTest";
            var store = new InMemorySharePointSiteUsageStore();
            var site = store.SeedSite(storedUrl);
            store.SeedWeek(site, LastSunday);

            var saved = await LoaderOver(store).SaveLoadedReportsIfRefreshOnDay(
                DayOfWeek.Sunday,
                new List<SharePointSiteUsageDetail> { Report(storedUrl.ToUpperInvariant(), ThisSunday) });

            Assert.AreEqual(1, saved);
            Assert.AreEqual(1, store.Sites.Count, "A case-differing report URL must reuse the existing site, not create a duplicate.");
            Assert.AreEqual(2, store.WeeklyStats.Count(s => s.SiteId == site.ID));
        }

        [TestMethod]
        public async Task SharePointSitesUsage_CaseDifferingStoredWeek_StillCountsAsAlreadyCurrent()
        {
            // The other half of case-insensitivity, and the half a "no duplicate site" assertion cannot
            // see: the LAST STORED WEEK lookup must match case-insensitively too, or the same week is
            // saved again on every run.
            var storedUrl = "https://contoso.sharepoint.com/sites/CaseTest";
            var store = new InMemorySharePointSiteUsageStore();
            var site = store.SeedSite(storedUrl);
            store.SeedWeek(site, ThisSunday);

            var saved = await LoaderOver(store).SaveLoadedReportsIfRefreshOnDay(
                DayOfWeek.Sunday,
                new List<SharePointSiteUsageDetail> { Report(storedUrl.ToUpperInvariant(), ThisSunday) });

            Assert.AreEqual(0, saved, "This week is already stored for that site, whatever case the report used.");
            Assert.AreEqual(1, store.WeeklyStats.Count);
            Assert.AreEqual(0, store.CommitCount, "Nothing to save means nothing to commit.");
        }

        [TestMethod]
        public async Task SharePointSitesUsage_TwoSitesSharingAUrl_UseTheirGreatestStoredWeek()
        {
            // Duplicate site rows for one URL exist in the wild. The last stored week for that URL is the
            // GREATEST across them. Seeded so that the LAST row read holds the OLDER week: taking whichever
            // row happens to be read last would re-save a week that is already held.
            var url = "https://contoso.sharepoint.com/sites/shared";
            var store = new InMemorySharePointSiteUsageStore();
            var upToDate = store.SeedSite(url);
            var behind = store.SeedSite(url.ToUpperInvariant());
            store.SeedWeek(upToDate, ThisSunday).SeedWeek(behind, LastSunday);

            var saved = await LoaderOver(store).SaveLoadedReportsIfRefreshOnDay(
                DayOfWeek.Sunday,
                new List<SharePointSiteUsageDetail> { Report(url, ThisSunday) });

            Assert.AreEqual(0, saved, "The greatest stored week across the duplicate sites already covers this report.");
            Assert.AreEqual(1, store.WeeklyStats.Count(s => s.ForWeekEnding == ThisSunday));
        }

        [TestMethod]
        public async Task SharePointSitesUsage_DuplicateNewSiteInSameRun_CreatesOneSite_WithoutSql()
        {
            var url = "https://contoso.sharepoint.com/sites/dup";
            var store = new InMemorySharePointSiteUsageStore();

            var saved = await LoaderOver(store).SaveLoadedReportsIfRefreshOnDay(
                DayOfWeek.Sunday,
                new List<SharePointSiteUsageDetail> { Report(url, ThisSunday, 1), Report(url, ThisSunday, 2) });

            Assert.AreEqual(2, saved);
            Assert.AreEqual(1, store.Sites.Count, "Two rows for the same new URL must map to a single created site.");
            Assert.AreEqual(2, store.WeeklyStats.Count(s => s.SiteId == store.Sites[0].ID),
                "Both weekly rows should attach to the one site.");
        }

        [TestMethod]
        public async Task SharePointSitesUsage_SiteRowWithNoUrl_IsNeverUsedAsAKey()
        {
            // Site.UrlBase is nullable, and issue #375 part 2 (PR #410) shipped a regression from treating
            // "no URL" and "no site" as the same answer. A null-URL site must not be keyed on, and must not
            // be picked up by a report row that happens to carry no URL either.
            var store = new InMemorySharePointSiteUsageStore();
            var urlless = store.SeedSite(null);
            store.SeedWeek(urlless, LastSunday);

            var saved = await LoaderOver(store).SaveLoadedReportsIfRefreshOnDay(
                DayOfWeek.Sunday,
                new List<SharePointSiteUsageDetail> { Report("https://contoso.sharepoint.com/sites/real", ThisSunday) });

            Assert.AreEqual(1, saved);
            Assert.AreEqual(2, store.Sites.Count, "The URL-less site must be left alone and a real site created.");
            Assert.AreEqual(0, store.WeeklyStats.Count(s => s.SiteId == urlless.ID && s.ForWeekEnding == ThisSunday),
                "The new week must not be attached to the URL-less site.");
        }

        [TestMethod]
        public async Task SharePointSitesUsage_ReadsStorageOncePerRun_NotOncePerSite()
        {
            // The per-site top-1 query this replaced was the dominant cost of the import (~1 query/site).
            // Reintroducing it would still produce correct rows, so only a call count catches it.
            var store = new InMemorySharePointSiteUsageStore();
            var reports = new List<SharePointSiteUsageDetail>();
            for (var i = 0; i < 50; i++)
            {
                var site = store.SeedSite($"https://contoso.sharepoint.com/sites/site{i}");
                store.SeedWeek(site, LastSunday);
                reports.Add(Report($"https://contoso.sharepoint.com/sites/site{i}", ThisSunday, i));
            }

            var saved = await LoaderOver(store).SaveLoadedReportsIfRefreshOnDay(DayOfWeek.Sunday, reports);

            Assert.AreEqual(50, saved);
            Assert.AreEqual(1, store.SitesReadCount, "Sites must be pre-loaded once, not queried per site.");
            Assert.AreEqual(1, store.LatestWeekReadCount, "The latest stored week must come from ONE grouped query.");
            Assert.AreEqual(1, store.CommitCount);
        }

        [TestMethod]
        public async Task SharePointSitesUsage_BulkWriteScopeIsClosedEvenWhenNothingIsSaved()
        {
            // Storage may be reused by the next report, so the change-tracking change has to be undone
            // whether or not the run wrote anything.
            var store = new InMemorySharePointSiteUsageStore();
            var site = store.SeedSite("https://contoso.sharepoint.com/sites/current");
            store.SeedWeek(site, ThisSunday);

            var saved = await LoaderOver(store).SaveLoadedReportsIfRefreshOnDay(
                DayOfWeek.Sunday,
                new List<SharePointSiteUsageDetail> { Report("https://contoso.sharepoint.com/sites/current", ThisSunday) });

            Assert.AreEqual(0, saved);
            Assert.AreEqual(1, store.BulkWriteBegun);
            Assert.AreEqual(1, store.BulkWriteEnded);
            Assert.IsFalse(store.BulkWriteOpen, "The bulk-write scope must be closed even when nothing was saved.");
        }

        [TestMethod]
        public async Task SharePointSitesUsage_GreekSiteUrl_RoundTripsUnchanged()
        {
            // SharePoint site URLs are unrestricted free text and routinely non-Latin. The URL is the key
            // the whole import matches on, so it must survive verbatim into the created site row.
            var greekUrl = "https://contoso.sharepoint.com/sites/Καλημέρα-κόσμε";
            var store = new InMemorySharePointSiteUsageStore();

            var saved = await LoaderOver(store).SaveLoadedReportsIfRefreshOnDay(
                DayOfWeek.Sunday,
                new List<SharePointSiteUsageDetail> { Report(greekUrl, ThisSunday, 3) });

            Assert.AreEqual(1, saved);
            Assert.AreEqual(greekUrl, store.Sites.Single().UrlBase);
        }

        [TestMethod]
        public void SharePointSitesUsage_ConstructingWithoutStorage_FailsImmediately()
        {
            // A null store would otherwise surface much later as a NullReferenceException inside the save
            // loop, after the whole report had been downloaded from Graph.
            Assert.ThrowsException<ArgumentNullException>(
                () => new SharePointSitesWeeklyUsageReportLoader((ISharePointSiteUsageStore)null, null, NullLogger.Instance, null));
        }
    }
}
