using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace WebJob.AppInsightsImporter.Engine.Sql
{
    internal sealed class SearchPageAdmission
    {
        // Do not lock configuration while the importer stages rows. Right before the fact merge,
        // compare the rules that authorized this batch and hold the small configuration table until
        // commit (also when it was empty). A concurrent policy change must cause a fresh decision.
        internal const string VerifyCurrentRulesSql = @"
IF EXISTS (
    SELECT 1 FROM (
        SELECT url_base, exact_match, 1 AS delta FROM dbo.org_urls WITH (TABLOCK, HOLDLOCK)
        UNION ALL SELECT url_base, exact_match, -1 FROM #appinsights_search_site_rules
    ) AS rules
    GROUP BY CONVERT(varbinary(max), url_base), exact_match HAVING SUM(delta) <> 0
) THROW 51000, 'Site filters changed while saving searches; retry the batch under the current rules.', 1;
";

        internal const string ReadHitsSql = @"
SELECT h.page_request_id, s.ai_session_id, u.user_name, s.user_id, p.full_url, site.url_base
FROM OPENJSON(@requests) WITH (request_id uniqueidentifier '$') AS r
JOIN dbo.hits AS h ON h.page_request_id = r.request_id
LEFT JOIN dbo.sessions AS s ON s.id = h.session_id
LEFT JOIN dbo.users AS u ON u.id = s.user_id
LEFT JOIN dbo.urls AS p ON p.id = h.url_id
LEFT JOIN dbo.webs AS w ON w.id = h.web_id
LEFT JOIN dbo.sites AS site ON site.id = w.site_id;";

        private readonly Dictionary<Guid, (string Session, string User, int UserId)?> _hits =
            new Dictionary<Guid, (string, string, int)?>();
        private bool _denyAll;

        // Only a confirmed empty configuration is unrestricted. In particular, ignoring a malformed
        // row must never turn a non-empty allowlist into permission to use just the browser session.
        internal static async Task<SearchPageAdmission> ReadAsync(SqlConnection connection, List<SearchEventAppInsightsQueryResult> searches,
            int timeout, CancellationToken token)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandTimeout = timeout;
                cmd.CommandText = @"SELECT url_base, exact_match INTO #appinsights_search_site_rules FROM dbo.org_urls;
SELECT url_base, exact_match FROM #appinsights_search_site_rules;";
                var rules = new List<(Uri Url, bool Exact)>();
                bool restricted = false;
                using (var r = await cmd.ExecuteReaderAsync(token))
                {
                    while (await r.ReadAsync(token))
                    {
                        restricted = true;
                        var value = r.IsDBNull(0) ? null : r.GetString(0);
                        var url = HttpUrl(value);
                        // Do not let URI normalization broaden a malformed configured prefix.
                        if (url != null && !value.Split('/').Any(p => Uri.UnescapeDataString(p) == ".." || Uri.UnescapeDataString(p) == "."))
                            rules.Add((url, !r.IsDBNull(1) && r.GetBoolean(1)));
                    }
                }
                if (!restricted) return null;

                var admission = new SearchPageAdmission();
                if (rules.Count == 0)
                {
                    admission._denyAll = true;
                    return admission;
                }

                var requests = searches.Select(e => e.CustomProperties.PageRequestId.GetValueOrDefault()).Where(id => id != Guid.Empty).Distinct().ToArray();
                if (requests.Length == 0) return admission;
                cmd.CommandText = ReadHitsSql;
                cmd.Parameters.Add("@requests", SqlDbType.NVarChar, -1).Value = JsonConvert.SerializeObject(requests);
                using (var r = await cmd.ExecuteReaderAsync(token))
                {
                    while (await r.ReadAsync(token))
                    {
                        // The request id is unique. Even a rejected or unattributable hit is a
                        // confirmed result: waiting for another page with that id cannot help.
                        var id = r.GetGuid(0);
                        admission._hits[id] = null;
                        if (r.IsDBNull(1) || r.IsDBNull(2) || r.IsDBNull(3) || r.IsDBNull(4)) continue;
                        var page = HttpUrl(r.GetString(4));
                        var site = r.IsDBNull(5) ? null : HttpUrl(r.GetString(5));
                        if (page != null && (r.IsDBNull(5) || (site != null && WithinSite(page, site)))
                            && rules.Any(rule => Allows(rule, page, site)))
                            admission._hits[id] = (r.GetString(1), r.GetString(2), r.GetInt32(3));
                    }
                }
                return admission;
            }
        }

        // Match the configured literal URL prefix, never a SQL LIKE pattern. Check the authority
        // separately so a host-name prefix cannot cross origins. Exact-site rules need the stored
        // site collection as well: the general page-view filter's fallback to a URL prefix when
        // site metadata is missing cannot verify historical hits under a narrower configuration.
        private static bool Allows((Uri Url, bool Exact) rule, Uri page, Uri site)
        {
            if (!SameOrigin(rule.Url, page)) return false;
            return rule.Exact
                ? site != null && string.Equals(rule.Url.AbsolutePath.TrimEnd('/'), site.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                : page.AbsolutePath.StartsWith(rule.Url.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        }

        private static bool WithinSite(Uri page, Uri site)
        {
            var path = site.AbsolutePath.TrimEnd('/');
            return SameOrigin(page, site) && (string.Equals(page.AbsolutePath, path, StringComparison.OrdinalIgnoreCase)
                || page.AbsolutePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));
        }

        private static bool SameOrigin(Uri first, Uri second) =>
            first.Scheme == second.Scheme && first.Port == second.Port && string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase);

        private static Uri HttpUrl(string value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && !string.IsNullOrEmpty(uri.Host) && uri.IsWellFormedOriginalString()
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? uri : null;

        internal int? Resolve(SearchEventAppInsightsQueryResult search, ISet<Guid> rejectedRequests, out bool pending)
        {
            pending = false;
            var id = search.CustomProperties.PageRequestId.GetValueOrDefault();
            if (_denyAll || id == Guid.Empty) return null;
            if (!_hits.TryGetValue(id, out var owner))
            {
                pending = rejectedRequests?.Contains(id) != true;
                return null;
            }
            return owner.HasValue && string.Equals(owner.Value.Session, search.CustomProperties.SessionId, StringComparison.Ordinal)
                && string.Equals(owner.Value.User, search.Username, StringComparison.OrdinalIgnoreCase) ? owner.Value.UserId : (int?)null;
        }
    }
}
