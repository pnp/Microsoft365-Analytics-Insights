using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using static WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents.PageUpdateEventAppInsightsQueryResult;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the perf refactor in <see cref="PageUpdateManager"/>:
    ///   - <c>SaveAll</c> now batches the post-chunk URL-refresh into
    ///     IN-clause chunks of 1000 instead of one <c>SingleOrDefaultAsync</c>
    ///     per URL (H4).
    ///   - <c>SaveChunk</c> pre-buckets the in-flight chunk by URL once instead
    ///     of running <c>chunk.Where(...)</c> with a per-item
    ///     <c>GetUrlBaseAddressIfValidUrl</c> call per matched URL (H5).
    ///
    /// These tests don't replace the existing functional tests in
    /// <see cref="AppInsightsImportTests"/> - they specifically exercise the
    /// path boundaries that the optimisation changed (cross-batch URL counts,
    /// many events per URL within a single chunk).
    /// </summary>
    [TestClass]
    public class PageUpdateManagerPerfTests
    {
        /// <summary>
        /// Crosses the URL_LOOKUP_BATCH=1000 boundary in <c>SaveAll</c>'s post-chunk
        /// URL refresh. Pre-refactor this was one <c>SingleOrDefaultAsync</c> per
        /// URL (1010 round-trips); post-refactor it's two <c>WHERE IN (...)</c>
        /// queries. Both must mark every URL as refreshed.
        /// </summary>
        [TestMethod]
        public async Task PageUpdateManager_SaveAll_BatchesAcross1000UrlBoundary()
        {
            // Just over the URL_LOOKUP_BATCH (1000) so two IN-clause round-trips run.
            const int UrlCount = 1010;
            var ticks = DateTime.Now.Ticks;
            var urlBase = $"http://perftest-batch-{ticks}/";

            // Insert URLs in the DB with stale MetadataLastRefreshed so they qualify for refresh.
            var insertedUrls = new List<Url>(UrlCount);
            using (var db = new AnalyticsEntitiesContext())
            {
                for (int i = 0; i < UrlCount; i++)
                {
                    var u = new Url { FullUrl = urlBase + i, MetadataLastRefreshed = null };
                    db.urls.Add(u);
                    insertedUrls.Add(u);
                }
                await db.SaveChangesAsync();
            }

            try
            {
                // One page-update event per URL, each with one custom property so
                // UpdateUrlMetadataWith reports "updates made" and the URL ends up
                // in the SaveAll cross-batch refresh list.
                var pageUpdates = new List<PageUpdateEventAppInsightsQueryResult>(UrlCount);
                for (int i = 0; i < UrlCount; i++)
                {
                    dynamic props = new ExpandoObject();
                    ((IDictionary<string, object>)props).Add("PerfTestProp", "v" + i);

                    pageUpdates.Add(new PageUpdateEventAppInsightsQueryResult
                    {
                        CustomProperties = new PageUpdateEventCustomProps
                        {
                            Url = urlBase + i,
                            PropsString = JsonConvert.SerializeObject(props),
                        },
                        AppInsightsTimestamp = DateTime.Now,
                    });
                }

                // Use a smaller chunk size so SaveChunk runs many times - exercises the
                // optimisation on the inner SaveChunk path AND on the cross-batch refresh.
                var pageUpdateManager = new PageUpdateManager(AnalyticsLogger.ConsoleOnlyTracer(), 100, new AppConfig());
                var saveResults = await pageUpdateManager.SaveAll(pageUpdates);

                Assert.AreEqual(UrlCount, saveResults.Count,
                    "Every URL should have been reported as updated.");

                using (var verifyDb = new AnalyticsEntitiesContext())
                {
                    var refreshed = await verifyDb.urls
                        .Where(u => u.FullUrl.StartsWith(urlBase) && u.MetadataLastRefreshed != null)
                        .CountAsync();
                    Assert.AreEqual(UrlCount, refreshed,
                        "Cross-batch refresh must mark every URL across the 1000-row IN-batch boundary.");
                }
            }
            finally
            {
                using (var cleanupDb = new AnalyticsEntitiesContext())
                {
                    var rows = await cleanupDb.urls.Where(u => u.FullUrl.StartsWith(urlBase)).ToListAsync();
                    cleanupDb.urls.RemoveRange(rows);
                    // FileMetadataPropertyValues will be cascade-deleted by the FK on urls.
                    await cleanupDb.SaveChangesAsync();
                }
            }
        }

        /// <summary>
        /// Pre-refactor <see cref="PageUpdateManager"/> ran
        /// <c>chunk.Where(p => GetUrlBaseAddressIfValidUrl(p.Url) == urlToUpdate.FullUrl).ToList()</c>
        /// inside the foreach over matched URLs - O(events * urls) per chunk. The
        /// refactor pre-buckets the chunk by URL once. This test puts multiple events
        /// against the same URL in a single chunk and asserts that all their custom
        /// properties end up applied (i.e. the bucket lookup didn't lose events).
        /// </summary>
        [TestMethod]
        public async Task PageUpdateManager_SaveChunk_MultipleEventsPerUrl_AllPropsApplied()
        {
            var ticks = DateTime.Now.Ticks;
            var url = $"http://perftest-bucket-{ticks}/";

            // Three distinct prop names spread across three events for the same URL.
            string p1 = "BucketProp1_" + ticks;
            string p2 = "BucketProp2_" + ticks;
            string p3 = "BucketProp3_" + ticks;

            Url dbUrl;
            using (var db = new AnalyticsEntitiesContext())
            {
                dbUrl = new Url { FullUrl = url, MetadataLastRefreshed = null };
                db.urls.Add(dbUrl);
                await db.SaveChangesAsync();
            }

            try
            {
                dynamic propsA = new ExpandoObject();
                ((IDictionary<string, object>)propsA).Add(p1, "v1");

                dynamic propsB = new ExpandoObject();
                ((IDictionary<string, object>)propsB).Add(p2, "v2");

                dynamic propsC = new ExpandoObject();
                ((IDictionary<string, object>)propsC).Add(p3, "v3");

                var pageUpdates = new List<PageUpdateEventAppInsightsQueryResult>
                {
                    new PageUpdateEventAppInsightsQueryResult
                    {
                        CustomProperties = new PageUpdateEventCustomProps { Url = url, PropsString = JsonConvert.SerializeObject(propsA) },
                        AppInsightsTimestamp = DateTime.Now.AddMinutes(-2),
                    },
                    new PageUpdateEventAppInsightsQueryResult
                    {
                        CustomProperties = new PageUpdateEventCustomProps { Url = url, PropsString = JsonConvert.SerializeObject(propsB) },
                        AppInsightsTimestamp = DateTime.Now.AddMinutes(-1),
                    },
                    new PageUpdateEventAppInsightsQueryResult
                    {
                        CustomProperties = new PageUpdateEventCustomProps { Url = url, PropsString = JsonConvert.SerializeObject(propsC) },
                        AppInsightsTimestamp = DateTime.Now,
                    },
                };

                // All three events go through SaveChunk in a single chunk (chunkSize = 10).
                var pageUpdateManager = new PageUpdateManager(AnalyticsLogger.ConsoleOnlyTracer(), 10, new AppConfig());
                var saveResults = await pageUpdateManager.SaveAll(pageUpdates);
                Assert.AreEqual(1, saveResults.Count, "Single URL should be reported as updated.");

                using (var verifyDb = new AnalyticsEntitiesContext())
                {
                    var savedFieldNames = await verifyDb.FileMetadataPropertyValues
                        .Include(v => v.Field)
                        .Where(v => v.Url.ID == dbUrl.ID)
                        .Select(v => v.Field.Name)
                        .ToListAsync();

                    CollectionAssert.Contains(savedFieldNames, p1,
                        "Bucket lookup must include the event carrying " + p1);
                    CollectionAssert.Contains(savedFieldNames, p2,
                        "Bucket lookup must include the event carrying " + p2);
                    CollectionAssert.Contains(savedFieldNames, p3,
                        "Bucket lookup must include the event carrying " + p3);
                }
            }
            finally
            {
                using (var cleanupDb = new AnalyticsEntitiesContext())
                {
                    var dbValues = await cleanupDb.FileMetadataPropertyValues
                        .Include(v => v.Field)
                        .Where(v => v.Url.ID == dbUrl.ID)
                        .ToListAsync();
                    cleanupDb.FileMetadataPropertyValues.RemoveRange(dbValues);

                    var fieldNames = await cleanupDb.FileMetadataFields
                        .Where(f => f.Name == p1 || f.Name == p2 || f.Name == p3)
                        .ToListAsync();
                    cleanupDb.FileMetadataFields.RemoveRange(fieldNames);

                    var rows = await cleanupDb.urls.Where(u => u.FullUrl == url).ToListAsync();
                    cleanupDb.urls.RemoveRange(rows);
                    await cleanupDb.SaveChangesAsync();
                }
            }
        }
    }
}
