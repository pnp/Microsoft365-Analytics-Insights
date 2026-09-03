using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory <see cref="ISiteUrlStore"/> - the local site-id/URL cache table replaced by a list, so the
    /// lookup precedence and write-back can be asserted with no SQL Server. See issue #375.
    ///
    /// Deliberately mirrors two things about the SQL adapter: a row is a hit even when its URL is null, and
    /// URL matching on the write path is CASE-INSENSITIVE (SQL Server's default collation), so a
    /// case-differing URL is re-keyed rather than duplicated.
    /// </summary>
    public class InMemorySiteUrlStore : ISiteUrlStore
    {
        /// <summary>The stored rows, in insertion order.</summary>
        public List<SPSiteIdToUrl> Rows { get; } = new List<SPSiteIdToUrl>();

        /// <summary>Every site id looked up, in order.</summary>
        public List<string> Reads { get; } = new List<string>();

        /// <summary>Every (siteId, url) pair written, in order.</summary>
        public List<Tuple<string, string>> Writes { get; } = new List<Tuple<string, string>>();

        public InMemorySiteUrlStore Seed(string siteId, string url)
        {
            Rows.Add(new SPSiteIdToUrl { SiteId = siteId, SiteUrl = url });
            return this;
        }

        public Task<SPSiteIdToUrl> TryGetForSiteIdAsync(string siteId)
        {
            Reads.Add(siteId);
            return Task.FromResult(Single(Rows.FindAll(r => string.Equals(r.SiteId, siteId, StringComparison.OrdinalIgnoreCase)), "site id", siteId));
        }

        public Task SaveSiteUrlAsync(string siteId, string url)
        {
            Writes.Add(Tuple.Create(siteId, url));

            // A NULL url matches no row: the production context sets UseDatabaseNullSemantics = true, so
            // `s.UrlBase == url` with a null parameter becomes `UrlBase = NULL`, which is never true in SQL
            // and therefore always takes the insert path. C# equality would have re-keyed a null-URL row.
            if (url != null)
            {
                // Re-key an existing row already holding this URL rather than storing it twice.
                // Case-insensitive to match the SQL collation the real adapter's `s.UrlBase == url` runs under.
                var existing = Single(Rows.FindAll(r => string.Equals(r.SiteUrl, url, StringComparison.OrdinalIgnoreCase)), "url", url);
                if (existing != null)
                {
                    existing.SiteId = siteId;
                    return Task.CompletedTask;
                }
            }

            Rows.Add(new SPSiteIdToUrl { SiteId = siteId, SiteUrl = url });
            return Task.CompletedTask;
        }

        /// <summary>
        /// Mirrors <c>SingleOrDefaultAsync</c>: the production adapter THROWS when a lookup matches more
        /// than one row, and duplicate site ids are the exact failure state this cache guards against
        /// (<c>site_id</c> is not unique). A fake that quietly returned the first match would hide it.
        /// </summary>
        private static SPSiteIdToUrl Single(List<SPSiteIdToUrl> matches, string by, string value)
        {
            if (matches.Count > 1)
            {
                throw new InvalidOperationException($"Sequence contains more than one element: {matches.Count} rows match {by} '{value}'.");
            }
            return matches.Count == 1 ? matches[0] : null;
        }
    }
}
