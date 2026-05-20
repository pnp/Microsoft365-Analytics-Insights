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
            // This filter is the SharePoint org-URL whitelist; it only applies to SharePoint/OneDrive
            // events that carry a URL in ObjectId. Non-SharePoint workloads (Power BI, Power Automate,
            // Copilot, Exchange, Azure AD, ...) store report names / flow IDs / GUIDs in ObjectId, so
            // running them through the URL matcher would always drop them. Their own filters live
            // alongside the workload dispatch logic.
            if (!(content is Entities.Serialisation.SharePointAuditLogContent spContent))
            {
                return true;
            }

            // SharePoint event without a URL ("ManagedSyncClientAllowed" for example). Assume we want it.
            if (string.IsNullOrEmpty(spContent.ObjectId))
            {
                return true;
            }

            // Analyse all org URLs to see which one matches this hit.
            return OrgUrlConfigs.UrlInScope(spContent.SiteUrl, spContent.ObjectId);
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
