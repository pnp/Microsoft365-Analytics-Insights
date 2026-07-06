using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities.Teams;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
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
