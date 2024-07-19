using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using System.Web.Cors;
using System.Web.Http.Cors;
using Common.Entities;
using System.Data.Entity;
using System.Collections.Generic;

namespace Web.AnalyticsWeb
{
    /// <summary>
    /// Allow CORs for urls in the org_urls table
    /// </summary>
    public class AllowCorsForOrgUrlsAttribute : Attribute, ICorsPolicyProvider
    {
        private List<string> _allowedCors = null;
        public async Task<CorsPolicy> GetCorsPolicyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_allowedCors == null)
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    _allowedCors = new List<string>();
                    var origins = await db.org_urls.ToListAsync();
                    foreach (var each in origins)
                    {
                        _allowedCors.Add(each.UrlBase);
                    }
                }
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
