using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI
{
    public abstract class AuditFilterConfig
    {
        public virtual bool InScope(AbstractAuditLogContent content) => true;
    }

    public class AllowAllFilterConfig : AuditFilterConfig
    {
    }

    public class SharePointOrgUrlsFilterConfig : AuditFilterConfig
    {
        public override bool InScope(AbstractAuditLogContent content)
        {
            // Only include SharePoint events that are in the URLs we're interested in
            string url = content.ObjectId;

            // Do we have a URL?
            if (!string.IsNullOrEmpty(url))
            {
                var siteFilter = string.Empty;
                if (content is Entities.Serialisation.SharePointAuditLogContent)
                {
                    // Find an org URL that exactly matches the sharepoint event
                    var spContent = content as Entities.Serialisation.SharePointAuditLogContent;
                    siteFilter = spContent.SiteUrl;
                }

                // Analyse all org URLs to see which one matches this hit
                return OrgUrlConfigs.UrlInScope(siteFilter, url);
            }

            // Something happened in SharePoint/OneDrive without a URL ("ManagedSyncClientAllowed" for example). Assume we want it
            return true;
        }

        public List<FilterUrlConfig> OrgUrlConfigs { get; set; } = new List<FilterUrlConfig>();
        public static async Task<SharePointOrgUrlsFilterConfig> Load(AnalyticsEntitiesContext db)
        {
            var orgUrlConfigs = await SiteFilterLoader.Load(db);

            return new SharePointOrgUrlsFilterConfig
            {
                OrgUrlConfigs = orgUrlConfigs
            };
        }

        public void Print(ILogger telemetry)
        {
            foreach (var url in this.OrgUrlConfigs)
            {
                if (url.ExactSiteMatch)
                {
                    telemetry.LogInformation($"+{url.Url} (exact match)");
                }
                else
                {
                    telemetry.LogInformation($"+{url.Url} (*)");
                }
            }
        }
    }
}
