using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory <see cref="ISiteUrlStore"/> - the local site-id/URL cache table replaced by a dictionary,
    /// so the lookup precedence and write-back can be asserted with no SQL Server. See issue #375.
    ///
    /// Mirrors the SQL adapter's write rule: a stored entry already holding the URL is re-keyed to the new
    /// site id rather than duplicated.
    /// </summary>
    public class InMemorySiteUrlStore : ISiteUrlStore
    {
        /// <summary>Site id -> URL, in insertion order.</summary>
        public Dictionary<string, string> UrlBySiteId { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every site id looked up, in order.</summary>
        public List<string> Reads { get; } = new List<string>();

        /// <summary>Every (siteId, url) pair written, in order.</summary>
        public List<Tuple<string, string>> Writes { get; } = new List<Tuple<string, string>>();

        public InMemorySiteUrlStore Seed(string siteId, string url)
        {
            UrlBySiteId[siteId] = url;
            return this;
        }

        public Task<string> TryGetUrlForSiteIdAsync(string siteId)
        {
            Reads.Add(siteId);
            return Task.FromResult(UrlBySiteId.TryGetValue(siteId, out var url) ? url : null);
        }

        public Task SaveSiteUrlAsync(string siteId, string url)
        {
            Writes.Add(Tuple.Create(siteId, url));

            // Re-key an existing entry for the same URL rather than storing it twice.
            foreach (var existing in new List<string>(UrlBySiteId.Keys))
            {
                if (string.Equals(UrlBySiteId[existing], url, StringComparison.Ordinal))
                {
                    UrlBySiteId.Remove(existing);
                    break;
                }
            }

            UrlBySiteId[siteId] = url;
            return Task.CompletedTask;
        }
    }
}
