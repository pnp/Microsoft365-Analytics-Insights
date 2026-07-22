using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using DataUtils.Sql;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;
using WebJob.Office365ActivityImporter.Engine.Graph.User;
using WebJob.Office365ActivityImporter.Engine.Properties;

namespace WebJob.Office365ActivityImporter.Engine
{
    /// <summary>
    /// SQL adaptor for saving activity reports. 
    /// Saves to a staging table, merges everything with a SQL script, then processes workload specific metadata updates seperately.
    /// </summary>
    public class ActivityReportSqlPersistenceManager : IActivityReportPersistenceManager
    {
        private readonly AuditFilterConfig _filterConfig;
        private readonly UserGroupsCache _userGroupsCache;
        private readonly ILogger _logger;
        private readonly AppConfig _appConfig;
        private string _defaultConnectionString = null;
        private UserGroupsFilterModel _userGroupsFilter = null;

        /// <summary>
        /// Process-wide gate that serializes writes to the staging tables. Intentionally <c>static</c> so that
        /// multiple <see cref="ActivityReportSqlPersistenceManager"/> instances (e.g. one per content type or
        /// per parallel batch) cannot interleave SQL inserts/merges into the shared staging schema.
        /// Single-permit; held only for the duration of <c>CommitAll</c>'s SQL phase. Used only in the
        /// default (serial) mode; see <see cref="_maxConcurrentSaves"/>.
        /// </summary>
        private static SemaphoreSlim _sqlSaveSemaphore = new SemaphoreSlim(1);      // Make sure we're only saving one thread at a time

        // --- Concurrent-save mode (opt-in; _maxConcurrentSaves > 1) ------------------------------------
        // When enabled, each CommitAll uses its OWN sharded staging table so multiple saves can build +
        // load their staging table in parallel (bounded by _saveConcurrencyGate). The parts that write
        // SHARED tables - the merge (lookup + fact inserts) and the metadata pass (webs/sites, etc.) -
        // are still serialised across all saves by _sharedWriteSemaphore, so there is no shared-table
        // race. Default is 1, which preserves the original strictly-serial behaviour exactly (single
        // static _sqlSaveSemaphore, no sharding).
        private readonly int _maxConcurrentSaves;
        private readonly SemaphoreSlim _saveConcurrencyGate;
        private static readonly SemaphoreSlim _sharedWriteSemaphore = new SemaphoreSlim(1, 1);

        // --- Run-scoped dedup cache (perf: build ONCE per cycle, not per batch) -------------------------
        // The set of audit-event ids already imported/ignored within the download window. This manager is
        // created once per import cycle, so the cache is built ONCE (lazily, for the whole window) and kept
        // current in-memory as each batch saves - replacing the old behaviour of re-querying audit_events on
        // every CommitAll. A 2000-event batch's [Min,Max] CreationTime spans almost the entire window (events
        // download out-of-order across ~130 threads), so the per-batch query materialised ~the whole in-window
        // audit_events set on EVERY batch: the dominant cost, and a large memory spike, at scale. Correctness
        // is unchanged - the same ids are cached (full window, keyed by id) and the merge SQL's NOT EXISTS
        // guards remain the authoritative cross-instance/cross-cycle dedup backstop. ActivityImportCache is
        // internally thread-safe, so one instance is shared safely across concurrent saves.
        //   _usePerBatchDedupCache is an ops safety-valve (app setting AUDIT_PERBATCH_DEDUP_CACHE=true) that
        //   restores the old per-batch build without a redeploy; default false = new per-cycle behaviour.
        private readonly bool _usePerBatchDedupCache;
        private ActivityImportCache _runImportCache;
        private bool _runImportCacheBuilt;
        private readonly SemaphoreSlim _runImportCacheInitLock = new SemaphoreSlim(1, 1);

        // Run-scoped Copilot Graph metadata loader, shared across every batch so its Graph caches (resolved
        // files, users, sites, and unresolvable contexts) persist for the whole import instead of being rebuilt
        // per batch. Lazily built once; best-effort (null on failure -> each SaveSession falls back to its own).
        private ICopilotMetadataLoader _sharedCopilotLoader;
        private bool _sharedCopilotLoaderTried;
        private readonly SemaphoreSlim _sharedLoaderInitLock = new SemaphoreSlim(1, 1);

