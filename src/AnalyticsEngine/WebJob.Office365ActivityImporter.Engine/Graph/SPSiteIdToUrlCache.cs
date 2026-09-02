using Common.Entities;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using System;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Resolve site ID to a URL using Graph API. Will try and use the DB if possible.
    /// </summary>
    public class GraphSPSiteIdToUrlCache : SPSiteIdToUrlCache
    {
        private readonly GraphServiceClient _graphServiceClient;
        public GraphSPSiteIdToUrlCache(GraphServiceClient graphServiceClient, AnalyticsEntitiesContext db, ILogger logger) : base(db, logger)
        {
            _graphServiceClient = graphServiceClient;
        }

        public override async Task<Microsoft.Graph.Models.Site> LoadSite(string id)
        {
            try
            {
                return await _graphServiceClient.Sites[id]
                    .GetAsync(rc => { rc.QueryParameters.Select = new[] { "WebUrl" }; });
            }
            catch (Exception ex)
            {
                base._logger.LogWarning(ex, $"{nameof(GraphSPSiteIdToUrlCache)}: Error loading site URL for {id}: {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>
    /// Resolve site ID to a URL. Will try and use the DB if possible. 
    /// Used because of this: https://admin.microsoft.com/Adminportal/Home?#/servicehealth/:/alerts/SP676147
    /// </summary>
    public abstract class SPSiteIdToUrlCache : ObjectByIdCache<SPSiteIdToUrl>
    {
        private readonly ISiteUrlStore _store;
        protected readonly ILogger _logger;

        public SPSiteIdToUrlCache(AnalyticsEntitiesContext db, ILogger logger)
            : this(new SqlSiteUrlStore(db), logger)
        {
        }

        /// <summary>
        /// As above, with the local cache supplied (issue #375). The context-taking constructor is kept as a
        /// delegating overload so no call site changes.
        /// </summary>
        public SPSiteIdToUrlCache(ISiteUrlStore store, ILogger logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger;
        }

        public abstract Task<Microsoft.Graph.Models.Site> LoadSite(string id);

        public override async Task<SPSiteIdToUrl> Load(string id)
        {
            try
            {
                // Try finding from the database 1st so we go easy on Graph.
                var storedUrl = await _store.TryGetUrlForSiteIdAsync(id);
                if (storedUrl != null)
                {
                    return new SPSiteIdToUrl
                    {
                        SiteId = id,
                        SiteUrl = storedUrl
                    };
                }
                var site = await LoadSite(id);
                _logger.LogInformation($"{nameof(SPSiteIdToUrlCache)}: Loaded site URL for {id}");

                // Cache in DB
                await _store.SaveSiteUrlAsync(id, site.WebUrl);

                return new SPSiteIdToUrl
                {
                    SiteId = id,
                    SiteUrl = site.WebUrl
                };
            }
            catch (ODataError ex) when (ex.ResponseStatusCode == (int)HttpStatusCode.NotFound)
            {
                _logger.LogWarning($"{nameof(SPSiteIdToUrlCache)}: Site with ID '{id}' not found");

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{nameof(SPSiteIdToUrlCache)}: Error loading site URL for {id}: {ex.Message}");
                return null;
            }
        }
    }

    public class SPSiteIdToUrl
    {
        public string SiteId { get; set; }
        public string SiteUrl { get; set; }
    }
}
