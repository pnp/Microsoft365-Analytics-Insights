using Azure.Identity;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.UserScope;
using DataUtils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Sql;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>
    /// Starting class for AppInsights importing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cycle decides what to read (<see cref="AppInsightsReadMode"/>): normally the rows App Insights ingested since
    /// the stored <c>ingestion_time()</c> cursor, whatever day their timestamp is on; until there is a cursor, whole days
    /// from the newest stored hit; and with <c>ReadHitsDaysBeforeToday</c>, whole days from that many days ago.
    /// </para>
    /// <para>
    /// One aggregate query then counts those rows per hour, which says which days to visit and how to cut each into
    /// windows of at most <see cref="AppInsightsWindowPlanner.DefaultMaxRowsPerWindow"/> rows. Each window is fetched,
    /// filtered, staged and merged before the next is fetched, so memory is bounded by a window rather than a day. A day's
    /// page views are saved before its events, which update the hits those page views create.
    /// </para>
    /// <para>
    /// A day that fails - to download or to save - stops the cycle there, and the next cycle retries it before any later
    /// day; after <see cref="AppInsightsDayFailureTracker.MaxAttempts"/> failures in a row its forced retries stop. The
    /// cursor only moves when the cycle got to the end and the ingestion hours of retryable searches still awaiting visits have
    /// been checkpointed. Those hours are retried separately without rereading unrelated telemetry.
    /// </para>
    /// </remarks>
    public class AppInsightsImporter
    {
        private const string StageDownload = "Download";
        private const string StageSave = "Save";

        private readonly AppConfig _importConfig;
        private readonly AnalyticsLogger _logger;
        private readonly IClock _clock;
        private readonly IAnalyticsDbContextFactory _contextFactory;

        // Optional ports (issue #374). When all of them are supplied the import runs with no database
        // context and no HTTP client created at all, which is what makes the orchestration testable.
        private readonly IAppInsightsSourceLoader _source;
        private readonly ISiteFilterLoader _siteFilterLoader;
        private readonly IHitWatermarkStore _hitWatermarkStore;
        private readonly IAppInsightsDayPersistenceManager _persistence;

        // UserGroupsFilter: who this cycle may import data about, and (process-lifetime) how far it has read.
        private readonly UserImportScope _userScope;
        private readonly AppInsightsScanWatermark _scanWatermark;

        // Process-lifetime: which days failed, and how many times in a row.
        private readonly AppInsightsDayFailureTracker _dayFailures;

        // Durable: how far App Insights' ingestion has been imported. Null reads whole days from the newest stored hit.
        private readonly IAppInsightsIngestionCursorStore _cursorStore;
        private readonly IAppInsightsIngestionCursorStore _searchRetryStore;

        private readonly int _maxRowsPerWindow;

        /// <param name="userScope">The <c>UserGroupsFilter</c> scope for this cycle; null means unfiltered.</param>
        /// <param name="scanWatermark">
        /// Process-lifetime record of how far the import has read, consulted only when the scope is filtered and there is
        /// no ingestion cursor. See <see cref="AppInsightsScanWatermark"/>.
        /// </param>
        /// <param name="dayFailureTracker">
        /// Process-lifetime record of the days that failed, so a failed day is retried next cycle before any later day,
        /// and its forced retries stop after a few attempts. See <see cref="AppInsightsDayFailureTracker"/>. Null uses
        /// one shared by the whole process.
        /// </param>
        /// <param name="cursorStore">
        /// Where the <c>ingestion_time()</c> cursor is kept. The web-job host supplies one backed by the runtime state
        /// table (or in memory, once per process, without Storage). Null reads whole days from the newest stored hit
        /// every cycle, as before the cursor existed.
        /// </param>
        /// <param name="maxRowsPerWindow">The most rows a planned query window holds. See <see cref="AppInsightsWindowPlanner"/>.</param>
        public AppInsightsImporter(AppConfig importConfig, AnalyticsLogger logger, IClock clock = null,
            IAppInsightsSourceLoader source = null,
            ISiteFilterLoader siteFilterLoader = null,
            IHitWatermarkStore hitWatermarkStore = null,
            IAppInsightsDayPersistenceManager persistence = null,
            IAnalyticsDbContextFactory contextFactory = null,
            UserImportScope userScope = null,
            AppInsightsScanWatermark scanWatermark = null,
            AppInsightsDayFailureTracker dayFailureTracker = null,
            IAppInsightsIngestionCursorStore cursorStore = null,
            int maxRowsPerWindow = AppInsightsWindowPlanner.DefaultMaxRowsPerWindow)
        {
            _importConfig = importConfig;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? SystemClock.Instance;
            _contextFactory = contextFactory ?? DefaultAnalyticsDbContextFactory.Instance;
            _source = source;
            _siteFilterLoader = siteFilterLoader;
            _hitWatermarkStore = hitWatermarkStore;
            _persistence = persistence;
            _userScope = userScope ?? UserImportScope.Unfiltered;
            _scanWatermark = scanWatermark;
            _dayFailures = dayFailureTracker ?? AppInsightsDayFailureTracker.ProcessDefault;
            _cursorStore = cursorStore;
            _searchRetryStore = cursorStore ?? new KeyValueIngestionCursorStore(_dayFailures.InMemorySearchRetryState,
                AppInsightsAPIClient.ParseConnectionStringValue(importConfig?.AppInsightsConnectionString, "ApplicationId"));
            if (maxRowsPerWindow < 1) throw new ArgumentOutOfRangeException(nameof(maxRowsPerWindow));
            _maxRowsPerWindow = maxRowsPerWindow;
        }

        /// <summary>
        /// True when every database-backed port was supplied, so no context needs creating at all.
        /// </summary>
        private bool AllDatabasePortsSupplied =>
            _siteFilterLoader != null && _hitWatermarkStore != null && _persistence != null;

        /// <summary>
        /// Runs one import cycle. A failure the cycle handles - a day that failed and will be retried, a cursor that
        /// could not be saved - is in the result's outcome rather than thrown, so the host can report a failed cycle as
        /// failed instead of as completed.
        /// </summary>
        public Task<AppInsightsImportCycleResult> ImportAndSave(bool saveRestResponses, int? daysBeforeOverride) =>
            ImportAndSave(saveRestResponses, daysBeforeOverride, CancellationToken.None);

        public async Task<AppInsightsImportCycleResult> ImportAndSave(bool saveRestResponses, int? daysBeforeOverride, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AllDatabasePortsSupplied)
            {
                return await ImportAndSaveWith(saveRestResponses, daysBeforeOverride,
                    _siteFilterLoader, _hitWatermarkStore, _persistence, cancellationToken);
            }

            // Production: one context for the whole run, exactly as before, with SQL adapters over it for
            // whichever ports were not supplied.
            using (var db = _contextFactory.Create())
            {
                return await ImportAndSaveWith(saveRestResponses, daysBeforeOverride,
                    _siteFilterLoader ?? new SqlSiteFilterLoader(db),
                    _hitWatermarkStore ?? new SqlHitWatermarkStore(db),
                    _persistence ?? new SqlAppInsightsDayPersistenceManager(db, _logger, _importConfig, _contextFactory), cancellationToken);
            }
        }

        private async Task<AppInsightsImportCycleResult> ImportAndSaveWith(bool saveRestResponses, int? daysBeforeOverride,
            ISiteFilterLoader siteFilterLoader,
            IHitWatermarkStore hitWatermarkStore, IAppInsightsDayPersistenceManager persistence, CancellationToken cancellationToken)
        {
            var result = new AppInsightsImportCycleResult();

            // App Insights timestamps are UTC, so the scan window must be UTC too. Using local
            // DateTime.Now on a non-UTC host shifts the day boundaries and the per-day KQL filter,
            // missing or duplicating edge hits near midnight. The rule itself lives in
            // AppInsightsImportWindow so it can be tested without a database or the wall clock (#374).
            var nowUtc = _clock.UtcNow;
            var scanFromDateOverride = AppInsightsImportWindow.ResolveOverrideStartUtc(daysBeforeOverride, nowUtc);

            var sw = Stopwatch.StartNew();
            var filterUrls = await siteFilterLoader.LoadAsync();
            _logger.LogInformation($"Startup: loaded {filterUrls.Count} URL filters in {sw.Elapsed.TotalSeconds:N1}s");

            var newestHitTimestamp = await hitWatermarkStore.GetNewestHitTimestampUtcAsync();

            AppInsightsReadWindow range;
            DateTime? cursorUtc = null;
            DateTime? storedCursorUtc = null;
            DateTime? newestStoredHitAtCursorUtc = null;
            var cursorReadFailed = false;
            if (scanFromDateOverride.HasValue)
            {
                result.Mode = AppInsightsReadMode.Backfill;
                var startDate = AppInsightsImportWindow.ResolveStartDateUtc(scanFromDateOverride, null, nowUtc);
                range = new AppInsightsReadWindow(startDate.Date, nowUtc.Date.AddDays(1));
            }
            else
            {
                if (_cursorStore != null)
                {
                    try
                    {
                        storedCursorUtc = cursorUtc = await _cursorStore.ReadAsync();
                        if (cursorUtc.HasValue)
                        {
                            newestStoredHitAtCursorUtc = await _cursorStore.ReadNewestStoredHitAtCursorAsync();
                        }
                    }
                    catch (Exception ex) when (!(ex is OutOfMemoryException))
                    {
                        // The cursor is not written at the end of this cycle, so the next one resumes from the stored cursor
                        // and nothing ingested since is skipped.
                        cursorReadFailed = true;
                        cursorUtc = null;
                        result.Degrade("CursorReadFailed");
                        _logger.LogWarning($"AppInsightsCursor: Outcome=ReadFailed; Store={_cursorStore.Description}; {ExceptionDescriber.Chain(ex)}. " +
                            "Reading whole days from the newest stored hit this cycle, and leaving the stored cursor as it is.", ex);
                    }
                }
                if (cursorUtc.HasValue && !newestHitTimestamp.HasValue)
                {
                    _logger.LogWarning($"AppInsightsCursor: the ingestion cursor ({cursorUtc.Value:O}) says data was imported, but the hits table is empty " +
                        "(a new or restored database?). Ignoring the cursor and starting again from scratch.");
                    cursorUtc = null;
                }
                else if (cursorUtc.HasValue)
                {
                    var newestStoredHitUtc = AppInsightsImportWindow.NormalizeStoredHitTimestampUtc(newestHitTimestamp).Value;
                    var rewound = AppInsightsIngestionCursorPolicy.CursorAfterDatabaseRestore(cursorUtc.Value, newestStoredHitUtc, newestStoredHitAtCursorUtc);
                    if (rewound.HasValue)
                    {
                        _logger.LogWarning($"AppInsightsCursor: Outcome=RewoundAfterRestore; Cursor={cursorUtc.Value:O}; RewoundTo={rewound.Value:O}; " +
                            $"NewestStoredHit={newestStoredHitUtc:O}; NewestStoredHitAtCursor={(newestStoredHitAtCursorUtc.HasValue ? newestStoredHitAtCursorUtc.Value.ToString("O", CultureInfo.InvariantCulture) : "none")}. " +
                            "The database holds fewer hits than when the cursor was written (a point-in-time restore or an older copy?), so " +
                            "ingestion since the newest stored hit is read again; rows already stored are skipped.");
                        cursorUtc = rewound;
                    }
                }

                if (cursorUtc.HasValue)
                {
                    result.Mode = AppInsightsReadMode.Incremental;
                    range = AppInsightsIngestionCursorPolicy.IncrementalRange(cursorUtc.Value, nowUtc);
                }
                else
                {
                    result.Mode = AppInsightsReadMode.Bootstrap;
                    var startDate = ResolveStartFromStoredHits(newestHitTimestamp, nowUtc);
                    range = new AppInsightsReadWindow(startDate.Date, nowUtc + AppInsightsIngestionCursorPolicy.FutureBound, null, nowUtc);
                }
            }

            _logger.LogInformation($"AppInsightsImport plan: Mode={result.Mode}; Range={range}; " +
                $"Cursor={(cursorUtc.HasValue ? cursorUtc.Value.ToString("O", CultureInfo.InvariantCulture) : "none")}; " +
                $"NewestStoredHit={(newestHitTimestamp.HasValue ? newestHitTimestamp.Value.ToString("O", CultureInfo.InvariantCulture) : "none")}.");

            // L-8: started, or the "Hits import" ElapsedSeconds was always 0.
            var jobTimer = new JobTimer(_logger, "Hits import");
            jobTimer.Start();

            AppInsightsSearchRetries searchRetries = null;
            if (!cursorReadFailed && result.Mode != AppInsightsReadMode.Backfill)
            {
                try
                {
                    searchRetries = new AppInsightsSearchRetries(await _searchRetryStore.ReadSearchRetriesAsync());
                    var lost = searchRetries.RemoveOutsideRetention(nowUtc);
                    if (lost > 0)
                    {
                        result.Degrade("SearchRetriesStopped");
                        _logger.LogWarning($"AppInsightsSearchRetry: {lost} pending ingestion hour(s) exceeded the source lookback after a pause; automatic retries stopped.");
                    }
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    searchRetries = null;
                    result.Degrade("SearchRetryReadFailed");
                    _logger.LogWarning($"AppInsightsSearchRetry: checkpoint could not be read. Leaving it and the ingestion cursor unchanged. {SearchFailure(ex)}");
                }
            }

            async Task<bool> ImportWithRetriesAsync(IAppInsightsSourceLoader source)
            {
                var finished = await ImportRangeAsync(source, range, saveRestResponses, filterUrls, persistence, result, cancellationToken);
                var withoutHour = result.Days.Sum(d => d.SearchesWithoutIngestionTime);
                if (withoutHour > 0)
                {
                    result.Degrade("SearchNoIngestionTime");
                    _logger.LogWarning($"AppInsightsSearchRetry: {withoutHour} unmatched search event(s) had no ingestion hour to retry. " +
                        "ReadHitsDaysBeforeToday can reread the timestamp day.");
                }
                if (result.Mode == AppInsightsReadMode.Backfill && result.Days.Any(d => d.UnmatchedSearches > 0))
                    result.Degrade("UnmatchedBackfillSearches");
                if (searchRetries == null) return finished;
                foreach (var hour in result.Days.SelectMany(d => d.UnmatchedSearchHours).Distinct())
                {
                    // Without hits Bootstrap can reread the same historical days every cycle; do not rearm an
                    // expired hour already passed by the stored cursor. Respect a restore's explicit rewind.
                    var alreadyCovered = cursorUtc ?? storedCursorUtc;
                    bool newlyRead = _cursorStore != null && (!alreadyCovered.HasValue || hour.AddHours(1) > alreadyCovered.Value);
                    if (!searchRetries.Observe(hour, nowUtc, newlyRead)) result.Degrade("SearchRetriesStopped");
                }
                // A later day's page view may create an earlier day's missing visit. Reconcile only after normal imports.
                if (finished)
                    await ReconcileSearchHoursAsync(source, persistence, searchRetries, nowUtc, result);
                result.PendingSearchHours = searchRetries.Hours.Count;
                if (result.PendingSearchHours > 0) result.Degrade("SearchSessionsPending");
                return finished;
            }

            bool completed;
            if (_source != null)
            {
                completed = await ImportWithRetriesAsync(_source);
            }
            else
            {
                var credential = new ClientSecretCredential(
                    this._importConfig.TenantGUID.ToString(),
                    this._importConfig.ClientID,
                    this._importConfig.ClientSecret);
                using (var ai = new AppInsightsAPIClient(this._importConfig.AppInsightsConnectionString, credential, _logger))
                {
                    completed = await ImportWithRetriesAsync(ai);
                }
            }

            if (searchRetries?.Changed == true)
            {
                try
                {
                    // Never move the main cursor past unfinished searches until their source hours are durable.
                    await _searchRetryStore.WriteSearchRetriesAsync(searchRetries.Hours);
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    result.PendingSearchHours = null;
                    result.Degrade("SearchRetryWriteFailed");
                    _logger.LogWarning($"AppInsightsSearchRetry: checkpoint could not be saved. The ingestion cursor was not advanced. {SearchFailure(ex)}");
                    return result;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!completed)
            {
                // Stopped early: the day it stopped at is retried next cycle, and the cursor stays where it was so nothing
                // read this cycle is skipped. Not a finished section, or liveness monitoring would count it as one.
                return result;
            }

            if (result.Mode != AppInsightsReadMode.Backfill && _cursorStore != null && searchRetries != null)
            {
                var newCursor = range.IngestedBeforeUtc.Value;
                try
                {
                    var newestStoredHitUtc = await ReadNewestStoredHitAfterImportAsync(hitWatermarkStore, newestHitTimestamp);
                    cancellationToken.ThrowIfCancellationRequested();
                    await _cursorStore.WriteAsync(newCursor, newestStoredHitUtc);
                    result.CursorAdvancedToUtc = newCursor;
                    _logger.LogInformation($"AppInsightsCursor: Outcome=Advanced; Cursor={newCursor:O}; Previous={(cursorUtc.HasValue ? cursorUtc.Value.ToString("O", CultureInfo.InvariantCulture) : "none")}.");
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException) && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
                {
                    result.Degrade("CursorWriteFailed");
                    _logger.LogWarning($"AppInsightsCursor: Outcome=WriteFailed; Store={_cursorStore.Description}; {ExceptionDescriber.Chain(ex)}. " +
                        "The next cycle reads from the previous cursor again; the SQL merges skip facts already stored.", ex);
                }
            }

            jobTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedSectionImport);
            return result;
        }

        /// <summary>
        /// The newest stored hit once this cycle has saved, recorded beside the cursor so the next cycle can tell that
        /// the database lost hits (#849). Falls back to the value read at the start of the cycle, which is older but
        /// still one this database has reached, if it cannot be read.
        /// </summary>
        private async Task<DateTime?> ReadNewestStoredHitAfterImportAsync(IHitWatermarkStore hitWatermarkStore, DateTime? newestAtStart)
        {
            try
            {
                var newest = await hitWatermarkStore.GetNewestHitTimestampUtcAsync();
                return AppInsightsImportWindow.NormalizeStoredHitTimestampUtc(newest ?? newestAtStart);
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                _logger.LogWarning($"AppInsightsCursor: could not re-read the newest stored hit after saving ({ExceptionDescriber.Chain(ex)}); " +
                    "recording the one read at the start of the cycle beside the cursor.", ex);
                return AppInsightsImportWindow.NormalizeStoredHitTimestampUtc(newestAtStart);
            }
        }

        /// <summary>
        /// Where a cycle with no ingestion cursor starts: the debug override, or the newest stored hit, or 31 days ago,
        /// rewound a little to catch edge hits - and never later than a day that failed and is awaiting retry.
        /// </summary>
        private DateTime ResolveStartFromStoredHits(DateTime? newestHitTimestamp, DateTime nowUtc)
        {
            var effectiveNewestHitTimestamp = AppInsightsImportWindow.NormalizeStoredHitTimestampUtc(newestHitTimestamp);

            // Under UserGroupsFilter the newest STORED hit stops moving whenever the people in scope stop browsing, so
            // resume from how far this process has already READ when that is later. See AppInsightsScanWatermark.
            var newestScanned = _userScope.IsFiltered ? _scanWatermark?.NewestScannedUtc : null;
            if (newestScanned.HasValue && (!effectiveNewestHitTimestamp.HasValue || newestScanned.Value > effectiveNewestHitTimestamp.Value))
            {
                _logger.LogInformation($"Resuming from {newestScanned.Value:O}, the newest page view already read this process; " +
                    "later page views by people outside UserGroupsFilter are not stored, so the newest stored hit is older.");
                effectiveNewestHitTimestamp = newestScanned;
            }
            if (effectiveNewestHitTimestamp.HasValue && effectiveNewestHitTimestamp.Value > nowUtc)
            {
                _logger.LogWarning($"App Insights hit watermark is in the future: newest stored hit_timestamp {effectiveNewestHitTimestamp.Value:O} is later than importer clock {nowUtc:O}. Using the importer clock as the watermark for this run so the import window does not walk backwards.");
                effectiveNewestHitTimestamp = nowUtc;
            }

            var startDate = AppInsightsImportWindow.ResolveStartDateUtc(null, effectiveNewestHitTimestamp, nowUtc);

            // A day that failed is retried before any later day, even if the newest stored hit has moved past it since
            // (a browser with its clock ahead can do that). See AppInsightsDayFailureTracker.
            var retryDayUtc = _dayFailures.EarliestDayAwaitingRetryUtc;
            if (retryDayUtc.HasValue && retryDayUtc.Value < startDate.Date)
            {
                _logger.LogInformation($"Day {retryDayUtc.Value:yyyy-MM-dd} failed in an earlier cycle; starting from it so it is retried before any later day.");
                startDate = retryDayUtc.Value;
            }
            return startDate;
        }

        /// <summary>
        /// Counts what <paramref name="range"/> holds, then imports it day by day.
        /// </summary>
        /// <returns>False when the cycle stopped early: the counts could not be read, or a day failed and will be retried.</returns>
        private async Task<bool> ImportRangeAsync(IAppInsightsSourceLoader source, AppInsightsReadWindow range, bool saveRestResponses,
            List<FilterUrlConfig> filterUrls, IAppInsightsDayPersistenceManager persistence, AppInsightsImportCycleResult result, CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            IReadOnlyList<AppInsightsHourlyRowCount> counts;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                counts = source is ICancellableAppInsightsSourceLoader cancellable
                    ? await cancellable.GetHourlyRowCountsAsync(range, cancellationToken)
                    : await source.GetHourlyRowCountsAsync(range);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException) && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                result.Fail("RowCountQueryFailed");
                _logger.LogError($"AppInsightsRowCounts: Outcome=Failed; Mode={result.Mode}; ElapsedMs={sw.ElapsedMilliseconds}; {ExceptionDescriber.Chain(ex)}. " +
                    "Nothing was read this cycle; trying again next cycle.", ex);
                if (Debugger.IsAttached)
                {
                    throw;
                }
                return false;
            }

            var countsByDay = counts.Where(c => c.Count > 0).GroupBy(c => c.HourUtc.Date).OrderBy(g => g.Key).ToList();
            _logger.LogInformation(string.Format(CultureInfo.InvariantCulture,
                "AppInsightsRowCounts: Outcome=Succeeded; Mode={0}; Days={1}; PageViews={2}; Events={3}; ElapsedMs={4}.",
                result.Mode, countsByDay.Count,
                counts.Where(c => c.Kind == AppInsightsTelemetryKind.PageViews).Sum(c => c.Count),
                counts.Where(c => c.Kind == AppInsightsTelemetryKind.Events).Sum(c => c.Count),
                sw.ElapsedMilliseconds));

            var position = 0;
            foreach (var day in countsByDay)
            {
                position++;
                var dayResult = await ImportDayAsync(source, day.Key, day.ToList(), range, saveRestResponses, filterUrls, persistence,
                    result.Mode, position, countsByDay.Count, cancellationToken);
                result.Days.Add(dayResult);
                AppInsightsImportTelemetry.TrackDay(_logger, dayResult, result.Mode);

                if (dayResult.Outcome == AppInsightsDayOutcome.Failed)
                {
                    result.Fail("DayFailed");
                    return false;
                }
                if (dayResult.Outcome == AppInsightsDayOutcome.RetriesStopped)
                {
                    result.Degrade("DayRetriesStopped");
                }
            }

            _logger.LogInformation($"Import loop finished: {result.Days.Count} days processed, {result.PageViews:n0} total page-views, {result.Events:n0} total events in {result.Windows} windows");
            return true;
        }

        /// <summary>
        /// One day: every page-view window, then every event window, each fetched and saved before the next is fetched.
        /// </summary>
        private async Task<AppInsightsDayResult> ImportDayAsync(IAppInsightsSourceLoader source, DateTime dayUtc,
            IReadOnlyList<AppInsightsHourlyRowCount> dayCounts, AppInsightsReadWindow range, bool saveRestResponses,
            List<FilterUrlConfig> filterUrls, IAppInsightsDayPersistenceManager persistence, AppInsightsReadMode mode, int position, int dayCount, CancellationToken cancellationToken)
        {
            var day = new AppInsightsDayResult(dayUtc);
            var dayTimer = Stopwatch.StartNew();
            var pageViewWindows = PlanWindows(dayUtc, dayCounts, AppInsightsTelemetryKind.PageViews, range);
            var eventWindows = PlanWindows(dayUtc, dayCounts, AppInsightsTelemetryKind.Events, range);
            _logger.LogInformation($"Importing day {position}/{dayCount}: {dayUtc:yyyy-MM-dd} in {pageViewWindows.Count} page-view and {eventWindows.Count} event window(s)...");

            // Newest page view READ for this day, before anything is filtered out: how far this day got.
            DateTime? newestRead = null;
            var removed = new AppInsightsScopeFilterResult();
            var stage = StageDownload;
            var cancellable = source as ICancellableAppInsightsSourceLoader;
            try
            {
                foreach (var window in pageViewWindows)
                {
                    stage = StageDownload;
                    var fetch = Stopwatch.StartNew();
                    cancellationToken.ThrowIfCancellationRequested();
                    var pageViews = cancellable == null ? await source.GetPageViewsAsync(window, saveRestResponses)
                        : await cancellable.GetPageViewsAsync(window, saveRestResponses, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    day.FetchSeconds += fetch.Elapsed.TotalSeconds;
                    day.Windows++;
                    var read = pageViews.Rows.Count;
                    if (read > 0)
                    {
                        var newest = pageViews.Rows.Max(v => v.Timestamp);
                        if (!newestRead.HasValue || newest > newestRead.Value) newestRead = newest;
                    }
                    Add(removed, AppInsightsUserScopeRules.Apply(pageViews, null, _userScope));

                    if (pageViews.Rows.Count > 0)
                    {
                        stage = StageSave;
                        var save = Stopwatch.StartNew();
                        await persistence.SavePageViewsAsync(pageViews, filterUrls);
                        day.SaveSeconds += save.Elapsed.TotalSeconds;
                        day.PageViews += pageViews.Rows.Count;
                    }
                    _logger.LogDebug($"AppInsightsWindow: Kind=PageViews; Window={window}; Read={read}; Saved={pageViews.Rows.Count}; FetchMs={fetch.ElapsedMilliseconds}.");
                }

                foreach (var window in eventWindows)
                {
                    stage = StageDownload;
                    var fetch = Stopwatch.StartNew();
                    cancellationToken.ThrowIfCancellationRequested();
                    var events = cancellable == null ? await source.GetCustomEventsAsync(window, saveRestResponses)
                        : await cancellable.GetCustomEventsAsync(window, saveRestResponses, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    day.FetchSeconds += fetch.Elapsed.TotalSeconds;
                    day.Windows++;
                    var read = events.Rows.Count;
                    Add(removed, AppInsightsUserScopeRules.Apply(null, events, _userScope));

                    int accepted = 0;
                    if (events.Rows.Count > 0)
                    {
                        stage = StageSave;
                        var save = Stopwatch.StartNew();
                        var saved = await persistence.SaveCustomEventsAsync(events);
                        day.SaveSeconds += save.Elapsed.TotalSeconds;
                        day.RecordSearches(saved.Searches);
                        accepted = events.Rows.Count - saved.Searches.Unmatched - saved.Searches.Skipped;
                        day.Events += accepted;
                    }
                    _logger.LogDebug($"AppInsightsWindow: Kind=Events; Window={window}; Read={read}; Saved={accepted}; FetchMs={fetch.ElapsedMilliseconds}.");
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException) && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                // Every exception counts, not just SqlException or HttpRequestException: one that escaped used to stall
                // the importer on the same day for months, and a download failure used to stop the cycle with no limit
                // at all. The saves are idempotent, so retrying the windows that did save costs time, not duplicates.
                var failures = _dayFailures.RecordFailure(dayUtc);
                day.ConsecutiveFailures = failures;
                day.FailedStage = stage;
                day.Exception = ex;
                if (ex is CustomEventSaveIncompleteException incomplete)
                {
                    day.FailedSections = incomplete.FailedSections;
                    day.RecordSearches(incomplete.Searches);
                }
                var what = stage == StageDownload ? "downloading" : "saving";
                string Detail(AppInsightsDayOutcome outcome) =>
                    string.Format(CultureInfo.InvariantCulture,
                        "AppInsightsDay: Day={0:yyyy-MM-dd}; Outcome={1}; Stage={2}; Attempt={3}/{4}; Windows={5}; PageViews={6}; Events={7}; ElapsedMs={8}",
                        dayUtc, outcome, stage, failures, _dayFailures.MaxAttempts, day.Windows, day.PageViews, day.Events, dayTimer.ElapsedMilliseconds)
                    + "; " + ExceptionDescriber.Chain(ex);

                if (!_dayFailures.ShouldGiveUp(failures))
                {
                    day.Outcome = AppInsightsDayOutcome.Failed;
                    _logger.LogError($"Failed {what} data for day {dayUtc:yyyy-MM-dd} (attempt {failures} of {_dayFailures.MaxAttempts}). " +
                        "Stopping this run here; the day will be retried next cycle, before any later day. " + Detail(day.Outcome), ex);
                    if (Debugger.IsAttached)
                    {
                        throw;
                    }
                    return day;
                }

                day.Outcome = AppInsightsDayOutcome.RetriesStopped;
                if (_dayFailures.StopsRetries(failures))
                {
                    _logger.LogError($"Failed {what} data for day {dayUtc:yyyy-MM-dd} again ({failures} failed attempts in a row). Stopped forced retries of this day " +
                        "and continuing with later days. What this cycle read for the day and did not save may not be read again automatically; " +
                        "ReadHitsDaysBeforeToday re-reads it. " + Detail(day.Outcome), ex);
                    AppInsightsImportTelemetry.TrackDayRetriesStopped(_logger, day);
                }
                else
                {
                    _logger.LogError($"Failed {what} data for day {dayUtc:yyyy-MM-dd} again ({failures} failed attempts in a row; its forced retries had already stopped). " +
                        "Continuing with later days. " + Detail(day.Outcome), ex);
                }
                if (Debugger.IsAttached)
                {
                    throw;
                }
                return day;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (removed.Total > 0)
            {
                _logger.LogInformation($"Left out data by people outside UserGroupsFilter for {dayUtc:yyyy-MM-dd}: {removed.PageViewsRemoved:n0} page-view(s), " +
                    $"{removed.EventsRemoved:n0} event(s), {removed.CommentsRemoved:n0} page comment(s) and {removed.LikesRemoved:n0} like(s).");
            }

            day.Outcome = day.UnmatchedSearches > 0 ? AppInsightsDayOutcome.UnmatchedSearches :
                day.PageViews + day.Events > 0 ? AppInsightsDayOutcome.Saved : AppInsightsDayOutcome.NoData;
            _logger.LogInformation(string.Format(CultureInfo.InvariantCulture,
                "AppInsightsDay: Day={0:yyyy-MM-dd}; Outcome={1}; Mode={2}; Windows={3}; PageViews={4}; Events={5}; FetchMs={6:0}; SaveMs={7:0}; ElapsedMs={8}.",
                dayUtc, day.Outcome, mode, day.Windows, day.PageViews, day.Events, day.FetchSeconds * 1000, day.SaveSeconds * 1000, dayTimer.ElapsedMilliseconds));

            // The day is fully processed - saved, or nothing left to save - so its earlier failures no longer count.
            _dayFailures.RecordSuccess(dayUtc);
            if (_userScope.IsFiltered)
            {
                _scanWatermark?.Advance(newestRead);
            }
            return day;
        }

        private async Task ReconcileSearchHoursAsync(IAppInsightsSourceLoader source, IAppInsightsDayPersistenceManager persistence,
            AppInsightsSearchRetries retries, DateTime nowUtc, AppInsightsImportCycleResult result)
        {
            foreach (var hour in retries.Due(nowUtc))
            {
                var expired = nowUtc >= hour.ExpiresUtc;
                try
                {
                    var range = AppInsightsReadWindow.SearchRetryHour(hour.HourUtc, nowUtc);
                    var counts = await source.GetHourlyRowCountsAsync(range);
                    var missing = 0;
                    var filtered = 0;
                    var read = 0;
                    var windows = counts.GroupBy(c => c.HourUtc.Date).OrderBy(d => d.Key)
                        .SelectMany(d => PlanWindows(d.Key, d.ToList(), AppInsightsTelemetryKind.Events, range));
                    foreach (var window in windows)
                    {
                        var events = await source.GetCustomEventsAsync(window, false);
                        read += events.Rows.Count;
                        filtered += AppInsightsUserScopeRules.Apply(null, events, _userScope).EventsRemoved;
                        if (events.Rows.Count == 0) continue;
                        var saved = await persistence.SaveSearchesAsync(events);
                        result.ReconciledSearches += saved.FactsInserted;
                        missing += saved.Unmatched;
                    }
                    // An empty source result cannot prove an earlier, known unmatched search was saved or
                    // filtered. It may have disappeared from Application Insights before this attempt.
                    retries.Finished(hour, missing > 0 || read == 0, nowUtc);
                    _logger.LogInformation($"AppInsightsSearchRetry: Hour={hour.HourUtc:O}; Unmatched={missing}; Filtered={filtered}; Final={expired}.");
                    if (read == 0)
                    {
                        result.Degrade(expired ? "SearchRetriesStopped" : "SearchRetrySourceEmpty");
                        _logger.LogWarning($"AppInsightsSearchRetry: no searches returned for pending ingestion hour {hour.HourUtc:O}; Final={expired}.");
                    }
                    else if (missing > 0 && expired)
                    {
                        result.Degrade("SearchRetriesStopped");
                        _logger.LogWarning($"AppInsightsSearchRetry: {missing} search event(s) still have no eligible visit for ingestion hour {hour.HourUtc:O}. " +
                            "Automatic retries stopped. ReadHitsDaysBeforeToday can reread the day while Application Insights retains it.");
                    }
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    retries.Finished(hour, true, nowUtc);
                    result.Degrade(expired ? "SearchRetriesStopped" : "SearchRetryFailed");
                    _logger.LogWarning($"AppInsightsSearchRetry: failed to reconcile ingestion hour {hour.HourUtc:O}; Final={expired}. " +
                        $"The normal import continues; ReadHitsDaysBeforeToday can reread the day. {SearchFailure(ex)}");
                }
            }
        }

        // Exception messages can quote a rejected value or response. Only classify search retries.
        private static string SearchFailure(Exception ex) =>
            string.Join("; ", ExceptionDescriber.Dimensions(ex).Select(d => $"{d.Key}={d.Value}"));

        /// <summary>The planned windows of one kind for a day, within the range's timestamp bounds.</summary>
        private List<AppInsightsReadWindow> PlanWindows(DateTime dayUtc, IReadOnlyList<AppInsightsHourlyRowCount> dayCounts,
            AppInsightsTelemetryKind kind, AppInsightsReadWindow range)
        {
            return AppInsightsWindowPlanner.PlanDay(dayUtc, dayCounts.Where(c => c.Kind == kind), _maxRowsPerWindow)
                .Select(w => (From: w.FromUtc < range.FromUtc ? range.FromUtc : w.FromUtc, To: w.ToUtc > range.ToUtc ? range.ToUtc : w.ToUtc))
                .Where(w => w.To > w.From)
                .Select(w => range.WithTimestamps(w.From, w.To))
                .ToList();
        }

        private static void Add(AppInsightsScopeFilterResult total, AppInsightsScopeFilterResult window)
        {
            total.PageViewsRemoved += window.PageViewsRemoved;
            total.EventsRemoved += window.EventsRemoved;
            total.CommentsRemoved += window.CommentsRemoved;
            total.LikesRemoved += window.LikesRemoved;
        }
    }
}