        // How many Copilot file contexts to resolve concurrently while pre-warming the cache (outside the SQL lock).
        private const int PrewarmConcurrency = 8;

        public ActivityReportSqlPersistenceManager(AuditFilterConfig filterConfig, UserGroupsCache userGroupsCache, ILogger logger, AppConfig appConfig, int maxConcurrentSaves = 1, bool usePerBatchDedupCache = false)
        {
            _filterConfig = filterConfig;
            _userGroupsCache = userGroupsCache;
            _logger = logger;
            _appConfig = appConfig;
            _userGroupsFilter = new UserGroupsFilterModel(appConfig.UserGroupsFilter);
            _maxConcurrentSaves = Math.Max(1, maxConcurrentSaves);
            _usePerBatchDedupCache = usePerBatchDedupCache;
            if (_maxConcurrentSaves > 1)
            {
                _saveConcurrencyGate = new SemaphoreSlim(_maxConcurrentSaves, _maxConcurrentSaves);
            }
        }

        /// <summary>
        /// Write all to SQL with a new data cache for the events only in activities content-set
        /// </summary>
        public async Task<ImportStat> CommitAll(ActivityReportSet activities)
        {
            if (activities.Count > 0)
            {
                // Build the dedup cache ONCE per cycle (shared, kept current in-memory) instead of
                // re-querying audit_events for every batch. The per-batch reload materialised ~the whole
                // in-window event set each time because a batch spans nearly the whole window. See the field
                // comment on _runImportCache. The AUDIT_PERBATCH_DEDUP_CACHE safety-valve restores the old path.
                var cache = _usePerBatchDedupCache
                    ? ActivityImportCache.GetAndBuildNewCache(activities.OldestContent, activities.NewestContent)
                    : await GetOrBuildRunImportCacheAsync();

                // Read default connection-string
                if (string.IsNullOrEmpty(_defaultConnectionString))
                {
                    using (var db = new AnalyticsEntitiesContext())
                    {
                        _defaultConnectionString = db.Database.Connection.ConnectionString;
                    }
                }

                return await CommitAllToSQL(activities, cache);
            }
            else return new ImportStat();
        }

        /// <summary>
        /// Write all to SQL with an existing cache
        /// </summary>
        async Task<ImportStat> CommitAllToSQL(ActivityReportSet activities, ActivityImportCache cache)
        {
#if DEBUG
            Console.WriteLine($"DEBUG: Processing {activities.Count.ToString("n0")} activity reports...");
#endif
            var allStats = new ImportStat();

            // Warm the run-scoped Copilot Graph metadata cache in PARALLEL, before taking the single-permit SQL
            // lock. The per-event ProcessExtendedProperties pass below runs serially inside that lock, and for
            // Copilot file events it calls Graph (network). Resolving those ahead of time - overlapping across
            // batches and within a batch - turns the in-lock calls into cache hits, so the lock is held only for
            // SQL work, not network round-trips.
            var sharedLoader = await GetSharedCopilotLoaderAsync();
            if (sharedLoader != null)
            {
                await PrewarmCopilotFileMetadataAsync(activities, sharedLoader);
            }

            if (_maxConcurrentSaves == 1)
            {
                // Default (serial) mode: one save at a time, using the shared staging table. Exactly the
                // original behaviour - the whole save (staging create + load + merge + metadata) is
                // serialised by the static semaphore.
                await _sqlSaveSemaphore.WaitAsync();
                try
                {
                    using (var con = new SqlConnection(_defaultConnectionString))
                    {
                        con.Open();
                        using (var db = new AnalyticsEntitiesContext(con))
                        {
                            var stats = await SaveToSqlAllTheThings(activities, db, con, cache, sharedLoader, null, null);
                            allStats.AddStats(stats);
                        }
                    }
                }
                finally
                {
                    _sqlSaveSemaphore.Release();
                }
            }
            else
            {
                // Concurrent mode: multiple saves run in parallel (bounded by _saveConcurrencyGate), each
                // with its OWN sharded staging table. Only the shared-table writes (merge + metadata) are
                // serialised, via _sharedWriteSemaphore passed into SaveToSqlAllTheThings.
                await _saveConcurrencyGate.WaitAsync();
                try
                {
                    var shardedStagingTable = "##import_staging_event_lookups_" + Guid.NewGuid().ToString("N");
                    using (var con = new SqlConnection(_defaultConnectionString))
                    {
                        con.Open();
                        using (var db = new AnalyticsEntitiesContext(con))
                        {
                            var stats = await SaveToSqlAllTheThings(activities, db, con, cache, sharedLoader, shardedStagingTable, _sharedWriteSemaphore);
                            allStats.AddStats(stats);
                        }
                    }
                }
                finally
                {
                    _saveConcurrencyGate.Release();
                }
            }

            return allStats;
        }

