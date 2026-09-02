using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Graph.User;
using WebJob.Office365ActivityImporter.Engine.Properties;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Persistence
{
    /// <summary>
    /// IDs staged by an earlier failed attempt. The next attempt must stage those IDs even if a fresh
    /// per-batch cache can already see an audit row committed before the failure; otherwise the retry
    /// skips the metadata phase and can falsely report success.
    /// </summary>
    internal sealed class ActivitySaveRetryState
    {
        private readonly ConcurrentDictionary<Guid, byte> _eventIds =
            new ConcurrentDictionary<Guid, byte>();

        public bool ShouldRetry(Guid eventId) => _eventIds.ContainsKey(eventId);

        public void MarkForRetry(IEnumerable<AbstractAuditLogContent> events)
        {
            foreach (var auditEvent in events)
            {
                if (auditEvent != null) _eventIds[auditEvent.Id] = 0;
            }
        }

        public void MarkSucceeded(IEnumerable<AbstractAuditLogContent> events)
        {
            foreach (var auditEvent in events)
            {
                if (auditEvent != null) _eventIds.TryRemove(auditEvent.Id, out _);
            }
        }
    }

    /// <summary>
    /// What one staging pass produced: the statistics for the batch and the events it handed to the
    /// staging batch, which the metadata pass then tries to match against the rows present in
    /// <c>audit_events</c> after the merge.
    /// </summary>
    internal sealed class ActivityStagingPassResult
    {
        internal ActivityStagingPassResult(ImportStat stats, ConcurrentBag<AbstractAuditLogContent> savedToSql)
        {
            Stats = stats;
            SavedToSql = savedToSql;
        }

        /// <summary>
        /// Totals plus the <c>SaveDedupMs</c> / <c>SaveMergeMs</c> phase timings. The metadata-phase timings
        /// are filled in afterwards by the caller.
        /// </summary>
        public ImportStat Stats { get; }

        /// <summary>
        /// The events handed to the staging batch. Note this is what was <i>offered</i> to SQL, not
        /// necessarily what reached <c>audit_events</c>: <c>InsertBatch</c> skips any row whose value is
        /// wider than its staging column (see issue #122 / #127), which is why the metadata pass tolerates
        /// an event having no saved row.
        ///
        /// A <see cref="ConcurrentBag{T}"/> rather than a list purely because that is what this pass has
        /// always produced, so the metadata pass's (unordered) enumeration behaves exactly as it did -
        /// <see cref="ConcurrentBag{T}"/> makes no insertion-order guarantee.
        /// </summary>
        public ConcurrentBag<AbstractAuditLogContent> SavedToSql { get; }
    }

    /// <summary>
    /// The SQL-facing half of an audit-log save batch: run every event through
    /// <see cref="ActivityStagingRules"/>, build its staging row, then load the staging table and merge it
    /// into the normal tables.
    ///
    /// Lifted out of <c>ActivityReportSqlPersistenceManager.SaveToSqlAllTheThings</c> by issue #373. Nothing
    /// here touches EF: the staging table is reached through <see cref="IActivityStagingBatch"/>, the
    /// org-URL whitelist and the user-groups filter through the collaborators the manager already held. That
    /// makes the batch's operator-facing outputs - the <c>ImportStat</c> counters, the "not in user groups
    /// filter" line, the merge SQL and which staging table it targets, and whether the shared-write lock is
    /// taken - assertable with no SQL Server and no Graph.
    ///
    /// Stateless between calls (everything is per-<see cref="RunAsync"/>), so one instance is shared by every
    /// batch of a cycle, including concurrent ones.
    /// </summary>
    internal sealed class ActivityStagingPass
    {
        /// <summary>
        /// Rows per parallel staging-insert thread. The production value, unchanged; the actual thread count
        /// is capped by <c>InsertBatchConcurrency.MaxConcurrentThreads</c>.
        /// </summary>
        public const int StagingInsertsPerThread = 10000;

        private readonly AuditFilterConfig _filterConfig;
        private readonly UserGroupsCache _userGroupsCache;
        private readonly UserGroupsFilterModel _userGroupsFilter;
        private readonly ILogger _logger;

        /// <remarks>
        /// Deliberately no null guards: the manager did not validate these either, and it dereferences them
        /// during the save. Adding an <c>ArgumentNullException</c> here would move a
        /// <c>NullReferenceException</c> raised mid-save to a different exception raised at construction -
        /// an operator-visible change, not an extraction.
        /// </remarks>
        public ActivityStagingPass(AuditFilterConfig filterConfig, UserGroupsCache userGroupsCache,
            UserGroupsFilterModel userGroupsFilter, ILogger logger)
        {
            _filterConfig = filterConfig;
            _userGroupsCache = userGroupsCache;
            _userGroupsFilter = userGroupsFilter;
            _logger = logger;
        }

        /// <param name="stagingTableName">
        /// This save's sharded staging table, or <c>null</c> for the default serial path's shared table.
        /// </param>
        /// <param name="mergeLock">
        /// Serialises only the merge (which writes shared lookup / fact tables) in concurrent-save mode;
        /// <c>null</c> on the serial path, where the whole save is already serialised.
        /// </param>
        public async Task<ActivityStagingPassResult> RunAsync(ActivityReportSet activities, ActivityImportCache cache,
            IActivityStagingBatch batch, string stagingTableName, SemaphoreSlim mergeLock)
        {
            return await RunAsync(activities, cache, batch, stagingTableName, mergeLock, retryState: null);
        }

        internal async Task<ActivityStagingPassResult> RunAsync(ActivityReportSet activities, ActivityImportCache cache,
            IActivityStagingBatch batch, string stagingTableName, SemaphoreSlim mergeLock,
            ActivitySaveRetryState retryState)
        {
            var listOfActivitiesSavedToSQL = new ConcurrentBag<AbstractAuditLogContent>();
            // Sequential dedup within this set: a HashSet gives O(1) Contains. The previous
            // ConcurrentBag.Contains was O(n) per row (an O(n^2) scan over a large activity set).
            var processedIds = new HashSet<Guid>();
            var stats = new ImportStat() { Total = activities.Count };

            // Phase timing, surfaced per cycle so operators can see where the save time actually goes: the
            // in-memory dedup + scope check, the SQL staging-load + merge, and the EF metadata pass. Aggregated
            // (summed) across batches in ImportStat.AddStats; in concurrent-save mode the merge/metadata are
            // serialised by mergeLock so their summed times approximate the real serialised wall-time.
            var swDedup = System.Diagnostics.Stopwatch.StartNew();

            // Hoisted out of the loop: all three capture only loop-invariant state, and Roslyn does not
            // cache capturing lambdas, so building them per event would allocate three delegates for every
            // one of the batch's events.
            Func<AbstractAuditLogContent, bool> urlInScope = log => _filterConfig.InScope(log);
            Func<string, Task<bool>> userInGroupsFilter = upn => _userGroupsCache.IsInGroupsFilter(upn, _userGroupsFilter);
            Action<AbstractAuditLogContent> stageRow = log => batch.AddRow(new AuditLogTempEntity(log, log.UserId));

            try
            {
                foreach (var abtractLog in activities)
                {
                    // Don't insert duplicates in same set. The decision itself (dedup -> URL scope -> user
                    // scope, plus what gets remembered where) lives in ActivityStagingRules so it can be
                    // asserted with no SQL and no Graph; see issue #373.
                    var recoverMetadata = activities.RequiresMetadataRecovery(abtractLog)
                        && cache.HaveSeenInProcessedOrIgnoredEvents(abtractLog)
                        && !cache.WasRememberedThisCycle(abtractLog);
                    var decision = await ActivityStagingRules.DecideAndRememberAsync(
                        abtractLog, processedIds, cache, urlInScope, userInGroupsFilter, stageRow,
                        retryState?.ShouldRetry(abtractLog.Id) == true || recoverMetadata);

                    if (!decision.IsDuplicate)
                    {
                        var result = decision.Result;
                        if (result == SaveResultEnum.UserOutOfScope)
                        {
                            _logger.LogInformation($"Skipping activity report for user '{abtractLog.UserId}' - not in user groups filter");
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
                // tables); the parallel staging LOAD inside the batch runs unlocked.
                var effectiveStagingTable = ActivitySaveConcurrencyPolicy.EffectiveStagingTableName(stagingTableName);
                var mergeSQL = Resources.Insert_Activity_from_Staging_Table.Replace("${STAGING_TABLE_ACTIVITY}", effectiveStagingTable);
                var swMerge = System.Diagnostics.Stopwatch.StartNew();
                await batch.LoadAndMergeAsync(StagingInsertsPerThread, mergeSQL, stagingTableName, mergeLock);
                swMerge.Stop();
                stats.SaveMergeMs = swMerge.Elapsed.TotalMilliseconds;

                return new ActivityStagingPassResult(stats, listOfActivitiesSavedToSQL);
            }
            catch
            {
                retryState?.MarkForRetry(listOfActivitiesSavedToSQL);
                var released = cache.ForgetProcessedEvents(listOfActivitiesSavedToSQL);
                if (released > 0)
                {
                    _logger.LogWarning($"Audit events import: save attempt failed after staging {released.ToString("n0")} event(s); " +
                        "released their in-memory dedup markers so a retry can stage them again.");
                }
                throw;
            }
        }
    }
}
