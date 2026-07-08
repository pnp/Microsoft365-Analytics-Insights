using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities.Teams;
using Common.Entities.Entities.UsageReports;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace Tests.UnitTests
{
    [TestClass]
    public class GraphUsageReportImportTests
    {

        [TestMethod]
        public async Task SPSiteIdToUrlCacheTests()
        {
            // Run all activity imports for test
            var logger = AnalyticsLogger.ConsoleOnlyTracer();

            using (var db = new AnalyticsEntitiesContext())
            {
                // Test the cache with new site URL & ID
                var fakeId = $"fake id {DateTime.Now.Ticks}";
                var fakeUrlNew = $"fake URL {DateTime.Now.Ticks}";
                var siteUrlCache = new FakeSPSiteIdToUrlCache(db, logger, fakeUrlNew);
                var site1 = await siteUrlCache.Load(fakeId);

                var dbRecord = db.sites.Where(s => s.SiteId == fakeId).SingleOrDefault();
                Assert.IsNotNull(dbRecord);
                Assert.AreEqual(fakeUrlNew, site1.SiteUrl);
                Assert.AreEqual(fakeId, site1.SiteId);

                // Pre-add a site with just the URL
                var fakeUrlExisting = $"fake URL {DateTime.Now.Ticks}";
                db.sites.Add(new Site { SiteId = null, UrlBase = fakeUrlExisting });
                await db.SaveChangesAsync();

                // Load the site with a new fake ID. Currently in the DB it doesn't have an ID
                var siteUrlCache2 = new FakeSPSiteIdToUrlCache(db, logger, fakeUrlExisting);
                var site2 = await siteUrlCache2.Load($"fake id2 {DateTime.Now.Ticks}");
                Assert.IsNotNull(site2);
                Assert.AreEqual(fakeUrlExisting, site2.SiteUrl);

                var dbRecord2 = db.sites.Where(s => s.UrlBase == fakeUrlExisting).SingleOrDefault();
                Assert.IsNotNull(dbRecord2);
            }
        }

        [TestMethod]
        public async Task AllO365ActivityTests()
        {
            // Run all activity imports for test
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var authConfig = new AppConfig();

            var graphAppIndentityOAuthContext = new GraphAppIndentityOAuthContext(logger, authConfig.ClientID, authConfig.TenantGUID.ToString(), authConfig.ClientSecret, authConfig.KeyVaultUrl, authConfig.UseClientCertificate);
            await graphAppIndentityOAuthContext.InitClientCredential();

            var graphClient = new Microsoft.Graph.GraphServiceClient(graphAppIndentityOAuthContext.Creds);
            var graphImporter = new GraphImporter(logger, new NoUsersHaveGroupsUserGroupsCache(logger), graphAppIndentityOAuthContext, graphClient, authConfig);

            await graphImporter.GetAndSaveActivityReportsMultiThreaded(1, new ManualGraphCallClient(graphAppIndentityOAuthContext, logger),
                new NoUsersHaveGroupsUserGroupsCache(logger), new UserGroupsFilterModel());
        }

        [TestMethod]
        public async Task SharePointSitesUsageLoaderTest()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var authConfig = new AppConfig();

            var graphAppIndentityOAuthContext = new GraphAppIndentityOAuthContext(logger, authConfig.ClientID, authConfig.TenantGUID.ToString(), authConfig.ClientSecret, authConfig.KeyVaultUrl, authConfig.UseClientCertificate);

            await graphAppIndentityOAuthContext.InitClientCredential();
            var graphClient = new Microsoft.Graph.GraphServiceClient(graphAppIndentityOAuthContext.Creds);
            using (var db = new AnalyticsEntitiesContext())
            {
                var siteUrlCache = new GraphSPSiteIdToUrlCache(graphClient, db, logger);
                var loader = new SharePointSitesWeeklyUsageReportLoader(db, new ManualGraphCallClient(graphAppIndentityOAuthContext, logger), logger, siteUrlCache);

                // Override/fake the last refresh date to be today
                var data = await loader.LoadReportData();
                foreach (var item in data)
                {
                    item.ReportRefreshDate = DateTime.Now;
                }
                await loader.SaveLoadedReportsIfRefreshOnDay(DateTime.Now.DayOfWeek, data);
            }
        }

        /// <summary>
        /// Regression for the bulk-preload / change-tracking rewrite of the SharePoint Site Usage save loop.
        /// Verifies the day-of-week gate, "only save when newer than last stored", existing-site FK reuse,
        /// new-site creation, and that EF auto change-detection is restored afterwards - all without any
        /// per-site DB round-trip. Runs SaveLoadedReportsIfRefreshOnDay directly (no Graph client needed).
        /// </summary>
        [TestMethod]
        public async Task SharePointSitesUsageLoader_SavesNewReusesExistingAndSkipsCurrent()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();

            // 2024-02-25 is a Sunday; 2024-02-18 the Sunday before.
            var thisSunday = new DateTime(2024, 2, 25);
            var lastSunday = new DateTime(2024, 2, 18);
            Assert.AreEqual(DayOfWeek.Sunday, thisSunday.DayOfWeek);

            var suffix = DateTime.Now.Ticks.ToString();
            var urlExistingStale = $"https://contoso.sharepoint.com/sites/stale-{suffix}";
            var urlExistingCurrent = $"https://contoso.sharepoint.com/sites/current-{suffix}";
            var urlBrandNew = $"https://contoso.sharepoint.com/sites/new-{suffix}";

            using (var db = new AnalyticsEntitiesContext())
            {
                // Existing site whose latest stored week is older than this Sunday -> should get a new row (reusing the site FK).
                var siteStale = new Site { UrlBase = urlExistingStale };
                db.sites.Add(siteStale);
                db.SharePointSiteStats.Add(new SharePointSitesFileWeeklyStats { Site = siteStale, ForWeekEnding = lastSunday });

                // Existing site already stored for this Sunday -> should be skipped (no duplicate).
                var siteCurrent = new Site { UrlBase = urlExistingCurrent };
                db.sites.Add(siteCurrent);
                db.SharePointSiteStats.Add(new SharePointSitesFileWeeklyStats { Site = siteCurrent, ForWeekEnding = thisSunday });
                await db.SaveChangesAsync();

                var loader = new SharePointSitesWeeklyUsageReportLoader(db, null, logger, null);
                var data = new List<SharePointSiteUsageDetail>
                {
                    new SharePointSiteUsageDetail { SiteUrl = urlExistingStale, FileCount = 10 },
                    new SharePointSiteUsageDetail { SiteUrl = urlExistingCurrent, FileCount = 20 },
                    new SharePointSiteUsageDetail { SiteUrl = urlBrandNew, FileCount = 30 },
                    // Not a Sunday refresh -> must be ignored entirely.
                    new SharePointSiteUsageDetail { SiteUrl = urlBrandNew, FileCount = 99, ReportRefreshDateString = "2024-02-24" },
                };
                data[0].ReportRefreshDate = thisSunday;
                data[1].ReportRefreshDate = thisSunday;
                data[2].ReportRefreshDate = thisSunday;

                var saved = await loader.SaveLoadedReportsIfRefreshOnDay(DayOfWeek.Sunday, data);

                // Only the stale-existing and the brand-new site should have been saved.
                Assert.AreEqual(2, saved, "Should save the stale-existing and the brand-new site, and skip the already-current one and the non-Sunday row");

                // Auto change-detection must be back on for anyone reusing this context.
                Assert.IsTrue(db.Configuration.AutoDetectChangesEnabled, "AutoDetectChangesEnabled must be restored after the save");
            }

            // Re-open a fresh context to assert what actually landed in the DB.
            using (var db = new AnalyticsEntitiesContext())
            {
                var staleRows = await db.SharePointSiteStats.Where(s => s.Site.UrlBase == urlExistingStale).ToListAsync();
                Assert.AreEqual(2, staleRows.Count, "Existing stale site should now have both the old and the new week");
                Assert.IsTrue(staleRows.Any(r => r.ForWeekEnding == thisSunday), "New week row should exist for the stale site");

                var currentRows = await db.SharePointSiteStats.Where(s => s.Site.UrlBase == urlExistingCurrent).ToListAsync();
                Assert.AreEqual(1, currentRows.Count, "Already-current site must not get a duplicate row");

                var newSite = await db.sites.SingleOrDefaultAsync(s => s.UrlBase == urlBrandNew);
                Assert.IsNotNull(newSite, "Brand-new site should have been created");
                var newRows = await db.SharePointSiteStats.Where(s => s.Site.UrlBase == urlBrandNew).ToListAsync();
                Assert.AreEqual(1, newRows.Count, "Brand-new site should have exactly one week row (the non-Sunday row is ignored)");
                Assert.AreEqual(30, newRows[0].FileCount);
            }
        }

        /// <summary>
        /// Two report rows for the same *new* site in one run must map to a single created site (the
        /// _newSitesByUrl reuse branch), not insert the site twice.
        /// </summary>
        [TestMethod]
        public async Task SharePointSitesUsageLoader_DuplicateNewSiteInSameRun_CreatesOneSite()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var thisSunday = new DateTime(2024, 2, 25);
            var url = $"https://contoso.sharepoint.com/sites/dup-{DateTime.Now.Ticks}";

            using (var db = new AnalyticsEntitiesContext())
            {
                var loader = new SharePointSitesWeeklyUsageReportLoader(db, null, logger, null);
                var data = new List<SharePointSiteUsageDetail>
                {
                    new SharePointSiteUsageDetail { SiteUrl = url, FileCount = 1 },
                    new SharePointSiteUsageDetail { SiteUrl = url, FileCount = 2 },
                };
                data[0].ReportRefreshDate = thisSunday;
                data[1].ReportRefreshDate = thisSunday;

                var saved = await loader.SaveLoadedReportsIfRefreshOnDay(DayOfWeek.Sunday, data);
                Assert.AreEqual(2, saved);
            }

            using (var db = new AnalyticsEntitiesContext())
            {
                var sites = await db.sites.Where(s => s.UrlBase == url).ToListAsync();
                Assert.AreEqual(1, sites.Count, "The two same-URL rows must map to a single new site, not duplicate sites");
                var rows = await db.SharePointSiteStats.Where(s => s.Site.UrlBase == url).ToListAsync();
                Assert.AreEqual(2, rows.Count, "Both weekly rows should attach to the one site");
            }
        }

        /// <summary>
        /// A report URL whose case differs from the stored site URL must still resolve to the existing site
        /// (the URL dictionaries are OrdinalIgnoreCase, matching SQL Server's case-insensitive collation),
        /// so we don't create a duplicate site.
        /// </summary>
        [TestMethod]
        public async Task SharePointSitesUsageLoader_MatchesExistingSiteUrlCaseInsensitively()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var thisSunday = new DateTime(2024, 2, 25);
            var lastSunday = new DateTime(2024, 2, 18);
            var storedUrl = $"https://contoso.sharepoint.com/sites/CaseTest-{DateTime.Now.Ticks}";
            var reportUrl = storedUrl.ToUpperInvariant();   // same site, different case (as Graph might return)

            using (var db = new AnalyticsEntitiesContext())
            {
                var site = new Site { UrlBase = storedUrl };
                db.sites.Add(site);
                db.SharePointSiteStats.Add(new SharePointSitesFileWeeklyStats { Site = site, ForWeekEnding = lastSunday });
                await db.SaveChangesAsync();

                var loader = new SharePointSitesWeeklyUsageReportLoader(db, null, logger, null);
                var data = new List<SharePointSiteUsageDetail> { new SharePointSiteUsageDetail { SiteUrl = reportUrl } };
                data[0].ReportRefreshDate = thisSunday;

                var saved = await loader.SaveLoadedReportsIfRefreshOnDay(DayOfWeek.Sunday, data);
                Assert.AreEqual(1, saved, "The newer week should be saved");
            }

            using (var db = new AnalyticsEntitiesContext())
            {
                // SQL collation is case-insensitive, so this returns every site CI-equal to storedUrl - a
                // duplicate insert (with the upper-cased URL) would make this 2.
                var sites = await db.sites.Where(s => s.UrlBase == storedUrl).ToListAsync();
                Assert.AreEqual(1, sites.Count, "Case-differing report URL must reuse the existing site, not create a duplicate");
                var siteId = sites[0].ID;
                var rows = await db.SharePointSiteStats.Where(s => s.SiteId == siteId).ToListAsync();
                Assert.AreEqual(2, rows.Count, "Existing site should now have the old and the new week");
            }
        }

        /// <summary>
        /// The base save loop must call EndSaveAsync in its finally even when the save throws - for the real
        /// SharePoint loader that is what restores EF auto change-detection on the (possibly reused) context.
        /// </summary>
        [TestMethod]
        public async Task SaveLoadedReportsIfRefreshOnDay_CallsEndSaveEvenWhenSaveThrows()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var loader = new ThrowingFakeWeeklyUsageReportLoader(logger);
            var data = new List<FakeStats> { new FakeStats { RandoId = "1", ReportRefreshDateString = "2024-02-25" } }; // a Sunday

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => loader.SaveLoadedReportsIfRefreshOnDay(DayOfWeek.Sunday, data));

            Assert.IsTrue(loader.EndSaveCalled, "EndSaveAsync must run in the finally even when the save loop throws");
        }

        /// <summary>
        /// Tests the saving on the right day of week logic works
        /// </summary>
        [TestMethod]
        public async Task SundayOrNotFakeUsageLoaderTest()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var loader = new SundayOrNotFakeWeeklyUsageReportLoader(logger);

            // First time we load, we return a report that's not a sunday
            var saves = await loader.LoadAndSaveLastWeeksReportsIfRefreshOnDay(DayOfWeek.Sunday);
            Assert.AreEqual(0, saves);

            // Second time we load, we return a report that's a sunday
            saves = await loader.LoadAndSaveLastWeeksReportsIfRefreshOnDay(DayOfWeek.Sunday);
            Assert.AreEqual(1, saves);
        }

        [TestMethod]
        public async Task MultiPageFakeUsageLoaderTest()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var loader = new MultiPageFakeWeeklyUsageReportLoader(logger);

            // We should have two items, each one loaded on a seperate page
            var fakeData = await loader.LoadReportData();
            Assert.IsTrue(fakeData.Count() == 2);
        }

        /// <summary>
        /// Regression: Teams audio/video/screen-share durations were stored with TimeSpan.Seconds
        /// (the 0-59 seconds COMPONENT), silently truncating any duration of a minute or more
        /// (e.g. PT45M -> 0). They must be stored as total seconds.
        /// </summary>
        [TestMethod]
        public void TeamsUserUsageLoader_DurationsUseTotalSecondsNotComponent()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var loader = new TestableTeamsUserUsageLoader(logger);

            var page = new TeamsUserActivityUserDetail
            {
                UserPrincipalName = "duration-test@contoso.com",
                AudioDuration = "PT1H2M3S",       // 3723s total; old .Seconds gave 3
                VideoDuration = "PT45M",          // 2700s total; old .Seconds gave 0
                ScreenShareDuration = "PT2M30S",  // 150s total; old .Seconds gave 30
            };

            var log = loader.Populate(page);

            Assert.AreEqual(3723, log.AudioDurationSeconds, "Audio duration must be total seconds, not the 0-59 component");
            Assert.AreEqual(2700, log.VideoDurationSeconds, "Video duration must be total seconds, not the 0-59 component");
            Assert.AreEqual(150, log.ScreenShareDurationSeconds, "Screen-share duration must be total seconds, not the 0-59 component");
        }

        /// <summary>
        /// Test-only subclass that reaches the protected PopulateReportSpecificMetadata without a
        /// live Graph client (the client/cache/filter are only used during paging, not here).
        /// </summary>
        private class TestableTeamsUserUsageLoader : TeamsUserUsageLoader
        {
            public TestableTeamsUserUsageLoader(ILogger logger) : base(null, null, null, logger) { }

            public GlobalTeamsUserUsageLog Populate(TeamsUserActivityUserDetail page)
            {
                var log = new GlobalTeamsUserUsageLog();
                PopulateReportSpecificMetadata(log, page);
                return log;
            }
        }
    }
}
