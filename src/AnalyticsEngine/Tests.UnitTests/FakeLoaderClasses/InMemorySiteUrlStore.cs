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
            return Task.FromResult(Rows.Find(r => string.Equals(r.SiteId, siteId, StringComparison.OrdinalIgnoreCase)));
        }

        public Task SaveSiteUrlAsync(string siteId, string url)
        {
            Writes.Add(Tuple.Create(siteId, url));

            // Re-key an existing row already holding this URL rather than storing it twice. Case-insensitive
            // to match the SQL collation the real adapter's `s.UrlBase == url` runs under.
            var existing = Rows.Find(r => string.Equals(r.SiteUrl, url, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.SiteId = siteId;
                return Task.CompletedTask;
            }

            Rows.Add(new SPSiteIdToUrl { SiteId = siteId, SiteUrl = url });
            return Task.CompletedTask;
        }
    }
}
