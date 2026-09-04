using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Cors;
using System.Web.Http.Cors;

namespace Web.AnalyticsWeb
{
    /// <summary>
    /// Allow CORs for urls in the org_urls table
    /// </summary>
    public class AllowCorsForOrgUrlsAttribute : Attribute, ICorsPolicyProvider
    {
        private readonly Func<Task<List<string>>> _loadUrlBases;
        private List<string> _allowedCors = null;

        public AllowCorsForOrgUrlsAttribute()
            : this(LoadOrgUrlBasesAsync)
        {
        }

        internal AllowCorsForOrgUrlsAttribute(Func<Task<List<string>>> loadUrlBases)
        {
            _loadUrlBases = loadUrlBases ?? throw new ArgumentNullException(nameof(loadUrlBases));
        }

        public async Task<CorsPolicy> GetCorsPolicyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_allowedCors == null)
            {
                _allowedCors = NormaliseAllowedOrigins(await _loadUrlBases());
            }

            var retval = new CorsPolicy();
            retval.AllowAnyHeader = true;
            retval.AllowAnyMethod = true;
            retval.AllowAnyOrigin = false;

            foreach (var url in _allowedCors)
            {
                if (url == "*" || url == "https://")
                {
                    retval.AllowAnyOrigin = true;
                    break;
                }
                retval.Origins.Add(url);
            }

            return retval;
        }

        private static async Task<List<string>> LoadOrgUrlBasesAsync()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                return await db.org_urls
                    .Select(each => each.UrlBase)
                    .ToListAsync();
            }
        }

        internal static List<string> NormaliseAllowedOrigins(IEnumerable<string> urlBases)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            AnalyticsLogger logger = null;

            foreach (var urlBase in urlBases ?? new string[0])
            {
                if (TryNormaliseAllowedOrigin(urlBase, out var origin, out var reason))
                {
                    allowed.Add(origin);
                }
                else if (!string.IsNullOrWhiteSpace(urlBase))
                {
                    LogInvalidOriginWarning(ref logger, reason);
                }
            }

            return new List<string>(allowed);
        }

        private static void LogInvalidOriginWarning(ref AnalyticsLogger logger, string reason)
        {
            try
            {
                if (logger == null)
                {
                    var config = new AppConfig();
                    logger = new AnalyticsLogger(config.AppInsightsConnectionString, nameof(AllowCorsForOrgUrlsAttribute));
                }
                logger.LogWarning($"Ignoring invalid org_urls.url_base row for CORS: {reason}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"WARNING: Ignoring invalid org_urls.url_base row for CORS: {reason}. Logging failed ({ex.GetBaseException().GetType().Name}).");
            }
        }

        internal static bool TryNormaliseAllowedOrigin(string urlBase, out string origin, out string reason)
        {
            origin = null;
            reason = null;

            if (string.IsNullOrWhiteSpace(urlBase))
            {
                return false;
            }

            var candidate = urlBase.Trim();
            if (candidate == "*" || string.Equals(candidate, "https://", StringComparison.OrdinalIgnoreCase))
            {
                origin = candidate.ToLowerInvariant();
                return true;
            }

            if (!candidate.Contains("://"))
            {
                candidate = "https://" + candidate;
            }

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || string.IsNullOrEmpty(uri.Host)
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                reason = "it is not a valid absolute origin";
                return false;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                reason = "only HTTP and HTTPS origins are supported";
                return false;
            }

            origin = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
            return true;
        }

    }

    public class AllowCorsForOrgUrlsFactory : ICorsPolicyProviderFactory
    {
        ICorsPolicyProvider _provider;
        public AllowCorsForOrgUrlsFactory()
        {
            _provider = new AllowCorsForOrgUrlsAttribute();
        }
        public ICorsPolicyProvider GetCorsPolicyProvider(HttpRequestMessage request)
        {
            return _provider;
        }
    }
}
