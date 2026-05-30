using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace Tests.UnitTests
{
    /// <summary>
    /// Regression coverage for the TTL-based eviction added to UserGroupsCache.
    /// Before the fix the dictionary grew unbounded for the lifetime of the WebJob,
    /// risking OOM on tenants with 100k+ unique UPNs across repeated import runs.
    /// </summary>
    [TestClass]
    public class UserGroupsCacheTtlTests
    {
        private sealed class ShortTtlCache : UserGroupsCache
        {
            private readonly TimeSpan _ttl;
            public int LoadCallCount;

            public ShortTtlCache(TimeSpan ttl, ILogger logger = null) : base(logger ?? NullLogger.Instance)
            {
                _ttl = ttl;
            }

            protected internal override TimeSpan CacheTtl => _ttl;

            protected override Task<List<string>> LoadGroupsFromExternalAsync(string upn)
            {
                Interlocked.Increment(ref LoadCallCount);
                return Task.FromResult(new List<string> { "GroupA", "GroupB" });
            }
        }

        [TestMethod]
        public async Task GetGroupsForUserAsync_WithinTtl_UsesCachedValue()
        {
            var cache = new ShortTtlCache(TimeSpan.FromMinutes(10));

            await cache.GetGroupsForUserAsync("a@contoso.com");
            await cache.GetGroupsForUserAsync("a@contoso.com");

            Assert.AreEqual(1, cache.LoadCallCount, "Second call within TTL must be served from cache.");
            Assert.AreEqual(1, cache.CachedEntryCount);
        }

        [TestMethod]
        public async Task GetGroupsForUserAsync_PastTtl_EvictsAndReloadsOnNextCall()
        {
            var cache = new ShortTtlCache(TimeSpan.FromMilliseconds(50));

            await cache.GetGroupsForUserAsync("a@contoso.com");
            await cache.GetGroupsForUserAsync("b@contoso.com");
            Assert.AreEqual(2, cache.CachedEntryCount, "Sanity: two distinct UPNs cached before TTL elapses.");

            // Wait past the TTL, then a single new access must trigger eviction.
            await Task.Delay(200);
            await cache.GetGroupsForUserAsync("c@contoso.com");

            // After eviction only the newly-loaded entry should remain in the cache.
            Assert.AreEqual(1, cache.CachedEntryCount,
                "Once TTL elapses the cache must be bulk-cleared on the next access (bounded memory growth).");
            Assert.AreEqual(3, cache.LoadCallCount, "Each unique UPN should have triggered exactly one external load.");
        }

        [TestMethod]
        public async Task GetGroupsForUserAsync_AfterEviction_ReloadsPreviouslyCachedUpn()
        {
            var cache = new ShortTtlCache(TimeSpan.FromMilliseconds(50));

            await cache.GetGroupsForUserAsync("a@contoso.com");
            await Task.Delay(200);
            await cache.GetGroupsForUserAsync("a@contoso.com");

            Assert.AreEqual(2, cache.LoadCallCount,
                "After TTL eviction the same UPN must be re-loaded from the external source.");
        }
    }
}
