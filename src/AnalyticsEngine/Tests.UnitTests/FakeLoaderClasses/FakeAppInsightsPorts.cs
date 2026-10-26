using Common.Entities.Config;
using DataUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace Tests.UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory <see cref="IAppInsightsSourceLoader"/> - the App Insights REST API replaced by lists of rows, each with
    /// a timestamp and an ingestion time, so the importer's day loop, windows and cursor can be driven with no HTTP at
    /// all. Every query honours the window's timestamp and ingestion bounds, as the real KQL does. See issue #374.
    /// </summary>
    public class FakeAppInsightsSourceLoader : IAppInsightsSourceLoader
    {
        /// <summary>The day of every page-view / custom-event window the importer asked for, in the order it asked.</summary>
        public List<DateTime> PageViewDaysRequested { get; } = new List<DateTime>();
        public List<DateTime> CustomEventDaysRequested { get; } = new List<DateTime>();

        /// <summary>Every window asked for, in order.</summary>
        public List<AppInsightsReadWindow> PageViewWindowsRequested { get; } = new List<AppInsightsReadWindow>();
        public List<AppInsightsReadWindow> CustomEventWindowsRequested { get; } = new List<AppInsightsReadWindow>();

        /// <summary>Every range the hourly counts were asked for.</summary>
        public List<AppInsightsReadWindow> CountRangesRequested { get; } = new List<AppInsightsReadWindow>();

        /// <summary>When set, every fetch and save is recorded here in order, shared with the persistence fake.</summary>
        public List<string> CallLog { get; set; }

        /// <summary>
        /// How many page-views / custom events each day has, at day + i minutes and ingested at the same moment.
        /// Missing day = empty.
        /// </summary>
        public Dictionary<DateTime, int> PageViewCountByDay { get; } = new Dictionary<DateTime, int>();
        public Dictionary<DateTime, int> CustomEventCountByDay { get; } = new Dictionary<DateTime, int>();

        /// <summary>Rows with an explicit timestamp and ingestion time, in addition to the per-day counts.</summary>
        public List<(DateTime TimestampUtc, DateTime IngestedUtc)> ExtraPageViews { get; } = new List<(DateTime, DateTime)>();
        public List<(DateTime TimestampUtc, DateTime IngestedUtc)> ExtraCustomEvents { get; } = new List<(DateTime, DateTime)>();

        /// <summary>
        /// Days whose window downloads blow up. The importer counts that against the day and stops the cycle there,
        /// until the day's forced retries stop.
        /// </summary>
        public HashSet<DateTime> DaysThatFailToDownload { get; } = new HashSet<DateTime>();

        /// <summary>When set, the hourly count query fails with it.</summary>
        public Exception CountsFailWith { get; set; }

        /// <summary>Who viewed the page, by (day, row index). Null leaves page views anonymous.</summary>
        public Func<DateTime, int, string> PageViewUsername { get; set; }

        private IEnumerable<(DateTime Timestamp, DateTime Ingested, string Username)> PageViewRows() =>
            PageViewCountByDay.SelectMany(d => Enumerable.Range(0, d.Value)
                    .Select(i => (Timestamp: d.Key.AddMinutes(i), Ingested: d.Key.AddMinutes(i), Username: PageViewUsername?.Invoke(d.Key, i))))
                .Concat(ExtraPageViews.Select(r => (Timestamp: r.TimestampUtc, Ingested: r.IngestedUtc, Username: (string)null)));

        private IEnumerable<(DateTime Timestamp, DateTime Ingested)> CustomEventRows() =>
            CustomEventCountByDay.SelectMany(d => Enumerable.Range(0, d.Value).Select(i => (Timestamp: d.Key.AddMinutes(i), Ingested: d.Key.AddMinutes(i))))
                .Concat(ExtraCustomEvents.Select(r => (Timestamp: r.TimestampUtc, Ingested: r.IngestedUtc)));

        private static bool InWindow(DateTime timestamp, DateTime ingested, AppInsightsReadWindow w) =>
            timestamp >= w.FromUtc && timestamp < w.ToUtc
            && (!w.IngestedFromUtc.HasValue || ingested >= w.IngestedFromUtc.Value)
            && (!w.IngestedBeforeUtc.HasValue || ingested < w.IngestedBeforeUtc.Value);

        private static DateTime Hour(DateTime t) => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);

        public Task<IReadOnlyList<AppInsightsHourlyRowCount>> GetHourlyRowCountsAsync(AppInsightsReadWindow range)
        {
            CountRangesRequested.Add(range);
            if (CountsFailWith != null)
            {
                return Task.FromException<IReadOnlyList<AppInsightsHourlyRowCount>>(CountsFailWith);
            }
            var counts = PageViewRows().Where(r => InWindow(r.Timestamp, r.Ingested, range))
                    .GroupBy(r => Hour(r.Timestamp)).Select(g => new AppInsightsHourlyRowCount(AppInsightsTelemetryKind.PageViews, g.Key, g.Count()))
                .Concat(CustomEventRows().Where(r => InWindow(r.Timestamp, r.Ingested, range))
                    .GroupBy(r => Hour(r.Timestamp)).Select(g => new AppInsightsHourlyRowCount(AppInsightsTelemetryKind.Events, g.Key, g.Count())))
                .ToList();
            return Task.FromResult<IReadOnlyList<AppInsightsHourlyRowCount>>(counts);
        }

        public Task<PageViewCollection> GetPageViewsAsync(AppInsightsReadWindow window, bool saveRestResponses)
        {
            PageViewDaysRequested.Add(window.FromUtc.Date);
            PageViewWindowsRequested.Add(window);
            CallLog?.Add($"fetch pageviews {window.FromUtc:yyyy-MM-dd HH:mm}");
            if (DaysThatFailToDownload.Contains(window.FromUtc.Date))
            {
                // A FAULTED TASK, not a synchronous throw: that is the shape a real HTTP failure arrives in.
                return Task.FromException<PageViewCollection>(
                    new InvalidOperationException($"Fake download failure for {window.FromUtc:yyyy-MM-dd}"));
            }

            var result = new PageViewCollection();
            foreach (var row in PageViewRows().Where(r => InWindow(r.Timestamp, r.Ingested, window)).OrderBy(r => r.Timestamp))
            {
                result.Rows.Add(new PageViewAppInsightsQueryResult { AppInsightsTimestamp = row.Timestamp, Username = row.Username });
            }
            return Task.FromResult(result);
        }

        public Task<CustomEventsResultCollection> GetCustomEventsAsync(AppInsightsReadWindow window, bool saveRestResponses)
        {
            CustomEventDaysRequested.Add(window.FromUtc.Date);
            CustomEventWindowsRequested.Add(window);
            CallLog?.Add($"fetch events {window.FromUtc:yyyy-MM-dd HH:mm}");
            if (DaysThatFailToDownload.Contains(window.FromUtc.Date))
            {
                return Task.FromException<CustomEventsResultCollection>(
                    new InvalidOperationException($"Fake download failure for {window.FromUtc:yyyy-MM-dd}"));
            }

            var result = new CustomEventsResultCollection();
            foreach (var row in CustomEventRows().Where(r => InWindow(r.Timestamp, r.Ingested, window)).OrderBy(r => r.Timestamp))
            {
                result.Rows.Add(new PageExitEventAppInsightsQueryResult { AppInsightsTimestamp = row.Timestamp });
            }
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// In-memory <see cref="IAppInsightsIngestionCursorStore"/> that can fail on demand.
    /// </summary>
    public class InMemoryIngestionCursorStore : IAppInsightsIngestionCursorStore
    {
        public InMemoryIngestionCursorStore(DateTime? cursorUtc = null)
        {
            CursorUtc = cursorUtc;
        }

        public DateTime? CursorUtc { get; set; }
        public int Writes { get; private set; }
        public Exception ReadFailsWith { get; set; }
        public Exception WriteFailsWith { get; set; }

        public string Description => "fake cursor store";

        public Task<DateTime?> ReadAsync() => ReadFailsWith != null ? Task.FromException<DateTime?>(ReadFailsWith) : Task.FromResult(CursorUtc);

        public Task WriteAsync(DateTime cursorUtc)
        {
            if (WriteFailsWith != null) return Task.FromException(WriteFailsWith);
            Writes++;
            CursorUtc = cursorUtc;
            return Task.CompletedTask;
        }
    }

    /// <summary>In-memory <see cref="IHitWatermarkStore"/>.</summary>
    public class InMemoryHitWatermarkStore : IHitWatermarkStore
    {
        public InMemoryHitWatermarkStore(DateTime? newestHitTimestampUtc = null)
        {
            NewestHitTimestampUtc = newestHitTimestampUtc;
        }

        public DateTime? NewestHitTimestampUtc { get; set; }
        public int ReadCount { get; private set; }

        public Task<DateTime?> GetNewestHitTimestampUtcAsync()
        {
            ReadCount++;
            return Task.FromResult(NewestHitTimestampUtc);
        }
    }

    /// <summary>In-memory <see cref="ISiteFilterLoader"/>.</summary>
    public class FakeSiteFilterLoader : ISiteFilterLoader
    {
        public List<FilterUrlConfig> Filters { get; set; } = new List<FilterUrlConfig>();

        public Task<List<FilterUrlConfig>> LoadAsync() => Task.FromResult(Filters);
    }

    /// <summary>
    /// In-memory <see cref="IAppInsightsDayPersistenceManager"/>, recording what each day saved and able to
    /// fail on demand so the per-day failure isolation can be asserted.
    /// </summary>
    public class InMemoryAppInsightsDayPersistenceManager : IAppInsightsDayPersistenceManager
    {
        public List<PageViewCollection> SavedPageViews { get; } = new List<PageViewCollection>();
        public List<CustomEventsResultCollection> SavedCustomEvents { get; } = new List<CustomEventsResultCollection>();
        public List<FilterUrlConfig> FiltersSeen { get; private set; }

        /// <summary>When set, every save is recorded here in order - share it with the source fake's.</summary>
        public List<string> CallLog { get; set; }

        /// <summary>Throw when the page-view batch's first row falls on one of these days.</summary>
        public HashSet<DateTime> DaysThatFailToSave { get; } = new HashSet<DateTime>();

        /// <summary>
        /// Throw from the custom-event save when the event batch's first row falls on one of these days - after the
        /// day's page views have saved, as when one event section fails. See <see cref="CustomEventSaveIncompleteException"/>.
        /// </summary>
        public HashSet<DateTime> DaysWhoseEventsFailToSave { get; } = new HashSet<DateTime>();

        public Task SavePageViewsAsync(PageViewCollection pageViews, List<FilterUrlConfig> filterUrls)
        {
            FiltersSeen = filterUrls;
            CallLog?.Add($"save pageviews {pageViews.Rows.Count}");
            var day = pageViews.Rows.Count > 0 ? pageViews.Rows[0].Timestamp.Date : (DateTime?)null;
            if (day.HasValue && DaysThatFailToSave.Contains(day.Value))
            {
                throw new InvalidOperationException($"Fake save failure for {day.Value:yyyy-MM-dd}");
            }
            SavedPageViews.Add(pageViews);
            return Task.CompletedTask;
        }

        public Task SaveCustomEventsAsync(CustomEventsResultCollection events)
        {
            CallLog?.Add($"save events {events.Rows.Count}");
            var day = events.Rows.Count > 0 ? events.Rows[0].Timestamp.Date : (DateTime?)null;
            if (day.HasValue && DaysWhoseEventsFailToSave.Contains(day.Value))
            {
                return Task.FromException(new CustomEventSaveIncompleteException(new[] { "Clicks" }));
            }
            SavedCustomEvents.Add(events);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// In-memory <see cref="IPageViewsPersistenceManager"/> (issue #369): records the batch and the org-URL
    /// filter list it was handed, and returns whatever result the test configures.
    /// </summary>
    public class InMemoryPageViewsPersistenceManager : IPageViewsPersistenceManager
    {
        public List<PageViewCollection> SavedPageViews { get; } = new List<PageViewCollection>();
        public List<FilterUrlConfig> FiltersSeen { get; private set; }

        /// <summary>What the next save reports back. Defaults to an all-zero result.</summary>
        public PageViewSaveResult NextResult { get; set; } = PageViewSaveResult.Empty;

        public Task<PageViewSaveResult> SavePageViewsAsync(PageViewCollection pageViews, List<FilterUrlConfig> filterUrls)
        {
            SavedPageViews.Add(pageViews);
            FiltersSeen = filterUrls;
            return Task.FromResult(NextResult);
        }
    }

    /// <summary>
    /// Shared behaviour of the four in-memory custom-event section write ports (issue #369): record the
    /// call in a shared log so section ORDER is assertable, remember the batch, and optionally fail.
    /// </summary>
    public abstract class InMemoryEventSectionPort
    {
        private readonly string _sectionName;
        private readonly List<string> _callLog;

        protected InMemoryEventSectionPort(string sectionName, List<string> callLog)
        {
            _sectionName = sectionName;
            _callLog = callLog ?? new List<string>();
        }

        /// <summary>What this section reports back when it succeeds.</summary>
        public int ReturnCount { get; set; }

        /// <summary>When set, the save fails with this exception instead of returning <see cref="ReturnCount"/>.</summary>
        public Exception FailWith { get; set; }

        /// <summary>
        /// When set, the save returns this (initially incomplete) task instead of a completed one, so a
        /// test can hold a section open and observe whether the orchestration waits for it.
        /// </summary>
        public TaskCompletionSource<int> Gate { get; set; }

        /// <summary>Every batch this port was asked to save, in order.</summary>
        public List<CustomEventsResultCollection> Saved { get; } = new List<CustomEventsResultCollection>();

        protected Task<int> RecordAndRespond(CustomEventsResultCollection events)
        {
            // Record BEFORE returning a possible failure, so "was this section attempted?" stays
            // assertable for a failing section too.
            _callLog.Add(_sectionName);
            Saved.Add(events);

            if (FailWith != null)
            {
                // A FAULTED TASK, not a synchronous throw. A synchronous throw would land in the caller's
                // catch whether or not the caller awaited the call, so a fake that threw synchronously
                // could not detect a dropped `await` in the section orchestration.
                return Task.FromException<int>(FailWith);
            }
            if (Gate != null)
            {
                return Gate.Task;
            }
            return Task.FromResult(ReturnCount);
        }
    }

    /// <summary>In-memory <see cref="IHitUpdatePersistenceManager"/>.</summary>
    public class InMemoryHitUpdatePersistenceManager : InMemoryEventSectionPort, IHitUpdatePersistenceManager
    {
        public InMemoryHitUpdatePersistenceManager(List<string> callLog = null) : base("Hit updates", callLog) { }

        public Task<int> SaveHitUpdatesAsync(CustomEventsResultCollection events) => RecordAndRespond(events);
    }

    /// <summary>In-memory <see cref="ISearchesPersistenceManager"/>.</summary>
    public class InMemorySearchesPersistenceManager : InMemoryEventSectionPort, ISearchesPersistenceManager
    {
        public InMemorySearchesPersistenceManager(List<string> callLog = null) : base("Searches", callLog) { }

        public Task<int> SaveSearchesAsync(CustomEventsResultCollection events) => RecordAndRespond(events);
    }

    /// <summary>In-memory <see cref="IPageUpdatePersistenceManager"/>.</summary>
    public class InMemoryPageUpdatePersistenceManager : InMemoryEventSectionPort, IPageUpdatePersistenceManager
    {
        public InMemoryPageUpdatePersistenceManager(List<string> callLog = null) : base("Page updates", callLog) { }

        public Task<int> SavePageUpdatesAsync(CustomEventsResultCollection events) => RecordAndRespond(events);
    }

    /// <summary>In-memory <see cref="IClicksPersistenceManager"/>.</summary>
    public class InMemoryClicksPersistenceManager : InMemoryEventSectionPort, IClicksPersistenceManager
    {
        public InMemoryClicksPersistenceManager(List<string> callLog = null) : base("Clicks", callLog) { }

        public Task<int> SaveClicksAsync(CustomEventsResultCollection events) => RecordAndRespond(events);
    }
}
