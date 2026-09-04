using DataUtils;
using System;
using System.Collections.Generic;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace WebJob.AppInsightsImporter.Engine.PageUpdates.Rules
{
    /// <summary>
    /// Groups a chunk of page-update events by the URL they belong to, which is the key the rest of the
    /// page-update save works from.
    ///
    /// Extracted from <c>PageUpdateManager.SaveChunk</c> (issue #369). The grouping is deliberately built
    /// ONCE per chunk: the code this replaced re-ran <c>chunk.Where(...)</c> - re-invoking
    /// <see cref="StringUtils.GetUrlBaseAddressIfValidUrl"/> on every event - once per matched URL, which is
    /// O(events x urls) and the dominant cost of a large chunk on a busy tenant.
    /// </summary>
    public static class PageUpdateGroupingRules
    {
        /// <summary>
        /// Bucket events by their URL's base address (scheme + host + path, query and fragment discarded).
        ///
        /// Matching is case-insensitive because SharePoint URLs differ only in casing all the time and the
        /// SQL collation is case-insensitive too; treating them as distinct would create duplicate URL rows.
        /// Events whose URL is null, empty, or resolves to an empty base address are dropped without being
        /// counted - they have nothing to attach metadata to.
        /// </summary>
        public static Dictionary<string, List<PageUpdateEventAppInsightsQueryResult>> GroupByUrl(
            IEnumerable<PageUpdateEventAppInsightsQueryResult> chunk)
        {
            var chunkByUrl = new Dictionary<string, List<PageUpdateEventAppInsightsQueryResult>>(StringComparer.OrdinalIgnoreCase);
            if (chunk == null)
            {
                return chunkByUrl;
            }

            foreach (var e in chunk)
            {
                var key = StringUtils.GetUrlBaseAddressIfValidUrl(e.CustomProperties?.Url);
                if (string.IsNullOrEmpty(key))
                    continue;
                if (!chunkByUrl.TryGetValue(key, out var list))
                {
                    list = new List<PageUpdateEventAppInsightsQueryResult>();
                    chunkByUrl[key] = list;
                }
                list.Add(e);
            }

            return chunkByUrl;
        }
    }
}
