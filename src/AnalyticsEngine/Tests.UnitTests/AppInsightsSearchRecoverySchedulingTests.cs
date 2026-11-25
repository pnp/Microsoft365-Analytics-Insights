using Common.Entities.Config;
using Common.Entities.State;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using UnitTests.FakeLoaderClasses;
using WebJob.AppInsightsImporter.Engine;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace Tests.UnitTests
{
    [TestClass]
    public class AppInsightsSearchRecoverySchedulingTests
    {
        private static readonly DateTime Start = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public TestContext TestContext { get; set; }

        // Use the same durable state adapter and ingestion-window source as the SQL tests; only the fact store
        // is in memory here so we can run months of hourly arrivals without thousands of SQL round trips.
        private sealed class Visits : IAppInsightsDayPersistenceManager, IHitWatermarkStore
        {
            private readonly HashSet<string> _visits = new HashSet<string>();
            internal readonly HashSet<DateTime> Facts = new HashSet<DateTime>();
            private DateTime? _newest;
            internal DateTime? UnavailableHour;
            internal bool AllRecoveryUnavailable;
            internal int FailureCount;

            public Task<DateTime?> GetNewestHitTimestampUtcAsync(CancellationToken token) => Task.FromResult(_newest);

            public Task SavePageViewsAsync(PageViewCollection views, List<FilterUrlConfig> filters, CancellationToken token)
            {
                foreach (var view in views.Rows)
                {
                    _visits.Add(view.CustomProperties.SessionId);
                    if (!_newest.HasValue || view.Timestamp > _newest) _newest = view.Timestamp;
                }
                return Task.CompletedTask;
            }

            public Task<CustomEventSaveResult> SaveCustomEventsAsync(CustomEventsResultCollection events, CancellationToken token) =>
                Task.FromResult(new CustomEventSaveResult(null, Merge(events)));

            public Task<SearchSaveResult> SaveSearchesAsync(CustomEventsResultCollection events, CancellationToken token)
            {
                if (AllRecoveryUnavailable || (UnavailableHour.HasValue && events.Rows.Any(e => e.SearchIngestionHourUtc == UnavailableHour)))
                {
                    FailureCount++;
                    throw new IOException("Synthetic search-only outage");
                }
                return Task.FromResult(Merge(events));
            }

            private SearchSaveResult Merge(CustomEventsResultCollection events)
            {
                var missing = new Dictionary<DateTime, int>();
                var inserted = 0;
                foreach (var search in events.Rows.Cast<SearchEventAppInsightsQueryResult>())
                {
                    var hour = search.SearchIngestionHourUtc.Value;
                    if (_visits.Contains(search.CustomProperties.SessionId))
                    {
                        if (Facts.Add(search.Timestamp)) inserted++;
                    }
                    else
                    {
                        missing.TryGetValue(hour, out var count);
                        missing[hour] = count + 1;
                    }
                }
                return new SearchSaveResult(inserted, inserted, 0, missing);
            }
        }

        private sealed class DailyImporter
        {
            internal readonly FixedClock Clock = new FixedClock(Start);
            internal readonly Visits Sql = new Visits();
            internal readonly FakeAppInsightsSourceLoader Source = new FakeAppInsightsSourceLoader
            {
                PageViewAt = t => new PageViewAppInsightsQueryResult
                {
                    AppInsightsTimestamp = t, Username = "reader@contoso.com", CustomProperties = new PageViewCustomProps { SessionId = t.Ticks.ToString() }
                },
                EventAt = t => new SearchEventAppInsightsQueryResult
                {
                    AppInsightsTimestamp = t, Username = "reader@contoso.com", CustomProperties = new SearchCustomProps { SessionId = t.Ticks.ToString(), SearchText = "Example" }
                }
            };
            internal readonly InMemoryKeyValueStore State;
            internal KeyValueIngestionCursorStore Cursor => new KeyValueIngestionCursorStore(State, "contoso-test");
            private readonly AppInsightsDayFailureTracker _failures = new AppInsightsDayFailureTracker();
            internal AppInsightsImporter Importer => new AppInsightsImporter(null, AnalyticsLogger.ConsoleOnlyTracer(), Clock,
                Source, new FakeSiteFilterLoader(), Sql, Sql, cursorStore: Cursor, dayFailureTracker: _failures);
            internal readonly List<DateTime> NormalStarts = new List<DateTime>();
            internal readonly List<TimeSpan> Waits = new List<TimeSpan>();
            internal int RecoveryPasses, MaxPending, NewSearches;
            internal readonly HashSet<string> Reasons = new HashSet<string>();

            internal DailyImporter()
            {
                State = new InMemoryKeyValueStore(() => Clock.UtcNow);
                Source.ExtraPageViews.Add((Start.AddHours(-1), Start.AddHours(-1)));
            }

            internal int RunUntil(DateTime until, bool oneShot = false)
            {
                AppInsightsImportCycleResult Run(DateTime? watermark = null, DateTime? deadline = null)
                {
                    var before = Source.CountRangesRequested.Count;
                    var views = Source.PageViewWindowsRequested.Count;
                    var result = watermark.HasValue
                        ? Importer.RecoverPendingSearchesAsync(watermark.Value, deadline.Value, default).GetAwaiter().GetResult()
                        : Importer.ImportAndSave(false, null).GetAwaiter().GetResult();
                    var counts = Source.CountRangesRequested.GetRange(before, Source.CountRangesRequested.Count - before);
                    var searchOnly = counts.Where(w => w.SearchesOnly).ToList();
                    Assert.IsTrue(searchOnly.Count <= 8);
                    Assert.IsTrue(searchOnly.All(w => w.IngestedBeforeUtc - w.IngestedFromUtc <= TimeSpan.FromHours(1)
                        && w.IngestedFromUtc >= Clock.UtcNow - AppInsightsIngestionCursorPolicy.MaxLookback));
                    if (watermark.HasValue)
                    {
                        RecoveryPasses++;
                        Assert.AreEqual(AppInsightsReadMode.SearchRecovery, result.Mode);
                        Assert.IsTrue(counts.All(w => w.SearchesOnly && w.IngestedBeforeUtc <= watermark));
                        Assert.AreEqual(views, Source.PageViewWindowsRequested.Count);
                        Assert.IsNull(result.CursorAdvancedToUtc);
                        Assert.AreEqual(watermark, Cursor.ReadAsync().GetAwaiter().GetResult());
                    }
                    else
                    {
                        NormalStarts.Add(Clock.UtcNow);
                        Assert.AreEqual(1, counts.Count(w => !w.SearchesOnly));
                        Assert.AreEqual(Clock.UtcNow, result.CursorAdvancedToUtc);
                    }
                    MaxPending = Math.Max(MaxPending, result.PendingSearchHours ?? 0);
                    NewSearches += result.Events + result.ReconciledSearches;
                    Reasons.UnionWith(result.Reasons);
                    return result;
                }
                return AppInsightsImportLoop.Run(oneShot, () => Run(), (through, deadline) => Run(through, deadline), 1440,
                    wait =>
                    {
                        Assert.IsTrue(wait > TimeSpan.Zero);
                        Waits.Add(wait);
                        Clock.Advance(wait);
                    }, () => Clock.UtcNow >= until, Clock);
            }
        }

        [TestMethod]
        public async Task DailyPolling_RecoversEveryHourlySearchBeforeRetentionWithoutRepeatingNormalIngestion()
        {
            const int days = 145;
            var job = new DailyImporter();
            var expected = Enumerable.Range(0, days * 24).Select(h => Start.AddHours(h).AddMinutes(2)).ToArray();
            foreach (var t in expected)
            {
                job.Source.ExtraCustomEvents.Add((t, t));
                // Timestamp is unchanged; the visit becomes visible to SQL on a later normal cycle.
                job.Source.ExtraPageViews.Add((t, t.AddHours(30)));
            }
            Assert.AreEqual(0, job.RunUntil(Start.AddDays(days + 6)));
            Assert.AreEqual(expected.Length, job.Sql.Facts.Count, "Newly due hours must not outgrow daily recovery capacity.");
            Assert.IsTrue(job.Sql.Facts.SetEquals(expected));
            Assert.AreEqual(expected.Length, job.NewSearches, "An idempotent replay is not another save.");
            CollectionAssert.AreEqual(Enumerable.Range(0, days + 6).Select(d => Start.AddDays(d)).ToArray(), job.NormalStarts.ToArray());
            Assert.IsTrue(job.RecoveryPasses > days, "The load requires multiple bounded search-only passes per normal run.");
            Assert.IsTrue(job.MaxPending <= 5 * 24, "Work stays bounded by the arrival delay, not by elapsed days.");
            Assert.IsFalse(job.Reasons.Contains("SearchRetriesStopped"));
            Assert.AreEqual(0, (await job.Cursor.ReadSearchRetriesAsync()).Count);
            var history = await job.Cursor.ReadSearchReplayHoursAsync(default);
            Assert.IsTrue(history.Count <= 90 * 24 + 1);
            Assert.AreEqual(history.Distinct().Count(), history.Count);
            TestContext.WriteLine($"Synthetic hours={expected.Length}; facts={job.Sql.Facts.Count}; normal polls={job.NormalStarts.Count}; " +
                $"additional passes={job.RecoveryPasses}; maximum pending hours={job.MaxPending}; retained replay hours={history.Count}.");
        }

        [TestMethod]
        public async Task RecoveryOutage_DoesNotConsumeTheFinalAttemptOrStarveOtherHoursOrNormalWork()
        {
            var job = new DailyImporter();
            var old = Start.AddDays(-6);
            var hours = Enumerable.Range(0, 20).Select(i => old.AddHours(i)).ToArray();
            foreach (var t in hours) job.Source.ExtraPageViews.Add((t, t));
            await job.Importer.ImportAndSave(false, null);
            foreach (var t in hours) job.Source.ExtraCustomEvents.Add((t, t));
            await job.Cursor.WriteSearchRetriesAsync(hours.Select(t => new AppInsightsSearchRetry(t, Start)).ToArray());
            job.Sql.UnavailableHour = hours.First();
            Assert.AreEqual(0, job.RunUntil(Start.AddDays(2)));
            CollectionAssert.AreEqual(new[] { Start, Start.AddDays(1) }, job.NormalStarts.ToArray());
            Assert.IsTrue(job.Waits.First() >= TimeSpan.FromMinutes(5), "A failure must not start a hot retry loop.");
            Assert.IsTrue(job.Sql.FailureCount > 0 && job.Sql.FailureCount <= 2);
            Assert.IsTrue(job.Sql.Facts.SetEquals(hours.Skip(1)), "The one inaccessible hour must not block the others.");
            Assert.AreEqual(hours.First(), (await job.Cursor.ReadSearchRetriesAsync()).Single().HourUtc);
            Assert.IsTrue(job.Reasons.Contains("SearchRetryFailed"));
            Assert.IsFalse(job.Reasons.Contains("SearchRetriesStopped"), "A failed request is not proof the final attempt found no visit.");
            job.Sql.UnavailableHour = null;
            Assert.AreEqual(0, job.RunUntil(Start.AddDays(3)));
            Assert.IsTrue(job.Sql.Facts.SetEquals(hours));
            Assert.AreEqual(0, (await job.Cursor.ReadSearchRetriesAsync()).Count);
        }

        [TestMethod]
        public async Task OneShot_DoesNotDrainTheBacklog_AndHealthyContinuousPollingDoesNotSweepOldSearches()
        {
            var job = new DailyImporter();
            var hours = Enumerable.Range(0, 12).Select(i => Start.AddDays(-4).AddHours(i)).ToArray();
            foreach (var t in hours) job.Source.ExtraPageViews.Add((t, t));
            await job.Importer.ImportAndSave(false, null);
            foreach (var t in hours) job.Source.ExtraCustomEvents.Add((t, t));
            Assert.AreEqual(0, job.RunUntil(Start.AddDays(1)));
            Assert.AreEqual(0, job.RecoveryPasses);
            Assert.AreEqual(0, job.Sql.Facts.Count, "No global sweep for a resource without pending markers.");
            CollectionAssert.AreEqual(new[] { TimeSpan.FromDays(1) }, job.Waits.ToArray());

            await job.Cursor.WriteSearchRetriesAsync(hours.Select(t => new AppInsightsSearchRetry(t, Start)).ToArray());
            Assert.AreEqual(0, job.RunUntil(Start.AddDays(2), oneShot: true));
            Assert.AreEqual(8, job.Sql.Facts.Count);
            Assert.AreEqual(4, (await job.Cursor.ReadSearchRetriesAsync()).Count);
            Assert.AreEqual(0, job.RecoveryPasses);
            Assert.AreEqual(Start.AddDays(1), job.Clock.UtcNow, "One-shot must not wait for another pass.");
        }

        [TestMethod]
        public async Task PersistentRecoveryFailure_BacksOffButStillImportsNewVisitsAtTheNormalCadence()
        {
            var job = new DailyImporter();
            var hours = Enumerable.Range(0, 60).Select(i => Start.AddDays(-7).AddHours(i)).ToArray();
            foreach (var t in hours) job.Source.ExtraPageViews.Add((t, t));
            await job.Importer.ImportAndSave(false, null);
            foreach (var t in hours) job.Source.ExtraCustomEvents.Add((t, t));
            await job.Cursor.WriteSearchRetriesAsync(hours.Select(h => new AppInsightsSearchRetry(h, Start)).ToArray());
            job.Sql.AllRecoveryUnavailable = true;
            var fresh = Start.AddHours(12);
            job.Source.ExtraPageViews.Add((fresh, fresh));
            Assert.AreEqual(0, job.RunUntil(Start.AddDays(2)));
            CollectionAssert.AreEqual(new[] { Start, Start.AddDays(1) }, job.NormalStarts.ToArray());
            Assert.AreEqual(fresh, await job.Sql.GetNewestHitTimestampUtcAsync(default));
            Assert.IsTrue(job.Waits.Contains(TimeSpan.FromHours(1)), "Repeated failures should back off rather than poll every minute.");
            Assert.IsTrue(job.RecoveryPasses > 2 && job.RecoveryPasses < 60);
            Assert.AreEqual(60, (await job.Cursor.ReadSearchRetriesAsync()).Count, "An outage must not erase the due searches.");
            Assert.IsTrue(job.Reasons.Contains("SearchRetryFailed"));
        }

        [TestMethod]
        public async Task AdditionalPass_CannotSpendTheLastAttemptBeforeNormalImportReadsTheLateVisit()
        {
            var job = new DailyImporter();
            var hour = Start.AddDays(-3);
            var final = hour.AddDays(3).AddHours(1);
            var search = hour.AddHours(1).AddSeconds(-5);
            var others = Enumerable.Range(0, 9).Select(i => Start.AddDays(-2).AddHours(i)).ToArray();
            foreach (var t in others) job.Source.ExtraPageViews.Add((t, t));
            await job.Importer.ImportAndSave(false, null);
            foreach (var t in others) job.Source.ExtraCustomEvents.Add((t, t));
            job.Source.ExtraCustomEvents.Add((search, search));
            job.Source.ExtraPageViews.Add((search, final.AddSeconds(-15)));
            await job.Cursor.WriteSearchRetriesAsync(others.Select(t => new AppInsightsSearchRetry(t, Start))
                .Concat(new[] { new AppInsightsSearchRetry(hour, final) }).ToArray());
            job.Clock.Advance(TimeSpan.FromMinutes(59.5));
            // One extra pass is needed, but its wall clock is now AFTER the final attempt's deadline;
            // the late visit wasn't in SQL at the preceding normal poll. Leave that hour alone.
            Assert.AreEqual(0, job.RunUntil(job.Clock.UtcNow.AddDays(1)));
            Assert.AreEqual(1, job.RecoveryPasses);
            Assert.AreEqual(hour, (await job.Cursor.ReadSearchRetriesAsync()).Single().HourUtc);
            Assert.IsFalse(job.Sql.Facts.Contains(search));
            Assert.AreEqual(0, job.RunUntil(job.Clock.UtcNow.AddDays(1)));
            Assert.IsTrue(job.Sql.Facts.Contains(search));
            Assert.AreEqual(0, (await job.Cursor.ReadSearchRetriesAsync()).Count);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task StaleOrLateAdditionalPass_LeavesThePersistedHourForTheNextNormalPoll(bool deadlineReached)
        {
            var job = new DailyImporter();
            await job.Importer.ImportAndSave(false, null);
            var hour = Start.AddDays(-5);
            await job.Cursor.WriteSearchRetriesAsync(new[] { new AppInsightsSearchRetry(hour, Start) });
            job.Source.CountRangesRequested.Clear();
            var pass = await job.Importer.RecoverPendingSearchesAsync(deadlineReached ? Start : Start.AddMinutes(-1),
                deadlineReached ? Start : Start.AddDays(1), default);
            Assert.AreEqual(AppInsightsCycleOutcome.Degraded, pass.Outcome);
            CollectionAssert.Contains(pass.Reasons, deadlineReached ? "SearchRecoveryYielded" : "SearchRecoveryCursorChanged");
            Assert.AreEqual(0, job.Source.CountRangesRequested.Count);
            Assert.AreEqual(Start, await job.Cursor.ReadAsync());
            Assert.AreEqual(hour, (await job.Cursor.ReadSearchRetriesAsync()).Single().HourUtc);
        }
    }
}
