using ActivityImporter.Engine.ActivityAPI.Copilot;
using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using DataUtils.Sql;
using Microsoft.Extensions.Logging;
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
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Persistence;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace WebJob.Office365ActivityImporter.Engine
{
    /// <summary>
    /// SQL adaptor for saving activity reports. 
    /// Saves to a staging table, merges everything with a SQL script, then processes workload specific metadata updates seperately.
    /// </summary>
    public class ActivityReportSqlPersistenceManager : IActivityReportPersistenceManager
    {
        private readonly ILogger _logger;
        private readonly AppConfig _appConfig;
        private readonly IClock _clock;
        private string _defaultConnectionString = null;

        // --- Collaborators (issue #373 part 2) ---------------------------------------------------------
        // The four jobs this adapter used to do inline, each now behind its own seam so the decisions around
        // them can be asserted without SQL Server or Graph. The production adapters are still constructed
        // here by default, so no call site changes.
        private readonly IActivityImportCacheProvider _cacheProvider;
        private readonly IActivityStagingWriter _stagingWriter;
        private readonly CopilotMetadataPrewarmer _copilotPrewarmer;
        private readonly ISaveSessionFactory _saveSessionFactory;
        private readonly ActivityStagingPass _stagingPass;

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
        // SHARED tables - the merge (lookup + fact inserts) and the metadata pass (webs/sites, etc.) - are
        // still serialised across all saves by _sharedWriteSemaphore, so there is no shared-table race.
        // Default is 1, which preserves the original strictly-serial behaviour exactly (single static
        // _sqlSaveSemaphore, no sharding).
        private readonly int _maxConcurrentSaves;
        private readonly SemaphoreSlim _saveConcurrencyGate;
        private static readonly SemaphoreSlim _sharedWriteSemaphore = new SemaphoreSlim(1, 1);

        // --- Run-scoped dedup cache (perf: build ONCE per cycle, not per batch) -------------------------
        // _usePerBatchDedupCache is an ops safety-valve (app setting AUDIT_PERBATCH_DEDUP_CACHE=true) that
        // restores the old per-batch cache build without a redeploy; default false = per-cycle behaviour.
        // The lifecycle itself lives in ActivityImportCacheProvider.
        private readonly bool _usePerBatchDedupCache;

        public ActivityReportSqlPersistenceManager(AuditFilterConfig filterConfig, UserGroupsCache userGroupsCache, ILogger logger, AppConfig appConfig, int maxConcurrentSaves = 1, bool usePerBatchDedupCache = false)
            : this(filterConfig, userGroupsCache, logger, appConfig, maxConcurrentSaves, usePerBatchDedupCache, null)
        {
        }

        /// <summary>
        /// As above, with the clock supplied (issue #368). The original signature is kept as a delegating
        /// overload rather than gaining an optional parameter: optional arguments are filled in by the
        /// compiler, so widening the existing constructor would be a binary-breaking change for any already
        /// compiled caller.
        /// </summary>
        public ActivityReportSqlPersistenceManager(AuditFilterConfig filterConfig, UserGroupsCache userGroupsCache, ILogger logger, AppConfig appConfig, int maxConcurrentSaves, bool usePerBatchDedupCache, IClock clock)
            : this(filterConfig, userGroupsCache, logger, appConfig, maxConcurrentSaves, usePerBatchDedupCache, clock, null, null, null, null)
        {
        }

        /// <summary>
        /// The collaborator constructor (issue #373 part 2). <c>internal</c> because
        /// <see cref="IActivityStagingWriter"/> carries the internal staging entity;
        /// <c>InternalsVisibleTo("Tests.UnitTests")</c> makes it reachable from the tests.
        ///
        /// A <c>null</c> collaborator means "build the production adapter". That is how the public
        /// constructors above keep the original field-initialisation order: <c>appConfig</c> is still
        /// dereferenced (and still throws a <see cref="NullReferenceException"/> if it is null) before any
        /// adapter is constructed, rather than a chained <c>: this(new SqlThing(appConfig...), ...)</c>
        /// moving an adapter's own validation in front of it.
        /// </summary>
        internal ActivityReportSqlPersistenceManager(AuditFilterConfig filterConfig, UserGroupsCache userGroupsCache, ILogger logger, AppConfig appConfig,
            int maxConcurrentSaves, bool usePerBatchDedupCache, IClock clock,
            IActivityImportCacheProvider cacheProvider, IActivityStagingWriter stagingWriter,
            ICopilotMetadataLoaderFactory copilotMetadataLoaderFactory, ISaveSessionFactory saveSessionFactory)
        {
            _logger = logger;
            _appConfig = appConfig;
            _clock = clock ?? SystemClock.Instance;
            var userGroupsFilter = new UserGroupsFilterModel(appConfig.UserGroupsFilter);
            _maxConcurrentSaves = ActivitySaveConcurrencyPolicy.NormaliseMaxConcurrentSaves(maxConcurrentSaves);
            _usePerBatchDedupCache = usePerBatchDedupCache;
            if (ActivitySaveConcurrencyPolicy.UseShardedStaging(_maxConcurrentSaves))
            {
                _saveConcurrencyGate = new SemaphoreSlim(_maxConcurrentSaves, _maxConcurrentSaves);
            }

            _stagingPass = new ActivityStagingPass(filterConfig, userGroupsCache, userGroupsFilter, logger);
            _cacheProvider = cacheProvider ?? new ActivityImportCacheProvider(SqlActivityImportCacheLoader.Instance, logger);
            _stagingWriter = stagingWriter ?? new SqlActivityStagingWriter(logger);
            _copilotPrewarmer = new CopilotMetadataPrewarmer(
                copilotMetadataLoaderFactory ?? new GraphCopilotMetadataLoaderFactory(logger, appConfig), logger);
            _saveSessionFactory = saveSessionFactory ?? new SaveSessionFactory(logger, appConfig);
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
                // in-window event set each time because a batch spans nearly the whole window. See
                // ActivityImportCacheProvider. The AUDIT_PERBATCH_DEDUP_CACHE safety-valve restores the old path.
                var cacheWindow = ActivityImportCacheWindow.Resolve(_usePerBatchDedupCache, activities.OldestContent,
                    activities.NewestContent, _appConfig.DaysBeforeNowToDownload, _clock.UtcNow);

                var cache = await _cacheProvider.GetForWindowAsync(cacheWindow);

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
            // The prewarm is skipped entirely when Copilot resource resolution is disabled - the save path makes
            // no Graph resource calls in that mode (every Copilot event is staged agent-metadata-only), so there
            // is nothing to warm. See CopilotMetadataPrewarmer / CopilotPrewarmPolicy.
            var sharedLoader = await _copilotPrewarmer.GetLoaderAndPrewarmAsync(activities, _appConfig.ResolveCopilotResourceMetadata);

            if (!ActivitySaveConcurrencyPolicy.UseShardedStaging(_maxConcurrentSaves))
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
                    var shardedStagingTable = ActivitySaveConcurrencyPolicy.NewShardedStagingTableName();
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
        /// The distinct (fileContextId -> eventUpn) map to pre-resolve for a batch. Thin wrapper kept so
        /// existing call sites and tests are unaffected; the rule itself lives in
        /// <see cref="CopilotPrewarmPolicy.ExtractFileContexts"/>.
        /// </summary>
        internal static Dictionary<string, string> ExtractCopilotFileContexts(IEnumerable<AbstractAuditLogContent> activities)
            => CopilotPrewarmPolicy.ExtractFileContexts(activities);

        /// <summary>
        /// Fill up staging table & return import result
        /// </summary>
        private async Task<ImportStat> SaveToSqlAllTheThings(ActivityReportSet activities, AnalyticsEntitiesContext db, SqlConnection con, ActivityImportCache cache, ICopilotMetadataLoader sharedCopilotLoader, string stagingTableName, SemaphoreSlim mergeLock)
        {
            // Dedup + scope check + staging-row build, then the staging load and merge. All of it lives in
            // ActivityStagingPass, which reaches SQL only through IActivityStagingBatch, so the batch's
            // counters, log lines and merge wiring can be asserted without a database (issue #373).
            var stagingBatch = _stagingWriter.CreateBatch(db);
            var staged = await _stagingPass.RunAsync(activities, cache, stagingBatch, stagingTableName, mergeLock);
            var stats = staged.Stats;

            #region Add Extra Metadata

            // The metadata pass writes SHARED tables (webs/sites via ProcessExtendedProperties, plus the
            // Copilot / Power Platform commits), so in concurrent mode it is serialised by the same lock.
            var swMeta = System.Diagnostics.Stopwatch.StartNew();
            if (mergeLock != null) await mergeLock.WaitAsync();
            try
            {
                await SaveMetadataAsync(db, staged.SavedToSql, sharedCopilotLoader, stats);
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
        private async Task SaveMetadataAsync(AnalyticsEntitiesContext db, ConcurrentBag<AbstractAuditLogContent> listOfActivitiesSavedToSQL, ICopilotMetadataLoader sharedCopilotLoader, ImportStat stats)
        {
            // Add metadata the traditional way with EF. By now should have all the sites saved.
            // Pass the run-scoped Copilot loader so per-event Graph resolution hits the cache warmed above.
            var saveSession = await _saveSessionFactory.CreateAsync(db, sharedCopilotLoader);

            int metaSaveIdx = 0, changesMadeCount = 0;
            double copilotResolveMs = 0;   // per-event Copilot resolution time (the Graph file/meeting calls)
#if DEBUG
            Console.WriteLine($"\nDEBUG: Updating metadata for {listOfActivitiesSavedToSQL.Count.ToString("n0")} saved events...");
#endif
            if (listOfActivitiesSavedToSQL.Count > 0)
            {
                // Time the metadata read-back (EF load of the just-saved audit + SharePoint events) on its own
                // so it appears in the per-cycle metadata breakdown instead of being folded into the total.
                var swMetaLoad = System.Diagnostics.Stopwatch.StartNew();
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
                swMetaLoad.Stop();
                stats.SaveMetadataLoadMs = swMetaLoad.Elapsed.TotalMilliseconds;

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
                    // Time the Copilot per-event work separately - this is where the Graph file/meeting
                    // resolution happens - so its cost shows up in the per-cycle summary.
                    var copilotSw = log is CopilotAuditLogContent ? System.Diagnostics.Stopwatch.StartNew() : null;
                    var changesMade = await log.ProcessExtendedProperties(saveSession, savedEvent, _logger);
                    if (copilotSw != null) { copilotSw.Stop(); copilotResolveMs += copilotSw.Elapsed.TotalMilliseconds; }
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

            // Surface the per-workload sub-costs (summed across batches in the cycle summary): the Copilot
            // per-event resolution measured above, and the Power Platform staging-merge cost.
            stats.SaveCopilotResolveMs = copilotResolveMs;
            stats.SaveCopilotCommitMs = saveSession.LastCopilotCommitMs;
            stats.SavePowerPlatformMs = saveSession.LastPowerPlatformCommitMs;
            stats.SaveEfChangesMs = saveSession.LastEfSaveChangesMs;
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

