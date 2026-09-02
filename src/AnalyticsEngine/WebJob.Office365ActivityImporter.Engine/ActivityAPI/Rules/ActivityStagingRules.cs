using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules
{
    /// <summary>
    /// What the staging rules decided for a single audit event. Previously these outcomes only existed as a
    /// local <see cref="SaveResultEnum"/> variable inside <c>ActivityReportSqlPersistenceManager</c>, so the
    /// only way to observe them was to run the whole SQL save. See issue #373.
    /// </summary>
    public sealed class ActivityStagingDecision
    {
        private ActivityStagingDecision(bool isDuplicate, bool staged, SaveResultEnum result)
        {
            IsDuplicate = isDuplicate;
            Staged = staged;
            Result = result;
        }

        /// <summary>
        /// The event was already decided earlier in this activity set, or is already in the de-duplication
        /// cache (imported or ignored on a previous batch/cycle). Nothing was staged, nothing was
        /// remembered, and - deliberately - no statistic was incremented; that is the existing behaviour.
        /// </summary>
        public static readonly ActivityStagingDecision Duplicate =
            new ActivityStagingDecision(true, false, SaveResultEnum.NotSaved);

        internal static ActivityStagingDecision Imported() => new ActivityStagingDecision(false, true, SaveResultEnum.Imported);
        internal static ActivityStagingDecision UserOutOfScope() => new ActivityStagingDecision(false, false, SaveResultEnum.UserOutOfScope);
        internal static ActivityStagingDecision UrlOutOfScope() => new ActivityStagingDecision(false, false, SaveResultEnum.UrlOutOfScope);

        /// <summary>True when the event was skipped as an already-seen id (no staging, no stats).</summary>
        public bool IsDuplicate { get; }

        /// <summary>True when the event was handed to the staging callback.</summary>
        public bool Staged { get; }

        /// <summary>The outcome the caller records against <c>ImportStat</c>.</summary>
        public SaveResultEnum Result { get; }
    }

    /// <summary>
    /// The per-event decision on the audit-log staging path: in-set de-duplication, the run/batch
    /// de-duplication cache, the SharePoint org-URL whitelist and the optional user-groups filter, plus the
    /// in-memory bookkeeping that keeps the cache current as batches commit.
    ///
    /// Lifted verbatim out of <c>ActivityReportSqlPersistenceManager.SaveToSqlAllTheThings</c> so it can be
    /// asserted without SQL Server or Graph (see issue #373). The two collaborators that do touch the outside
    /// world - the org-URL filter and the user-groups lookup - are injected as delegates, which keeps the
    /// original short-circuiting intact: the (potentially Graph-backed) user lookup is still only reached for
    /// an event whose URL is already in scope, and neither is reached for a duplicate.
    ///
    /// <see cref="ActivityImportCache"/> is a plain in-memory object for the members used here
    /// (<c>HaveSeenInProcessedOrIgnoredEvents</c> / <c>RememberProcessedEvent</c> /
    /// <c>RememberNewlyIgnoredEvent</c>), so a test can use <c>ActivityImportCache.GetEmptyCache()</c> and
    /// stay entirely off the database.
    /// </summary>
    public static class ActivityStagingRules
    {
        /// <summary>
        /// Has this event already been decided - either earlier in this same activity set, or on a previous
        /// batch/cycle (the de-duplication cache holds both imported and ignored ids)?
        /// </summary>
        public static bool IsAlreadyProcessed(AbstractAuditLogContent log, HashSet<Guid> decidedInThisSet, ActivityImportCache cache)
        {
            if (log == null) throw new ArgumentNullException(nameof(log));
            if (decidedInThisSet == null) throw new ArgumentNullException(nameof(decidedInThisSet));
            if (cache == null) throw new ArgumentNullException(nameof(cache));

            return decidedInThisSet.Contains(log.Id) || cache.HaveSeenInProcessedOrIgnoredEvents(log);
        }

        /// <summary>
        /// Decide what happens to one event and apply the in-memory bookkeeping, in exactly the order the
        /// original loop used:
        ///
        /// <list type="number">
        /// <item>already decided in this set, or already in the cache -> skip entirely (no stats, no logging);</item>
        /// <item>URL out of scope -> remember as newly-ignored (so it is not reconsidered, and so it reaches
        ///       <c>ignored_audit_events</c>) and report <see cref="SaveResultEnum.UrlOutOfScope"/>;</item>
        /// <item>URL in scope but user outside the groups filter -> report
        ///       <see cref="SaveResultEnum.UserOutOfScope"/> and remember <b>nothing</b>. That asymmetry is
        ///       existing behaviour, preserved deliberately;</item>
        /// <item>otherwise stage the row first, then remember it as processed.</item>
        /// </list>
        ///
        /// The id is added to <paramref name="decidedInThisSet"/> for every non-duplicate outcome, so a
        /// repeated id inside one set is only ever considered once regardless of what was decided.
        /// </summary>
        /// <param name="stageRow">
        /// Invoked - before the event is remembered - for an event that should be staged. Kept as a callback
        /// so the staging-row construction stays with the caller and the original ordering (and therefore the
        /// original behaviour if that construction throws) is preserved exactly.
        /// </param>
        public static async Task<ActivityStagingDecision> DecideAndRememberAsync(
            AbstractAuditLogContent log,
            HashSet<Guid> decidedInThisSet,
            ActivityImportCache cache,
            Func<AbstractAuditLogContent, bool> urlInScope,
            Func<string, Task<bool>> userInGroupsFilter,
            Action<AbstractAuditLogContent> stageRow)
        {
            if (urlInScope == null) throw new ArgumentNullException(nameof(urlInScope));
            if (userInGroupsFilter == null) throw new ArgumentNullException(nameof(userInGroupsFilter));
            if (stageRow == null) throw new ArgumentNullException(nameof(stageRow));

            if (IsAlreadyProcessed(log, decidedInThisSet, cache))
            {
                return ActivityStagingDecision.Duplicate;
            }

            ActivityStagingDecision decision;
            if (urlInScope(log))
            {
                if (await userInGroupsFilter(log.UserId))
                {
                    stageRow(log);

                    // Remember we've done this one now
                    cache.RememberProcessedEvent(log);
                    decision = ActivityStagingDecision.Imported();
                }
                else
                {
                    decision = ActivityStagingDecision.UserOutOfScope();
                }
            }
            else
            {
                // No URL
                cache.RememberNewlyIgnoredEvent(log);
                decision = ActivityStagingDecision.UrlOutOfScope();
            }

            decidedInThisSet.Add(log.Id);
            return decision;
        }
    }
}
