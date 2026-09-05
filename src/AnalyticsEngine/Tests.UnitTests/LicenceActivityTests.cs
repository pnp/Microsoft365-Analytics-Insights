extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.LicenceActivity;
using Common.Entities.LicenceActivity;
using DataUtils;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class LicenceActivityTests
    {
        internal static readonly DateTime Now = new DateTime(2000, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void CustomDates_AreExactUtcAndNeverRounded()
        {
            var query = LicenceActivityQuery.Create("2000-05-03", "2000-06-21", Now);
            Assert.AreEqual("2000-05-03", query.From);
            Assert.AreEqual("2000-06-21", query.To);
            Assert.AreEqual(50, query.Days);
            Assert.AreEqual(DateTimeKind.Utc, query.FromUtc.Kind);
            Assert.AreEqual(new DateTime(2000, 6, 22, 0, 0, 0, DateTimeKind.Utc), query.EndExclusiveUtc);
            var changed = query.ForUsers(2, "copilot", "Contoso", "activity", "desc", 100, 3, 100, Now);
            Assert.AreEqual(query.From, changed.From);
            Assert.AreEqual(query.To, changed.To);
        }

        [TestMethod]
        public void DefaultRange_UsesFourFullySettledWeeks()
        {
            var query = LicenceActivityQuery.Create(null, null, Now);
            Assert.AreEqual("2000-05-29", query.From);
            Assert.AreEqual("2000-06-25", query.To);
            Assert.AreEqual(DayOfWeek.Monday, query.FromUtc.DayOfWeek);
            Assert.AreEqual(DayOfWeek.Sunday, query.ToUtc.DayOfWeek);
            Assert.IsTrue(query.ToUtc <= Now.Date.AddDays(-3));
        }

        [TestMethod]
        public void InvalidRangesAndBounds_AreRejected()
        {
            foreach (var range in new[]
            {
                new[] { "2000-06-01", (string)null }, new[] { "", "2000-06-21" },
                new[] { "2000-06-20", "2000-06-21" }, new[] { "1999-01-01", "2000-06-21" },
                new[] { "2000-07-01", "2000-07-28" }, new[] { "2000-06-30", "2000-06-01" },
                new[] { "2000-05-03T12:00:00Z", "2000-06-21" }
            })
                Assert.ThrowsException<ArgumentException>(() => LicenceActivityQuery.Create(range[0], range[1], Now));
            Assert.ThrowsException<ArgumentException>(() => LicenceActivityQuery.Create(null, null, Now, top: 0));
            Assert.ThrowsException<ArgumentException>(() => LicenceActivityQuery.Create(null, null, Now, pageSize: 101));
            Assert.ThrowsException<ArgumentException>(() => LicenceActivityQuery.Create(null, null, Now, page: 10001));
            Assert.ThrowsException<ArgumentException>(() => LicenceActivityQuery.Create(null, null, Now, workload: "total"));
            Assert.ThrowsException<ArgumentException>(() => LicenceActivityQuery.Create(null, null, Now, sort: "user_name;DROP"));
            Assert.ThrowsException<ArgumentException>(() => LicenceActivityQuery.Create(null, null, Now, search: new string('x', 101)));
        }

        [TestMethod]
        public void Bands_NeedCompleteEvidenceAndUseExplainableThresholds()
        {
            Assert.AreEqual("unknown", LicenceActivityRules.Band(0, 0, 4));
            Assert.AreEqual("unknown", LicenceActivityRules.Band(0, 3, 4));
            Assert.AreEqual("unknown", LicenceActivityRules.Band(0, 0, 0));
            Assert.AreEqual("zero", LicenceActivityRules.Band(0, 4, 4));
            Assert.AreEqual("low", LicenceActivityRules.Band(1, 8, 8));
            Assert.AreEqual("moderate", LicenceActivityRules.Band(1, 4, 4));
            Assert.AreEqual("high", LicenceActivityRules.Band(3, 4, 4));
        }

        [TestMethod]
        public void CacheKeys_IncludeEveryResultAffectingInput()
        {
            var first = LicenceActivityQuery.Create(null, null, Now, licenceTypeId: 1);
            var keys = new[]
            {
                first.CacheKey(),
                LicenceActivityQuery.Create(null, null, Now, licenceTypeId: 2).CacheKey(),
                LicenceActivityQuery.Create(null, null, Now, licenceTypeId: 1, departmentId: 3).CacheKey(),
                LicenceActivityQuery.Create(null, null, Now, licenceTypeId: 1, countryId: 0).CacheKey(),
                LicenceActivityQuery.Create(null, null, Now, licenceTypeId: 1, workload: "outlook").CacheKey(),
                LicenceActivityQuery.Create(null, null, Now, licenceTypeId: 1, search: "Contoso").CacheKey(),
                LicenceActivityQuery.Create(null, null, Now, licenceTypeId: 1, page: 2).CacheKey(),
                LicenceActivityQuery.Create(null, null, Now, licenceTypeId: 1, pageSize: 100).CacheKey(),
                LicenceActivityQuery.Create(null, null, Now, licenceTypeId: 1, top: 25).CacheKey(),
                LicenceActivityQuery.Create(null, null, Now, licenceTypeId: 1, direction: "desc").CacheKey(),
                LicenceActivityQuery.Create(null, null, Now, licenceTypeId: 1, sort: "activity").CacheKey()
            };
            Assert.AreEqual(keys.Length, keys.Distinct().Count());
        }

        [TestMethod]
        public void Excel_ExportsTheDisplayedSnapshotWithUnicodeAndNoFormulaInjection()
        {
            var overview = SampleOverview();
            var users = new LicenceActivityUsers
            {
                OverviewId = overview.SnapshotId, Query = overview.Query.ForUsers(1, "teams", "Contoso", "upn", "asc", 10, 1, 50, Now),
                GeneratedUtc = Now
            };
            users.LeastActive.Add(new LicenceActivityUser
            {
                UserId = 1, Department = "Καλημέρα κόσμε", UserPrincipalName = "synthetic@contoso.example",
                Country = "=SUM(1,2)",
                Workloads = { new LicenceActivityEvidence { Workload = "teams", Band = "zero", Status = "available", AverageActions = 0 } }
            });
            using (var archive = new ZipArchive(new MemoryStream(LicenceActivityWorkbook.Build(overview, users))))
            {
                var xml = string.Join("\n", archive.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal))
                    .Select(Read));
                StringAssert.Contains(xml, "Καλημέρα κόσμε");
                StringAssert.Contains(xml, "=SUM(1,2)");
                StringAssert.Contains(xml, "synthetic@contoso.example");
                StringAssert.Contains(xml, "zero");
                Assert.IsFalse(xml.Contains("<f>"), "Tenant text must be inline strings, never spreadsheet formulae.");
                var workbook = Read(archive.GetEntry("xl/workbook.xml"));
                StringAssert.Contains(workbook, "Least active");
                StringAssert.Contains(workbook, "Workload coverage");
            }
            using (var archive = new ZipArchive(new MemoryStream(LicenceActivityWorkbook.Build(overview))))
                Assert.IsFalse(Read(archive.GetEntry("xl/workbook.xml")).Contains("Least active"));
            users.OverviewId = "different";
            Assert.ThrowsException<ArgumentException>(() => LicenceActivityWorkbook.Build(overview, users));
        }

        private static string Read(ZipArchiveEntry entry)
        {
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) return reader.ReadToEnd();
        }

        internal static LicenceActivityOverview SampleOverview() => new LicenceActivityOverview
        {
            SnapshotId = "synthetic-overview", Query = LicenceActivityQuery.Create(null, null, Now),
            GeneratedUtc = Now, ExpiresUtc = Now.AddMinutes(5), DistinctAssignedUsers = 3,
            Licences =
            {
                new LicenceActivitySku
                {
                    LicenceTypeId = 1, Name = "Contoso licence", SkuId = "00000000-0000-0000-0000-000000000000",
                    AssignedUsers = 3,
                    Workloads = { new LicenceActivityDistribution { Workload = "teams", High = 1, Zero = 1, Unknown = 1 } }
                }
            }
        };
    }

    [TestClass]
    public class LicenceActivityCacheTests
    {
        private static TaskCompletionSource<bool> Gate() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static LicenceActivitySnapshotCache<LicenceActivityOverview> Cache(
            int capacity = 4, Func<DateTime> clock = null,
            Func<string, LicenceActivityRunDiagnostics> diagnostics = null,
            Action<string, Exception> failure = null, TimeSpan? timeout = null) =>
            new LicenceActivitySnapshotCache<LicenceActivityOverview>(
                capacity, TimeSpan.FromMinutes(5), new SemaphoreSlim(4), clock ?? (() => LicenceActivityTests.Now),
                diagnostics ?? (id => new LicenceActivityRunDiagnostics(id, _ => true)), timeout,
                failure ?? ((id, ex) => { }));

        [TestMethod]
        public async Task DuplicateColdRequests_DeduplicateAndCallerCancellationDoesNotCancelTheRun()
        {
            var cache = Cache();
            var entered = Gate();
            var release = Gate();
            var calls = 0;
            CancellationToken sharedToken = default(CancellationToken);
            Func<ILicenceActivityDiagnostics, CancellationToken, Task<LicenceActivityOverview>> load = async (d, token) =>
            {
                Interlocked.Increment(ref calls);
                sharedToken = token;
                entered.TrySetResult(true);
                await release.Task;
                return LicenceActivityTests.SampleOverview();
            };
            var first = cache.GetAsync("tenant", "same", load);
            var second = cache.GetAsync("tenant", "same", load);
            await entered.Task;
            using (var cancelled = new CancellationTokenSource())
            {
                var caller = LicenceActivitySnapshotCache<LicenceActivityOverview>.WaitForCallerAsync(first, cancelled.Token);
                cancelled.Cancel();
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => caller);
                Assert.IsFalse(sharedToken.IsCancellationRequested);
            }
            release.TrySetResult(true);
            Assert.AreSame(await first, await second);
            Assert.AreEqual(1, calls);
            var warm = await cache.GetAsync("tenant", "same", load);
            Assert.AreSame(await second, warm);
            Assert.ThrowsException<LicenceActivityExpiredException>(() => cache.Find("other-tenant", warm.SnapshotId));
        }

        [TestMethod]
        public async Task SharedRun_InnerAwaitsDoNotCaptureEndedRequestContext()
        {
            var cache = Cache();
            var release = Gate();
            SynchronizationContext observed = null;
            var request = new EndedRequestContext();
            Task<LicenceActivityOverview> run;
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(request);
                run = cache.GetAsync("tenant", "context", async (d, token) =>
                {
                    observed = SynchronizationContext.Current;
                    await release.Task;
                    return LicenceActivityTests.SampleOverview();
                });
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            release.TrySetResult(true);
            Assert.AreSame(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))));
            await run;
            Assert.IsNull(observed);
            Assert.AreEqual(0, request.Posts);
        }

        [TestMethod]
        public async Task Failure_IsEvictedAndRetried_AndRefusedFailureUsesFallback()
        {
            var reported = Gate();
            var cache = Cache(diagnostics: id => new LicenceActivityRunDiagnostics(id, _ => false),
                failure: (id, ex) => reported.TrySetResult(true));
            var task = cache.GetAsync("tenant", "failure", (d, token) =>
                Task.FromException<LicenceActivityOverview>(new InvalidOperationException("private search text")));
            var exception = await Assert.ThrowsExceptionAsync<LicenceActivityFailedException>(() => task);
            Assert.IsFalse(exception.Message.Contains("private search text"));
            Assert.AreSame(reported.Task, await Task.WhenAny(reported.Task, Task.Delay(TimeSpan.FromSeconds(5))));
            var fresh = await cache.GetAsync("tenant", "failure", (d, token) => Task.FromResult(LicenceActivityTests.SampleOverview()));
            Assert.IsNotNull(fresh.SnapshotId);
        }

        [TestMethod]
        public async Task PublicationAndCapacityRelease_PrecedeOptionalTelemetry()
        {
            var blocked = Gate();
            using (var release = new ManualResetEventSlim())
            {
                var cache = Cache(diagnostics: id => new LicenceActivityRunDiagnostics(id, e =>
                {
                    if (e.Stage == "CachePublished")
                    {
                        blocked.TrySetResult(true);
                        release.Wait(TimeSpan.FromSeconds(5));
                    }
                    return true;
                }));
                try
                {
                    var result = await cache.GetAsync("tenant", "publication", (d, token) => Task.FromResult(LicenceActivityTests.SampleOverview()));
                    Assert.AreSame(blocked.Task, await Task.WhenAny(blocked.Task, Task.Delay(TimeSpan.FromSeconds(5))));
                    Assert.AreSame(result, cache.Find("tenant", result.SnapshotId));
                }
                finally { release.Set(); }
            }
        }

        [TestMethod]
        public async Task BoundedCache_EvictsCompletedEntriesAndDoesNotEvictInFlightWork()
        {
            var now = LicenceActivityTests.Now;
            var cache = Cache(1, () => now);
            var first = await cache.GetAsync("tenant", "one", (d, ct) => Task.FromResult(LicenceActivityTests.SampleOverview()));
            var release = Gate();
            var secondTask = cache.GetAsync("tenant", "two", async (d, ct) => { await release.Task; return LicenceActivityTests.SampleOverview(); });
            Assert.ThrowsException<LicenceActivityExpiredException>(() => cache.Find("tenant", first.SnapshotId));
            Assert.ThrowsException<LicenceActivityBusyException>(() =>
                cache.GetAsync("tenant", "three", (d, ct) => Task.FromResult(LicenceActivityTests.SampleOverview())));
            release.TrySetResult(true);
            var second = await secondTask;
            now = now.AddMinutes(6);
            Assert.ThrowsException<LicenceActivityExpiredException>(() => cache.Find("tenant", second.SnapshotId));
            var third = await cache.GetAsync("tenant", "two", (d, ct) => Task.FromResult(LicenceActivityTests.SampleOverview()));
            Assert.AreNotEqual(second.SnapshotId, third.SnapshotId, "An expired generation must not persist through its finished task.");
        }

        [TestMethod]
        public async Task ExpiryAndResponseSizeBounds_AreEnforced()
        {
            var cache = Cache();
            Assert.ThrowsException<LicenceActivityExpiredException>(() =>
                cache.GetAsync("tenant", "expired", (d, ct) => Task.FromResult(LicenceActivityTests.SampleOverview()), LicenceActivityTests.Now));
            var tooLarge = cache.GetAsync("tenant", "huge", (d, ct) =>
            {
                var value = LicenceActivityTests.SampleOverview();
                value.Messages.Add(new string('x', LicenceActivitySnapshotCache<LicenceActivityOverview>.MaximumJsonBytes));
                return Task.FromResult(value);
            });
            await Assert.ThrowsExceptionAsync<LicenceActivityFailedException>(() => tooLarge);
        }

        [TestMethod]
        public async Task TelemetryConstructionOrDeliveryFailure_DoesNotFailTheReport()
        {
            foreach (var factory in new Func<string, LicenceActivityRunDiagnostics>[]
            {
                id => throw new InvalidOperationException("telemetry construction"),
                id => new LicenceActivityRunDiagnostics(id, e => throw new InvalidOperationException("telemetry delivery"))
            })
            {
                var cache = Cache(diagnostics: factory);
                var result = await cache.GetAsync("tenant", "telemetry", (d, ct) =>
                {
                    d.Stage("OverviewSqlStarted");
                    d.Stage("OverviewSqlCompleted", 1);
                    return Task.FromResult(LicenceActivityTests.SampleOverview());
                });
                Assert.AreEqual(3, result.DistinctAssignedUsers);
            }
        }

        [TestMethod]
        public async Task RunTimeout_IsIndependentOfCallerAndAllowsRetry()
        {
            var cache = Cache(timeout: TimeSpan.FromMilliseconds(30));
            var timedOut = cache.GetAsync("tenant", "slow", async (d, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return LicenceActivityTests.SampleOverview();
            });
            await Assert.ThrowsExceptionAsync<LicenceActivityFailedException>(() => timedOut);
            var retry = await cache.GetAsync("tenant", "slow", (d, ct) => Task.FromResult(LicenceActivityTests.SampleOverview()));
            Assert.IsNotNull(retry);
        }

        [TestMethod]
        public async Task DeadlineBoundsResponseEvenWhenLoaderIgnoresCancellation_WithoutReleasingLiveCapacity()
        {
            var slots = new SemaphoreSlim(1, 1);
            var release = Gate();
            var entered = Gate();
            var cache = new LicenceActivitySnapshotCache<LicenceActivityOverview>(
                2, TimeSpan.FromMinutes(5), slots, () => LicenceActivityTests.Now,
                id => new LicenceActivityRunDiagnostics(id, _ => true), TimeSpan.FromMilliseconds(100),
                (id, ex) => { });
            var first = cache.GetAsync("tenant", "slow", async (d, token) =>
            {
                entered.TrySetResult(true);
                await release.Task;
                return LicenceActivityTests.SampleOverview();
            });
            try
            {
                await entered.Task;
                Assert.AreSame(first, await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(3))),
                    "The response deadline must not depend on loader cooperation.");
                await Assert.ThrowsExceptionAsync<LicenceActivityFailedException>(() => first);
                Assert.AreEqual(0, slots.CurrentCount,
                    "An operation still owning SQL resources must keep its concurrency slot.");
                Assert.ThrowsException<LicenceActivityBusyException>(() =>
                    cache.GetAsync("tenant", "other", (d, token) => Task.FromResult(LicenceActivityTests.SampleOverview())));
            }
            finally { release.TrySetResult(true); }

            await WaitForSlots(slots, 1);
            var retry = await cache.GetAsync("tenant", "slow", (d, token) => Task.FromResult(LicenceActivityTests.SampleOverview()));
            Assert.IsNotNull(retry);
        }

        [TestMethod]
        public async Task TimedOutLateGeneration_CannotPublishOverOrEvictItsReplacement()
        {
            var slots = new SemaphoreSlim(2, 2);
            var release = Gate();
            var cache = new LicenceActivitySnapshotCache<LicenceActivityOverview>(
                4, TimeSpan.FromMinutes(5), slots, () => LicenceActivityTests.Now,
                id => new LicenceActivityRunDiagnostics(id, _ => true), TimeSpan.FromMilliseconds(100),
                (id, ex) => { });
            var first = cache.GetAsync("tenant", "same", async (d, token) =>
            {
                await release.Task;
                return LicenceActivityTests.SampleOverview();
            });
            LicenceActivityOverview replacement = null;
            try
            {
                Assert.AreSame(first, await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(3))));
                await Assert.ThrowsExceptionAsync<LicenceActivityFailedException>(() => first);
                replacement = await cache.GetAsync("tenant", "same", (d, token) =>
                    Task.FromResult(LicenceActivityTests.SampleOverview()));
            }
            finally { release.TrySetResult(true); }
            await WaitForSlots(slots, 2);
            Assert.AreSame(replacement, cache.Find("tenant", replacement.SnapshotId));
            Assert.AreSame(replacement, await cache.GetAsync("tenant", "same",
                (d, token) => throw new AssertFailedException("A valid replacement was evicted by an older generation.")));
        }

        private static async Task WaitForSlots(SemaphoreSlim slots, int count)
        {
            for (var attempt = 0; attempt < 300 && slots.CurrentCount != count; attempt++)
                await Task.Delay(10);
            Assert.AreEqual(count, slots.CurrentCount, "The completed loader must release its concurrency slot.");
        }

        [TestMethod]
        public void Diagnostics_AreAllowListedAndContainNoFilterValues()
        {
            var events = new ConcurrentQueue<LicenceActivityDiagnosticEvent>();
            var diagnostics = new LicenceActivityRunDiagnostics("synthetic-run", item => { events.Enqueue(item); return true; });
            diagnostics.Stage("OverviewSqlCompleted", 12);
            diagnostics.Failed(new InvalidOperationException("private payload"));
            Assert.ThrowsException<ArgumentException>(() => diagnostics.Stage("private payload"));
            Assert.IsTrue(events.All(e => e.ExceptionType != "private payload"));
            Assert.AreEqual(nameof(InvalidOperationException), events.Last().ExceptionType);
            Assert.AreEqual(1L, events.First().Sequence);
            Assert.AreEqual(2L, events.Last().Sequence);
        }

        [TestMethod]
        public void DrainedTelemetry_FlushesItsChannelBeforeTheBoundedShutdownWait()
        {
            var channel = new FlushRecordingChannel();
            using (var configuration = new TelemetryConfiguration
            {
                TelemetryChannel = channel,
                ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001"
            })
            {
                var logger = new AnalyticsLogger(new TelemetryClient(configuration), "LicenceActivityTest");
                var waited = false;
                LicenceActivityTelemetry.DrainEvents(new[]
                {
                    new LicenceActivityDiagnosticEvent
                    {
                        RunId = "synthetic-shutdown", Stage = "HostStopping", OccurredUtc = DateTimeOffset.UtcNow
                    }
                }, () => logger, () =>
                {
                    Assert.IsTrue(channel.Sent > 0);
                    Assert.IsTrue(channel.Flushed > 0, "Flush must precede the bounded channel-drain interval.");
                    waited = true;
                });
                Assert.IsTrue(waited);
                Assert.AreEqual(1, channel.Flushed);
            }
        }

        private sealed class FlushRecordingChannel : ITelemetryChannel
        {
            internal int Sent;
            internal int Flushed;
            public bool? DeveloperMode { get; set; }
            public string EndpointAddress { get; set; }
            public void Send(ITelemetry item) => Sent++;
            public void Flush() => Flushed++;
            public void Dispose() { }
        }

        private sealed class EndedRequestContext : SynchronizationContext
        {
            internal int Posts;
            public override void Post(SendOrPostCallback d, object state) => Interlocked.Increment(ref Posts);
        }
    }
}
