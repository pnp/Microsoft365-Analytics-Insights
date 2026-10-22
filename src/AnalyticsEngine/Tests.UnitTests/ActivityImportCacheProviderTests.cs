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
        public async Task ImportCache_PerRunScope_IsCreatedOnceSharedByEveryBatchAndLoadsNoWindow()
        {
            var loader = new FakeActivityImportCacheLoader();
            var provider = new ActivityImportCacheProvider(loader, new RecordingLogger());
            var window = PerRunWindow();

            var first = await provider.GetForWindowAsync(window);
            var second = await provider.GetForWindowAsync(window);
            var third = await provider.GetForWindowAsync(window);

            Assert.AreSame(first, second, "Every batch of the cycle must share one cache, so ids remembered by one batch are seen by the next.");
            Assert.AreSame(first, third);

            // The fix for the per-cycle memory blow-up: the run cache used to load every audit_events /
            // ignored_audit_events id in the download window + 1 day here and hold them for the whole cycle.
            Assert.AreEqual(0, loader.LoadCount, "The run-scoped cache must not load the download window into memory.");
            Assert.AreEqual(0, loader.Lookups.Count, "Nothing is looked up until a batch asks about its own ids.");
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
        public async Task ImportCache_ConcurrentFirstUse_AllCallersShareOneCache()
        {
            // In concurrent-save mode several batches enter the save path at once, all before the cache
            // exists. They must all get the SAME cache, or a reservation made by one batch could not stop a
            // concurrent batch staging the same id.
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
            // Deliberately no using/finally on the barrier: Barrier.Dispose is not safe against a concurrent
            // SignalAndWait, and on the timeout path a racer may still be sitting in one. Leaving it to the
            // GC on an already-failing path is simpler than reasoning about what happens if we don't.
            Assert.AreSame(allRacers, finished, "A racer did not finish - the init lock may be deadlocked.");

            var caches = await allRacers;
            startLine.Dispose();

            Assert.AreEqual(0, loader.LoadCount, "No racer may load the download window.");
            Assert.IsTrue(caches.All(c => c != null && ReferenceEquals(c, caches[0])), "All concurrent callers must get the same cache instance.");
        }

        [TestMethod]
        public async Task ImportCache_PerRun_LogsOncePerCycleThatTheWindowIsNotLoaded()
        {
            // Operator-facing: this line replaces "built run dedup cache from audit_events in Ns (N
            // already-processed id(s), D-day window)", because there is no longer a window load to report.
            var logger = new RecordingLogger();
            var loader = new FakeActivityImportCacheLoader();
            var provider = new ActivityImportCacheProvider(loader, logger);
            var window = PerRunWindow(daysBeforeNowToDownload: 3);

            await provider.GetForWindowAsync(window);
            await provider.GetForWindowAsync(window);

            var lines = logger.Entries.Where(e => e.Message.Contains("run dedup cache ready")).ToList();

            Assert.AreEqual(1, lines.Count, "Logged once per cycle, not once per batch.");
            Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Information, lines[0].Level);
            StringAssert.Contains(lines[0].Message, "by primary key (up to 1000 per query)");
            StringAssert.Contains(lines[0].Message, $"instead of loading the {window.DaysBack}-day window into memory");
            Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains("built run dedup cache")),
                "The old build line would claim a load that no longer happens.");
        }

        [TestMethod]
        public async Task ImportCache_PerRun_ChecksOnlyTheBatchsOwnIdsAndKeepsNoneOfTheAnswers()
        {
            // The "database" holds a large backlog of already-imported events. A batch must cost a lookup of its
            // own ids, not a load of everything else.
            var loader = new FakeActivityImportCacheLoader();
            for (var i = 0; i < 5000; i++) loader.AlreadyProcessedIds.Add(Guid.NewGuid());
            var imported = Guid.NewGuid();
            var ignored = Guid.NewGuid();
            var brandNew = Guid.NewGuid();
            loader.AlreadyProcessedIds.Add(imported);
            loader.AlreadyProcessedIds.Add(ignored);

            var runCache = await new ActivityImportCacheProvider(loader, new RecordingLogger()).GetForWindowAsync(PerRunWindow());
            var batchCache = await runCache.WithStoreLookupAsync(new[] { imported, ignored, brandNew, imported });

            Assert.AreEqual(1, loader.Lookups.Count, "One lookup for the batch.");
            CollectionAssert.AreEquivalent(new[] { imported, ignored, brandNew }, loader.Lookups[0],
                "Only the batch's own ids are asked about, each once.");
            Assert.AreEqual(0, loader.LoadCount);

            Assert.IsTrue(batchCache.HaveSeenInProcessedOrIgnoredEvents(imported));
            Assert.IsTrue(batchCache.HaveSeenInProcessedOrIgnoredEvents(ignored));
            Assert.IsFalse(batchCache.HaveSeenInProcessedOrIgnoredEvents(brandNew));

            // The answers belong to the batch's view and go with it; the run cache - which lives for the whole
            // cycle - holds only what the cycle itself stages or ignores.
            Assert.IsFalse(runCache.HaveSeenInProcessedOrIgnoredEvents(imported));
            Assert.AreEqual(0, runCache.ProcessedIdCount);
        }

        [TestMethod]
        public async Task ImportCache_PerRun_IdsThisCycleAlreadyDecidedAreNotLookedUpAgain()
        {
            var loader = new FakeActivityImportCacheLoader();
            var runCache = await new ActivityImportCacheProvider(loader, new RecordingLogger()).GetForWindowAsync(PerRunWindow());
            var staged = Event(Now.AddHours(-1));
            var ignored = Event(Now.AddHours(-2));
            var unknown = Guid.NewGuid();
            runCache.RememberProcessedEvent(staged);
            runCache.RememberNewlyIgnoredEvent(ignored);

            var batchCache = await runCache.WithStoreLookupAsync(new[] { staged.Id, ignored.Id, unknown });

            CollectionAssert.AreEqual(new[] { unknown }, loader.Lookups.Single(),
                "Memory already answers for ids this cycle staged or ignored.");
            Assert.IsTrue(batchCache.HaveSeenInProcessedOrIgnoredEvents(staged));
            Assert.IsTrue(batchCache.HaveSeenInProcessedOrIgnoredEvents(ignored));
            Assert.IsTrue(batchCache.WasRememberedThisCycle(staged), "The view shares the run's reservations.");
        }

        [TestMethod]
        public void ImportCache_LookupQueries_NeverExceedTheParameterLimitAndCoverEveryId()
        {
            // SQL Server refuses a command with more than 2,100 parameters, and a dropped id would be re-imported.
            var ids = Enumerable.Range(0, 2345).Select(_ => Guid.NewGuid()).ToList();

            var chunks = ProcessedAuditEventLookupQuery.Chunk(ids).ToList();

            Assert.IsTrue(chunks.All(c => c.Length <= ProcessedAuditEventLookupQuery.MaxIdsPerQuery));
            Assert.IsTrue(chunks.All(c => ProcessedAuditEventLookupQuery.ParameterCounts.Contains(c.Length)),
                "Every query uses one of the few padded sizes, so SQL Server reuses a handful of plans.");
            CollectionAssert.AreEquivalent(ids, chunks.SelectMany(c => c.Distinct()).ToList(),
                "Every id is looked up exactly once; padding only repeats an id within its own query.");
        }
    }
}
