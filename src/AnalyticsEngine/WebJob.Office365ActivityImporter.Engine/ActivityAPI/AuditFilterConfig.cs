using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
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
            // A DLP.All record detected in SharePoint/OneDrive names the file in ObjectId, so it gets the same
            // whitelist: a policy match on an excluded site isn't imported either. Checked separately because
            // the rules differ - see DlpRecordInScope.
            if (content is Entities.Serialisation.DlpAuditLogContent dlpContent)
            {
                return DlpRecordInScope(dlpContent);
            }

            // This filter is the SharePoint org-URL whitelist; it only applies to SharePoint/OneDrive
            // events that carry a URL in ObjectId. Non-SharePoint workloads (Power BI, Power Automate,
            // Copilot, Exchange, Azure AD, ...) store report names / flow IDs / GUIDs in ObjectId, so
            // running them through the URL matcher would always drop them. Their own filters live
            // alongside the workload dispatch logic.
            if (!(content is Entities.Serialisation.SharePointAuditLogContent spContent))
            {
                return true;
            }

            // SharePoint event without a URL (e.g. ManagedSyncClientAllowed). We only persist SP
            // events whose URL falls inside the configured org-URL whitelist, so a SharePoint event
            // without a URL has nothing to match against and is treated as out of scope.
            if (string.IsNullOrEmpty(spContent.ObjectId))
            {
                return false;
            }

            if (OrgUrlConfigs.Count == 0)
            {
                return false;
            }

            // Analyse all org URLs to see which one matches this hit.
            return OrgUrlConfigs.UrlInScope(spContent.SiteUrl, spContent.ObjectId);
        }

        /// <summary>
        /// Org-URL scope for a DLP.All record. Only SharePoint/OneDrive detections are tested (Exchange and Endpoint
        /// DLP have no site), and only when they carry a URL and a whitelist is configured. Unlike a SharePoint
        /// activity event, a DLP record is kept when there is nothing to test: the policy match is what the DLP
        /// report counts, no site detail is stored for it, and a DLP-only tenant has no org URLs at all.
        /// </summary>
        /// <remarks>
        /// The record names its site COLLECTION (SharePointMetaData.SiteCollectionUrl), never the subsite. So an exact
        /// rule for the site collection is applied at that level - a document in one of its subsites can't be told
        /// apart from one in its root site - and an exact rule for a subsite, below it, is matched on the document's URL
        /// instead. Without the site collection, an exact rule can't match at all: <c>UrlInScope</c> would otherwise
        /// quietly treat it as starts-with, admitting every site whose URL begins with the rule's.
        /// </remarks>
        private bool DlpRecordInScope(Entities.Serialisation.DlpAuditLogContent dlpContent)
        {
            var detectedInSharePoint =
                string.Equals(dlpContent.Workload, ActivityImportConstants.WORKLOAD_SP, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(dlpContent.Workload, ActivityImportConstants.WORKLOAD_OD, System.StringComparison.OrdinalIgnoreCase);
            if (!detectedInSharePoint || OrgUrlConfigs.Count == 0
                || string.IsNullOrEmpty(dlpContent.ObjectId)
                || !StringUtils.IsValidAbsoluteUrl(StringUtils.ConvertSharePointUrl(dlpContent.ObjectId)))
            {
                return true;
            }

            var siteCollectionUrl = (dlpContent as Dlp.DlpAuditLogContentWithSharePointMetadata)?.SharePointMetaData?.SiteCollectionUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(siteCollectionUrl))
            {
                var startsWithRules = OrgUrlConfigs.Where(rule => !rule.ExactSiteMatch).ToList();

                // UrlInScope treats an empty rule list as "everything", so an all-exact whitelist must be checked here.
                return startsWithRules.Count > 0 && startsWithRules.UrlInScope(null, dlpContent.ObjectId);
            }

            return OrgUrlConfigs.UrlInScope(siteCollectionUrl, dlpContent.ObjectId)
                || OrgUrlConfigs.Any(rule => ExactSubsiteRuleMatches(rule, siteCollectionUrl, dlpContent.ObjectId));
        }

        /// <summary>
        /// An exact rule for a subsite of the record's site collection matches a document in that subsite, by its URL:
        /// <c>…/sites/hr/benefits</c> admits <c>…/sites/hr/benefits/Shared Documents/a.docx</c> but not
        /// <c>…/sites/hr/benefits-archive/…</c>. Case-insensitive, and a trailing slash on either URL is ignored.
        /// </summary>
        private static bool ExactSubsiteRuleMatches(FilterUrlConfig rule, string siteCollectionUrl, string documentUrl)
        {
            if (!rule.ExactSiteMatch || string.IsNullOrEmpty(rule.Url))
            {
                return false;
            }

            var subsiteUrl = rule.Url.TrimEnd('/');
            return subsiteUrl.StartsWith(siteCollectionUrl + "/", System.StringComparison.OrdinalIgnoreCase)
                && documentUrl.StartsWith(subsiteUrl + "/", System.StringComparison.OrdinalIgnoreCase);
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

        public void Print(ILogger logger)
        {
            foreach (var url in this.OrgUrlConfigs)
            {
                if (url.ExactSiteMatch)
                {
                    logger.LogInformation($"+{url.Url} (exact match)");
                }
                else
                {
                    logger.LogInformation($"+{url.Url} (*)");
                }
            }
        }
    }
}
