using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Web.AnalyticsWeb
{
    /// <summary>
    /// CORS origin matching for the tracker's org URLs, used by the ASP.NET Core CORS policy.
    /// </summary>
    /// <remarks>
    /// The allowed set comes from the <c>org_urls</c> table and is normalised by
    /// <see cref="AllowCorsForOrgUrlsAttribute.NormaliseAllowedOrigins"/>, so the browser-origin rules
    /// (lower-cased, scheme + host + port, no path) are shared with the original implementation rather
    /// than reimplemented here.
    ///
    /// The list is cached for the lifetime of the process exactly as the System.Web version cached it,
    /// so adding an org URL still requires a restart - deliberately unchanged, since altering that
    /// during a framework port would be a behaviour change rather than a port.
    /// </remarks>
    public static class OrgUrlCorsPolicy
    {
        public const string PolicyName = "AllowOrgUrls";

        private static List<string> _cached;
        private static bool _allowAny;
        private static readonly object _gate = new object();

        /// <summary>
        /// True when the request's Origin header matches an org URL, or when the table asks for any origin.
        /// </summary>
        public static bool IsOriginAllowed(string origin)
        {
            EnsureLoaded();

            if (_allowAny) return true;
            if (string.IsNullOrWhiteSpace(origin)) return false;

            // Ordinal comparison against the normalised (lower-cased) set, matching the original.
            return _cached.Contains(origin.ToLowerInvariant(), StringComparer.Ordinal);
        }

        private static void EnsureLoaded()
        {
            if (_cached != null) return;

            lock (_gate)
            {
                if (_cached != null) return;

                List<string> normalised;
                try
                {
                    var provider = new AllowCorsForOrgUrlsAttribute();
                    var policy = provider.GetPolicyAsync(null, PolicyName).GetAwaiter().GetResult();

                    _allowAny = policy.AllowAnyOrigin;
                    normalised = policy.Origins.ToList();
                }
                catch (Exception)
                {
                    // A database that is unreachable at first request must not take the whole site down;
                    // the original returned an empty policy in that situation too. Left uncached so a
                    // later request can succeed once the database is back.
                    _allowAny = false;
                    return;
                }

                _cached = normalised;
            }
        }

        /// <summary>Clears the cache. Test seam only.</summary>
        internal static void Reset()
        {
            lock (_gate)
            {
                _cached = null;
                _allowAny = false;
            }
        }
    }
}
