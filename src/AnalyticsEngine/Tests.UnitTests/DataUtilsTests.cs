using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Models;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.LookupCaches;
using DataUtils;
using DataUtils.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeEntities;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Engine.Entities;

namespace Tests.UnitTests
{
    [TestClass]
    public class DataUtilsTests
    {
        [TestMethod]
        public void IsValidUrlTests()
        {
            Assert.IsFalse(StringUtils.IsValidAbsoluteUrl(null));
            Assert.IsFalse(StringUtils.IsValidAbsoluteUrl(""));
            Assert.IsFalse(StringUtils.IsValidAbsoluteUrl("asdfasdf"));
            Assert.IsFalse(StringUtils.IsValidAbsoluteUrl("http://"));
            Assert.IsTrue(StringUtils.IsValidAbsoluteUrl("http://asdfasdf"));
            Assert.IsTrue(StringUtils.IsValidAbsoluteUrl("http://asdfasdf.com"));
            Assert.IsTrue(StringUtils.IsValidAbsoluteUrl("http://asdfasdf.com/"));
            Assert.IsTrue(StringUtils.IsValidAbsoluteUrl("http://asdfasdf.com/asdfasdf"));
            Assert.IsTrue(StringUtils.IsValidAbsoluteUrl("http://asdfasdf.com/asdfasdf/"));
            Assert.IsTrue(StringUtils.IsValidAbsoluteUrl("https://contoso.sharepoint.com/sites/site1/_layouts/15/Doc.aspx?sourcedoc=%7BF2CB77E7-186C-4F9A-B949-FA078F48AA53%7D&file=RD%20Consejer%C3%ADa%20en%20el%20exterior%20v.1.docx&action=default&mobileredirect=true"));
        }

        [TestMethod]
        public void DatabaseUpgraderTests()
        {
            var config = new AppConfig();
            var initInfo = new DatabaseUpgradeInfo { ConnectionString = config.ConnectionStrings.DatabaseConnectionString };

            config.ConnectionStrings.TestSQLSettings(AnalyticsLogger.ConsoleOnlyTracer());
            DatabaseUpgrader.CheckDbUpgraded(initInfo, (s) => Console.WriteLine(s));
        }

        [TestMethod]
        public async Task ParallelCallsForSingleReturnListHanderTests()
        {
            var p = new ParallelCallsForSingleReturnListHander<int, string>();

            // Test we get the same number of results back as went in
            var result = await p.CallAndCompileToSingleList(new List<int> { 1, 2, 3, 4, 5 }, (chunk) =>
            {
                var l = new List<string>();
                foreach (var i in chunk)
                {
                    l.Add(i.ToString());
                }
                return Task.FromResult(l);
            }, 2);

            Assert.IsTrue(result.Count == 5);
        }

        [TestMethod]
        public async Task ParallelCallsForSingleReturnListHander_PropagatesChunkExceptions()
        {
            // A faulting chunk must surface to the caller, not be silently swallowed (see the
            // ParallelListProcessor fix). Previously chunk exceptions vanished and callers were told
            // everything succeeded.
            var p = new ParallelCallsForSingleReturnListHander<int, string>();
            Func<List<int>, Task<List<string>>> faulting = (chunk) => throw new InvalidOperationException("boom");
            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                p.CallAndCompileToSingleList(new List<int> { 1, 2, 3 }, faulting, 1));
            Assert.AreEqual("boom", ex.Message);
        }

        [TestMethod]
        public void GetUrlBaseAddressIfValidUrl()
        {
            // Test GetUrlBaseAddress
            Assert.IsNull(StringUtils.GetUrlBaseAddressIfValidUrl(null));
            Assert.AreEqual(StringUtils.GetUrlBaseAddressIfValidUrl(""), string.Empty);
            Assert.AreEqual(StringUtils.GetUrlBaseAddressIfValidUrl("123"), "123");

            Assert.IsTrue(StringUtils.GetUrlBaseAddressIfValidUrl("https://contoso.sharepoint.com")
                == "https://contoso.sharepoint.com/");      // Will add trailing slash

            Assert.IsTrue(StringUtils.GetUrlBaseAddressIfValidUrl("https://contoso.sharepoint.com/")
                == "https://contoso.sharepoint.com/");

            Assert.IsTrue(StringUtils.GetUrlBaseAddressIfValidUrl("https://contoso.sharepoint.com/sites/Doc1.aspx")
                == "https://contoso.sharepoint.com/sites/Doc1.aspx");

            // Clean up querystring & bookmarks
            Assert.IsTrue(StringUtils.GetUrlBaseAddressIfValidUrl("https://contoso.sharepoint.com/sites/Doc1.aspx?query1=val")
                == "https://contoso.sharepoint.com/sites/Doc1.aspx");
            Assert.IsTrue(StringUtils.GetUrlBaseAddressIfValidUrl("https://contoso.sharepoint.com/sites/Doc1.aspx?")
                == "https://contoso.sharepoint.com/sites/Doc1.aspx");

            Assert.IsTrue(StringUtils.GetUrlBaseAddressIfValidUrl("https://contoso.sharepoint.com/sites/Doc1.aspx#bookmark")
                == "https://contoso.sharepoint.com/sites/Doc1.aspx");
            Assert.IsTrue(StringUtils.GetUrlBaseAddressIfValidUrl("https://contoso.sharepoint.com/sites/Doc1.aspx#")
                == "https://contoso.sharepoint.com/sites/Doc1.aspx");
        }

        [TestMethod]
        public void RemoveXsDataParamTests()
        {
            // Null / empty / no xsdata parameter -> returned unchanged.
            Assert.IsNull(StringUtils.RemoveXsDataParam(null));
            Assert.AreEqual(string.Empty, StringUtils.RemoveXsDataParam(string.Empty));
            Assert.AreEqual("https://x/a?foo=1&bar=2", StringUtils.RemoveXsDataParam("https://x/a?foo=1&bar=2"));

            // xsdata as the first / middle / last / only parameter (everything else preserved).
            Assert.AreEqual("https://x/a.aspx?sdata=z&v=1",
                StringUtils.RemoveXsDataParam("https://x/a.aspx?xsdata=ABC&sdata=z&v=1"));
            Assert.AreEqual("https://x/a.aspx?foo=1&bar=2",
                StringUtils.RemoveXsDataParam("https://x/a.aspx?foo=1&xsdata=ABC&bar=2"));
            Assert.AreEqual("https://x/a.aspx?foo=1",
                StringUtils.RemoveXsDataParam("https://x/a.aspx?foo=1&xsdata=ABC"));
            Assert.AreEqual("https://x/a.aspx",
                StringUtils.RemoveXsDataParam("https://x/a.aspx?xsdata=ABC"));

            // Any #fragment is preserved.
            Assert.AreEqual("https://x/a.aspx#sec",
                StringUtils.RemoveXsDataParam("https://x/a.aspx?xsdata=ABC#sec"));
            Assert.AreEqual("https://x/a.aspx?b=2#sec",
                StringUtils.RemoveXsDataParam("https://x/a.aspx?xsdata=ABC&b=2#sec"));
            Assert.AreEqual("https://x/a?foo=1#sec",
                StringUtils.RemoveXsDataParam("https://x/a?foo=1&xsdata=ABC#sec"));

            // Case-insensitive on the parameter name.
            Assert.AreEqual("https://x/a?b=2", StringUtils.RemoveXsDataParam("https://x/a?XSDATA=ABC&b=2"));

            // Boundary-aware: 'xsdata' as a substring of another parameter name is NOT removed.
            Assert.AreEqual("https://x/a?notxsdata=1&b=2", StringUtils.RemoveXsDataParam("https://x/a?notxsdata=1&b=2"));
            Assert.AreEqual("https://x/a?b=1&myxsdata=2", StringUtils.RemoveXsDataParam("https://x/a?b=1&myxsdata=2"));

            // Real-world shape: a long Teams deep-link token as the first parameter, followed by the
            // usual trailing parameters. The cleaned URL keeps the rest and no longer contains xsdata.
            var basePath = "https://contoso.sharepoint.com/sites/Marketing/Lists/Announcements/AllItems.aspx";
            var rest = "&sdata=AAAA%3D%3D&ovuser=user%40contoso.com&viewid=00000000";
            var oversized = basePath + "?xsdata=" + new string('Q', 900) + rest;
            var cleaned = StringUtils.RemoveXsDataParam(oversized);
            Assert.AreEqual(basePath + "?" + rest.TrimStart('&'), cleaned);
            Assert.IsFalse(cleaned.ToLowerInvariant().Contains("xsdata="));
            Assert.IsTrue(cleaned.Length < oversized.Length);
        }

