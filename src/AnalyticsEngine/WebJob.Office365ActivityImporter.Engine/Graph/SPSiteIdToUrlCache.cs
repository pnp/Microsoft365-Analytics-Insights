using Common.Entities;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Resolve site ID to a URL using Graph API. Will try and use the DB if possible.
    /// </summary>
    public class GraphSPSiteIdToUrlCache : SPSiteIdToUrlCache
    {
        private readonly GraphServiceClient _graphServiceClient;
        public GraphSPSiteIdToUrlCache(GraphServiceClient graphServiceClient, AnalyticsEntitiesContext db, ILogger debugTracer) : base(db, debugTracer)
        {
            _graphServiceClient = graphServiceClient;
        }

        public override async Task<Microsoft.Graph.Site> LoadSite(string id)
        {
            return await _graphServiceClient.Sites[id].Request().Select("WebUrl").GetAsync();
        }
    }

    /// <summary>
    /// Resolve site ID to a URL. Will try and use the DB if possible. 
    /// Used because of this: https://admin.microsoft.com/Adminportal/Home?#/servicehealth/:/alerts/SP676147
    /// </summary>
    public abstract class SPSiteIdToUrlCache : ObjectByIdCache<SPSiteIdToUrl>
    {
        private readonly AnalyticsEntitiesContext _db;
        private readonly ILogger _debugTracer;

        public SPSiteIdToUrlCache(AnalyticsEntitiesContext db, ILogger debugTracer)
        {
            _db = db;
            _debugTracer = debugTracer;
        }

        public abstract Task<Microsoft.Graph.Site> LoadSite(string id);

        public override async Task<SPSiteIdToUrl> Load(string id)
        {
            try
            {
                // Try finding from the database 1st so we go easy on Graph
                var dbRecordBySiteId = await _db.sites.Where(s => s.SiteId.ToLower() == id.ToLower()).SingleOrDefaultAsync();
                if (dbRecordBySiteId != null)
                {
                    return new SPSiteIdToUrl
                    {
                        SiteId = dbRecordBySiteId.SiteId,
                        SiteUrl = dbRecordBySiteId.UrlBase
                    };
                }
                var site = await LoadSite(id);
                _debugTracer.LogInformation($"{nameof(SPSiteIdToUrlCache)}: Loaded site URL for {id}");

                // Cache in DB
                var dbRecordBySiteUrl = await _db.sites.Where(s => s.UrlBase.ToLower() == site.WebUrl.ToLower()).SingleOrDefaultAsync();
                if (dbRecordBySiteUrl != null)
                {
                    dbRecordBySiteUrl.SiteId = id;
                }
                else
                {
                    dbRecordBySiteId = new Common.Entities.Site
                    {
                        SiteId = id,
                        UrlBase = site.WebUrl
                    };
                    _db.sites.Add(dbRecordBySiteId);
                }
                await _db.SaveChangesAsync();

                return new SPSiteIdToUrl
                {
                    SiteId = id,
                    SiteUrl = site.WebUrl
                };
            }
            catch (ServiceException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _debugTracer.LogWarning($"{nameof(SPSiteIdToUrlCache)}: Site with ID '{id}' not found");

                return null;
            }
            catch (Exception ex)
            {
                _debugTracer.LogError(ex, $"{nameof(SPSiteIdToUrlCache)}: Error loading site URL for {id}: {ex.Message}");
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
