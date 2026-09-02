using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// The site-id to URL resolution: database first, Graph only on a miss, then write the answer back.
    /// That precedence is the whole point of the cache - it exists because of a Microsoft service incident
    /// that made these Graph lookups unreliable - and it previously had no test that did not need SQL
    /// Server. See issue #375.
    /// </summary>
    [TestClass]
    public class SiteIdToUrlCacheTests
    {
        /// <summary>A cache whose Graph half is a scripted stub, counting the calls it receives.</summary>
        private sealed class StubGraphSiteCache : SPSiteIdToUrlCache
        {
            private readonly Dictionary<string, string> _urlBySiteId;

            public StubGraphSiteCache(ISiteUrlStore store, Dictionary<string, string> urlBySiteId)
                : base(store, NullLogger.Instance)
            {
                _urlBySiteId = urlBySiteId;
            }

            public List<string> GraphCalls { get; } = new List<string>();

            /// <summary>When set, the Graph call throws this instead of answering.</summary>
            public Exception ThrowInstead { get; set; }

            public override Task<Microsoft.Graph.Models.Site> LoadSite(string id)
            {
                GraphCalls.Add(id);
                if (ThrowInstead != null) throw ThrowInstead;

                return Task.FromResult(_urlBySiteId.TryGetValue(id, out var url)
                    ? new Microsoft.Graph.Models.Site { WebUrl = url }
                    : new Microsoft.Graph.Models.Site { WebUrl = null });
            }
        }

        private static Dictionary<string, string> Graph(params (string Id, string Url)[] sites)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sites) d[s.Id] = s.Url;
            return d;
        }

        [TestMethod]
        public async Task SiteUrlCache_DatabaseHit_DoesNotCallGraph()
        {
            // The reason this cache exists: Graph site lookups were unreliable during a Microsoft incident,
            // so a stored answer must be used without a round-trip.
            var store = new InMemorySiteUrlStore().Seed("site-1", "https://contoso.sharepoint.com/sites/one");
            var cache = new StubGraphSiteCache(store, Graph(("site-1", "https://should-not-be-used")));

            var result = await cache.Load("site-1");

            Assert.AreEqual("https://contoso.sharepoint.com/sites/one", result.SiteUrl);
            Assert.AreEqual("site-1", result.SiteId);
            Assert.AreEqual(0, cache.GraphCalls.Count, "A stored site must not cost a Graph call.");
            Assert.AreEqual(0, store.Writes.Count, "Nothing new was learned, so nothing should be written.");
        }

        [TestMethod]
        public async Task SiteUrlCache_DatabaseMiss_FallsBackToGraphAndWritesResultBack()
        {
            // Uses a non-Latin URL purely as the sample value, so a pass-through that mangled it fails here.
            // NOTE this is deliberately NOT described as encoding coverage - nothing in this path
            // serialises, so a lossy Graph deserialisation or an ANSI EF mapping would not show up.
            const string url = "https://contoso.sharepoint.com/sites/Καλημέρα";
            var store = new InMemorySiteUrlStore();
            var cache = new StubGraphSiteCache(store, Graph(("site-2", url)));

            var result = await cache.Load("site-2");

            Assert.AreEqual(url, result.SiteUrl);
            CollectionAssert.AreEqual(new[] { "site-2" }, cache.GraphCalls.ToArray());

            // Written back, so the next import does not repeat the round-trip.
            Assert.AreEqual(1, store.Writes.Count);
            Assert.AreEqual("site-2", store.Writes[0].Item1);
            Assert.AreEqual(url, store.Writes[0].Item2);
        }

        [TestMethod]
        public async Task SiteUrlCache_StoredRowWithNoUrl_IsStillAHitAndDoesNotReachGraph()
        {
            // Site.UrlBase is nullable, so "no row" and "a row whose URL is null" are different answers.
            // Collapsing them sends a null-URL row to Graph and then inserts a SECOND row for the same site
            // id, after which the single-row lookup throws for that site forever.
            var store = new InMemorySiteUrlStore().Seed("site-null", null);
            var cache = new StubGraphSiteCache(store, Graph(("site-null", "https://should-not-be-used")));

            var result = await cache.Load("site-null");

            Assert.IsNotNull(result);
            Assert.IsNull(result.SiteUrl);
            Assert.AreEqual(0, cache.GraphCalls.Count);
            Assert.AreEqual(0, store.Writes.Count, "A second row for the same site id would break the lookup.");
        }

        [TestMethod]
        public async Task SiteUrlCache_DatabaseHit_ReturnsTheStoredSiteIdSpellingNotTheRequestedOne()
        {
            // The site-id lookup runs under a case-insensitive collation, so the stored spelling can differ
            // from the one asked for. The original returned the STORED value; preserve that.
            var store = new InMemorySiteUrlStore().Seed("Site-MixedCase", "https://contoso.sharepoint.com/sites/mc");
            var cache = new StubGraphSiteCache(store, Graph());

            var result = await cache.Load("site-mixedcase");

            Assert.AreEqual("Site-MixedCase", result.SiteId);
            Assert.AreEqual(0, cache.GraphCalls.Count);
        }

        [TestMethod]
        public async Task SiteUrlCache_SiteNotFoundInGraph_ReturnsNullWithoutThrowing()
        {
            // A 404 is an expected outcome (a deleted site), not a failure: it must not abort the report
            // import that is resolving the id.
            var store = new InMemorySiteUrlStore();
            var cache = new StubGraphSiteCache(store, Graph())
            {
                ThrowInstead = new ODataError { ResponseStatusCode = (int)HttpStatusCode.NotFound }
            };

            var result = await cache.Load("site-missing");

            Assert.IsNull(result);
            Assert.AreEqual(0, store.Writes.Count, "A site that does not exist must not be cached.");
        }

        [TestMethod]
        public async Task SiteUrlCache_GraphFailsForAnyOtherReason_ReturnsNullWithoutThrowing()
        {
            // Same containment for a transient/permission failure - the caller gets null rather than an
            // exception that would take down the whole usage-report import.
            var store = new InMemorySiteUrlStore();
            var cache = new StubGraphSiteCache(store, Graph())
            {
                ThrowInstead = new InvalidOperationException("Graph is unwell")
            };

            Assert.IsNull(await cache.Load("site-3"));
            Assert.AreEqual(0, store.Writes.Count);
        }

        [TestMethod]
        public async Task SiteUrlCache_RepeatedLookupForSameSiteId_CallsGraphOnce()
        {
            // The in-memory ObjectByIdCache layer must hold the answer for the rest of the run; without it
            // a report with thousands of rows for one site would re-query per row.
            var store = new InMemorySiteUrlStore();
            var cache = new StubGraphSiteCache(store, Graph(("site-4", "https://contoso.sharepoint.com/sites/four")));

            var first = await cache.GetResource("site-4");
            var second = await cache.GetResource("site-4");
            var third = await cache.GetResource("site-4");

            Assert.AreEqual("https://contoso.sharepoint.com/sites/four", first.SiteUrl);
            Assert.AreEqual(first.SiteUrl, second.SiteUrl);
            Assert.AreEqual(first.SiteUrl, third.SiteUrl);
            Assert.AreEqual(1, cache.GraphCalls.Count, "Repeated lookups must be served from memory.");
            Assert.AreEqual(1, store.Reads.Count, "...and must not re-query the database either.");
        }

    }
}