        /// <summary>
        /// Lazily build the run-scoped dedup cache ONCE per import cycle (this manager is created per cycle),
        /// covering the whole download window [now - DaysBeforeNowToDownload, now]. Every event processed this
        /// cycle has a CreationTime inside that window (the API only serves it there) and the cache is keyed by
        /// event id, so a single full-window load is equivalent to the old per-batch [Min,Max] loads - without
        /// the massive redundancy. Kept current thereafter in-memory by RememberProcessedEvent /
        /// RememberNewlyIgnoredEvent as batches save. Thread-safe (double-checked init + a thread-safe cache).
        /// </summary>
        private async Task<ActivityImportCache> GetOrBuildRunImportCacheAsync()
        {
            if (_runImportCacheBuilt) return _runImportCache;
            await _runImportCacheInitLock.WaitAsync();
            try
            {
                if (!_runImportCacheBuilt)
                {
                    // +1 day of lower margin so an event created just outside the exact window boundary (the
                    // download window is computed slightly earlier, at cycle start) can never be missed.
                    var daysBack = Math.Max(_appConfig.DaysBeforeNowToDownload, 1) + 1;
                    var cacheFrom = DateTime.UtcNow.AddDays(-daysBack);
                    var cacheTo = DateTime.UtcNow.AddMinutes(2);

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var built = ActivityImportCache.GetAndBuildNewCache(cacheFrom, cacheTo);
                    sw.Stop();

                    _logger.LogInformation($"Audit events import: built run dedup cache from audit_events in " +
                        $"{sw.Elapsed.TotalSeconds.ToString("n1")}s ({built.ProcessedIdCount.ToString("n0")} already-processed id(s), " +
                        $"{daysBack}-day window) - reused across all save batches this cycle instead of reloading per batch.");

                    _runImportCache = built;
                    _runImportCacheBuilt = true;
                }
            }
            finally
            {
                _runImportCacheInitLock.Release();
            }
            return _runImportCache;
        }

        /// <summary>
        /// Lazily build the run-scoped Copilot metadata loader (once). Best-effort: on any failure (e.g. no Graph
        /// creds in a test) returns null and callers fall back to the per-session loader. Thread-safe.
        /// </summary>
        private async Task<ICopilotMetadataLoader> GetSharedCopilotLoaderAsync()
        {
            if (_sharedCopilotLoaderTried)
            {
                return _sharedCopilotLoader;
            }
            await _sharedLoaderInitLock.WaitAsync();
            try
            {
                if (!_sharedCopilotLoaderTried)
                {
                    try
                    {
                        var auth = new GraphAppIndentityOAuthContext(_logger, _appConfig.ClientID, _appConfig.TenantGUID.ToString(), _appConfig.ClientSecret, _appConfig.KeyVaultUrl, _appConfig.UseClientCertificate);
                        await auth.InitClientCredential();
                        _sharedCopilotLoader = new GraphFileMetadataLoader(new GraphServiceClient(auth.Creds), _logger);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not build a run-scoped Copilot metadata loader; falling back to per-batch loaders.");
                        _sharedCopilotLoader = null;
                    }
                    _sharedCopilotLoaderTried = true;
                }
            }
            finally
            {
                _sharedLoaderInitLock.Release();
            }
            return _sharedCopilotLoader;
        }

        /// <summary>
        /// Resolve the file metadata for this batch's Copilot file contexts concurrently, warming the shared
        /// loader's cache. Errors are swallowed - the authoritative resolution + logging happens in the serial
        /// ProcessExtendedProperties pass (this only pre-populates the cache).
        /// </summary>
        private async Task PrewarmCopilotFileMetadataAsync(ActivityReportSet activities, ICopilotMetadataLoader loader)
        {
            var fileContexts = ExtractCopilotFileContexts(activities);
            if (fileContexts.Count == 0) return;

            using (var throttle = new SemaphoreSlim(PrewarmConcurrency))
            {
                var tasks = fileContexts.Select(async kvp =>
                {
                    await throttle.WaitAsync();
                    try
                    {
                        await loader.GetSpoFileInfo(kvp.Key, kvp.Value);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Copilot metadata prewarm failed for context {ctx} (will retry in the serial pass)", kvp.Key);
                    }
                    finally
                    {
                        throttle.Release();
                    }
                });
                await Task.WhenAll(tasks);
            }
        }

