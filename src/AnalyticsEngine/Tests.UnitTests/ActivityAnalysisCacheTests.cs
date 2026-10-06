using Common.Entities.ActivityAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The cache behind the Activity analysis read models and filtered series: one load per key, one period loading at a
    /// time with a bounded wait, least-recently-used eviction and idle expiry.
    /// </summary>
    [TestClass]
    public class ActivityAnalysisCacheTests
    {
        private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

        private DateTime _now = new DateTime(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc);

        private ActivityAnalysisLoadCache<string> Cache(int capacity = 2, int loads = 1, TimeSpan? wait = null, TimeSpan? maximumAge = null) =>
            new ActivityAnalysisLoadCache<string>(capacity, TimeSpan.FromMinutes(15), maximumAge, loads, wait ?? Generous, () => _now);

        [TestMethod]
        public async Task ConcurrentRequestsForOnePeriod_ShareOneLoad()
        {
            var cache = Cache();
            var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var loads = 0;
            Func<CancellationToken, Task<string>> load = _ => { Interlocked.Increment(ref loads); return release.Task; };

            var first = cache.GetAsync("2026-01-05..2026-02-02", load, CancellationToken.None);
            var second = cache.GetAsync("2026-01-05..2026-02-02", load, CancellationToken.None);
            var third = cache.GetAsync("2026-01-05..2026-02-02", load, CancellationToken.None);
            release.SetResult("model");

            CollectionAssert.AreEqual(new[] { "model", "model", "model" }, await Task.WhenAll(first, second, third));
            Assert.AreEqual(1, loads);
            Assert.AreEqual("model", await cache.GetAsync("2026-01-05..2026-02-02", load, CancellationToken.None));
            Assert.AreEqual(1, loads, "A loaded model is served from memory.");
        }

        [TestMethod]
        public async Task OnePeriodLoadsAtATime_AndAnotherWaitingTooLongIsToldTheSiteIsBusy()
        {
            var cache = Cache(wait: TimeSpan.FromMilliseconds(200));
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var slow = cache.GetAsync("one", _ => { started.TrySetResult(true); return release.Task; }, CancellationToken.None);
            await started.Task.WithTimeout(Generous);

            await Assert.ThrowsExceptionAsync<ActivityAnalysisBusyException>(
                () => cache.GetAsync("two", _ => Task.FromResult("two"), CancellationToken.None));

            release.SetResult("one");
            Assert.AreEqual("one", await slow);
            Assert.AreEqual("two", await cache.GetAsync("two", _ => Task.FromResult("two"), CancellationToken.None),
                "Busy is not remembered: the next request loads.");
        }

        [TestMethod]
        public async Task AWaitingLoad_StartsAsSoonAsTheSlotIsFree()
        {
            var cache = Cache(wait: Generous);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var slow = cache.GetAsync("one", _ => { started.TrySetResult(true); return release.Task; }, CancellationToken.None);
            await started.Task.WithTimeout(Generous);
            var waiting = cache.GetAsync("two", _ => Task.FromResult("two"), CancellationToken.None);

            await Task.Delay(100);
            Assert.IsFalse(waiting.IsCompleted, "Only one period loads at a time.");

            release.SetResult("one");
            Assert.AreEqual("two", await waiting.WithTimeout(Generous));
            Assert.AreEqual("one", await slow);
        }

        [TestMethod]
        public async Task AFailedLoad_IsNotCached()
        {
            var cache = Cache();
            var attempts = 0;
            Func<CancellationToken, Task<string>> load = _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("Synthetic failure.");
                return Task.FromResult("model");
            };

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => cache.GetAsync("key", load, CancellationToken.None));
            Assert.AreEqual("model", await cache.GetAsync("key", load, CancellationToken.None));
            Assert.AreEqual(2, attempts);
        }

        [TestMethod]
        public async Task TheLeastRecentlyUsedModelIsEvicted_BeforeTheNextOneLoads()
        {
            var cache = Cache(capacity: 2);
            await cache.GetAsync("a", _ => Task.FromResult("a"), CancellationToken.None);
            _now = _now.AddMinutes(1);
            await cache.GetAsync("b", _ => Task.FromResult("b"), CancellationToken.None);
            _now = _now.AddMinutes(1);
            await cache.GetAsync("a", _ => Task.FromResult("reloaded"), CancellationToken.None);
            _now = _now.AddMinutes(1);

            var heldDuringLoad = -1;
            await cache.GetAsync("c", _ => { heldDuringLoad = cache.Count; return Task.FromResult("c"); }, CancellationToken.None);

            Assert.AreEqual(1, heldDuringLoad, "Room is made before the load, so three models are never held at once.");
            Assert.IsTrue(cache.Contains("a"), "Used more recently than b.");
            Assert.IsFalse(cache.Contains("b"));
            Assert.IsTrue(cache.Contains("c"));
            Assert.AreEqual(2, cache.Count);
        }

        [TestMethod]
        public async Task AResult_ExpiresAfterFifteenIdleMinutes_AndAtItsMaximumAgeWhenOneIsSet()
        {
            var cache = Cache(maximumAge: TimeSpan.FromHours(1));
            var loads = 0;
            Func<CancellationToken, Task<string>> load = _ => Task.FromResult("model " + Interlocked.Increment(ref loads));

            await cache.GetAsync("key", load, CancellationToken.None);
            _now = _now.AddMinutes(14);
            Assert.IsTrue(cache.Contains("key"));
            await cache.GetAsync("key", load, CancellationToken.None);
            _now = _now.AddMinutes(14);
            Assert.IsTrue(cache.Contains("key"), "Each use restarts the idle clock.");
            _now = _now.AddMinutes(16);
            Assert.IsFalse(cache.Contains("key"));
            Assert.AreEqual("model 2", await cache.GetAsync("key", load, CancellationToken.None));

            for (var i = 0; i < 6; i++)
            {
                _now = _now.AddMinutes(10);
                await cache.GetAsync("key", load, CancellationToken.None);
            }

            Assert.AreEqual(3, loads, "In constant use, but an hour old: read again.");
        }

        [TestMethod]
        public async Task ACallerWhoGivesUp_DoesNotCancelTheLoadOthersAreWaitingFor()
        {
            var cache = Cache();
            var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken loadToken = new CancellationToken(true);
            var loads = 0;
            Func<CancellationToken, Task<string>> load = token => { loadToken = token; Interlocked.Increment(ref loads); return release.Task; };

            using (var leaving = new CancellationTokenSource())
            {
                var gaveUp = cache.GetAsync("key", load, leaving.Token);
                var staying = cache.GetAsync("key", load, CancellationToken.None);
                leaving.Cancel();

                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => gaveUp);
                release.SetResult("model");
                Assert.AreEqual("model", await staying);
            }

            Assert.IsFalse(loadToken.CanBeCanceled, "The shared load is not tied to any one request.");
            Assert.AreEqual(1, loads);
        }

        [TestMethod]
        public async Task TheSharedCaches_HoldTwoPeriods_LoadOneAtATime_AndKeepAPeriodInUse()
        {
            Assert.AreEqual(2, ActivityAnalysisCaches.ReadModelCapacity);
            Assert.AreEqual(64, ActivityAnalysisCaches.WeeklyTotalsCapacity);
            Assert.AreEqual(TimeSpan.FromMinutes(15), ActivityAnalysisCaches.IdleLifetime);
            Assert.AreEqual(TimeSpan.FromSeconds(90), ActivityAnalysisCaches.LoadWait);
            Assert.AreEqual(TimeSpan.FromMinutes(5), ActivityAnalysisCaches.SchemaLifetime);

            var caches = new ActivityAnalysisCaches(() => _now);
            var loads = 0;
            Func<CancellationToken, Task<ActivityAnalysisReadModel>> load = _ =>
            {
                Interlocked.Increment(ref loads);
                return Task.FromResult(new ActivityAnalysisFakeSource().Build(
                    ActivityAnalysisPeriod.Create(new DateTime(2026, 1, 5), new DateTime(2026, 1, 5))));
            };

            for (var i = 0; i < 13; i++)
            {
                await caches.ReadModels.GetAsync("period", load, CancellationToken.None);
                _now = _now.AddMinutes(10);
            }

            Assert.AreEqual(1, loads, "Two hours in constant use: the weeks it holds never change, so it is never re-read.");
            _now = _now.AddMinutes(16);
            await caches.ReadModels.GetAsync("period", load, CancellationToken.None);
            Assert.AreEqual(2, loads, "A quarter of an hour unused, and it is gone.");

            var schemaLoads = 0;
            Func<CancellationToken, Task<ActivityAnalysisSchema>> readSchema = _ =>
            {
                Interlocked.Increment(ref schemaLoads);
                return Task.FromResult(ActivityAnalysisSchema.Complete(null, null));
            };

            for (var i = 0; i < 4; i++)
            {
                await caches.Schemas.GetAsync("scope", readSchema, CancellationToken.None);
                _now = _now.AddMinutes(2);
            }

            Assert.AreEqual(2, schemaLoads, "The availability is re-read every five minutes, in use or not.");
        }
    }

    internal static class ActivityAnalysisTaskExtensions
    {
        internal static async Task<T> WithTimeout<T>(this Task<T> task, TimeSpan timeout)
        {
            if (await Task.WhenAny(task, Task.Delay(timeout)) != task) throw new TimeoutException("The task did not finish in time.");
            return await task;
        }
    }
}
