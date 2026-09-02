using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Persistence;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// The audit-log de-duplication cache lifecycle, split out of ActivityReportSqlPersistenceManager by
    /// issue #373 part 2. Runs with zero SQL Server: the only thing that touched the database was the load
    /// itself, and that is now <see cref="IActivityImportCacheLoader"/>.
    ///
    /// What is being pinned here is one of the two operator safety-valves this class carries -
    /// <c>AUDIT_PERBATCH_DEDUP_CACHE</c> - plus the build-once-per-cycle optimisation it exists to undo.
    /// </summary>
    [TestClass]
    public class ActivityImportCacheProviderTests
    {
        private static readonly DateTime Now = new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc);

        private static AbstractAuditLogContent Event(DateTime createdUtc)
            => new AzureADAuditLogContent { Id = Guid.NewGuid(), UserId = "someone@contoso.onmicrosoft.com", CreationTime = createdUtc };

        private static ActivityDedupCacheWindow PerRunWindow(int daysBeforeNowToDownload = 3)
            => ActivityImportCacheWindow.Resolve(usePerBatchDedupCache: false, oldestContentUtc: Now.AddHours(-1),
                newestContentUtc: Now, daysBeforeNowToDownload: daysBeforeNowToDownload, nowUtc: Now);

        private static ActivityDedupCacheWindow PerBatchWindow(DateTime oldestUtc, DateTime newestUtc)
            => ActivityImportCacheWindow.Resolve(usePerBatchDedupCache: true, oldestContentUtc: oldestUtc,
                newestContentUtc: newestUtc, daysBeforeNowToDownload: 3, nowUtc: Now);

        [TestMethod]
        public async Task ImportCache_PerRunScope_IsBuiltOnceAndReusedByEveryBatch()
        {
            var loader = new FakeActivityImportCacheLoader();
            var provider = new ActivityImportCacheProvider(loader, new RecordingLogger());
            var window = PerRunWindow();

            var first = await provider.GetForWindowAsync(window);
            var second = await provider.GetForWindowAsync(window);
            var third = await provider.GetForWindowAsync(window);

            Assert.AreEqual(1, loader.LoadCount,
                "The run-scoped cache must be loaded from audit_events ONCE per import cycle, not once per save batch.");
            Assert.AreSame(first, second, "Every batch of the cycle must share one cache, so ids remembered by one batch are seen by the next.");
            Assert.AreSame(first, third);

            // ...and over the whole download window, not the batch's own [oldest, newest] span.
            Assert.AreEqual(window.FromUtc, loader.Loads[0].FromUtc);
            Assert.AreEqual(window.ToUtc, loader.Loads[0].ToUtc);
        }

        [TestMethod]
        public async Task ImportCache_PerBatchSafetyValve_RebuildsForEveryBatchOverThatBatchsOwnSpan()
        {
            // AUDIT_PERBATCH_DEDUP_CACHE=true. The valve exists so an operator can restore the pre-#373
            // per-batch behaviour without a redeploy, so it must genuinely reload for each batch AND use
            // each batch's own span - not the cycle-wide window.
            var loader = new FakeActivityImportCacheLoader();
            var provider = new ActivityImportCacheProvider(loader, new RecordingLogger());

            var batchOne = PerBatchWindow(Now.AddHours(-5), Now.AddHours(-4));
            var batchTwo = PerBatchWindow(Now.AddHours(-2), Now.AddHours(-1));

            var first = await provider.GetForWindowAsync(batchOne);
            var second = await provider.GetForWindowAsync(batchTwo);

            Assert.AreEqual(2, loader.LoadCount, "With the safety-valve on, every batch rebuilds the cache.");
            Assert.AreNotSame(first, second, "Each batch gets its own cache instance under the safety-valve.");

            Assert.AreEqual(Now.AddHours(-5), loader.Loads[0].FromUtc);
            Assert.AreEqual(Now.AddHours(-4), loader.Loads[0].ToUtc);
            Assert.AreEqual(Now.AddHours(-2), loader.Loads[1].FromUtc);
            Assert.AreEqual(Now.AddHours(-1), loader.Loads[1].ToUtc);
        }

        [TestMethod]
        public async Task ImportCache_ConcurrentFirstUse_StillBuildsOnlyOnce()
        {
            // In concurrent-save mode several batches enter the save path at once, all before the cache
            // exists. Without the init lock each of them would run the (expensive, whole-window)
            // audit_events query.
            //
            // LongRunning tasks plus a Barrier rather than plain Task.Run: the ThreadPool injects threads
            // slowly once its minimum is exhausted, so pool-scheduled racers can arrive hundreds of
            // milliseconds apart and never actually race. LongRunning gives each racer a dedicated thread
            // while still surfacing its exceptions through the returned Task - a raw Thread would let an
            // unhandled exception tear down the whole test host.
            const int racerCount = 8;
            var loader = new FakeActivityImportCacheLoader { LoadDuration = TimeSpan.FromMilliseconds(250) };
            var provider = new ActivityImportCacheProvider(loader, new RecordingLogger());
            var window = PerRunWindow();

            var startLine = new Barrier(racerCount);
            var racers = Enumerable.Range(0, racerCount)
                .Select(_ => Task.Factory.StartNew(
                    () =>
                    {
                        startLine.SignalAndWait();
                        return provider.GetForWindowAsync(window).GetAwaiter().GetResult();
                    },
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
                .ToArray();

            var allRacers = Task.WhenAll(racers);
            var finished = await Task.WhenAny(allRacers, Task.Delay(TimeSpan.FromSeconds(30)));
            // Deliberately no using/finally on the barrier: on the timeout path a racer may still be sitting
            // in SignalAndWait, and disposing it out from under that racer would fault its task with
            // ObjectDisposedException - a fault nothing observes, because the timed-out racers are never
            // awaited. Leaking a Barrier in an already-failing test is the simpler correct outcome.
            Assert.AreSame(allRacers, finished, "A racer did not finish - the init lock may be deadlocked.");

            var caches = await allRacers;
            startLine.Dispose();

            Assert.AreEqual(1, loader.LoadCount, "Concurrent first callers must not each build the cache.");
            Assert.IsTrue(caches.All(c => c != null && ReferenceEquals(c, caches[0])), "All concurrent callers must get the same cache instance.");
        }

        [TestMethod]
        public async Task ImportCache_PerRunBuild_LogsTheIdCountAndWindowOnce()
        {
            // Operator-facing telemetry: #381 forbids changing log text or the numbers inside it, and this
            // line is how an operator sees whether the per-cycle cache is working.
            var logger = new RecordingLogger();
            var loader = new FakeActivityImportCacheLoader
            {
                SeedCache = cache =>
                {
                    cache.RememberProcessedEvent(Event(Now.AddHours(-1)));
                    cache.RememberProcessedEvent(Event(Now.AddHours(-2)));
                    cache.RememberProcessedEvent(Event(Now.AddHours(-3)));
                }
            };
            var provider = new ActivityImportCacheProvider(loader, logger);
            var window = PerRunWindow(daysBeforeNowToDownload: 3);

            await provider.GetForWindowAsync(window);
            await provider.GetForWindowAsync(window);

            var buildLines = logger.Entries
                .Where(e => e.Message.Contains("built run dedup cache from audit_events"))
                .ToList();

            Assert.AreEqual(1, buildLines.Count, "The build line must be logged once per cycle, not once per batch.");
            Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Information, buildLines[0].Level);
            StringAssert.Contains(buildLines[0].Message, "(3 already-processed id(s)",
                "The count comes from ActivityImportCache.ProcessedIdCount - the ids actually loaded.");
            // daysBeforeNowToDownload 3 + the one-day lower margin.
            StringAssert.Contains(buildLines[0].Message, $"{window.DaysBack}-day window");
            Assert.AreEqual(4, window.DaysBack, "Precondition: the reported window is the download window plus its lower margin.");
        }
    }
}