        [TestMethod]
        public void EnsureUrlWithinLengthTests()
        {
            const int max = 50;
            Assert.IsNull(StringUtils.EnsureUrlWithinLength(null, max));

            // Has xsdata but is within the limit -> left unchanged (no needless churn).
            var shortWithXsData = "https://x/a?xsdata=AB&b=2"; // <= 50 chars
            Assert.IsTrue(shortWithXsData.Length <= max);
            Assert.AreEqual(shortWithXsData, StringUtils.EnsureUrlWithinLength(shortWithXsData, max));

            // Over the limit and has xsdata -> stripping xsdata alone is enough; the rest is preserved.
            var longWithXsData = "https://x/a.aspx?xsdata=" + new string('Q', 100) + "&b=2";
            Assert.IsTrue(longWithXsData.Length > max);
            Assert.AreEqual("https://x/a.aspx?b=2", StringUtils.EnsureUrlWithinLength(longWithXsData, max));

            // Over the limit, no xsdata -> nothing to strip, so reduced to the page path (not truncated).
            var longNoXsData = "https://x/a.aspx?b=" + new string('Q', 100);
            Assert.AreEqual("https://x/a.aspx", StringUtils.EnsureUrlWithinLength(longNoXsData, max));

            // Over the limit AND still over after stripping xsdata -> reduced to the page path.
            var stillLong = "https://x/a.aspx?xsdata=" + new string('Q', 100) + "&" + new string('Z', 100);
            Assert.AreEqual("https://x/a.aspx", StringUtils.EnsureUrlWithinLength(stillLong, max));

            // Reducing to path drops the query string AND any #fragment.
            var withFragment = "https://x/a.aspx?notxs=" + new string('P', 100) + "#section";
            Assert.AreEqual("https://x/a.aspx", StringUtils.EnsureUrlWithinLength(withFragment, max));

            // Pathological: even the bare path exceeds the limit -> hard-truncated as an absolute last resort.
            var longPath = "https://x/" + new string('a', 100); // no '?' or '#'
            var truncated = StringUtils.EnsureUrlWithinLength(longPath, max);
            Assert.AreEqual(max, truncated.Length);
            Assert.AreEqual(longPath.Substring(0, max), truncated);

            // Real column width: a 900-char xsdata token pushes a SharePoint URL over 850, but once
            // removed it comfortably fits, so the rest (filters etc.) is kept in full - NOT reduced to path.
            var oversized = "https://contoso.sharepoint.com/sites/Marketing/a.aspx?xsdata=" + new string('Q', 900) + "&v=1";
            Assert.IsTrue(oversized.Length > Url.FullUrlMaxLength);
            var result = StringUtils.EnsureUrlWithinLength(oversized, Url.FullUrlMaxLength);
            Assert.AreEqual("https://contoso.sharepoint.com/sites/Marketing/a.aspx?v=1", result);
            Assert.IsTrue(result.Length <= Url.FullUrlMaxLength);

            // Real column width: still over 850 even after xsdata (a huge SafelinksUrl) -> reduced to path.
            var basePath = "https://contoso.sharepoint.com/sites/Marketing/Lists/Announcements/AllItems.aspx";
            var stuck = basePath + "?xsdata=" + new string('Q', 900) + "&SafelinksUrl=" + new string('Z', 900);
            Assert.IsTrue(stuck.Length > Url.FullUrlMaxLength);
            var stuckResult = StringUtils.EnsureUrlWithinLength(stuck, Url.FullUrlMaxLength);
            Assert.AreEqual(basePath, stuckResult);
            Assert.IsTrue(stuckResult.Length <= Url.FullUrlMaxLength);
        }

        /// <summary>
        /// Locks in the behaviour against the shape of the real customer URLs that aborted the
        /// ShrinkUrlsFullUrlColumn migration: SharePoint list/page URLs opened from Microsoft Teams,
        /// carrying a large transient xsdata token plus the usual sdata/ovuser/OR/CT/clickparams/
        /// SafelinksUrl/Filter*/viewid tail (sometimes comma-merged). All values here are anonymised
        /// (contoso tenant, fictional site/list, zeroed GUIDs, filler tokens) - no real tenant data.
        /// </summary>
        [TestMethod]
        public void EnsureUrlWithinLength_RealWorldTeamsDeepLinkExamples()
        {
            const int max = 850; // Url.FullUrlMaxLength

            var path = "https://contoso.sharepoint.com/sites/Marketing/Lists/Announcements/AllItems.aspx";
            var xsdata = "xsdata=" + new string('A', 700);   // ~700-char opaque base64 deep-link token
            var trailing =
                "&sdata=" + new string('B', 40) +
                "&ovuser=00000000%2D0000%2D0000%2D0000%2D000000000000%2Cuser%40contoso.com" +
                "&OR=Teams%2DHL&CT=1677594627797" +
                "&clickparams=" + new string('C', 80) +
                "&FilterField1=field%5F1&FilterValue1=Manager%20Transport&FilterType1=Text" +
                "&viewid=00000000%2D0000%2D0000%2D0000%2D000000000000";

            // Case 1 (mirrors the ~99% auto-fixable rows): xsdata is the bulk of the bloat. Removing it
            // alone brings the URL back under 850 and keeps the distinguishing params (filters, viewid).
            var teamsUrl = path + "?" + xsdata + trailing;
            Assert.IsTrue(teamsUrl.Length > max, "sample should exceed the column width");
            var fixedUrl = StringUtils.EnsureUrlWithinLength(teamsUrl, max);
            Assert.AreEqual(path + "?" + trailing.Substring(1), fixedUrl); // '?' kept, 'xsdata=...&' removed
            Assert.IsTrue(fixedUrl.Length <= max);
            Assert.IsFalse(fixedUrl.ToLowerInvariant().Contains("xsdata="));
            StringAssert.Contains(fixedUrl, "viewid=");                    // distinguishing params preserved

            // Case 2 (mirrors the handful of genuinely-stuck rows): still over 850 even after removing
            // xsdata, because of a huge SafelinksUrl copy + duplicated comma-merged params. Reduced to path.
            var safelinks = "&SafelinksUrl=" + new string('D', 700);
            var doubled = "&OR=Teams%2DHL%2CTeams%2DHL&CT=1698908960461%2C1698908960461";
            var stuckUrl = path + "?" + xsdata + trailing + safelinks + doubled;
            Assert.IsTrue(StringUtils.RemoveXsDataParam(stuckUrl).Length > max, "should still exceed 850 after xsdata strip");
            var reduced = StringUtils.EnsureUrlWithinLength(stuckUrl, max);
            Assert.AreEqual(path, reduced);                                // whole query string dropped
            Assert.IsTrue(reduced.Length <= max);

            // Case 3: a URL ending with a bare '#' fragment marker (seen in the real data).
            var withTrailingHash = path + "?xsdata=" + new string('A', 800) + "&viewid=00000000#";
            Assert.IsTrue(withTrailingHash.Length > max);
            var fixedHash = StringUtils.EnsureUrlWithinLength(withTrailingHash, max);
            Assert.IsTrue(fixedHash.Length <= max);
            Assert.IsFalse(fixedHash.ToLowerInvariant().Contains("xsdata="));
            StringAssert.StartsWith(fixedHash, path);
        }

        [TestMethod]
        public void ConvertSharePointUrl()
        {
            const string expectedResult = "https://contoso.sharepoint.com/sites/site/Shared%20Documents/";

            const string input = "https://contoso.sharepoint.com/sites/site/Shared Documents/";
            Assert.IsFalse(Uri.IsWellFormedUriString(input, UriKind.Absolute));
            Assert.IsTrue(StringUtils.ConvertSharePointUrl(input) == expectedResult);
            Assert.IsTrue(Uri.IsWellFormedUriString(expectedResult, UriKind.Absolute));

        }
        [TestMethod]
        public void IUrlObjectResolver()
        {
            var urls = new List<Url>()
            {
                new Url { FullUrl = "https://server/SITE1" },
                new Url { FullUrl = "https://server/site1/sub1" },
                new Url { FullUrl = "https://server/site2" },
                null
            };

            Assert.IsTrue(urls.GetClosest("https://server/site1/doc.aspx").FullUrl.ToLower() == "https://server/site1");
            Assert.IsTrue(urls.GetClosest("https://server/site1/sub1/doc.aspx").FullUrl == "https://server/site1/sub1");
            Assert.IsNull(urls.GetClosest("https://server/site3/doc.aspx"));

        }

        [TestMethod]
        public async Task CacheEncodingTest()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var c = new UserJobTitleCache(db);

                const string n1 = "GEsundheits- und Krankenpfleger im Aussendienst";
                const string n2 = "Gesundheits- und Krankenpfleger im Außendienst";     // Rando German char.
                                                                                        // In SQL it's the same as above name, even though it's not in .Net normally
                Assert.AreNotEqual(n1, n2);

                var jobTitle = await c.GetOrCreateNewResource(n1, new UserJobTitle { Name = n1 });
                var jobTitle2 = await c.GetOrCreateNewResource(n2, new UserJobTitle { Name = n2 });

                await db.SaveChangesAsync();