        /// <summary>
        /// The distinct (fileContextId -> eventUpn) map to pre-resolve for a batch. Mirrors
        /// <c>CopilotAuditEventManager</c>: only the first file-type context per event is used; a Teams meeting
        /// context ends file processing for that event; Teams chat contexts are additive (not files).
        /// </summary>
        internal static Dictionary<string, string> ExtractCopilotFileContexts(IEnumerable<AbstractAuditLogContent> activities)
        {
            var fileContexts = new Dictionary<string, string>();
            foreach (var copilot in activities.OfType<CopilotAuditLogContent>())
            {
                var contexts = copilot.CopilotEventData?.Contexts;
                if (contexts == null) continue;
                foreach (var context in contexts)
                {
                    if (context == null) continue;
                    // Type is checked before the id guard so a (typically non-null) meeting/chat context
                    // controls flow exactly as CopilotAuditEventManager does, even if its id were null.
                    if (context.Type == ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING) break;   // meeting ends file/meeting processing
                    if (context.Type == ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_CHAT) continue;   // chat is additive, not a file
                    // First file-type context for this event (a null-id file resolves to nothing, so skip it but still stop).
                    // Also skip contexts Graph can never resolve (local C:\ / UNC / DataAgent) so the concurrent
                    // prewarm doesn't fire a guaranteed-miss round-trip for each one (mirrors TryAddFileAsync).
                    if (context.Id != null
                        && !CopilotAuditEventManager.ShouldSkipGraphFileLookup(context.Id)
                        && !fileContexts.ContainsKey(context.Id))
                    {
                        fileContexts[context.Id] = copilot.UserId;
                    }
                    break;
                }
            }
            return fileContexts;
        }

