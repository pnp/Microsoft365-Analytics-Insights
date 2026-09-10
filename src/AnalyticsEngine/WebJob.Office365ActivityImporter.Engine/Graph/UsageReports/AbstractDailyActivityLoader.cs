using Common.Entities;
using Common.Entities.ActivityReports;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Rules;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    /// <summary>
    /// Generic Graph report loader. Recursively loads and saves any Graph activity report.
    /// </summary>
    /// <typeparam name="TReportDbType">Type of EF table</typeparam>
    /// <typeparam name="TUserActivityUserDetail">Type of report page</typeparam>
    public abstract class AbstractDailyActivityLoader<TReportDbType, TUserActivityUserDetail, TLookupType, CACHETYPE> : ActivityReportLoader
        where TReportDbType : AbstractUsageActivityLog, new()
        where TUserActivityUserDetail : AbstractActivityRecord<TLookupType>
        where TLookupType : AbstractEFEntity
        where CACHETYPE : DBLookupCache<TLookupType>
    {
        protected readonly ManualGraphCallClient _client;
        internal AbstractDailyActivityLoader(ManualGraphCallClient client, ILogger logger) : base(logger)
        {
            _client = client;
            SaveInstrumentation = AnalyticsUsageReportSaveInstrumentation.ForLogger(logger);
        }

        public abstract DbSet<TReportDbType> GetTable(AnalyticsEntitiesContext context);

        public Dictionary<DateTime, List<TUserActivityUserDetail>> LoadedReportPages { get; set; } = new Dictionary<DateTime, List<TUserActivityUserDetail>>();

        /// <summary>
        /// How many usage-log rows to persist per EF SaveChanges (and per existence query). Kept small enough
        /// that EF6 never builds millions of insert/update command trees in a single call (which
        /// OutOfMemoryExceptions at large-tenant scale) and that the per-batch IN clause stays well under SQL
        /// Server's parameter limit. Settable so tests can exercise the batch boundary cheaply.
        /// </summary>
        public int SaveBatchSize { get; set; } = 1000;

        /// <summary>
        /// Recent-day window (in days) during which Graph usage data can still change and therefore must be
        /// re-imported every run. Graph usage reports have a ~2-3 day latency and are stable once finalized, so
        /// dates older than this can be treated as final. A date is skipped only when it was already inside the
        /// window of a previously completed import phase; rows newer than that completion marker may be from an
        /// interrupted save and are retried. Settable so tests can exercise the boundary.
        /// </summary>
        public int RefreshableRecentDays { get; set; } = 3;

        /// <summary>
        /// Number of rows actually inserted/updated in SQL by the last <see cref="SaveLoadedReportsToSql"/> call.
        /// Unchanged rows are dirty-checked and skipped, so this is 0 when nothing changed. Exposed for tests and
        /// diagnostics.
        /// </summary>
        public int LastSaveDbWriteCount { get; private set; }

        private static int ClampDaysBack(int daysBackMax) => UsageReportRefreshPolicy.ClampDaysBack(daysBackMax);

        /// <summary>
        /// Storage-shape questions and columnstore maintenance. Injectable so the index-aware branch and the
        /// maintenance trigger can be exercised without SQL Server (#375); defaults to the SQL adapter over
        /// whichever context the caller passes in.
        /// </summary>
        public IUsageReportStorageInspector StorageInspector { get; set; }

        private IUsageReportStorageInspector InspectorFor(AnalyticsEntitiesContext db)
            => StorageInspector ?? new SqlUsageReportStorageInspector(db);

        /// <summary>
        /// Reads and writes for this report's table. Injectable so the whole finalized-date scan and the
        /// entire save loop - the upsert, the scope/lookup filters, the dirty check, the batch boundary -
        /// can be exercised without SQL Server (#375); defaults to the EF adapter over whichever context
        /// the caller passes in.
        /// </summary>
        public IUsageReportStore<TReportDbType> ReportStore { get; set; }

        // Note the `??`: when a store is injected, the right-hand side is never evaluated, so GetTable(db)
        // is not called and a null context is never dereferenced.
        private IUsageReportStore<TReportDbType> StoreFor(AnalyticsEntitiesContext db)
            => ReportStore ?? new SqlUsageReportStore<TReportDbType>(db, GetTable(db));

        /// <summary>
        /// Source of "now" for the import window and the finalized-date scan. Defaults to
        /// <see cref="SystemClock"/>, i.e. <c>DateTime.UtcNow</c>, so behaviour is unchanged; injectable
        /// (#368/#375) so a test asserting exact window bounds cannot disagree with the loader's own clock
        /// read when the two straddle UTC midnight.
        /// </summary>
        public IClock Clock { get; set; } = SystemClock.Instance;

        /// <summary>
        /// Opt-in, privacy-safe stage diagnostics for the daily usage-report save path. The default is a
        /// no-op unless <c>AI_USAGE_REPORT_SAVE_DIAGNOSTICS</c> is enabled and the logger can emit App
        /// Insights events, so the hot path keeps the pre-existing cost when diagnostics are disabled.
        /// </summary>
        public IUsageReportSaveInstrumentation SaveInstrumentation { get; set; }

        /// <summary>
        /// The set of dates within the [now-daysBackMax, now) import window that are already stored in SQL, old
        /// enough that Graph will no longer change them, and covered by a previously completed import phase. These
        /// can be skipped entirely on the next import - no re-download, no re-write. Dates within the recent window
        /// or newer than the completion marker are never returned because they can still change or be partial.
        ///
        /// The window rule itself is <see cref="UsageReportRefreshPolicy"/>, which takes the instant as a
        /// parameter and so can be asserted without a database (#375).
        /// </summary>
        public async Task<HashSet<DateTime>> GetFinalizedStoredDatesToSkipAsync(
            AnalyticsEntitiesContext db,
            int daysBackMax,
            DateTime? lastSuccessfulImport)
        {
            var window = UsageReportRefreshPolicy.ResolveSkipWindow(
                daysBackMax, lastSuccessfulImport, RefreshableRecentDays, Clock.UtcNow);

            if (!window.CanSkipAnyDate)
            {
                // Existing rows could be from an interrupted import. Until a full usage-report
                // phase completes, no stored date is proven complete enough to skip safely.
                return new HashSet<DateTime>();
            }

            var store = StoreFor(db);
            if (await HasLeadingDateIndexAsync(db))
            {
                // The window contains at most 25 finalized dates. DISTINCT over an indexed range
                // still scans every user's row for every date (millions of rows at 200k users);
                // bounded existence seeks touch one index entry per candidate date instead.
                var storedFinalizedDates = new HashSet<DateTime>();
                foreach (var date in UsageReportRefreshPolicy.EnumerateSkipCandidates(window))
                {
                    if (await store.HasAnyRowForDateAsync(date))
                    {
                        storedFinalizedDates.Add(date);
                    }
                }

                return storedFinalizedDates;
            }

            // Some account/group report tables have no date-leading index. Repeated existence
            // probes would each scan the table, so use one range scan until an index is available.
            var scannedDates = await store.GetStoredDatesInRangeAsync(window.WindowStartUtc, window.SafeCutoffUtc);

            return new HashSet<DateTime>(scannedDates.Select(date => date.Date));
        }

        internal Task<bool> HasLeadingDateIndexAsync(AnalyticsEntitiesContext db)
            => InspectorFor(db).HasLeadingDateIndexAsync(UsageReportTableName.Resolve(typeof(TReportDbType)));

        /// <summary>
        /// Compacts this report table's columnstore delta rowgroups, if it has a columnstore index. Skipped
        /// entirely for an entity that declares no table. See <see cref="SqlUsageReportStorageInspector"/>
        /// for why plain REORGANIZE, and why it runs outside a transaction.
        /// </summary>
        internal Task CompactColumnstoreAsync(AnalyticsEntitiesContext db)
        {
            var qualifiedTableName = UsageReportTableName.TryResolve(typeof(TReportDbType));
            if (qualifiedTableName == null)
            {
                return Task.CompletedTask;
            }

            return InspectorFor(db).CompactColumnstoreAsync(qualifiedTableName);
        }

        public async Task PopulateLoadedReportPagesFromGraph(int daysBackMax, ISet<DateTime> datesToSkip = null)
        {
            daysBackMax = ClampDaysBack(daysBackMax);

            LoadedReportPages.Clear();

            for (int daysBackIdx = 0; daysBackIdx < daysBackMax; daysBackIdx++)
            {
                // Go back one extra day always. Otherwise we risk asking for data too soon...
                // Example: Message: {"error":{"code":"InvalidArgument","message":"Invalid date value specified: $DateTime.Now. Only support data for the past 28 days."}}
                var daysBack = (daysBackIdx + 1) * -1;
                // Graph Usage Reports API operates in UTC; DateTime.Now on a non-UTC server
                // produces the wrong date bucket near midnight. Read per iteration, as before.
                var dt = Clock.UtcNow.AddDays(daysBack);

                // Finalized days we already hold don't change in Graph - skip the (often slow) paged download entirely.
                if (datesToSkip != null && datesToSkip.Contains(dt.Date))
                {
                    Telemetry.LogInformation($"Skipping {this.GetType().Name} for date {dt.ToString("dd-MM-yyyy")} - already stored and finalized (no longer changes in Graph).");
                    continue;
                }

                Telemetry.LogInformation($"Loading {this.GetType().Name} for date {dt.ToString("dd-MM-yyyy")}");

                var dayReports = await LoadReportPageForDateFromGraph(dt);

                if (LoadedReportPages.ContainsKey(dt))
                {
                    Telemetry.LogWarning($"Duplicate date {dt.ToString("dd-MM-yyyy")}");
                }
                else
                {
                    Telemetry.LogInformation($"Finished loading {this.GetType().Name} for date {dt.ToString("dd-MM-yyyy")}");
                    LoadedReportPages.Add(dt, dayReports);
                }
            }
        }

        /// <summary>
        /// Fetch one day's report from Graph (all pages, with throttle retries). Virtual so tests can supply canned
        /// data with no HTTP, and so the date-skipping in <see cref="PopulateLoadedReportPagesFromGraph"/> can be
        /// verified without hitting Graph.
        /// </summary>
        /// <remarks>
        /// Uses STRICT paging. These reports have exactly the problem issue #285 described for the Copilot
        /// reports: a 403 from a missing <c>Reports.Read.All</c> grant, or a 5xx mid-paging, used to return
        /// the pages gathered so far without throwing - so a failed day was recorded as a successfully
        /// loaded EMPTY day, and the whole activity-report phase then stamped itself as complete for 24
        /// hours. A broken import looked healthy and idle.
        ///
        /// Throwing restores the contract the phase is already written around: see
        /// <c>GraphImporter.GetAndSaveActivityReportsMultiThreaded</c>, which deletes the "last imported"
        /// timestamp BEFORE loading and only re-saves it after every loader has completed, precisely so
        /// that "if this phase fails after a partial save, the next cycle must re-import". The exception
        /// propagates out of the <c>Task.WhenAll</c>, the timestamp is never re-saved, and the phase is
        /// retried next cycle with the real HTTP status recorded in Application Insights.
        ///
        /// A genuinely empty day (Graph returns 200 with no rows) is unaffected and still loads as empty.
        /// </remarks>
        protected virtual Task<List<TUserActivityUserDetail>> LoadReportPageForDateFromGraph(DateTime date)
        {
            var requestUrl = $"{ReportGraphURL}(date={date.ToString("yyyy-MM-dd")})?$format=application/json";
            return _client.LoadAllPagesWithThrottleRetries<TUserActivityUserDetail>(requestUrl, Telemetry, throwOnHttpError: true);
        }

        /// <summary>
        /// Save to SQL. Needs a shared ConcurrentLookupDbIdsCache if running in parallel with other imports.
        /// </summary>
        public async Task SaveLoadedReportsToSql(ConcurrentLookupDbIdsCache userEmailToDbIdCache, CACHETYPE lookupCache)
        {
            int i = 0; int dbWrites = 0; var enUS = new System.Globalization.CultureInfo("en-US");
            var db = lookupCache.DB;
            var instrumentation = SaveInstrumentation ?? NullUsageReportSaveInstrumentation.Instance;
            var instrumentationEnabled = instrumentation.IsEnabled;
            var loaderType = this.GetType().Name;
            var reportTable = instrumentationEnabled ? (UsageReportTableName.TryResolve(typeof(TReportDbType)) ?? typeof(TReportDbType).Name) : null;
            var saveWatch = instrumentationEnabled ? Stopwatch.StartNew() : null;
            var totals = instrumentationEnabled ? new SaveLoopMetrics() : null;

            Telemetry.LogInformation($"Saving {loaderType} for {LoadedReportPages.Keys.Count} dates");

            // Compute total once. The previous "LoadedReportPages.SelectMany(r => r.Value).Count()"
            // call ran on every 1000-row progress print, making progress O(n^2).
            var totalReports = LoadedReportPages.Sum(kv => kv.Value.Count);
            if (instrumentationEnabled)
            {
                TrackSaveStage(instrumentation, UsageReportSaveStageIds.SaveStarted, loaderType, reportTable, "Started", m =>
                {
                    m["DateCount"] = LoadedReportPages.Keys.Count;
                    m["InputRowCount"] = totalReports;
                    m["SaveBatchSize"] = SaveBatchSize;
                    UsageReportSaveInstrumentationRuntime.AddRuntimeMetrics(m);
                });
            }

            // Persist one day at a time, committing in fixed-size batches. Previously every row across every date
            // was added to a single context and committed in ONE SaveChangesAsync, and every existing row across
            // the whole date range was pre-loaded and tracked. At ~200k users x up to 28 days EF6 builds an
            // insert/update command tree for every pending row at once and throws OutOfMemoryException on a small
            // App Service. Reading only one day at a time and flushing in batches keeps the command-tree build
            // bounded; auto change-detection is turned off so adding a day's rows stays O(n) instead of O(n^2).
            // AssociatedLookupId is [NotMapped] (it maps to UserID / YammerGroupID per subclass), so existing
            // rows can only be filtered in SQL by the mapped Date column - we key them by lookup id in memory.
            var store = StoreFor(db);
            try
            {
                using (store.BeginBulkWrite())
                {
                    var dateIndex = 0;
                    foreach (var dateTime in LoadedReportPages.Keys)
                    {
                        var existingLoadWatch = instrumentationEnabled ? Stopwatch.StartNew() : null;
                        var existingRows = await store.GetRowsForDateAsync(dateTime.Date);
                        existingLoadWatch?.Stop();

                        var existingByLookupId = new Dictionary<int, TReportDbType>();
                        // Graph returns one row per lookup per date; last wins if the DB somehow has duplicates.
                        foreach (var existingRow in existingRows)
                        {
                            existingByLookupId[existingRow.AssociatedLookupId] = existingRow;
                        }

                        if (instrumentationEnabled)
                        {
                            totals.ExistingRowLoadMs += existingLoadWatch.ElapsedMilliseconds;
                            TrackSaveStage(instrumentation, UsageReportSaveStageIds.ExistingRowsLoaded, loaderType, reportTable, "Completed", m =>
                            {
                                m["DateIndex"] = dateIndex;
                                m["DurationMs"] = existingLoadWatch.ElapsedMilliseconds;
                                m["ExistingRowCount"] = existingRows.Count;
                                m["MaterializedLookupCount"] = existingByLookupId.Count;
                                m["TrackedEntityCount"] = store.TrackedEntityCount;
                            });
                        }

                        var pendingChanges = 0;
                        var pendingBatchRows = 0;
                        var dateMetrics = instrumentationEnabled ? new SaveLoopMetrics() : null;
                        foreach (var reportPage in LoadedReportPages[dateTime])
                        {
                            dateMetrics?.RecordInputRow();
                            totals?.RecordInputRow();

                            // A usage-report row with no user/group identifier (e.g. a Graph row with a null
                            // userPrincipalName when report anonymisation is enabled on the tenant) can't be matched
                            // to a DB lookup. Skip it rather than NRE / ArgumentNullException deeper in the loop.
                            if (string.IsNullOrWhiteSpace(reportPage.LookupFieldValue))
                            {
                                dateMetrics?.RecordMissingLookupValue();
                                totals?.RecordMissingLookupValue();
                                Telemetry.LogWarning($"Skipping a {typeof(TReportDbType).Name} report row with no lookup identifier (null/empty user or group name).");
                                continue;
                            }

                            // Usually an Entra ID group-membership check for a group filter.
                            var scopeTicks = instrumentationEnabled ? Stopwatch.GetTimestamp() : 0;
                            var inScope = await IdInScope(reportPage.LookupFieldValue);
                            if (instrumentationEnabled)
                            {
                                var elapsed = ElapsedMillisecondsSince(scopeTicks);
                                dateMetrics.ScopeFilterMs += elapsed;
                                totals.ScopeFilterMs += elapsed;
                            }
                            if (!inScope)
                            {
                                dateMetrics?.RecordOutOfScope();
                                totals?.RecordOutOfScope();
                                Telemetry.LogInformation($"Skipping {reportPage.LookupFieldValue} as not in scope");
                                continue;
                            }

                            var lookupStats = instrumentationEnabled ? new LookupResolutionStats() : null;
                            var lookupTicks = instrumentationEnabled ? Stopwatch.GetTimestamp() : 0;
                            var lookupId = await ResolveLookupIdAsync(reportPage, userEmailToDbIdCache, lookupCache, lookupStats);
                            if (instrumentationEnabled)
                            {
                                var elapsed = ElapsedMillisecondsSince(lookupTicks);
                                dateMetrics.LookupResolveMs += elapsed;
                                totals.LookupResolveMs += elapsed;
                                dateMetrics.Add(lookupStats);
                                totals.Add(lookupStats);
                            }

                            if (i > 0 && i % 1000 == 0)
                            {
                                Console.WriteLine($"{loaderType}: Saved {i} / {totalReports}");
                            }

                            var isNewLog = !existingByLookupId.TryGetValue(lookupId, out var dateRequestedLog);
                            if (isNewLog)
                            {
                                dateRequestedLog = new TReportDbType() { AssociatedLookupId = lookupId };
                                existingByLookupId[lookupId] = dateRequestedLog;
                            }

                            dateRequestedLog.Date = dateTime.Date;

                            var parseTicks = instrumentationEnabled ? Stopwatch.GetTimestamp() : 0;
                            var activityDate = DateTime.MinValue;
                            if (!string.IsNullOrEmpty(reportPage.LastActivityDateString))
                            {
                                if (DateTime.TryParseExact(reportPage.LastActivityDateString, "yyyy-MM-dd", enUS, System.Globalization.DateTimeStyles.None, out activityDate))
                                {
                                    dateRequestedLog.LastActivityDate = activityDate;
                                }
                                else
                                {
                                    Telemetry.LogInformation($"Invalid LastActivity value: '{reportPage.LastActivityDateString}'");
                                    dateRequestedLog.LastActivityDate = null;
                                }
                            }
                            if (instrumentationEnabled)
                            {
                                var elapsed = ElapsedMillisecondsSince(parseTicks);
                                dateMetrics.DateParseMs += elapsed;
                                totals.DateParseMs += elapsed;
                            }

                            var projectionTicks = instrumentationEnabled ? Stopwatch.GetTimestamp() : 0;
                            PopulateReportSpecificMetadata(dateRequestedLog, reportPage);
                            if (instrumentationEnabled)
                            {
                                var elapsed = ElapsedMillisecondsSince(projectionTicks);
                                dateMetrics.ProjectionMs += elapsed;
                                totals.ProjectionMs += elapsed;
                            }

                            // Auto-detect is off, so state the change explicitly. Only write when something actually
                            // changed: existing rows for finalized days re-fetched by the recent-window rule are almost
                            // always identical to what's stored, so dirty-checking skips the vast majority of UPDATEs -
                            // the dominant cost of this import at large-tenant scale.
                            bool willWrite;
                            var dirtyTicks = instrumentationEnabled ? Stopwatch.GetTimestamp() : 0;
                            if (isNewLog)
                            {
                                store.AddRow(dateRequestedLog);
                                willWrite = true;
                                dateMetrics?.RecordAdded();
                                totals?.RecordAdded();
                            }
                            else
                            {
                                willWrite = store.MarkUpdatedIfChanged(dateRequestedLog);
                                if (willWrite)
                                {
                                    dateMetrics?.RecordChanged();
                                    totals?.RecordChanged();
                                }
                                else
                                {
                                    dateMetrics?.RecordUnchanged();
                                    totals?.RecordUnchanged();
                                }
                            }
                            if (instrumentationEnabled)
                            {
                                var elapsed = ElapsedMillisecondsSince(dirtyTicks);
                                dateMetrics.DirtyCheckMs += elapsed;
                                totals.DirtyCheckMs += elapsed;
                            }

                            i++;
                            if (willWrite)
                            {
                                dbWrites++;
                                pendingChanges++;
                                pendingBatchRows++;
                                if (pendingChanges >= SaveBatchSize)
                                {
                                    await SavePendingBatchAsync(store, instrumentation, loaderType, reportTable, pendingBatchRows);
                                    pendingChanges = 0;
                                    pendingBatchRows = 0;
                                }
                            }
                        }

                        if (pendingChanges > 0)
                        {
                            await SavePendingBatchAsync(store, instrumentation, loaderType, reportTable, pendingBatchRows);
                        }

                        if (instrumentationEnabled)
                        {
                            TrackSaveStage(instrumentation, UsageReportSaveStageIds.RowsProcessed, loaderType, reportTable, "Completed", m => dateMetrics.WriteTo(m, dateIndex));
                        }

                        var releaseTicks = instrumentationEnabled ? Stopwatch.GetTimestamp() : 0;
                        var trackedBeforeRelease = instrumentationEnabled ? store.TrackedEntityCount : 0;
                        store.ReleaseSavedRows();
                        if (instrumentationEnabled)
                        {
                            TrackSaveStage(instrumentation, UsageReportSaveStageIds.RowsReleased, loaderType, reportTable, "Completed", m =>
                            {
                                m["DateIndex"] = dateIndex;
                                m["DurationMs"] = ElapsedMillisecondsSince(releaseTicks);
                                m["TrackedEntityCountBeforeRelease"] = trackedBeforeRelease;
                                m["TrackedEntityCountAfterRelease"] = store.TrackedEntityCount;
                            });
                        }
                        dateIndex++;
                    }
                }

                LastSaveDbWriteCount = dbWrites;
                if (instrumentationEnabled)
                {
                    saveWatch.Stop();
                    TrackSaveStage(instrumentation, UsageReportSaveStageIds.SaveCompleted, loaderType, reportTable, "Completed", m =>
                    {
                        totals.WriteTo(m, null);
                        m["DurationMs"] = saveWatch.ElapsedMilliseconds;
                        m["InputRowCount"] = totalReports;
                        m["DbWriteCount"] = dbWrites;
                        UsageReportSaveInstrumentationRuntime.AddRuntimeMetrics(m);
                    });
                }
            }
            catch (Exception ex)
            {
                if (instrumentationEnabled)
                {
                    saveWatch.Stop();
                    TrackSaveStage(instrumentation, UsageReportSaveStageIds.SaveFailed, loaderType, reportTable, "Failed", m =>
                    {
                        m["DurationMs"] = saveWatch.ElapsedMilliseconds;
                        m["InputRowCount"] = totalReports;
                        m["DbWriteCount"] = dbWrites;
                        UsageReportSaveInstrumentationRuntime.AddRuntimeMetrics(m);
                    }, ex.GetType().Name);
                }
                throw;
            }
        }

        private async Task SavePendingBatchAsync(
            IUsageReportStore<TReportDbType> store,
            IUsageReportSaveInstrumentation instrumentation,
            string loaderType,
            string reportTable,
            int batchRows)
        {
            if (instrumentation == null || !instrumentation.IsEnabled)
            {
                await store.SaveChangesAsync();
                return;
            }

            var trackedBeforeSave = store.TrackedEntityCount;
            var saveWatch = Stopwatch.StartNew();
            await store.SaveChangesAsync();
            saveWatch.Stop();
            TrackSaveStage(instrumentation, UsageReportSaveStageIds.SaveBatch, loaderType, reportTable, "Completed", m =>
            {
                m["DurationMs"] = saveWatch.ElapsedMilliseconds;
                m["BatchRowCount"] = batchRows;
                m["TrackedEntityCountBeforeSave"] = trackedBeforeSave;
                m["TrackedEntityCountAfterSave"] = store.TrackedEntityCount;
            });
        }

        // Resolve the DB id for a report row's user/group lookup, using the shared cross-thread id cache and
        // only hitting the DB (via GetOrCreateLookup) on a cache miss. Resolution happens OUTSIDE the cache lock:
        // the previous .Result-inside-lock pattern held a shared mutex through a full DB round-trip, starving all
        // parallel import threads on the same cache.
        private async Task<int> ResolveLookupIdAsync(
            TUserActivityUserDetail reportPage,
            ConcurrentLookupDbIdsCache userEmailToDbIdCache,
            CACHETYPE lookupCache,
            LookupResolutionStats stats = null)
        {
            int? lookupId;
            var lockStart = stats == null ? 0 : Stopwatch.GetTimestamp();
            Monitor.Enter(userEmailToDbIdCache);
            try
            {
                stats?.AddSynchronizationWait(ElapsedMillisecondsSince(lockStart));
                lookupId = userEmailToDbIdCache.GetCachedIdForName<TReportDbType>(reportPage.LookupFieldValue);
            }
            finally
            {
                Monitor.Exit(userEmailToDbIdCache);
            }
            if (lookupId != null)
            {
                stats?.RecordHit();
                return lookupId.Value;
            }

            stats?.RecordMiss();
            var dbCallStart = stats == null ? 0 : Stopwatch.GetTimestamp();
            var lookup = await reportPage.GetOrCreateLookup(lookupCache);
            stats?.RecordDatabaseCall(ElapsedMillisecondsSince(dbCallStart));
            if (!lookup.IsSavedToDB)
            {
                throw new InvalidOperationException("Cannot use unsaved lookups for activity records");
            }

            lockStart = stats == null ? 0 : Stopwatch.GetTimestamp();
            Monitor.Enter(userEmailToDbIdCache);
            try
            {
                stats?.AddSynchronizationWait(ElapsedMillisecondsSince(lockStart));
                // Re-check in case another thread populated it while we were resolving.
                lookupId = userEmailToDbIdCache.GetCachedIdForName<TReportDbType>(reportPage.LookupFieldValue);
                if (lookupId == null)
                {
                    lookupId = lookup.ID;
                    userEmailToDbIdCache.AddOrUpdateForName<TReportDbType>(reportPage.LookupFieldValue, lookupId.Value);
                }
                else
                {
                    stats?.RecordDuplicateConcurrentMiss();
                }
            }
            finally
            {
                Monitor.Exit(userEmailToDbIdCache);
            }
            return lookupId.Value;
        }

        private static long ElapsedMillisecondsSince(long startTimestamp)
        {
            if (startTimestamp == 0) return 0;
            return (long)((Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency);
        }

        private static void TrackSaveStage(
            IUsageReportSaveInstrumentation instrumentation,
            string stage,
            string loaderType,
            string reportTable,
            string outcome,
            Action<Dictionary<string, double>> populateMetrics,
            string exceptionType = null)
        {
            var point = new UsageReportSaveTelemetryPoint
            {
                Stage = stage,
                ReportId = loaderType,
                LoaderType = loaderType,
                ReportTable = reportTable,
                Outcome = outcome,
                ExceptionType = exceptionType,
            };
            populateMetrics?.Invoke(point.Metrics);
            instrumentation.Track(point);
        }

        private sealed class LookupResolutionStats
        {
            public long CacheHits;
            public long CacheMisses;
            public long DatabaseCalls;
            public long DuplicateConcurrentMisses;
            public long SynchronizationWaitMs;
            public long DatabaseCallMs;

            public void RecordHit() => CacheHits++;
            public void RecordMiss() => CacheMisses++;
            public void RecordDuplicateConcurrentMiss() => DuplicateConcurrentMisses++;
            public void AddSynchronizationWait(long milliseconds) => SynchronizationWaitMs += milliseconds;
            public void RecordDatabaseCall(long milliseconds)
            {
                DatabaseCalls++;
                DatabaseCallMs += milliseconds;
            }
        }

        private sealed class SaveLoopMetrics
        {
            public long InputRows;
            public long MissingLookupValueRows;
            public long OutOfScopeRows;
            public long AddedRows;
            public long ChangedRows;
            public long UnchangedRows;
            public long ExistingRowLoadMs;
            public long ScopeFilterMs;
            public long LookupResolveMs;
            public long DateParseMs;
            public long ProjectionMs;
            public long DirtyCheckMs;
            public long LookupCacheHits;
            public long LookupCacheMisses;
            public long LookupDatabaseCalls;
            public long DuplicateConcurrentMisses;
            public long LookupSynchronizationWaitMs;
            public long LookupDatabaseCallMs;

            public void RecordInputRow() => InputRows++;
            public void RecordMissingLookupValue() => MissingLookupValueRows++;
            public void RecordOutOfScope() => OutOfScopeRows++;
            public void RecordAdded() => AddedRows++;
            public void RecordChanged() => ChangedRows++;
            public void RecordUnchanged() => UnchangedRows++;

            public void Add(LookupResolutionStats stats)
            {
                if (stats == null) return;
                LookupCacheHits += stats.CacheHits;
                LookupCacheMisses += stats.CacheMisses;
                LookupDatabaseCalls += stats.DatabaseCalls;
                DuplicateConcurrentMisses += stats.DuplicateConcurrentMisses;
                LookupSynchronizationWaitMs += stats.SynchronizationWaitMs;
                LookupDatabaseCallMs += stats.DatabaseCallMs;
            }

            public void WriteTo(Dictionary<string, double> metrics, int? dateIndex)
            {
                if (dateIndex.HasValue) metrics["DateIndex"] = dateIndex.Value;
                metrics["InputRowCount"] = InputRows;
                metrics["MissingLookupValueRowCount"] = MissingLookupValueRows;
                metrics["OutOfScopeRowCount"] = OutOfScopeRows;
                metrics["AddedRowCount"] = AddedRows;
                metrics["ChangedRowCount"] = ChangedRows;
                metrics["UnchangedRowCount"] = UnchangedRows;
                metrics["ExistingRowLoadMs"] = ExistingRowLoadMs;
                metrics["ScopeFilterMs"] = ScopeFilterMs;
                metrics["LookupResolveMs"] = LookupResolveMs;
                metrics["DateParseMs"] = DateParseMs;
                metrics["ProjectionMs"] = ProjectionMs;
                metrics["DirtyCheckMs"] = DirtyCheckMs;
                metrics["LookupCacheHitCount"] = LookupCacheHits;
                metrics["LookupCacheMissCount"] = LookupCacheMisses;
                metrics["LookupDatabaseCallCount"] = LookupDatabaseCalls;
                metrics["DuplicateConcurrentMissCount"] = DuplicateConcurrentMisses;
                metrics["LookupSynchronizationWaitMs"] = LookupSynchronizationWaitMs;
                metrics["LookupDatabaseCallMs"] = LookupDatabaseCallMs;
            }
        }

        protected virtual Task<bool> IdInScope(string lookupId)
        {
            // Default implementation assumes all IDs are in scope
            // Override this method to filter out IDs that should not be processed
            return Task.FromResult(true);
        }

        protected abstract long CountActivity(TUserActivityUserDetail activityPage);
        protected abstract void PopulateReportSpecificMetadata(TReportDbType newRecord, TUserActivityUserDetail activityPage);

        protected int GetOptionalInt(int? i)
        {
            if (i.HasValue)
            {
                return i.Value;
            }
            return 0;
        }
    }

}
