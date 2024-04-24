using Common.DataUtils;
using Common.Entities;
using Common.Entities.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate;

namespace Tests.UnitTests
{
    [TestClass]
    public class GraphUsageReportImportTests
    {

        [TestMethod]
        public async Task SPSiteIdToUrlCacheTests()
        {
            // Run all activity imports for test
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();

            using (var db = new AnalyticsEntitiesContext())
            {
                // Test the cache with new site URL & ID
                var fakeId = $"fake id {DateTime.Now.Ticks}";
                var fakeUrlNew = $"fake URL {DateTime.Now.Ticks}";
                var siteUrlCache = new FakeSPSiteIdToUrlCache(db, telemetry, fakeUrlNew);
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
                var siteUrlCache2 = new FakeSPSiteIdToUrlCache(db, telemetry, fakeUrlExisting);
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
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var authConfig = new AppConfig();

            var graphImporter = new GraphImporter(telemetry, authConfig);
            var graphAppIndentityOAuthContext = new GraphAppIndentityOAuthContext(telemetry, authConfig.ClientID, authConfig.TenantGUID.ToString(), authConfig.ClientSecret, authConfig.KeyVaultUrl, authConfig.UseClientCertificate);

            await graphImporter.GetAndSaveActivityReportsMultiThreaded(1, new ManualGraphCallClient(graphAppIndentityOAuthContext, telemetry));
        }

        [TestMethod]
        public async Task SharePointSitesUsageLoaderTest()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var authConfig = new AppConfig();

            var graphAppIndentityOAuthContext = new GraphAppIndentityOAuthContext(telemetry, authConfig.ClientID, authConfig.TenantGUID.ToString(), authConfig.ClientSecret, authConfig.KeyVaultUrl, authConfig.UseClientCertificate);

            await graphAppIndentityOAuthContext.InitClientCredential();
            var graphClient = new Microsoft.Graph.GraphServiceClient(graphAppIndentityOAuthContext.Creds);
            using (var db = new AnalyticsEntitiesContext())
            {
                var siteUrlCache = new GraphSPSiteIdToUrlCache(graphClient, db, telemetry);
                var loader = new SharePointSitesWeeklyUsageReportLoader(db, new ManualGraphCallClient(graphAppIndentityOAuthContext, telemetry), telemetry, siteUrlCache);

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
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var loader = new SundayOrNotFakeWeeklyUsageReportLoader(telemetry);

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
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var loader = new MultiPageFakeWeeklyUsageReportLoader(telemetry);

            // We should have two items, each one loaded on a seperate page
            var fakeData = await loader.LoadReportData();
            Assert.IsTrue(fakeData.Count() == 2);
        }
    }
}
