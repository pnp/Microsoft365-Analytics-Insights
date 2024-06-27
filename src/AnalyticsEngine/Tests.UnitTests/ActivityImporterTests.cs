using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities;
using Common.Entities.Entities.AuditLog;
using DataUtils;
using DataUtils.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.SqlTypes;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Http;
using Tests.UnitTests.FakeEntities;
using Tests.UnitTests.FakeLoaderClasses;
using Tests.UnitTests.Properties;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Loaders;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    [TestClass]
    public class ActivityImporterTests
    {
        [ClassInitialize]
        public static void TestStartup(TestContext context)
        {
            // Make sure always 1 org_url
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                var urls = db.org_urls.ToList();
                db.org_urls.RemoveRange(urls);
                db.org_urls.Add(new OrgUrl() { UrlBase = "http" });

                db.SaveChanges();
            }
        }

        #region Utility Class Tests

        [TestMethod]
        public void ContentSetBasicTest()
        {

            AbstractAuditLogContent oldLog = DataGenerators.GetRandomAnyLog();
            AbstractAuditLogContent newLog = DataGenerators.GetRandomAnyLog();
            oldLog.CreationTime = DateTime.Now.AddDays(-1);
            newLog.CreationTime = DateTime.Now;
            var cs = new TestActivityReportSet() { oldLog, newLog };

            Assert.IsTrue(cs.OldestContent < cs.NewestContent);
        }

        [TestMethod]
        public void CacheBasicTest()
        {
            var cache = ActivityImportCache.GetEmptyCache();

            Guid testingId = Guid.NewGuid();
            AbstractAuditLogContent testLog = new SharePointAuditLogContent() { Id = testingId };
            cache.RememberProcessedEvent(testLog);
            Assert.IsTrue(cache.HaveSeenInProcessedOrIgnoredEvents(testLog));

            // Check false for different ID
            Assert.IsFalse(cache.HaveSeenInProcessedOrIgnoredEvents(new SharePointAuditLogContent() { Id = Guid.NewGuid() }));

            // Check false for same ID but different date
            Assert.IsTrue(cache.HaveSeenInProcessedOrIgnoredEvents(new SharePointAuditLogContent() { Id = testingId, CreationTime = DateTime.Now }));
        }

        [TestMethod]
        public void CacheBoundsTest()
        {
            ActivityImportCache cache = ActivityImportCache.GetEmptyCache();

            // 11,979,891 before OutOfMemoryException for ignored IDs. We support 5 million activities an hour (could be more). 
            const int FIVE_MILLION = 5000000, MAX_CHUNKS = 3;
            for (int chunks = 0; chunks < MAX_CHUNKS; chunks++)
            {
                // Add a day
                DateTime dt = DateTime.Now;
                dt = dt.AddDays(chunks);

                for (int i = 0; i < FIVE_MILLION; i++)
                {
                    if (i % 1000 == 0)
                    {

                        float percentDone = ((float)i / (float)FIVE_MILLION) * 100;
                        Console.Write($"{Math.Round(percentDone, 0)}%...");
                    }

                    // Cache. Should split into chunks so new OutOfMemoryException
                    cache.RememberProcessedEvent(new SharePointAuditLogContent() { Id = Guid.NewGuid(), CreationTime = dt });
                }
            }

            int processedCount = 0;
            foreach (var cacheChunk in cache.GetIds(ActivityImportCache.CacheType.Processed))
            {
                processedCount += cacheChunk.Count;
            }
            Assert.AreEqual(processedCount, MAX_CHUNKS * FIVE_MILLION);

        }

        #endregion

        /// <summary>
        /// Check with in-memory adaptors so we can test save logic with various data ranges quickly
        /// </summary>
        [TestMethod]
        public async Task FakeInMemoryImportTests()
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();

            var saveManager = new FakeActivityReportPersistenceManager();
            for (int i = 1; i < 1000; i++)
            {
                var testLoader = new FakeActivityImporter(i, new AppConfig(), telemetry);

                var reportSaveStats = await testLoader.LoadReportsAndSave(saveManager);
                var expected = i * reportSaveStats.ForTimeSlots.Count;

                Assert.AreEqual(reportSaveStats.Total, expected);
            }
        }

        #region Real Import Tests

        /// <summary>
        /// Check OneDrive data is treated as SP
        /// </summary>
        [TestMethod]
        public async Task OneDriveAppearsAsSPEventTests()
        {

            // Build a random content-set, but with x2 duplicate lookups
            var hits = new TestActivityReportSet();

            var oneDriveEvent = DataGenerators.GetRandomSharePointLog();
            oneDriveEvent.Workload = ActivityImportConstants.WORKLOAD_OD;
            hits.Add(oneDriveEvent);
            var sqlPersist = new ActivityReportSqlPersistenceManager(new AllowAllFilterConfig(), AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig());

            await hits.CommitAllToSQL(sqlPersist);

        }

        /// <summary>
        /// Save real SharePoint activity data
        /// </summary>
        [TestMethod]
        public async Task RealSPActivityImportTests()
        {
            var sharePointLogs = new WebActivityReportSet();

            sharePointLogs.AddRange(JsonConvert.DeserializeObject<List<SharePointAuditLogContent>>(Resources.ActivitySharePoint_Various_Responses));

            sharePointLogs.AddRange(JsonConvert.DeserializeObject<List<SharePointAuditLogContent>>(Resources.ActivitySharePoint_Permission_Change));

            // Verify JSon parsing
            Assert.IsTrue(sharePointLogs.Count == 4);
            foreach (var log in sharePointLogs)
            {
                VerifySharePointLog((SharePointAuditLogContent)log);
            }


            // Save to SQL
            using (var db = new AnalyticsEntitiesContext())
            {

                // Delete old test data so we can run multiple times
                KillmetaRecordEventsById(sharePointLogs, db);

                // Save
                int preSPLogsInsertSPEventsCount = db.sharepoint_events.Count();
                var sqlPersist = new ActivityReportSqlPersistenceManager(new AllowAllFilterConfig(), AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig());
                await sharePointLogs.CommitAllToSQL(sqlPersist);

                // Validate new count
                int postSPLogsInsertSPEventsCount = db.sharepoint_events.Count();
                Assert.IsTrue(preSPLogsInsertSPEventsCount + sharePointLogs.Count == postSPLogsInsertSPEventsCount, "Unexpected number of SP activities post-save");


                // Reload hits now with time-on-page
                var lastInsertedSPEvents = new List<SharePointEventMetadata>();
                foreach (var log in sharePointLogs)
                {
                    var spEvent = db.sharepoint_events
                        .Include(e => e.Event)
                        .Include(e => e.Event.User)
                        .Include(e => e.Event.Operation)
                        .Include(e => e.related_web)
                        .Include(e => e.file_extension)
                        .Include(e => e.file_name)
                        .Include(e => e.item_type)
                        .Include(e => e.url)
                        .Where(e => e.EventID == log.Id).SingleOrDefault();
                    lastInsertedSPEvents.Add(spEvent);
                }

                Assert.IsTrue(lastInsertedSPEvents.Count == sharePointLogs.Count, "Unexpected DB load count");
                int i = 0;
                foreach (var activity in lastInsertedSPEvents)
                {
                    // Refresh after SQL changes
                    CompareSPReports(activity, (SharePointAuditLogContent)sharePointLogs[i]);

                    i++;
                }
            }
        }

        /// <summary>
        /// Saves real data that's not SharePoint
        /// </summary>
        [TestMethod]
        public async Task RealOtherActivityImportTests()
        {
            var jsonLogs = new List<AbstractAuditLogContent>();
            jsonLogs.AddRange(JsonConvert.DeserializeObject<List<AzureADAuditLogContent>>(Resources.ActivityAzure_AD_Login));
            jsonLogs.AddRange(JsonConvert.DeserializeObject<List<ExchangeAuditLogContent>>(Resources.ActivityExchange));

            // Verify JSon parsing
            Assert.IsTrue(jsonLogs.Count == 2);
            foreach (var log in jsonLogs)
            {
                VerifyMiscLog(log);
            }

            WebActivityReportSet otherLogs = new WebActivityReportSet();
            otherLogs.AddRange(jsonLogs);


            // Save to SQL
            using (var db = new AnalyticsEntitiesContext())
            {

                // Delete old test data so we can run multiple times
                KillmetaRecordEventsById(otherLogs, db);

                // Save
                int preSPLogsInsertSPEventsCount = db.AuditEventsCommon.Count();
                var sqlPersist = new ActivityReportSqlPersistenceManager(new AllowAllFilterConfig(), AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig());
                await otherLogs.CommitAllToSQL(sqlPersist);

                // Validate new events count
                int postSPLogsInsertSPEventsCount = db.AuditEventsCommon.Count();
                Assert.IsTrue(preSPLogsInsertSPEventsCount + otherLogs.Count == postSPLogsInsertSPEventsCount, "Unexpected number of SP activities post-save");


                // Reload hits now with time-on-page
                var lastInsertedEvents = new List<BaseOfficeEvent>();
                foreach (var log in otherLogs)
                {
                    if (log.Workload == ActivityImportConstants.WORKLOAD_EXCHANGE)
                    {
                        var dbEvent = db.exchange_events
                            .Include(e => e.Event)
                            .Include(e => e.Event.User)
                            .Include(e => e.Event.Operation)
                            .Include(e => e.Properties).Where(e => e.EventID == log.Id).SingleOrDefault();
                        CompareExchangeReports(dbEvent, log);

                        lastInsertedEvents.Add(dbEvent);
                    }
                    else if (log.Workload == ActivityImportConstants.WORKLOAD_AZURE_AD)
                    {
                        var dbEvent = db.azure_ad_events
                            .Include(e => e.Event)
                            .Include(e => e.Event.User)
                            .Include(e => e.Event.Operation)
                            .Include(e => e.Properties)
                            .Where(e => e.EventID == log.Id).SingleOrDefault();
                        CompareAzureReports(dbEvent, log);

                        lastInsertedEvents.Add(dbEvent);
                    }
                }

                Assert.IsTrue(lastInsertedEvents.Count == otherLogs.Count, "Unexpected DB load count");
            }
        }


        private void KillmetaRecordEventsById(ActivityReportSet logs, AnalyticsEntitiesContext db)
        {
            foreach (var log in logs)
            {
                if (log.Workload == ActivityImportConstants.WORKLOAD_SP)
                {
                    var metaRecord = db.sharepoint_events.Where(l => l.EventID == log.Id).SingleOrDefault();
                    if (metaRecord != null) db.sharepoint_events.Remove(metaRecord);

                    db.SaveChanges();
                }
                else if (log.Workload == ActivityImportConstants.WORKLOAD_EXCHANGE)
                {
                    // This is a hack because we don't have the right tables in the EF context yet
                    var metaRecordProps = db.Database.ExecuteSqlCommand(
                        $"delete from audit_event_exchange_props where [event_id] = '{log.Id}'"
                    );

                    var metaRecord = db.exchange_events.Where(l => l.EventID == log.Id).SingleOrDefault();
                    if (metaRecord != null) db.exchange_events.Remove(metaRecord);

                    db.SaveChanges();
                }
                else if (log.Workload == ActivityImportConstants.WORKLOAD_AZURE_AD)
                {
                    // This is a hack because we don't have the right tables in the EF context yet
                    var metaRecordProps = db.Database.ExecuteSqlCommand(
                        $"delete from audit_event_azure_ad_props where [event_id] = '{log.Id}'"
                    );

                    var metaRecord = db.azure_ad_events.Where(l => l.EventID == log.Id).SingleOrDefault();
                    if (metaRecord != null) db.azure_ad_events.Remove(metaRecord);

                    db.SaveChanges();
                }
                else if (log.Workload != ActivityImportConstants.WORKLOAD_EXCHANGE && log.Workload != ActivityImportConstants.WORKLOAD_SP && log.Workload != ActivityImportConstants.WORKLOAD_AZURE_AD)
                {
                    var metaRecord = db.general_audit_events.Where(l => l.EventID == log.Id).SingleOrDefault();
                    if (metaRecord != null) db.general_audit_events.Remove(metaRecord);

                    db.SaveChanges();
                }
                var existingGeneric = db.AuditEventsCommon.Where(l => l.Id == log.Id).SingleOrDefault();
                if (existingGeneric != null) db.AuditEventsCommon.Remove(existingGeneric);
            }

            db.SaveChanges();
        }

        private void CompareSPReports(SharePointEventMetadata databaseObj, SharePointAuditLogContent jsonObj)
        {
            CompareBaseReports(databaseObj, jsonObj);
            Assert.IsTrue(databaseObj.file_extension.extension_name == jsonObj.SourceFileExtension);
            Assert.IsTrue(databaseObj.file_name.Name == jsonObj.SourceFileName);
            Assert.IsTrue(databaseObj.item_type.type_name == jsonObj.ItemType);
            Assert.IsTrue(databaseObj.related_web.url_base == StringUtils.RemoveTrailingSlash(jsonObj.SiteUrl));
            Assert.IsTrue(databaseObj.url.FullUrl == jsonObj.ObjectId);
            Assert.IsTrue(databaseObj.related_web.url_base == StringUtils.RemoveTrailingSlash(jsonObj.SiteUrl));
        }

        private void CompareBaseReports(BaseOfficeEvent databaseObj, AbstractAuditLogContent jsonObj)
        {
            Assert.IsTrue(databaseObj.EventID == jsonObj.Id);
            //if (jsonObj.EventData != null) Assert.IsTrue(databaseObj.Event.event_data == jsonObj.EventData);

            Assert.IsTrue(databaseObj.Event.User.UserPrincipalName == jsonObj.UserId);
            Assert.IsTrue(databaseObj.Event.Operation.Name == jsonObj.Operation);
            Assert.IsTrue(databaseObj.Event.TimeStamp == jsonObj.CreationTime);
            //Assert.IsTrue(databaseObj.Event.event_data == jsonObj.EventData);
        }

        private void CompareAzureReports(AzureADEventMetadata databaseObj, AbstractAuditLogContent jsonObj)
        {
            CompareBaseReports(databaseObj, jsonObj);

            Assert.AreEqual(databaseObj.Properties.Count, jsonObj.ExtendedProperties.Count);
        }
        private void CompareExchangeReports(ExchangeEventMetadata databaseObj, AbstractAuditLogContent jsonObj)
        {
            CompareBaseReports(databaseObj, jsonObj);

            Assert.AreEqual(databaseObj.Properties.Count, jsonObj.ExtendedProperties.Count);
        }

        private void VerifySharePointLog(SharePointAuditLogContent auditLogContent)
        {
            Assert.IsTrue(auditLogContent.CreationTime > DateTime.MinValue);
            Assert.IsTrue(auditLogContent.ExtendedProperties.Count == 0);
            Assert.IsTrue(auditLogContent.Id != Guid.Empty);
            Assert.IsFalse(string.IsNullOrEmpty(auditLogContent.ObjectId));
            Assert.IsFalse(string.IsNullOrEmpty(auditLogContent.UserId));
            Assert.IsTrue(auditLogContent.Workload == ActivityImportConstants.WORKLOAD_SP);

            Assert.IsFalse(string.IsNullOrEmpty(auditLogContent.ObjectId));
            Assert.IsFalse(string.IsNullOrEmpty(auditLogContent.SourceFileExtension));
            Assert.IsFalse(string.IsNullOrEmpty(auditLogContent.SourceFileName));
            Assert.IsFalse(string.IsNullOrEmpty(auditLogContent.SiteUrl));
            Assert.IsFalse(string.IsNullOrEmpty(auditLogContent.Operation));
        }
        private void VerifyMiscLog(AbstractAuditLogContent auditLogContent)
        {
            Assert.IsTrue(auditLogContent.CreationTime > DateTime.MinValue);
            Assert.IsNotNull(auditLogContent.ExtendedProperties);
            Assert.IsTrue(auditLogContent.Id != Guid.Empty);
            Assert.IsFalse(string.IsNullOrEmpty(auditLogContent.UserId));

            Assert.IsFalse(string.IsNullOrEmpty(auditLogContent.Workload));
            Assert.IsTrue(auditLogContent.Workload != ActivityImportConstants.WORKLOAD_SP);

            Assert.IsFalse(string.IsNullOrEmpty(auditLogContent.Operation));
        }

        #endregion

        //[TestMethod]
        public void ImportCacheTest()
        {
            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                Guid g = Guid.NewGuid();
                DateTime dt = DateTime.Now.AddHours(-1);

                // Add an ignored event from before 
                db.ignored_audit_events.Add(new IgnoredEvent() { event_id = g, processed_timestamp = dt.AddHours(-2) });

                ActivityImportCache cache = ActivityImportCache.GetAndBuildNewCache(dt, DateTime.Now);

                throw new NotImplementedException();
                /* Problem:
                 * 
Event date: 				16/07/2019 07:14:39
Time saved in DB ignored events: 	17/07/2019 10:56:15

Loading activity cache from 16/07/2019 05:58:32 to 16/07/2019 08:58:11.

Event found in API, doesn't find it in cache, assumes it's a new ignored event, and crashes
                 */

            }
        }

        [TestMethod]
        public async Task DuplicateActivitiesTest()
        {
            // Create x2 random activities with same ID
            var randomActivity = DataGenerators.GetRandomSharePointLog();
            var duplicateIdRandomActivity = DataGenerators.GetRandomSharePointLog();
            duplicateIdRandomActivity.Id = randomActivity.Id;

            using (var db = new AnalyticsEntitiesContext())
            {
                int preInsertCount = db.sharepoint_events.Count();
                var sqlPersist = new ActivityReportSqlPersistenceManager(new AllowAllFilterConfig(), AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig());

                // Create content-set for two different-but-same-id activities
                TestActivityReportSet duplicateContent = new TestActivityReportSet() { randomActivity, duplicateIdRandomActivity };

                // Should not crash with duplicate primary-keys
                await duplicateContent.CommitAllToSQL(sqlPersist);

                // Should only be 1 extra
                int secondInsertCount = db.sharepoint_events.Count();
                Assert.IsTrue(preInsertCount + 1 == secondInsertCount);

                await duplicateContent.CommitAllToSQL(sqlPersist);

                // Should be no extra inserts
                int thirdInsertCount = db.sharepoint_events.Count();
                Assert.IsTrue(secondInsertCount == thirdInsertCount);
            }

        }

        /// <summary>
        /// Tests SP event filtering works as expected
        /// </summary>
        [TestMethod]
        public async Task OrgURLsTests()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var randomActivity = DataGenerators.GetRandomSharePointLog();

                // Test load cache 
                try
                {
                    await SharePointOrgUrlsFilterConfig.Load(db);
                }
                catch (InvalidOperationException ex)
                {
                    Assert.IsTrue(ex.Message.Contains("No org_urls loaded"));
                    // Good. Sanity check worked.
                }

                // Test scope logic
                var spFilterConfig = new SharePointOrgUrlsFilterConfig();

                const string TEST_PREFIX = "https://unittesting.sharepoint.local";
                spFilterConfig.OrgUrlConfigs.Add(new FilterUrlConfig { Url = TEST_PREFIX });

                var spEventWithMatchingUrl = DataGenerators.GetRandomSharePointLog();
                spEventWithMatchingUrl.SiteUrl = TEST_PREFIX;
                spEventWithMatchingUrl.ObjectId = TEST_PREFIX + "/file";

                var spEventWithoutMatchingUrl = DataGenerators.GetRandomSharePointLog();
                spEventWithoutMatchingUrl.ObjectId = "http://whatever";

                // Test URLs in & out of scope
                Assert.IsTrue(spFilterConfig.InScope(spEventWithMatchingUrl));
                Assert.IsFalse(spFilterConfig.InScope(spEventWithoutMatchingUrl));

                // Test not-sharepoint events
                var oneDriveEvent = DataGenerators.GetRandomSharePointLog();
                oneDriveEvent.Workload = ActivityImportConstants.WORKLOAD_OD;
                oneDriveEvent.ObjectId = TEST_PREFIX;
                Assert.IsTrue(spFilterConfig.InScope(oneDriveEvent));

                // Check exact site scope ----------->
                spFilterConfig.OrgUrlConfigs.Add(new FilterUrlConfig { Url = spEventWithMatchingUrl.SiteUrl, ExactSiteMatch = true });
                Assert.IsTrue(spFilterConfig.InScope(spEventWithMatchingUrl));      // File URL doesn't match site. Should not be in scope.

                var spFilterConfigWithSlash = new SharePointOrgUrlsFilterConfig();
                spFilterConfigWithSlash.OrgUrlConfigs.Add(new FilterUrlConfig { Url = spEventWithMatchingUrl.SiteUrl + "/", ExactSiteMatch = true });
                Assert.IsTrue(spFilterConfigWithSlash.InScope(spEventWithMatchingUrl));

            }
        }

        /// <summary>
        /// Tests SP events save & load correctly, all properties
        /// </summary>
        [TestMethod]
        public async Task GenerateAndSaveSPEvents()
        {
            await InsertAndTestSPEvents(0, false);

            await InsertAndTestSPEvents(1, false);
            await InsertAndTestSPEvents(2001, true);        // Split over 3 threads
        }
        async Task InsertAndTestSPEvents(int count, bool allRandomLookups)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                string FILE_NAME = "file" + DataGenerators.GetRandomString(5);
                string FILE_EXT = "ext" + DataGenerators.GetRandomString(5);
                string USER_ID = "user" + DataGenerators.GetRandomString(5);


                // Build a random content-set, but with x2 duplicate lookups
                var hitsActivity = new TestActivityReportSet();
                for (int i = 0; i < count; i++)
                {
                    var log = DataGenerators.GetRandomSharePointLog();

                    // This will blow-up if caching is used on each thread
                    if (!allRandomLookups)
                    {
                        // Override each thing with the same random data, to try and cause update conflicts in SQL IDXs
                        log.UserId = USER_ID;
                        log.SourceFileName = FILE_NAME;
                        log.SourceFileExtension = FILE_EXT;
                    }
                    hitsActivity.Add(log);
                }

                // Save
                var tempCache = ActivityImportCache.GetEmptyCache();
                var sqlPersist = new ActivityReportSqlPersistenceManager(new AllowAllFilterConfig(), AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig());

                var s = await hitsActivity.CommitAllToSQL(sqlPersist);

                // Verify count
                int expected = s.Imported + s.URLsOutOfScope;
                Assert.AreEqual(count, expected, "Unexpected SP event-count after save");

                // Verify data
                var targetIds = hitsActivity.Select(h => h.Id).ToList();
                var dbLogs = await db.sharepoint_events
                    .Include(l => l.file_name)
                    .Include(l => l.file_extension)
                    .Include(l => l.related_web)
                    .Include(l => l.item_type)
                    .Include(l => l.Event)
                    .Include(l => l.url)
                    .Include(l => l.Event.User)
                    .Include(l => l.Event.Operation)
                    .Where(l => targetIds.Contains(l.EventID)).ToListAsync();
                Assert.IsTrue(dbLogs.Count == hitsActivity.Count);

                foreach (var dbLog in dbLogs)
                {
                    var activityLog = (SharePointAuditLogContent)hitsActivity.SingleOrDefault(a => a.Id == dbLog.EventID);
                    Assert.AreEqual(dbLog.file_name.Name, activityLog.SourceFileName);
                    Assert.AreEqual(dbLog.url.FullUrl, activityLog.ObjectId);
                    Assert.AreEqual(dbLog.file_extension.extension_name, activityLog.SourceFileExtension);
                    Assert.AreEqual(dbLog.Event.User.UserPrincipalName, activityLog.UserId);

                    Assert.AreEqual(dbLog.Event.Operation.Name, activityLog.Operation);
                    Assert.AreEqual(dbLog.Event.TimeStamp, new SqlDateTime(activityLog.CreationTime));
                    Assert.AreEqual(dbLog.Event.EventData, activityLog.EventData);
                    Assert.AreEqual(dbLog.related_web.url_base.ToLower(), StringUtils.RemoveTrailingSlash(activityLog.SiteUrl).ToLower());
                    Assert.AreEqual(dbLog.item_type.type_name, activityLog.ItemType);
                    Assert.AreEqual(dbLog.EventID, activityLog.Id);

                }
            }
        }


        [TestMethod]
        public async Task FakeSubscriptionTests()
        {
            // Get settings
            var s = GetSettings();

            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var auth = new ActivityAPIAppIndentityOAuthContext(telemetry, s.ClientID, s.TenantGUID.ToString(), s.ClientSecret, s.KeyVaultUrl, s.UseClientCertificate);


            var config = new HttpConfiguration();
            config.MapHttpAttributeRoutes();
            var server = new HttpServer(config);

            // Clear down DB
            using (var db = new FakeOfficeServicesDB())
            {
                db.Database.ExecuteSqlCommand("TRUNCATE TABLE TestingSubscriptions");
            }
            using (var fakeClient = new ConfidentialClientApplicationThrottledHttpClient(server, telemetry))
            {
                var importer = new ActivitySubscriptionManager(s, telemetry, fakeClient);

                var active = await importer.GetActiveSubscriptionContentTypes();

                // Should have no subs
                Assert.IsTrue(active.Count == 0, "There should be no subscriptions yet");

                // Should now not have but then create the configured subsubscriptions
                active = await importer.EnsureActiveSubscriptionContentTypesActive();

                Assert.IsTrue(active.Count > 0, "There should now be some subscriptions created");
            }
        }


        [TestMethod]
        public async Task FakeActivityTests()
        {
            // HACK: run FakeSubscriptionTests() first to make sure there are some subs in the fake subscriptions DB.
            // Otherwise, this test will have no subs, and therefore won't return any results
            await FakeSubscriptionTests();

            // Get settings
            var s = GetSettings();

            HttpConfiguration config = new HttpConfiguration();
            config.MapHttpAttributeRoutes();
            HttpServer server = new HttpServer(config);

            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();

            // Start new download session
            using (var fakeClient = new ConfidentialClientApplicationThrottledHttpClient(server, telemetry))
            {
                var importer = new ActivityWebImporter(fakeClient, s, telemetry);

                // Download all the things & get stats.
                var stats = await importer.LoadReportsAndSave(new ActivityReportSqlPersistenceManager(new AllowAllFilterConfig(), telemetry, s));

                var contentMetaDataLoader = new WebContentMetaDataLoader(telemetry, fakeClient, s);

                // Each activity call should return X activities per page of results, + another page
                var timeChunks = contentMetaDataLoader.GetScanningTimeChunksFromNow();

                // x2 report URLs per query, as we fake another activity page. 1 summary requested per time-chunk
                int expectedSummaryReports = Constants.ACTIVITIES_PER_SUMMARY * 2 * timeChunks.Count;

                int totalJobsWeShouldFind = (Constants.REPORTS_PER_ACTIVITY * expectedSummaryReports);

                int totalSuccesses = 0;

                totalSuccesses += stats.Imported;

                // We should have x2 reports for each job
                int expectedReportNumber = totalJobsWeShouldFind * Constants.REPORTS_PER_ACTIVITY;
                Assert.IsTrue(totalSuccesses == expectedReportNumber,
                    $"Unexpected number of reports - got {totalSuccesses}, instead of {expectedReportNumber}");
            }
        }

        AppConfig GetSettings()
        {
            try
            {
                var s = new AppConfig();
                return s;
            }
            catch (FormatException)
            {
                Console.WriteLine("Error converting configurations values to int/guid/timespan. Please verify App Settings.");
                throw;
            }
            throw new Exception("Error loading app settings");
        }
    }


    /// <summary>
    /// Testing only
    /// </summary>
    public class TestActivityReportSet : ActivityReportSet
    {
    }

}