                Assert.AreSame(jobTitle, jobTitle2);
            }
        }

        [TestMethod]
        public async Task ObjectByIdCacheTests()
        {
            var c = new TestObjectByIdCache();
            await Assert.ThrowsExceptionAsync<ArgumentException>(async () => await c.GetResource(null));

            var id1 = Guid.NewGuid().ToString();

            // Not found yet
            await Assert.ThrowsExceptionAsync<ArgumentException>(async () => await c.GetResource(id1));

            // Normie execution
            c.AddId(id1.ToUpper());
            var r = await c.GetResource(id1);
            Assert.IsNotNull(r);

            var r2 = await c.GetResource(id1);
            Assert.AreSame(r, r2);

            // Call again but override the "not found anywhere" callback
            var id2 = Guid.NewGuid().ToString();
            var r3 = await c.GetResource(id2, () => Task.FromResult(r2));
            Assert.AreSame(r2, r3);

            // Id2 should now be cached. Get with UPPER
            var r4 = await c.GetResource(id2.ToUpper());
            Assert.AreSame(r4, r3);

            var nullResult = await c.GetResourceOrNullIfNotExists(Guid.NewGuid().ToString());
            Assert.IsNull(nullResult);

            // Test concurrent access (verifies SemaphoreSlim deadlock fix)
            var concurrentCache = new TestObjectByIdCache();
            var concurrentIds = new List<string>();
            for (int i = 0; i < 50; i++)
            {
                var id = Guid.NewGuid().ToString();
                concurrentIds.Add(id);
                concurrentCache.AddId(id);
            }

            // Access cache from multiple threads simultaneously
            var tasks = new List<Task<TestObject>>();
            foreach (var id in concurrentIds)
            {
                tasks.Add(Task.Run(async () => await concurrentCache.GetResource(id)));
            }

            var results = await Task.WhenAll(tasks);
            Assert.AreEqual(concurrentIds.Count, results.Length);
            Assert.IsTrue(results.All(result => result != null));

            // Test concurrent access with callback (verifies no deadlock with async callback)
            var callbackCache = new TestObjectByIdCache();
            var callbackTasks = new List<Task<TestObject>>();
            var callbackIds = new List<string>();

            for (int i = 0; i < 20; i++)
            {
                var id = Guid.NewGuid().ToString();
                callbackIds.Add(id);

                // Simulate async load with delay
                callbackTasks.Add(Task.Run(async () =>
                    await callbackCache.GetResource(id, async () =>
                    {
                        await Task.Delay(10); // Simulate async work
                        return new TestObject();
                    })));
            }

            var callbackResults = await Task.WhenAll(callbackTasks);
            Assert.AreEqual(callbackIds.Count, callbackResults.Length);
            Assert.IsTrue(callbackResults.All(result => result != null));

            // Verify objects are properly cached after concurrent callback creation
            foreach (var id in callbackIds)
            {
                var cached = await callbackCache.GetResource(id);
                Assert.IsNotNull(cached);
            }
        }

        /// <summary>
        /// Regression (B6/D6): the cache used to hold one lock across every load, so two unrelated ids could not be
        /// looked up at the same time and even a cache hit queued behind somebody else's slow (e.g. throttled) Graph call.
        /// </summary>
        [TestMethod]
        public async Task ObjectByIdCache_UnrelatedKeysLoadConcurrently_AndAHitNeverWaitsBehindALoad()
        {
            var cache = new GatedObjectByIdCache();
            cache.Release("primed");
            var primed = await cache.GetResource("primed");

            var slowA = cache.GetResource("slow-a");
            var slowB = cache.GetResource("slow-b");
            Assert.IsTrue(await cache.WaitUntilLoading(TimeSpan.FromSeconds(10), "slow-a", "slow-b"),
                "Two unrelated ids must be able to load at the same time.");

            var hit = cache.GetResource("PRIMED");
            Assert.AreSame(hit, await Task.WhenAny(hit, Task.Delay(TimeSpan.FromSeconds(10))),
                "A cache hit must not wait for an unrelated load to finish.");
            Assert.AreSame(primed, await hit);

            cache.Release("slow-a");
            cache.Release("slow-b");
            await Task.WhenAll(slowA, slowB);
        }

        /// <summary>
        /// Callers asking for the same id share one load, and a load that failed is retried by the next caller rather
        /// than remembered.
        /// </summary>
        [TestMethod]
        public async Task ObjectByIdCache_SameKeySharesOneLoad_AndAFailedLoadIsRetried()
        {
            var cache = new GatedObjectByIdCache();
            var callers = Enumerable.Range(0, 10).Select(i => cache.GetResource(i % 2 == 0 ? "shared" : "SHARED")).ToList();
            Assert.IsTrue(await cache.WaitUntilLoading(TimeSpan.FromSeconds(10), "shared"));
            cache.Release("shared");
            var results = await Task.WhenAll(callers);
            Assert.IsTrue(results.All(r => ReferenceEquals(r, results[0])));
            Assert.AreEqual(1, cache.LoadCount("shared"), "Concurrent callers for one id share a single load.");

            cache.FailNextLoad("flaky");
            cache.Release("flaky");
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => cache.GetResource("flaky"));
            Assert.IsNotNull(await cache.GetResource("flaky"), "A failed load must not be cached.");
            Assert.AreEqual(2, cache.LoadCount("flaky"));
        }

        /// <summary>A cache whose loads wait until the test releases them, recording which ids are loading.</summary>
        class GatedObjectByIdCache : ObjectByIdCache<TestObject>
        {
            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> _gates
                = new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>>(StringComparer.OrdinalIgnoreCase);
            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _loads
                = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _failNext
                = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            private TaskCompletionSource<bool> Gate(string id) =>
                _gates.GetOrAdd(id, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

            public void Release(string id) => Gate(id).TrySetResult(true);

            public void FailNextLoad(string id) => _failNext[id] = true;

            public int LoadCount(string id) => _loads.TryGetValue(id, out var count) ? count : 0;

            public async Task<bool> WaitUntilLoading(TimeSpan timeout, params string[] ids)
            {
                var deadline = DateTime.UtcNow + timeout;
                while (!ids.All(id => LoadCount(id) > 0))
                {
                    if (DateTime.UtcNow > deadline) return false;
                    await Task.Delay(10);
                }
                return true;
            }

            public override async Task<TestObject> Load(string id)
            {
                _loads.AddOrUpdate(id, 1, (_, count) => count + 1);
                await Gate(id).Task;
                if (_failNext.TryRemove(id, out _))
                {
                    throw new InvalidOperationException("Synthetic load failure");
                }
                return new TestObject();
            }
        }
        class TestObjectByIdCache : ObjectByIdCache<TestObject>
        {
            List<string> ids = new List<string>();
            public override Task<TestObject> Load(string id)
            {
                if (!ids.Contains(id.ToLower()))
                {
                    TestObject nullObj = null;
                    return Task.FromResult(nullObj);
                }
                return Task.FromResult(new TestObject());
            }

            public void AddId(string id)
            {
                ids.Add(id.ToLower());
            }
        }
        class TestObject
        {
        }

        [TestMethod]
        public void JsonDecodeFromPropValueString()
        {
            // Test string needing decoding
            var obj = StringUtils.JsonDecodeFromPropValueString(Properties.Resources.StringifiedJson);
            Assert.IsNotNull(obj);

            // Normal json but with some prop with stringified (no decoding needed)
            const string str = @"{""PublishingPageContent"": ""<h2>Welcome to Your Publishing Site</h2><h3 class=\""ms-rteElement-H3\"">These links will help you get started.</h3>""}";
            obj = StringUtils.JsonDecodeFromPropValueString(str);
            Assert.IsNotNull(obj);
        }

        [TestMethod]
        public async Task ListBatchProcessor()
        {
            // Test BufferSize moves
            var listBatchProcessor = new ListBatchProcessor<int>(10, (newChunk) => Task.CompletedTask);
            await listBatchProcessor.Add(1);
            Assert.IsTrue(listBatchProcessor.BufferSize > 0);

            await TestListBatch(1);
            await TestListBatch(100);
            await TestListBatch(101);
        }

        async Task TestListBatch(int size)
        {

            var resultList = new List<int>();
            var listBatchProcessor = new ListBatchProcessor<int>(10, (newChunk) =>
            {
                resultList.AddRange(newChunk);
                return Task.CompletedTask;
            });

            Assert.IsTrue(listBatchProcessor.BufferSize == 0);

            // Add direct to ListBatchProcessor, multithreaded
            var tasks = new List<Task>();
            for (int i = 0; i < size; i++)
            {
                int index = i;
                tasks.Add(Task.Run(async () => await listBatchProcessor.Add(index)));
            }
            await Task.WhenAll(tasks);

            var intList = new List<int>();
            for (int i = 0; i < size; i++)
            {
                intList.Add(i);
            }

            await listBatchProcessor.Flush();
            Assert.IsTrue(listBatchProcessor.BufferSize == 0);
            Assert.IsTrue(resultList.Count == intList.Count);
            foreach (var a in resultList)
            {
                Assert.IsTrue(intList.Contains(a));
            }

            // Reset and test again via Add(List<T>)
            resultList.Clear();
            await listBatchProcessor.AddRange(intList);
            await listBatchProcessor.Flush();
            Assert.IsTrue(resultList.Count == intList.Count);
        }

        #region InsertBatchTests

        const string TABLE_NAME = "tmp_whatever";
        const string TEMP_TABLE_NAME = "##tmp_whatever";
        [TestMethod]
        public async Task InsertBatchTests()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Normal table
                var batch = new EFInsertBatch<TestMultiPropTypeTempEntity>(db, AnalyticsLogger.ConsoleOnlyTracer());

                // Verify prop cache
                Assert.IsTrue(batch.PropCache.PropertyMappingInfo.Count > 0);

                // Save empty
                var emptyResult = await batch.SaveToStagingTable(string.Empty);
                Assert.IsTrue(emptyResult == 0);

                // Save with data
                batch.Rows.Add(new TestMultiPropTypeTempEntity());// Use class defaults
                batch.Rows.Add(new TestMultiPropTypeTempEntity { NullableIntProp = 1 });
                await batch.SaveToStagingTable($"select * from {TABLE_NAME}");

                // Verify saved data
                var selectCmd = db.Database.Connection.CreateCommand();
                await db.Database.Connection.OpenAsync();
                selectCmd.CommandText = $"select * from {TABLE_NAME}";
                var results = await selectCmd.ExecuteReaderAsync();

                // x2 records
                int count = 0;
                while (results.Read())
                {
                    // Recheck each property
                    foreach (var pi in batch.PropCache.PropertyMappingInfo)
                    {
                        var sqlFieldResult = results[pi.SqlInfo.FieldName];
                        Assert.IsNotNull(sqlFieldResult);

                        var objectVal = pi.Property.GetValue(batch.Rows[count]);
                        if (sqlFieldResult is double)
                        {
                            if (pi.Property.PropertyType == typeof(double))
                            {
                                Assert.AreEqual(sqlFieldResult, objectVal);
                            }
                            else if (pi.Property.PropertyType == typeof(float))
                            {
                                // SQL reader interprets SQL "float" fields as doubles. If the original prop is a float, the double read from SQL will not match unless rounded back to float
                                Assert.AreEqual(Convert.ToSingle(sqlFieldResult), objectVal);
                            }
                            else
                            {
                                throw new NotSupportedException("Unknown property type");
                            }
                        }
                        else if (sqlFieldResult is DBNull)
                        {
                            // Nulls are never read back as literal nulls...
                            Assert.IsTrue(objectVal == null);
                        }
                        else
                        {
                            Assert.AreEqual(sqlFieldResult, objectVal);
                        }
                    }
                    count++;
                }

                Assert.IsTrue(count == batch.Rows.Count);
                db.Database.Connection.Close();

                // Temp table tests
                var batchTemp = new EFInsertBatch<TestTempEntityTempTable>(db, AnalyticsLogger.ConsoleOnlyTracer());

                // Save empty
                await batchTemp.SaveToStagingTable(string.Empty);

                // Save with data over several threads
                for (int i = 0; i < 105; i++)
                {
                    batchTemp.Rows.Add(new TestTempEntityTempTable { Prop = "whatever " + i });
                }
                var tempUpdates = await batchTemp.SaveToStagingTable(10, $"delete from {TEMP_TABLE_NAME}");

                // Can't verify saved data as temp table will be closed
                Assert.IsTrue(tempUpdates == batchTemp.Rows.Count);
            }
        }
        [TempTableName(TABLE_NAME)]
        class TestMultiPropTypeTempEntity
        {
            [Column("guid")]
            public Guid Id { get; set; } = Guid.NewGuid();

            [Column("prop")]
            public string Prop { get; set; } = "Whatever";

            /// <summary>
            /// Specifically override the nullability of this property
            /// </summary>
            [Column("nullstringprop", true)]
            public string NullProp { get; set; } = null;

            [Column("intprop")]
            public int IntProp { get; set; } = 1;

            /// <summary>
            /// This should automatically be nullable without needing to specify it in the attribute
            /// </summary>
            [Column("nullable_intprop")]
            public int? NullableIntProp { get; set; } = null;

            [Column("floatprop")]
            public float FloatProp { get; set; } = 1.001f;

            [Column("doubleprop")]
            public double DoubleProp { get; set; } = -1.001f;

            [Column("boolprop")]
            public bool BoolProp { get; set; } = true;

            /// <summary>
            /// This should automatically be nullable without needing to specify it in the attribute
            /// </summary>
            [Column("nullable_boolprop")]
            public bool? NullableBoolProp { get; set; } = null;

            [Column("nullable_boolprop_with_value")]
            public bool? NullableBoolPropWithValue { get; set; } = false;

            [Column("prop2", ColationOverride = "SQL_Latin1_General_CP1_CS_AS")]
            public string CaseSensitiveProp { get; set; } = "OhHey";

            [Column("date")]
            public DateTime Timestamp { get; set; } = DateTime.Now;


            [Column("date_nullable")]
            public DateTime? NullableTimestamp { get; set; } = null;
        }

        [TempTableName(TEMP_TABLE_NAME)]
        class TestTempEntityTempTable
        {
            [Column("prop")]
            public string Prop { get; set; }
        }

        #endregion

        // Basic sanity for JobTimer
        [TestMethod]
        public void JobTimerTests()
        {
            var trace = AnalyticsLogger.ConsoleOnlyTracer();
            var t = new JobTimer(trace, "Unit test");
            Assert.IsTrue(t.Elapsed == TimeSpan.Zero);

            t.Start();
            Thread.Sleep(10);
            Assert.IsTrue(t.Elapsed > TimeSpan.Zero);

            Thread.Sleep(1000);

            var outStr = t.PrintElapsed();
            Assert.IsTrue(outStr.Contains("1 seconds"));

            Assert.IsTrue(t.Elapsed > TimeSpan.FromSeconds(1));

            outStr = t.StopAndPrintElapsed();
            Assert.IsNotNull(outStr);
            Assert.IsTrue(t.Elapsed == TimeSpan.Zero);

            t.Start();
            t.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedImportCycle);
            Assert.IsTrue(t.Elapsed == TimeSpan.Zero);
        }

        // Days must only appear when the run took a day or more, otherwise a multi-day
        // import is misreported as just its hours component (e.g. "14 hours" for a 1d 14h run).
        [TestMethod]
        public void JobTimerFormatElapsedDays()
        {
            // Under a day: no "day(s)" mentioned
            var under = JobTimer.FormatElapsed("Audit events import", new TimeSpan(0, 14, 28, 44));
            Assert.AreEqual("Audit events import: 14 hours, 28 mins, and 44 seconds.", under);
            Assert.IsFalse(under.Contains("day"));

            // A day or more: days are included up front
            var over = JobTimer.FormatElapsed("Audit events import", new TimeSpan(2, 14, 28, 44));
            Assert.AreEqual("Audit events import: 2 days, 14 hours, 28 mins, and 44 seconds.", over);
        }

        // The FinishedImportCycle custom event carries the elapsed time in its "context" dimension via the
        // same StopAndPrintElapsed -> ToString -> FormatElapsed path as the trace log, so the days handling
        // applies there too. This pins that wiring so the custom event can't silently drop days on its own.
        [TestMethod]
        public void JobTimerFinishedEventContextUsesFormatElapsed()
        {
            var trace = AnalyticsLogger.ConsoleOnlyTracer();
            var t = new JobTimer(trace, "Import cycle");

            var originalOut = Console.Out;
            var captured = new System.IO.StringWriter();
            try
            {
                Console.SetOut(captured);
                t.Start();
                Thread.Sleep(10);
                t.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedImportCycle);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            var output = captured.ToString();
            // e.g. New event 'FinishedImportCycle'; 'context=Import cycle: 0 hours, 0 mins, and 0 seconds.'.
            StringAssert.Contains(output, "New event 'FinishedImportCycle'");
            StringAssert.Contains(output, "context=Import cycle: ");
            StringAssert.Contains(output, "OperationName=Import cycle");
            StringAssert.Contains(output, " hours, ");
            StringAssert.Contains(output, " mins, and ");
            StringAssert.Contains(output, " seconds.");
        }

        [TestMethod]
        public void TrimStringFromStart()
        {
            var s1 = "string and that";
            const string EXTRA = " plus some extra bits";
            var s2 = s1 + EXTRA;
            var trimmed = s2.TrimStringFromStart(s1);

            Assert.IsTrue(trimmed == EXTRA);

            Assert.ThrowsException<ArgumentException>(() => "randoString".TrimStringFromStart(EXTRA));
        }

        [TestMethod]
        public void FindValueForProp()
        {
            // Documented case: value terminated by ';'.
            Assert.AreEqual("SPOInsightsDev", StringUtils.FindValueForProp("Initial Catalog=SPOInsightsDev;", "initial catalog"));

            // Property in the middle of the string.
            Assert.AreEqual("myserver", StringUtils.FindValueForProp("Data Source=myserver;Initial Catalog=db;", "data source"));

            // Regression: a property that is the LAST token with no trailing ';' used to throw
            // ArgumentOutOfRangeException (IndexOf returned -1, fed straight into Substring length).
            Assert.AreEqual("db", StringUtils.FindValueForProp("Data Source=myserver;Initial Catalog=db", "initial catalog"));

            // Case-insensitive property-name match.
            Assert.AreEqual("db", StringUtils.FindValueForProp("DATA SOURCE=srv;INITIAL CATALOG=db", "initial catalog"));

            // Real ManageDataControl usage: a connection string with no trailing ';' for both props.
            const string connStr = "Data Source=tcp:contoso.database.windows.net;Initial Catalog=AnalyticsDb";
            Assert.AreEqual("tcp:contoso.database.windows.net", StringUtils.FindValueForProp(connStr, "data source"));
            Assert.AreEqual("AnalyticsDb", StringUtils.FindValueForProp(connStr, "initial catalog"));

            // Missing property -> empty string (previously threw / returned garbage).
            Assert.AreEqual(string.Empty, StringUtils.FindValueForProp("Data Source=srv;", "initial catalog"));

            // Null / empty inputs -> empty string rather than throwing.
            Assert.AreEqual(string.Empty, StringUtils.FindValueForProp(null, "data source"));
            Assert.AreEqual(string.Empty, StringUtils.FindValueForProp("", "data source"));
            Assert.AreEqual(string.Empty, StringUtils.FindValueForProp("Data Source=srv;", null));
        }

        [TestMethod]
        public void RedactSqlConnectionStringTests()
        {
            // The exact shape leaked in installer logs before redaction (issue: plain-text password in logs).
            const string connStr = "data source=contoso-sql.database.windows.net;initial catalog=officeanalytics;" +
                "persist security info=True;user id=sqladmin;password=FakePwd01.;MultipleActiveResultSets=True;Connection Timeout=120";

            var cleaned = StringUtils.RedactSqlConnectionString(connStr);

            // Password is gone; non-secret diagnostics preserved.
            Assert.IsFalse(cleaned.Contains("FakePwd01."), "Plain-text password must never appear in a redacted string");
            Assert.IsTrue(cleaned.Contains(StringUtils.RedactedSecretMarker));
            Assert.IsTrue(cleaned.Contains("contoso-sql.database.windows.net"));
            Assert.IsTrue(cleaned.Contains("officeanalytics"));
            Assert.IsTrue(cleaned.Contains("sqladmin"));

            // 'pwd' alias is redacted too.
            Assert.IsFalse(StringUtils.RedactSqlConnectionString("Server=s;Database=d;Uid=u;Pwd=secretpass;").Contains("secretpass"));

            // No password -> non-secret content (server name) preserved; builder normalizes Server->Data Source.
            var noPwd = StringUtils.RedactSqlConnectionString("Server=s;Database=d;Integrated Security=true;");
            Assert.IsTrue(noPwd.Contains("Data Source=s"));
            Assert.IsTrue(noPwd.Contains("Initial Catalog=d"));

            // Null / empty inputs are passed through, not thrown on.
            Assert.IsNull(StringUtils.RedactSqlConnectionString(null));
            Assert.AreEqual(string.Empty, StringUtils.RedactSqlConnectionString(string.Empty));

            // Unparseable garbage still gets the password stripped via the regex fallback.
            Assert.IsFalse(StringUtils.RedactSqlConnectionString("garbage password=topsecret rubbish").Contains("topsecret"));
        }

        [TestMethod]
        public void IsJson()
        {
            Assert.IsFalse(StringUtils.IsJson("false"));
            Assert.IsTrue(StringUtils.IsJson("{}"));
            Assert.IsTrue(StringUtils.IsJson("{ \"prop\": \"val\", \"test\": true }"));
            Assert.IsTrue(StringUtils.IsJson("[]"));
            Assert.IsTrue(StringUtils.IsJson("[ { \"prop\": \"val\"} ]"));
        }

        [TestMethod]
        public void StringProtectTests()
        {
            var randomString = "String" + DateTime.Now.Ticks;
            var encryptedPayload = StringUtils.ProtectString(randomString);
            //Assert.AreNotEqual(randomString, encryptedPayload);
            var unencryptedString = StringUtils.UnprotectString(encryptedPayload);
            Assert.AreEqual(randomString, unencryptedString);

            // Should fail
            Assert.ThrowsException<CryptographicException>(() => StringUtils.UnprotectString(System.Text.Encoding.UTF8.GetBytes("asdfasdf")));

        }
        [TestMethod]
        public void SecureLocalPreferencesTests()
        {
            var p = new UnitTestsPreferences() { NumberVal = DateTime.Now.Ticks, StringVal = "Whatever " + DateTime.Now.Ticks };

            var savedP = p.SaveToTempFile();
            Assert.IsNotNull(savedP);
            Assert.IsTrue(System.IO.File.Exists(savedP.FullName));

            var loadedP = SecureLocalPreferences.Load<UnitTestsPreferences>();
            Assert.AreEqual(loadedP.NumberVal, p.NumberVal);
            Assert.AreEqual(loadedP.StringVal, p.StringVal);

            // Test with no local saved data. Should return new object anyway
            p.DeleteTempFile();
            SecureLocalPreferences.Load<UnitTestsPreferences>();
        }
        public class UnitTestsPreferences : SecureLocalPreferences
        {
            public string StringVal { get; set; }
            public long NumberVal { get; set; }

            protected override string FileTitle => "test.dat";
        }


        [TestMethod]
        public void UrlFilterTests()
        {
            var orgUrlConfigs = new List<FilterUrlConfig>();

            string ROOT_SITE_PREFIX1 = "https://unittesting.sharepoint.local";

            // Test empty
            Assert.IsTrue(orgUrlConfigs.UrlInScope(ROOT_SITE_PREFIX1, ROOT_SITE_PREFIX1.ToUpper() + "/file"));
            Assert.IsTrue(orgUrlConfigs.UrlInScope(String.Empty, ROOT_SITE_PREFIX1.ToUpper() + "/file"));

            // Add 1st rule
            orgUrlConfigs.Add(new FilterUrlConfig { Url = ROOT_SITE_PREFIX1 });

            // Mix case test
            Assert.IsTrue(orgUrlConfigs.UrlInScope(ROOT_SITE_PREFIX1, ROOT_SITE_PREFIX1.ToUpper() + "/file"));

            // Child site URL of a rule and exact match not specified, so should match
            Assert.IsTrue(orgUrlConfigs.UrlInScope(ROOT_SITE_PREFIX1 + "/subsite", ROOT_SITE_PREFIX1 + "/subsite/file"));

            Assert.IsFalse(orgUrlConfigs.UrlInScope("http://urlfromanothersite", "http://urlfromanothersite/page"));


            string ROOT_SITE_PREFIX2 = "https://unittesting2.sharepoint.local";
            orgUrlConfigs.Add(new FilterUrlConfig { Url = ROOT_SITE_PREFIX2, ExactSiteMatch = true });

            Assert.IsTrue(orgUrlConfigs.UrlInScope(ROOT_SITE_PREFIX2, ROOT_SITE_PREFIX2 + "/file"));

            // Child site URL of a rule but exact match specified, so should not match. Mix CASES
            Assert.IsFalse(orgUrlConfigs.UrlInScope(ROOT_SITE_PREFIX2.ToUpper() + "/subsite", ROOT_SITE_PREFIX2 + "/subsite/file"));

            // Test root SC scenario
            var rootSCUrlConfigs = new List<FilterUrlConfig>();

            rootSCUrlConfigs.Add(new FilterUrlConfig { ExactSiteMatch = true, Url = ROOT_SITE_PREFIX1 });
            rootSCUrlConfigs.Add(new FilterUrlConfig { ExactSiteMatch = false, Url = $"{ROOT_SITE_PREFIX1}/sites/site1" });

            // Subsite of root1 rule (exact). Should not be in scope.
            Assert.IsFalse(rootSCUrlConfigs.UrlInScope($"{ROOT_SITE_PREFIX1}/sites/site2", $"{ROOT_SITE_PREFIX1}/sites/site2/file"));

            Assert.IsTrue(rootSCUrlConfigs.UrlInScope(ROOT_SITE_PREFIX1, ROOT_SITE_PREFIX1 + "/file"));

            // Another site not in rules. Not in scope.
            Assert.IsFalse(rootSCUrlConfigs.UrlInScope(ROOT_SITE_PREFIX2, ROOT_SITE_PREFIX2 + "/file"));

            // Site-collection has no exact match req. Should be in scope.
            Assert.IsTrue(rootSCUrlConfigs.UrlInScope($"{ROOT_SITE_PREFIX1}/sites/site1/sub1", $"{ROOT_SITE_PREFIX1}/sites/site1/sub1/file"));
        }

        [TestMethod]
        public void ImportJobSettingsTests()
        {
            Assert.IsTrue(new ImportTaskSettings().Equals(new ImportTaskSettings()));

            // All [ImportProp] flags default to false (opt-in). Enabling a subset must not equal a fresh default.
            var someEnabled = new ImportTaskSettings() { GraphTeams = true, GraphUsageReports = true, GraphUsersMetadata = true };
            Assert.IsFalse(someEnabled.Equals(new ImportTaskSettings()));

            var someEnabled2 = new ImportTaskSettings(someEnabled.ToSettingsString());
            Assert.IsTrue(someEnabled2.Equals(someEnabled));

            // Check doesn't blow up
            new ImportTaskSettings(null);
        }

        [TestMethod]
        public void TimePeriodTests()
        {
            var d1 = DateTime.ParseExact("01-01-2019 09:00:00", "dd-MM-yyyy HH:mm:ss", null);
            var d2 = DateTime.ParseExact("01-01-2019 09:00:01", "dd-MM-yyyy HH:mm:ss", null);
            var bothDTsSame = new TimePeriod(d1, d1);

            Assert.IsTrue(bothDTsSame.InRange(d1));
            Assert.IsFalse(bothDTsSame.InRange(DateTime.ParseExact("01-01-2019 09:00:01", "dd-MM-yyyy HH:mm:ss", null)));

            var differentDts = new TimePeriod(d1, d2);
            Assert.IsTrue(differentDts.InRange(d1));
            Assert.IsTrue(differentDts.InRange(d2));


            var d1Plus3days = d1.AddDays(3);
            var totalTestTP = new TimePeriod(d1, d1Plus3days);

            var dayChunks = TimePeriod.GetScanningTimeChunksFrom(d1, d1Plus3days);
            foreach (var dayChunk in dayChunks)
            {
                Assert.IsTrue(totalTestTP.InRange(dayChunk.Start));
                Assert.IsTrue(totalTestTP.InRange(dayChunk.End));
            }

            // Test invalid range throws exception
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => TimePeriod.GetScanningTimeChunksFrom(d2, d1));

            // Test that chunks are 24 hours apart (no overlap)
            var dayChunksNoOverlap = TimePeriod.GetScanningTimeChunksFrom(d1, d1Plus3days, 0);
            if (dayChunksNoOverlap.Count > 1)
            {
                for (int i = 0; i < dayChunksNoOverlap.Count - 1; i++)
                {
                    var chunk1End = dayChunksNoOverlap[i].End;
                    var chunk2Start = dayChunksNoOverlap[i + 1].Start;
                    Assert.AreEqual(chunk1End, chunk2Start, "Chunks should be consecutive with no overlap");
                }
            }
        }

        [TestMethod]
        public void TimePeriodOverlapTests()
        {
            // Test with 5-minute overlap
            var startDate = DateTime.ParseExact("01-01-2019 00:00:00", "dd-MM-yyyy HH:mm:ss", null);
            var endDate = startDate.AddDays(3);
            const int OVERLAP_MINUTES = 5;

            var chunksWithOverlap = TimePeriod.GetScanningTimeChunksFrom(startDate, endDate, OVERLAP_MINUTES);

            // Verify chunks overlap by exactly 5 minutes
            Assert.IsTrue(chunksWithOverlap.Count > 1, "Should have multiple chunks for 3-day period");

            for (int i = 0; i < chunksWithOverlap.Count - 1; i++)
            {
                var chunk1 = chunksWithOverlap[i];
                var chunk2 = chunksWithOverlap[i + 1];

                // The second chunk should start 5 minutes before the first chunk ends
                var expectedChunk2Start = chunk1.End.AddMinutes(-OVERLAP_MINUTES);
                Assert.AreEqual(expectedChunk2Start, chunk2.Start,
                    $"Chunk {i + 1} should start {OVERLAP_MINUTES} minutes before chunk {i} ends");

                // Verify there is actually an overlap window
                var overlapStart = chunk2.Start;
                var overlapEnd = chunk1.End;
                Assert.IsTrue(overlapStart < overlapEnd, "Chunks should have overlapping time period");
                Assert.AreEqual(OVERLAP_MINUTES, (overlapEnd - overlapStart).TotalMinutes,
                    $"Overlap should be exactly {OVERLAP_MINUTES} minutes");

                // Verify that a time in the overlap period is in range for both chunks
                var timeInOverlap = overlapStart.AddMinutes(2);
                Assert.IsTrue(chunk1.InRange(timeInOverlap), "Time in overlap should be in first chunk");
                Assert.IsTrue(chunk2.InRange(timeInOverlap), "Time in overlap should be in second chunk");
            }

            // Test with zero overlap (should behave like original)
            var chunksNoOverlap = TimePeriod.GetScanningTimeChunksFrom(startDate, endDate, 0);
            var chunksDefault = TimePeriod.GetScanningTimeChunksFrom(startDate, endDate);

            Assert.AreEqual(chunksNoOverlap.Count, chunksDefault.Count, "Zero overlap should match default behavior");
            for (int i = 0; i < chunksNoOverlap.Count; i++)
            {
                Assert.AreEqual(chunksNoOverlap[i].Start, chunksDefault[i].Start);
                Assert.AreEqual(chunksNoOverlap[i].End, chunksDefault[i].End);
            }

            // Test with larger overlap (30 minutes)
            var chunksLargeOverlap = TimePeriod.GetScanningTimeChunksFrom(startDate, endDate, 30);
            Assert.IsTrue(chunksLargeOverlap.Count > 1, "Should have multiple chunks");

            if (chunksLargeOverlap.Count > 1)
            {
                var firstChunk = chunksLargeOverlap[0];
                var secondChunk = chunksLargeOverlap[1];
                var expectedStart = firstChunk.End.AddMinutes(-30);
                Assert.AreEqual(expectedStart, secondChunk.Start, "Should have 30-minute overlap");
            }

            // Test edge case: very short time period
            var shortPeriodStart = startDate;
            var shortPeriodEnd = startDate.AddHours(12);
            var shortPeriodChunks = TimePeriod.GetScanningTimeChunksFrom(shortPeriodStart, shortPeriodEnd, 5);

            // A 12-hour period should result in 1 chunk (the cleanup only removes chunks < 1 hour)
            Assert.IsTrue(shortPeriodChunks.Count == 1, "12-hour period should result in 1 chunk");
            Assert.AreEqual(12, (shortPeriodChunks[0].End - shortPeriodChunks[0].Start).TotalHours, "Chunk should be 12 hours long");

            // Test very short period (< 1 hour) - should be removed
            var veryShortPeriodStart = startDate;
            var veryShortPeriodEnd = startDate.AddMinutes(30);
            var veryShortPeriodChunks = TimePeriod.GetScanningTimeChunksFrom(veryShortPeriodStart, veryShortPeriodEnd, 5);

            // Should be empty after removal of chunks < 1 hour
            Assert.IsTrue(veryShortPeriodChunks.Count == 0, "Period less than 1 hour should result in no chunks after last chunk removal");

            // Test with negative overlap (should behave as zero overlap - no exception)
            var chunksNegativeOverlap = TimePeriod.GetScanningTimeChunksFrom(startDate, endDate, -10);
            Assert.IsNotNull(chunksNegativeOverlap, "Negative overlap should not throw exception");

            // Should behave same as zero overlap
            Assert.AreEqual(chunksNoOverlap.Count, chunksNegativeOverlap.Count, "Negative overlap should behave as zero overlap");
            if (chunksNegativeOverlap.Count > 1)
            {
                for (int i = 0; i < chunksNegativeOverlap.Count - 1; i++)
                {
                    var chunk1End = chunksNegativeOverlap[i].End;
                    var chunk2Start = chunksNegativeOverlap[i + 1].Start;
                    Assert.AreEqual(chunk1End, chunk2Start, "Negative overlap should behave as no overlap");
                }
            }

            // Test invalid overlap (>= 24 hours should throw)
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                TimePeriod.GetScanningTimeChunksFrom(startDate, endDate, 24 * 60),
                "Overlap >= 24 hours should throw ArgumentOutOfRangeException");

            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                TimePeriod.GetScanningTimeChunksFrom(startDate, endDate, 25 * 60),
                "Overlap > 24 hours should throw ArgumentOutOfRangeException");
        }

        // Define lists of int, and copy list into another one. Verify various lengths
        [TestMethod]
        public async Task ParallelListProcessorTests()
        {
            // Test super-quick tasks. Previously would deadlock.
            var intList = new List<int>();
            for (int i = 0; i < 50; i++)
            {
                intList.Add(i);
            }
            var superQuickListWorkload = new ParallelListProcessor<int>(1);
            await superQuickListWorkload.ProcessListInParallel(intList, (chunk, threadIndex) =>
            {
                // Some task that takes no time
                return Task.CompletedTask;
            });

            await ParallelListProcessorTest(100);   // Even number
            await ParallelListProcessorTest(0);     // Nothing to do
            await ParallelListProcessorTest(1);     // Single thing to do 
            await ParallelListProcessorTest(103);   // Odd number
            await ParallelListProcessorTest(1000);   // Big number
        }

        [TestMethod]
        public void InsertBatchConcurrency_DefaultAndClamp()
        {
            // The SQL-commit concurrency lever (issue #161 / PR #162). Defaults to the legacy 20 so
            // behaviour is unchanged unless a host lowers it; values < 1 clamp to 1.
            var original = DataUtils.Sql.Inserts.InsertBatchConcurrency.MaxConcurrentThreads;
            try
            {
                Assert.AreEqual(20, original, "Default SQL-commit concurrency should be 20 (no behaviour change unless lowered).");

                DataUtils.Sql.Inserts.InsertBatchConcurrency.MaxConcurrentThreads = 8;
                Assert.AreEqual(8, DataUtils.Sql.Inserts.InsertBatchConcurrency.MaxConcurrentThreads);

                DataUtils.Sql.Inserts.InsertBatchConcurrency.MaxConcurrentThreads = 0;
                Assert.AreEqual(1, DataUtils.Sql.Inserts.InsertBatchConcurrency.MaxConcurrentThreads, "0 must clamp to 1.");

                DataUtils.Sql.Inserts.InsertBatchConcurrency.MaxConcurrentThreads = -5;
                Assert.AreEqual(1, DataUtils.Sql.Inserts.InsertBatchConcurrency.MaxConcurrentThreads, "Negative must clamp to 1.");
            }
            finally
            {
                DataUtils.Sql.Inserts.InsertBatchConcurrency.MaxConcurrentThreads = original;
            }
        }

        [TestMethod]
        public async Task ParallelListProcessor_PropagatesChunkExceptions()
        {
            // Regression guard: a chunk that throws must surface the exception to the caller. The old
            // ContinueWith(_ => _sem.Release()) tracked the always-successful release continuation
            // instead of the work, so chunk exceptions were silently swallowed and Task.WhenAll
            // always reported success. We need to know when a parallel chunk breaks.
            var l = new ParallelListProcessor<int>(1);
            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                l.ProcessListInParallel(new List<int> { 1, 2, 3 },
                    (chunk, threadIndex) => throw new InvalidOperationException("boom")));
            Assert.AreEqual("boom", ex.Message);
        }
        async Task ParallelListProcessorTest(int size)
        {
            const int MAX_SIZE = 10;
            var intListAll = new List<int>();
            for (int i = 0; i < size; i++)
            {
                intListAll.Add(i);
            }

            var concurrentIntList = new ConcurrentBag<int>();

            var l = new ParallelListProcessor<int>(MAX_SIZE);

            // Add a copy of the list into a new list, inserted in parallel
            await l.ProcessListInParallel(intListAll, async (chunk, threadIndex) =>
            {
                Console.WriteLine($"Processing on thread index {threadIndex}");

                Assert.IsTrue(chunk.Count <= MAX_SIZE);

                foreach (var i in chunk)
                {
                    concurrentIntList.Add(i);
                }

                await Task.Delay(1000);
                Console.WriteLine($"END on thread index {threadIndex}");

            });

            // Verify everything is there
            Assert.AreEqual(intListAll.Count, concurrentIntList.Count);
            foreach (var i in intListAll)
            {
                Assert.IsTrue(concurrentIntList.Contains(i));
            }
        }

        [TestMethod]
        public void ConcurrentLookupDbIdsCacheTests()
        {
            var cache = new ConcurrentLookupDbIdsCache();

            // Sanity 
            Assert.ThrowsException<ArgumentNullException>(() => cache.AddOrUpdateForName<DataUtilsTests>(null, 0));
            Assert.ThrowsException<ArgumentNullException>(() => cache.AddOrUpdateForName<DataUtilsTests>("", 0));

            const int MAX = 10000;

            // Bombard the ConcurrentDictionary with 10000 competing AddOrUpdates
            Parallel.For(0, MAX, i =>
            {
                cache.AddOrUpdateForName<DataUtilsTests>(i.ToString(), i);
            });

            // Do the same to verify all exist fine
            Parallel.For(0, MAX, i =>
            {
                Assert.IsTrue(cache.GetCachedIdForName<DataUtilsTests>(i.ToString()) == i);
            });

            // Check for some other name too
            Assert.IsNull(cache.GetCachedIdForName<AuditTests>(""));
        }

        [TestMethod]
        public void RandomStringGeneratorUniquenessTest()
        {
            // Should take about 10 seconds
            const int LOOPS = 5000000;
            Dictionary<string, object> strings = new Dictionary<string, object>(LOOPS);
            for (int i = 0; i < LOOPS; i++)
            {
                // Will blow up if key isn't unique
                strings.Add(DataGenerators.GetRandomString(20), null);
            }
        }


        //https://github.com/BcryptNet/bcrypt.net
        [TestMethod]
        public void BasicUsernameHashTests()
        {
            const string PWD = "PWD";
            var hash = StringUtils.GetHashedStringWithSalt(PWD);

            var val = StringUtils.IsHashedMatch(PWD, hash);
            Assert.IsTrue(val);
        }


        [TestMethod]
        public void EmaillAddressTests()
        {
            Assert.ThrowsException<ArgumentNullException>(() => StringUtils.IsEmail(null));
            Assert.ThrowsException<ArgumentException>(() => StringUtils.IsEmail(""));
            Assert.IsFalse(StringUtils.IsEmail("asdfasdfasdf"));
            Assert.IsFalse(StringUtils.IsEmail("$2a$11$Jufq8qMclZHKsnVfLHKWGOgx3XT3tmgji9KSZdGiAptDjaDk6b9I6"));
            Assert.IsTrue(StringUtils.IsEmail("sambetts@contoso.com"));
            Assert.IsTrue(StringUtils.IsEmail("sam.betts@b.org"));
        }


        [TestMethod]
        public void EnsureMaxLengthTests()
        {
            var shortString = "test";
            Assert.AreEqual(StringUtils.EnsureMaxLength(shortString, 10), shortString);

            var longString = "$2a$11$Jufq8qMclZHKsnVfLHKWGOgx3XT3tmgji9KSZdGiAptDjaDk6b9I6$2a$11$Jufq8qMclZHKsnVfLHKWGOgx3XT3tmgji9KSZdGiAptDjaDk6b9I6";
            var longStringResult = StringUtils.EnsureMaxLength(longString, 10);
            Assert.AreNotEqual(longStringResult, longString);

            Assert.IsTrue(longStringResult.EndsWith("..."));

            longString = "123456";
            Assert.IsTrue(StringUtils.EnsureMaxLength(longString, 4) == "1...");
        }

        // ---- Host logging foundation: exception text, console format, stages, shutdown, command lines ----

        // A real SqlException (its constructor is internal): opening a database that does not exist on the test server.
        private static Microsoft.Data.SqlClient.SqlException RealSqlException()
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(new AppConfig().ConnectionStrings.DatabaseConnectionString)
            {
                InitialCatalog = "DoesNotExist_" + Guid.NewGuid().ToString("N"),
                ConnectTimeout = 15,
            };
            using (var connection = new Microsoft.Data.SqlClient.SqlConnection(builder.ConnectionString))
            {
                try
                {
                    connection.Open();
                }
                catch (Microsoft.Data.SqlClient.SqlException ex)
                {
                    return ex;
                }
            }
            Assert.Fail("Opening a missing database must fail.");
            return null;
        }

        private static string[] CaptureConsole(Action action)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            var stdout = new System.IO.StringWriter();
            var stderr = new System.IO.StringWriter();
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                action();
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
            return new[] { stdout.ToString(), stderr.ToString() };
        }

        [TestMethod]
        public void ExceptionDescriber_Chain_NamesEveryExceptionAndTheSqlErrorNumber()
        {
            var sql = RealSqlException();
            // EF6 wraps a login or open failure; async code wraps again in an AggregateException.
            var wrapped = new AggregateException(new InvalidOperationException("The underlying provider failed on Open.", sql));

            var chain = ExceptionDescriber.Chain(wrapped);

            StringAssert.Contains(chain, "AggregateException");
            StringAssert.Contains(chain, "InvalidOperationException: The underlying provider failed on Open.");
            StringAssert.Contains(chain, "SqlException");
            StringAssert.Contains(chain, "(SQL error " + sql.Number + ")");
            Assert.AreEqual(sql.Number, ExceptionDescriber.FindSqlErrorNumber(wrapped));
            Assert.IsFalse(chain.Contains("   at "), "The chain is one line; stacks are printed separately.");
        }

        [TestMethod]
        public void ExceptionDescriber_Dimensions_ClassifyWithoutMessageText()
        {
            var dimensions = ExceptionDescriber.Dimensions(new InvalidOperationException("secret-looking message", new TimeoutException("inner")));

            Assert.AreEqual("InvalidOperationException", dimensions["ExceptionType"]);
            Assert.AreEqual("TimeoutException", dimensions["RootExceptionType"]);
            Assert.IsFalse(dimensions.Values.Any(v => v.Contains("secret-looking")), "Dimensions classify; they never carry message text.");
            Assert.IsFalse(dimensions.ContainsKey("SqlErrorNumber"));
        }

        [TestMethod]
        public void HostRuntime_FatalMessage_NamesTheJobTheStageAndTheWholeChain()
        {
            var message = HostRuntime.FatalMessage("Office365ActivityImporter", "Main",
                new InvalidOperationException("The underlying provider failed on Open.", new System.IO.IOException("Login failed")));

            StringAssert.StartsWith(message, "FATAL Office365ActivityImporter: unhandled exception in Main");
            StringAssert.Contains(message, "InvalidOperationException: The underlying provider failed on Open. ---> IOException: Login failed");
        }

        [TestMethod]
        public void ConsoleLog_Format_HasUtcTimestampLevelAndShortOperationId()
        {
            var line = ConsoleLog.Format(new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc), "ERR", "0123456789abcdef", "something failed");

            Assert.AreEqual("2026-01-02T03:04:05.678Z [ERR] [op 01234567] something failed", line);
            Assert.AreEqual("2026-01-02T03:04:05.678Z [INF] hello",
                ConsoleLog.Format(new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc), "INF", null, "hello"));
        }

        [TestMethod]
        public void AnalyticsLogger_Console_SendsErrorsToStderrWithChainAndStack_AndTheRestToStdout()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            Exception thrown;
            try
            {
                throw new InvalidOperationException("outer failure", new TimeoutException("inner timeout"));
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            var captured = CaptureConsole(() =>
            {
                logger.LogInformation("an information line");
                logger.LogWarning("a warning line");
                logger.LogError("the unit of work failed", thrown);
                logger.LogCritical("the feature is down");
            });
            var stdout = captured[0];
            var stderr = captured[1];

            StringAssert.Contains(stdout, "[INF] an information line");
            StringAssert.Contains(stdout, "[WRN] a warning line");
            Assert.IsFalse(stdout.Contains("[ERR]") || stdout.Contains("[CRT]"), "Errors must not be on stdout, or an error-level filter loses them.");
            StringAssert.Contains(stderr, "[ERR] the unit of work failed");
            StringAssert.Contains(stderr, "InvalidOperationException: outer failure ---> TimeoutException: inner timeout");
            StringAssert.Contains(stderr, " at ", "An error line carries the full stack.");
            StringAssert.Contains(stderr, "[CRT] the feature is down");
        }

        [TestMethod]
        public void AnalyticsLogger_Console_WarningWithExceptionHasTheChainButNoStack()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();

            var captured = CaptureConsole(() => logger.LogWarning("degraded", new InvalidOperationException("synthetic")));

            StringAssert.Contains(captured[0], "[WRN] degraded Exception: InvalidOperationException: synthetic");
            Assert.AreEqual(string.Empty, captured[1]);
        }

        [TestMethod]
        public void AnalyticsLogger_Console_EventLinesAreTimestampedAndCarryTheOperation()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();

            var captured = CaptureConsole(() =>
            {
                using (logger.BeginOperationScope("fedcba9876543210"))
                {
                    logger.TrackEvent(AnalyticsLogger.AnalyticsEvent.ImportStage, "x");
                }
            });

            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(captured[0],
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \[EVT\] \[op fedcba98\] New event 'ImportStage'"), captured[0]);
        }

        [TestMethod]
        public void StageTracker_ReportsTheCurrentStagePerSlot_AndEmitsAFinishEventOnFailureToo()
        {
            var tracker = new StageTracker();
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            Assert.IsNull(tracker.Snapshot(StageSlot.Cycle));

            var captured = CaptureConsole(() =>
            {
                var outer = tracker.Begin(logger, "GraphMainPass");
                var inner = tracker.Begin(logger, "UserMetadata");
                var background = tracker.Begin(logger, "GraphDeferredPass", StageSlot.Background);

                Assert.AreEqual("GraphMainPass/UserMetadata", tracker.Snapshot(StageSlot.Cycle).Stage);
                Assert.IsTrue((DateTime.UtcNow - tracker.Snapshot(StageSlot.Cycle).StartedUtc.Value).TotalMinutes < 1);
                Assert.AreEqual("GraphDeferredPass", tracker.Snapshot(StageSlot.Background).Stage);

                inner.Failed(new InvalidOperationException("synthetic"));
                Assert.AreEqual("GraphMainPass", tracker.Snapshot(StageSlot.Cycle).Stage);
                outer.Succeeded();
                background.Skipped("throttled");
                Assert.IsNull(tracker.Snapshot(StageSlot.Cycle));
                Assert.IsNull(tracker.Snapshot(StageSlot.Background));
            });

            StringAssert.Contains(captured[0], "ImportStage start: Stage=GraphMainPass; Slot=Cycle");
            StringAssert.Contains(captured[0], "Stage=UserMetadata;Slot=Cycle;Outcome=Failed;ExceptionType=InvalidOperationException");
            StringAssert.Contains(captured[0], "Stage=GraphMainPass;Slot=Cycle;Outcome=Succeeded");
            StringAssert.Contains(captured[0], "Stage=GraphDeferredPass;Slot=Background;Outcome=Skipped;Reason=throttled");
            StringAssert.Contains(captured[1], "[ERR] ImportStage finish: Stage=UserMetadata; Outcome=Failed; DurationMs=");
        }

        [TestMethod]
        public void StageTracker_RunAsync_RecordsFailureAndRethrows_AndAStageWithNoOutcomeIsFailedNotDropped()
        {
            var tracker = new StageTracker();
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var captured = CaptureConsole(() =>
            {
                Assert.ThrowsException<InvalidOperationException>(() =>
                    tracker.RunAsync(logger, "ActivityImport", () => throw new InvalidOperationException("synthetic")).GetAwaiter().GetResult());
                using (tracker.Begin(logger, "Abandoned"))
                {
                }
            });

            StringAssert.Contains(captured[0], "Stage=ActivityImport;Slot=Cycle;Outcome=Failed");
            StringAssert.Contains(captured[0], "Stage=Abandoned;Slot=Cycle;Outcome=Failed;Reason=NoOutcomeRecorded");
            Assert.IsNull(tracker.Snapshot(), "A finished stage must leave the slot idle.");
        }

        [TestMethod]
        public void ShutdownWatcher_NoticesTheFileKuduCreates_OnceAndWakesWaiters()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shutdown-" + Guid.NewGuid().ToString("N"));
            var raised = 0;
            try
            {
                using (var watcher = new ShutdownWatcher(path, TimeSpan.FromMilliseconds(100), () => Interlocked.Increment(ref raised)))
                {
                    Assert.IsFalse(watcher.IsRequested);
                    Assert.IsFalse(watcher.Wait(TimeSpan.FromMilliseconds(150)), "Nothing has asked for a stop yet.");

                    System.IO.File.WriteAllText(path, string.Empty);

                    Assert.IsTrue(watcher.Wait(TimeSpan.FromSeconds(10)), "The watcher or the poll must see the file.");
                    Assert.IsTrue(watcher.IsRequested);
                    watcher.Check();
                    Thread.Sleep(300);
                    Assert.AreEqual(1, raised, "The shutdown callback runs once.");
                }
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        [TestMethod]
        public void ShutdownWatcher_WithNoFileConfigured_NeverRequestsShutdown()
        {
            using (var watcher = new ShutdownWatcher(null, TimeSpan.FromMilliseconds(50)))
            {
                Assert.IsFalse(watcher.Wait(TimeSpan.FromMilliseconds(200)));
                Assert.IsFalse(watcher.IsRequested);
            }
        }

        [TestMethod]
        public void StrictArguments_RefusesWhatItDoesNotUnderstand()
        {
            var flags = new[] { "-logApiResponses" };
            var values = new[] { "-readHitsDaysBeforeToday" };

            var ok = StrictArguments.Parse(new[] { "-LOGAPIRESPONSES", "-readHitsDaysBeforeToday", "3" }, flags, values);
            Assert.IsTrue(ok.IsValid);
            Assert.IsTrue(ok.Has("-logApiResponses"));
            Assert.AreEqual("3", ok.ValueOf("-readHitsDaysBeforeToday"));

            Assert.IsTrue(StrictArguments.Parse(new string[0], flags, values).IsValid);
            StringAssert.Contains(StrictArguments.Parse(new[] { "-logApiResponse" }, flags, values).Error, "Unrecognised argument '-logApiResponse'");
            StringAssert.Contains(StrictArguments.Parse(new[] { "-readHitsDaysBeforeToday" }, flags, values).Error, "needs a value");
            StringAssert.Contains(StrictArguments.Parse(new[] { "-readHitsDaysBeforeToday", "-logApiResponses" }, flags, values).Error, "needs a value");
            StringAssert.Contains(StrictArguments.Parse(new[] { "-logApiResponses", "-logApiResponses" }, flags, values).Error, "more than once");
        }

        [TestMethod]
        public void StartupSqlCheck_BacksOffAndRetriesAtCritical_InsteadOfExiting()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var probes = 0;
            var waits = new List<TimeSpan>();

            bool result = false;
            var captured = CaptureConsole(() => result = StartupSqlCheck.WaitUntilReachable(
                () => ++probes >= 3, logger, "Office365ActivityImporter",
                wait: d => { waits.Add(d); return false; },
                backoff: new[] { TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) }));

            Assert.IsTrue(result);
            Assert.AreEqual(3, probes);
            CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) }, waits);
            StringAssert.Contains(captured[1], "[CRT] STARTUP-SQL-CHECK Failed: job=Office365ActivityImporter; attempt=1;");
            StringAssert.Contains(captured[1], "attempt=2;");
            StringAssert.Contains(captured[0], "STARTUP-SQL-CHECK Recovered: job=Office365ActivityImporter; attempt=3;");
        }

        [TestMethod]
        public void StartupSqlCheck_StopsWaitingWhenTheHostAsksTheJobToStop()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();

            bool result = true;
            var captured = CaptureConsole(() => result = StartupSqlCheck.WaitUntilReachable(() => false, logger, "AppInsightsImporter", wait: d => true));

            Assert.IsFalse(result);
            StringAssert.Contains(captured[0], "STARTUP-SQL-CHECK Abandoned: job=AppInsightsImporter; shutdown requested by host");
        }

        [DataTestMethod]
        [DataRow("--repair-dlp-history", "2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z", "checkpoint.json")]
        [DataRow("--repair-copilot-history", "2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z", "checkpoint.json")]
        public void ImporterCommandLine_AcceptsAWellFormedRepairCommand(string command, string from, string to, string file)
        {
            var parsed = ImporterCommandLine.Parse(new[] { command, from, to, file }, name => null);

            Assert.IsTrue(parsed.IsValid, parsed.Error);
            Assert.AreEqual(command == "--repair-dlp-history" ? ImporterMode.RepairDlpHistory : ImporterMode.RepairCopilotHistory, parsed.Mode);
        }

        [DataTestMethod]
        [DataRow("--repair-dlp-histor|a|b|c")]                                              // mistyped: would have started the endless import
        [DataRow("--repair-DLP-history|a|b|c")]                                             // case
        [DataRow("--webhook|https://contoso.example/hook|--repair-dlp-history|a|b|c")]      // misplaced
        [DataRow("--repair-copilot-history|a|b")]                                           // missing value
        [DataRow("--repair-copilot-history|a|b|c|d")]                                       // extra value
        [DataRow("import-now")]                                                             // unknown positional
        [DataRow("--callId")]                                                               // option without its value
        [DataRow("--webhook|not a url")]
        [DataRow("--traceAuditEmail|a@contoso.example|--traceAuditEmail|b@contoso.example")]
        public void ImporterCommandLine_RefusesAnythingItDoesNotUnderstand_RatherThanStartingTheImport(string joinedArgs)
        {
            var args = joinedArgs.Split('|');
            var parsed = ImporterCommandLine.Parse(args, name => "SomeWebJob");

            Assert.IsFalse(parsed.IsValid, "Accepted: " + string.Join(" ", args));
            Assert.IsNotNull(parsed.Error);
            StringAssert.Contains(ImporterCommandLine.Usage, "--repair-dlp-history");
        }

        [TestMethod]
        public void ImporterCommandLine_EndlessImportOutsideAWebJob_NeedsConsoleImport()
        {
            Assert.IsTrue(ImporterCommandLine.Parse(new string[0], name => "Office365ActivityImporter").IsValid, "Kudu sets WEBJOBS_NAME.");
            Assert.IsTrue(ImporterCommandLine.Parse(new string[0], name => null, interactiveImportAllowed: true).IsValid);

            var refused = ImporterCommandLine.Parse(new string[0], name => null);
            Assert.IsFalse(refused.IsValid);
            StringAssert.Contains(refused.Error, "--console-import");

            var allowed = ImporterCommandLine.Parse(new[] { "--console-import", "--traceAuditDir", "C:\\temp\\trace" }, name => null);
            Assert.IsTrue(allowed.IsValid, allowed.Error);
            Assert.AreEqual(ImporterMode.Import, allowed.Mode);
            Assert.AreEqual("C:\\temp\\trace", allowed.TraceAuditDirectory);
        }

        [TestMethod]
        public void ImporterCommandLine_RoutesTheOtherModes()
        {
            var single = ImporterCommandLine.Parse(new[] { "--callId", "00000000-0000-0000-0000-000000000000" }, name => null);
            Assert.IsTrue(single.IsValid, single.Error);
            Assert.AreEqual(ImporterMode.SingleCall, single.Mode);
            Assert.AreEqual("00000000-0000-0000-0000-000000000000", single.CallId);

            Assert.AreEqual(ImporterMode.ReplayCallDeadLetters,
                ImporterCommandLine.Parse(new[] { "--replayCallDeadLetters", "--dry-run" }, name => null).Mode);

            var hook = ImporterCommandLine.Parse(new[] { "--webhook", "https://contoso.example/api/CallRecordWebhook" }, name => "SomeWebJob");
            Assert.AreEqual("https://contoso.example/api/CallRecordWebhook", hook.WebhookOverride.ToString());
        }
    }
}
