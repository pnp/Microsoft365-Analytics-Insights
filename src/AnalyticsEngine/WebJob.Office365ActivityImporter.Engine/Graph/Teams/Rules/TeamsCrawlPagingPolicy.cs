namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// How deep the Teams crawl is allowed to page, and when it must stop. Previously four separate
    /// private consts spread across <see cref="TeamsFinder"/>, <c>O365Team</c> and
    /// <see cref="ChannelMessagesLoader"/>; collected here so the crawl's depth limits are stated in
    /// one place and the stop condition can be unit tested. See issue #377.
    ///
    /// The values are unchanged from the ones the importer shipped with - this is a move, not a
    /// re-tuning. They exist so a misbehaving <c>nextLink</c> cannot loop forever or fill memory at
    /// 200k-user scale; each call site logs a warning and returns a partial set when it trips one.
    /// </summary>
    public static class TeamsCrawlPagingPolicy
    {
        /// <summary>Maximum groups walked when searching the tenant for groups with a Team.</summary>
        public const int MaxGroups = 500_000;

        /// <summary>Maximum members read for a single group/Team.</summary>
        public const int MaxTeamMembers = 200_000;

        /// <summary>Maximum root messages read from one channel in a single delta window.</summary>
        public const int MaxMessagesPerChannel = 100_000;

        /// <summary>Maximum replies read for a single root message.</summary>
        public const int MaxRepliesPerMessage = 10_000;

        /// <summary>
        /// Whether the page iterator should keep going. Called after the item has been counted, so
        /// <paramref name="loadedSoFar"/> includes the item just added: at the cap we stop, which is
        /// what keeps the retained set exactly <paramref name="max"/> items.
        /// </summary>
        public static bool ShouldContinuePaging(int loadedSoFar, int max)
        {
            return loadedSoFar < max;
        }
    }
}