        /// <summary>
        /// Fill up staging table & return import result
        /// </summary>
        private async Task<ImportStat> SaveToSqlAllTheThings(ActivityReportSet activities, AnalyticsEntitiesContext db, SqlConnection con, ActivityImportCache cache, ICopilotMetadataLoader sharedCopilotLoader, string stagingTableName, SemaphoreSlim mergeLock)
        {
            var listOfActivitiesSavedToSQL = new ConcurrentBag<AbstractAuditLogContent>();
            var logsToInsert = new EFInsertBatch<AuditLogTempEntity>(db, _logger);
            // Sequential dedup within this set: a HashSet gives O(1) Contains. The previous
            // ConcurrentBag.Contains was O(n) per row (an O(n^2) scan over a large activity set).
            var processedIds = new HashSet<Guid>();
            var stats = new ImportStat() { Total = activities.Count };

            // Phase timing, surfaced per cycle so operators can see where the save time actually goes: the
            // in-memory dedup + scope check, the SQL staging-load + merge, and the EF metadata pass. Aggregated
            // (summed) across batches in ImportStat.AddStats; in concurrent-save mode the merge/metadata are
            // serialised by mergeLock so their summed times approximate the real serialised wall-time.
            var swDedup = System.Diagnostics.Stopwatch.StartNew();
            foreach (var abtractLog in activities)
            {
                // Don't insert duplicates in same set
                if (!processedIds.Contains(abtractLog.Id) && !cache.HaveSeenInProcessedOrIgnoredEvents(abtractLog))
                {
                    var result = SaveResultEnum.NotSaved;
                    if (_filterConfig.InScope(abtractLog))
                    {
                        if (await _userGroupsCache.IsInGroupsFilter(abtractLog.UserId, _userGroupsFilter))
                        {
                            logsToInsert.Rows.Add(new AuditLogTempEntity(abtractLog, abtractLog.UserId));

                            // Remember we've done this one now
                            cache.RememberProcessedEvent(abtractLog);
                            result = SaveResultEnum.Imported;
                        }
                        else
                        {
                            result = SaveResultEnum.UserOutOfScope;
                            _logger.LogInformation($"Skipping activity report for user '{abtractLog.UserId}' - not in user groups filter");
                        }
                    }
                    else
                    {
                        // No URL
                        cache.RememberNewlyIgnoredEvent(abtractLog);
                        result = SaveResultEnum.UrlOutOfScope;
                    }

                    // Update stats
                    if (result == SaveResultEnum.Imported)
                    {
                        stats.Imported++;
                        listOfActivitiesSavedToSQL.Add(abtractLog);
                    }
                    else if (result == SaveResultEnum.ProcessedAlready) stats.ProcessedAlready++;
                    else if (result == SaveResultEnum.UrlOutOfScope) stats.URLsOutOfScope++;
                    else if (result == SaveResultEnum.UserOutOfScope) stats.UsersOutOfScope++;
                    else _logger.LogError($"Unexpected log result for log {abtractLog.Id}");

                    processedIds.Add(abtractLog.Id);
                }
            }
            swDedup.Stop();
            stats.SaveDedupMs = swDedup.Elapsed.TotalMilliseconds;

            // Merge data
#if DEBUG
            Console.WriteLine("\nDEBUG: Merging activity staging table...");
#endif
            // Merge to normal tables. In concurrent mode each save has its own sharded staging table
            // (stagingTableName) and mergeLock serialises ONLY the merge (which writes shared lookup/fact
            // tables); the parallel staging LOAD inside SaveToStagingTable runs unlocked.
            var effectiveStagingTable = stagingTableName ?? ActivityImportConstants.STAGING_TABLE_ACTIVITY;
            var mergeSQL = Resources.Insert_Activity_from_Staging_Table.Replace("${STAGING_TABLE_ACTIVITY}", effectiveStagingTable);
            var swMerge = System.Diagnostics.Stopwatch.StartNew();
            await logsToInsert.SaveToStagingTable(10000, mergeSQL, stagingTableName, mergeLock);
            swMerge.Stop();
            stats.SaveMergeMs = swMerge.Elapsed.TotalMilliseconds;

            #region Add Extra Metadata

            // The metadata pass writes SHARED tables (webs/sites via ProcessExtendedProperties, plus the
            // Copilot / Power Platform commits), so in concurrent mode it is serialised by the same lock.
            var swMeta = System.Diagnostics.Stopwatch.StartNew();
            if (mergeLock != null) await mergeLock.WaitAsync();
            try
            {
                await SaveMetadataAsync(db, listOfActivitiesSavedToSQL, sharedCopilotLoader);
            }
            finally
            {
                if (mergeLock != null) mergeLock.Release();
            }
            swMeta.Stop();
            stats.SaveMetadataMs = swMeta.Elapsed.TotalMilliseconds;

            #endregion

            return stats;
        }

        /// <summary>
        /// The EF metadata pass (webs/sites + workload-specific resolvers). Extracted so the concurrent-save
        /// path can wrap it in the shared-write lock. Writes shared tables, so callers serialise it.
        /// </summary>
        private async Task SaveMetadataAsync(AnalyticsEntitiesContext db, ConcurrentBag<AbstractAuditLogContent> listOfActivitiesSavedToSQL, ICopilotMetadataLoader sharedCopilotLoader)
        {
            // Add metadata the traditional way with EF. By now should have all the sites saved.
            // Pass the run-scoped Copilot loader so per-event Graph resolution hits the cache warmed above.
            var saveSession = new SaveSession(_logger, db, _appConfig, sharedCopilotLoader);
            await saveSession.Init();

            int metaSaveIdx = 0, changesMadeCount = 0;
#if DEBUG
            Console.WriteLine($"\nDEBUG: Updating metadata for {listOfActivitiesSavedToSQL.Count.ToString("n0")} saved events...");
#endif
            if (listOfActivitiesSavedToSQL.Count > 0)
            {
                var ids = listOfActivitiesSavedToSQL.Select(l => l.Id).ToList();
                var eventsJustSaved = db.AuditEventsCommon
                    .Include(e => e.User)
                    .Where(e => ids.Contains(e.Id)).ToList();

                // O(1) lookup by Id - the previous foreach (...) eventsJustSaved.Where(e => e.Id == log.Id)
                // pattern was O(n^2) over the batch and dominated CPU for large imports.
                var eventsJustSavedById = eventsJustSaved.ToDictionary(e => e.Id);

                var spEventsJustSaved = db.sharepoint_events
                    .Include(spe => spe.AuditEvent)
                    .Where(e => ids.Contains(e.EventID)).ToList();

                foreach (var e in spEventsJustSaved)
                {
                    saveSession.CachedSpEvents.Add(e.EventID, e);
                }

                foreach (var log in listOfActivitiesSavedToSQL)
                {
#if DEBUG
                    if (metaSaveIdx > 0 && metaSaveIdx % 1000 == 0)
                    {
                        float percentDone = ((float)metaSaveIdx / (float)listOfActivitiesSavedToSQL.Count) * 100;
                        Console.Write($"{Math.Round(percentDone, 0)}%...");
                    }
#endif
                    // Add metadata. If the event isn't in eventsJustSavedById it wasn't persisted
                    // (e.g. its staging row was skipped because a column value exceeded the staging
                    // column width - see InsertBatch over-width handling), so there's no row to
                    // attach metadata to. Skip it rather than risk a NullReferenceException.
                    if (!eventsJustSavedById.TryGetValue(log.Id, out var savedEvent))
                    {
                        metaSaveIdx++;
                        continue;
                    }
                    var changesMade = await log.ProcessExtendedProperties(saveSession, savedEvent, _logger);
                    if (changesMade)
                        changesMadeCount++;

                    metaSaveIdx++;
                }
            }
#if DEBUG
            Console.WriteLine($"DEBUG: Updated metadata for {changesMadeCount.ToString("n0")} saved events");
#endif

            // Save metadata updates
            await saveSession.CommitAllChanges();
        }
    }

