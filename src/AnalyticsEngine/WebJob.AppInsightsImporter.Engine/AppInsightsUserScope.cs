using Common.Entities.UserScope;
using System;
using System.Threading;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>What <see cref="AppInsightsUserScopeRules.Apply"/> removed from one day of data.</summary>
    public sealed class AppInsightsScopeFilterResult
    {
        public int PageViewsRemoved { get; set; }
        public int EventsRemoved { get; set; }
        public int CommentsRemoved { get; set; }
        public int LikesRemoved { get; set; }

        public int Total => PageViewsRemoved + EventsRemoved + CommentsRemoved + LikesRemoved;
    }

    /// <summary>
    /// Applies <c>UserGroupsFilter</c> to the web traffic the SharePoint tracker sends to Application Insights.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Page views, clicks, searches and page exits carry the viewer's sign-in name (<c>_spPageContextInfo.userLoginName</c>,
    /// the UPN in SharePoint Online) and are dropped when the viewer is outside the scope.
    /// </para>
    /// <para>
    /// A page-update event is different: it describes the page - its metadata, comments and likes - as seen by
    /// whoever happened to view it. The page metadata is not about any person, so the event is kept whoever the
    /// viewer was; its comments and likes are each written by someone, so those are filtered by their author.
    /// </para>
    /// </remarks>
    public static class AppInsightsUserScopeRules
    {
        public static AppInsightsScopeFilterResult Apply(PageViewCollection pageViews, CustomEventsResultCollection events, UserImportScope userScope)
        {
            var result = new AppInsightsScopeFilterResult();
            if (userScope == null || !userScope.IsFiltered)
            {
                return result;
            }

            if (pageViews?.Rows != null)
            {
                result.PageViewsRemoved = pageViews.Rows.RemoveAll(v => v == null || !userScope.IsInScope(v.Username));
            }

            if (events?.Rows != null)
            {
                foreach (var pageUpdate in events.Rows)
                {
                    if (pageUpdate is PageUpdateEventAppInsightsQueryResult update && update.CustomProperties != null)
                    {
                        result.LikesRemoved += update.CustomProperties.Likes?.RemoveAll(l => l == null || !userScope.IsInScope(l.Email)) ?? 0;
                        result.CommentsRemoved += update.CustomProperties.PageComments?.RemoveAll(c => c == null || !userScope.IsInScope(c.Email)) ?? 0;
                    }
                }

                result.EventsRemoved = events.Rows.RemoveAll(e => e == null
                    || (!(e is PageUpdateEventAppInsightsQueryResult) && !userScope.IsInScope(e.Username)));
            }

            return result;
        }
    }

    /// <summary>
    /// How far the import has read, as opposed to how far it has saved. Process-lifetime.
    /// </summary>
    /// <remarks>
    /// The import resumes from the newest hit stored in SQL. Under <c>UserGroupsFilter</c> that stops moving whenever
    /// the people in scope stop browsing - over a weekend, or for a small pilot group - and every cycle would then
    /// re-download every day since their last visit, only to discard it all again. This remembers the newest page view
    /// read from a day that was fully processed, so the next cycle starts from there instead. It is only consulted when
    /// the scope is filtered, which leaves an unfiltered deployment behaving exactly as before, and it is in-memory: after
    /// a restart the first cycle re-reads from the newest stored hit once.
    /// </remarks>
    public sealed class AppInsightsScanWatermark
    {
        private long _newestTicks;

        /// <summary>The newest page view read from a fully processed day, in UTC, or null when none.</summary>
        public DateTime? NewestScannedUtc
        {
            get
            {
                var ticks = Interlocked.Read(ref _newestTicks);
                return ticks == 0 ? (DateTime?)null : new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        /// <summary>Moves the watermark forward to <paramref name="scannedUtc"/>; never backwards.</summary>
        public void Advance(DateTime? scannedUtc)
        {
            if (!scannedUtc.HasValue)
            {
                return;
            }

            var value = scannedUtc.Value;
            if (value.Kind == DateTimeKind.Local)
            {
                value = value.ToUniversalTime();
            }
            var ticks = value.Ticks;

            long current;
            do
            {
                current = Interlocked.Read(ref _newestTicks);
                if (ticks <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _newestTicks, ticks, current) != current);
        }
    }
}
