using DataUtils;
using System;
using System.Collections.Generic;
using WebJob.AppInsightsImporter.Engine.ApiImporter;
using WebJob.AppInsightsImporter.Engine.Sql.Models;

namespace WebJob.AppInsightsImporter.Engine.Sql.Rules
{
    /// <summary>
    /// What the page-view rules decided to stage, and why the rest was dropped. These counts were
    /// previously local variables that only ever reached a log line, so no test could assert them.
    /// See issue #369.
    /// </summary>
    internal class PageViewStagingPlan
    {
        public List<HitTempEntity> RowsToStage { get; } = new List<HitTempEntity>();

        /// <summary>
        /// Page-views rejected because their page-request id had already been seen in this batch.
        ///
        /// Note this also counts a page-view whose id is <c>Guid.Empty</c>, which is not really a
        /// duplicate. That is the existing behaviour and the number that is logged today, so it is
        /// preserved deliberately rather than quietly changed.
        /// </summary>
        public int DuplicatePageRequestIds { get; set; }

        /// <summary>Page-views rejected because their URL is outside the configured org URL filters.</summary>
        public int OutOfScopeUrls { get; set; }

        /// <summary>Total page-views considered, before any filtering.</summary>
        public int RawPageViews { get; set; }
    }

    /// <summary>
    /// Pure rules for the page-view staging path - de-duplication by page-request id and the org URL
    /// in-scope filter, with no SQL, no ADO.NET and no logging. Modelled on
    /// ActivityAPI/Loaders/AuditLogContentDispatcher.
    /// </summary>
    internal static class PageViewStagingRules
    {
        /// <summary>
        /// Reproduces the original save-path loop: rows with no page-request id are dropped without
        /// being counted, <c>Guid.Empty</c> counts as a duplicate, and de-duplication is decided
        /// BEFORE the URL filter (so an id first seen on an out-of-scope URL still consumes that id).
        ///
        /// One deliberate divergence: a null <paramref name="filterUrls"/> fails fast here, whereas the
        /// original only threw once a row actually reached the filter - so an empty batch used to
        /// succeed with a null list. That is hardening, not preservation. It is unobservable in
        /// production because the only caller loads the list via SiteFilterLoader.Load and dereferences
        /// filterUrls.Count immediately (AppInsightsImporter.cs), long before this is reached.
        /// </summary>
        public static PageViewStagingPlan Plan(PageViewCollection pageViews, List<FilterUrlConfig> filterUrls)
        {
            if (filterUrls == null)
            {
                throw new ArgumentNullException(nameof(filterUrls));
            }

            var plan = new PageViewStagingPlan();
            if (pageViews?.Rows == null)
            {
                return plan;
            }

            plan.RawPageViews = pageViews.Rows.Count;

            // O(1) duplicate lookups. At a 200k-user tenant a single cycle can carry a very large
            // page-view batch, so this must not become a List.Contains scan.
            var seenPageRequestIds = new HashSet<Guid>();

            foreach (var pv in pageViews.Rows)
            {
                if (pv?.CustomProperties?.PageRequestId == null)
                {
                    continue; // no id at all - not counted, matching the original Where clause
                }

                var isNew = pv.CustomProperties.PageRequestId != Guid.Empty
                    && seenPageRequestIds.Add(pv.CustomProperties.PageRequestId.Value);

                if (!isNew)
                {
                    plan.DuplicatePageRequestIds++;
                    continue;
                }

                if (!filterUrls.UrlInScope(pv.CustomProperties.SiteUrl, pv.Url))
                {
                    plan.OutOfScopeUrls++;
                    continue;
                }

                plan.RowsToStage.Add(new HitTempEntity(pv));
            }

            return plan;
        }
    }
}
