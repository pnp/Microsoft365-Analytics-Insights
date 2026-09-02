using Common.Entities.Config;
using DataUtils;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace Tests.UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory <see cref="IAppInsightsSourceLoader"/> - the App Insights REST API replaced by a
    /// dictionary, so the importer's day loop can be driven with no HTTP at all. See issue #374.
    /// </summary>
    public class FakeAppInsightsSourceLoader : IAppInsightsSourceLoader
    {
        /// <summary>Every day the importer asked for, in the order it asked.</summary>
        public List<DateTime> PageViewDaysRequested { get; } = new List<DateTime>();
        public List<DateTime> CustomEventDaysRequested { get; } = new List<DateTime>();

        /// <summary>How many page-views / custom events each day returns. Missing day = empty.</summary>
        public Dictionary<DateTime, int> PageViewCountByDay { get; } = new Dictionary<DateTime, int>();
        public Dictionary<DateTime, int> CustomEventCountByDay { get; } = new Dictionary<DateTime, int>();

        /// <summary>
        /// Days whose download blows up. The importer treats that as fatal for the whole run - DEBUG
        /// rethrows, Release abandons the run without reporting the section finished.
        /// </summary>
        public HashSet<DateTime> DaysThatFailToDownload { get; } = new HashSet<DateTime>();

        public Task<PageViewCollection> GetPageViewsAsync(DateTime forDateUtc, bool saveRestResponses)
        {
            PageViewDaysRequested.Add(forDateUtc);
            if (DaysThatFailToDownload.Contains(forDateUtc))
            {
                throw new InvalidOperationException($"Fake download failure for {forDateUtc:yyyy-MM-dd}");
            }

            var result = new PageViewCollection();
            var count = PageViewCountByDay.TryGetValue(forDateUtc, out var c) ? c : 0;
            for (var i = 0; i < count; i++)
            {
                result.Rows.Add(new PageViewAppInsightsQueryResult { AppInsightsTimestamp = forDateUtc.AddMinutes(i) });
            }
            return Task.FromResult(result);
        }

        public Task<CustomEventsResultCollection> GetCustomEventsAsync(DateTime forDateUtc, bool saveRestResponses)
        {
            CustomEventDaysRequested.Add(forDateUtc);
            if (DaysThatFailToDownload.Contains(forDateUtc))
            {
                throw new InvalidOperationException($"Fake download failure for {forDateUtc:yyyy-MM-dd}");
            }

            var result = new CustomEventsResultCollection();
            var count = CustomEventCountByDay.TryGetValue(forDateUtc, out var c) ? c : 0;
            for (var i = 0; i < count; i++)
            {
                result.Rows.Add(new PageExitEventAppInsightsQueryResult { AppInsightsTimestamp = forDateUtc.AddMinutes(i) });
            }
            return Task.FromResult(result);
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

    /// <summary><see cref="IImportDbMaintenance"/> that only records that it was asked to run.</summary>
    public class FakeImportDbMaintenance : IImportDbMaintenance
    {
        public int RunCount { get; private set; }

        public Task RunStartupMaintenanceAsync()
        {
            RunCount++;
            return Task.CompletedTask;
        }
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

        /// <summary>Throw when the page-view batch's first row falls on one of these days.</summary>
        public HashSet<DateTime> DaysThatFailToSave { get; } = new HashSet<DateTime>();

        public Task SavePageViewsAsync(PageViewCollection pageViews, List<FilterUrlConfig> filterUrls)
        {
            FiltersSeen = filterUrls;
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
            SavedCustomEvents.Add(events);
            return Task.CompletedTask;
        }
    }
}