    /// <summary>
    /// Class for inserting staging data to temp SQL table
    /// </summary>
    [TempTableName(ActivityImportConstants.STAGING_TABLE_ACTIVITY)]
    internal class AuditLogTempEntity
    {
        public AuditLogTempEntity(AbstractAuditLogContent abtractLog, string userNameOrHash)
        {

            this.Id = abtractLog.Id;
            this.UserName = userNameOrHash;
            this.OperationName = abtractLog.Operation;
            this.TimeStamp = abtractLog.CreationTime;
            this.TypeName = abtractLog.ItemType;
            // Keep the SharePoint URL within the urls.full_url column width (nvarchar(850)): strip the
            // volatile xsdata token, else reduce to the page path. See issue #122.
            this.ObjectId = StringUtils.EnsureUrlWithinLength(abtractLog.ObjectId, Common.Entities.Url.FullUrlMaxLength);
            this.Workload = abtractLog.Workload;


            if (abtractLog is SharePointAuditLogContent)
            {
                var spLog = (SharePointAuditLogContent)abtractLog;

                this.FileName = spLog.SourceFileName;
                this.ExtensionName = spLog.SourceFileExtension;
                this.UrlBase = spLog.SiteUrl;
                this.EventData = spLog.EventData;
            }

            if (abtractLog is CopilotAuditLogContent)
            {
                var copilotLog = (CopilotAuditLogContent)abtractLog;
                this.EventData = copilotLog.EventRaw;
            }
        }

        [Column("log_id")]
        public Guid Id { get; set; }

        [Column("user_name")]
        public string UserName { get; set; }

        [Column("file_name", true)]
        public string FileName { get; set; }

        [Column("extension_name", true)]
        public string ExtensionName { get; set; }

        [Column("operation_name")]
        public string OperationName { get; set; }

        [Column("time_stamp")]
        public DateTime TimeStamp { get; set; }

        [Column("workload")]
        public string Workload { get; set; }

        [Column("url_base", true)]
        public string UrlBase { get; set; }

        [Column("event_data", true)]
        public string EventData { get; set; }

        [Column("type_name", true)]
        public string TypeName { get; set; }

        // Must match dbo.urls.full_url (nvarchar(850), see migration ShrinkUrlsFullUrlColumn /
        // issue #122) so the join in "Insert Activity from Staging Table.sql" can use
        // IX_urls_full_url instead of an implicit type conversion that defeats the index.
        // nvarchar (not varchar) so Unicode URLs (e.g. Greek) aren't corrupted. See #122 (#108/#109).
        [Column("object_id", true, SqlTypeOverride = "nvarchar(850)")]
        public string ObjectId { get; set; }

        [Column("web_url", true)]
        public string WebUrl { get; set; }
    }
}

