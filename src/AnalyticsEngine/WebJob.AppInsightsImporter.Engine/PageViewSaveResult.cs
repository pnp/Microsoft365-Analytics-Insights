using WebJob.AppInsightsImporter.Engine.Sql.Rules;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>
    /// What a page-view save actually did. The counts already existed - PageViewStagingRules works them
    /// out - but they only ever reached a log line, so nothing above the save could assert or aggregate
    /// them. Issue #369.
    ///
    /// This is a report, not a decision: nothing in the import branches on it today, so returning it
    /// changes no behaviour.
    /// </summary>
    public class PageViewSaveResult
    {
        public PageViewSaveResult(int rawPageViews, int staged, int duplicatePageRequestIds, int outOfScopeUrls, int mergeRowsAffected)
        {
            RawPageViews = rawPageViews;
            Staged = staged;
            DuplicatePageRequestIds = duplicatePageRequestIds;
            OutOfScopeUrls = outOfScopeUrls;
            MergeRowsAffected = mergeRowsAffected;
        }

        /// <summary>Page-views in the batch before any filtering.</summary>
        public int RawPageViews { get; }

        /// <summary>
        /// Rows the rules selected for the staging table. Not necessarily the number that reached SQL:
        /// InsertBatch drops (and warns about) any row with a value too wide for its staging column.
        /// </summary>
        public int Staged { get; }

        /// <summary>
        /// Page-views dropped because their page-request id had already been seen in this batch. As
        /// documented on PageViewStagingPlan this also counts <c>Guid.Empty</c> ids, which are not really
        /// duplicates - preserved deliberately because it is the number logged today.
        /// </summary>
        public int DuplicatePageRequestIds { get; }

        /// <summary>Page-views dropped because their URL is outside the configured org URL filters.</summary>
        public int OutOfScopeUrls { get; }

        /// <summary>
        /// The ADO.NET rows-affected total the merge script reported, NOT the number of hits inserted:
        /// "Migrate Hits Import into Hits.sql" also upserts a dozen lookup tables (urls, page_titles,
        /// users, sites, webs, sessions, ...) in the same batch and ExecuteNonQuery sums them all.
        /// Zero when nothing was staged, because InsertBatch returns early without running the merge.
        /// </summary>
        public int MergeRowsAffected { get; }

        /// <summary>Nothing at all was in the batch.</summary>
        public static PageViewSaveResult Empty => new PageViewSaveResult(0, 0, 0, 0, 0);

        /// <summary>
        /// Maps the staging plan onto the reported result. A named seam purely so the mapping can be
        /// asserted without a database - five same-typed ints in a row (the four plan counts plus
        /// <paramref name="mergeRowsAffected"/>) is exactly the shape that gets silently transposed.
        /// </summary>
        internal static PageViewSaveResult FromPlan(PageViewStagingPlan plan, int mergeRowsAffected)
        {
            return new PageViewSaveResult(plan.RawPageViews, plan.RowsToStage.Count, plan.DuplicatePageRequestIds,
                plan.OutOfScopeUrls, mergeRowsAffected);
        }
    }
}
