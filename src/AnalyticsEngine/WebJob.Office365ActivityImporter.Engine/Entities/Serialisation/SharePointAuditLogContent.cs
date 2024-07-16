using Common.Entities;
using DataUtils;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    public class SharePointAuditLogContent : AbstractAuditLogContent
    {
        public string SiteUrl { get; set; }

        public string SourceFileName { get; set; }

        public string SourceFileExtension { get; set; }

        public string EventData { get; set; }



        public override async Task<bool> ProcessExtendedProperties(SaveSession saveBatch, Office365Event relatedAuditEvent)
        {
            // Is there a site/web for this event (SP file events only)?
            if (!string.IsNullOrEmpty(this.SiteUrl))
            {
                await AssignWeb(saveBatch, this.SiteUrl, true);     // Create site if not existing already
                return true;
            }
            else
            {
                if (StringUtils.IsValidAbsoluteUrl(StringUtils.ConvertSharePointUrl(this.ObjectId)))
                {
                    await AssignWeb(saveBatch, this.ObjectId, false);   // Do not create site if we can't find already
                    return true;
                }
                else
                    return false;
            }
        }

        async Task AssignWeb(SaveSession saveBatch, string url, bool createIfNotFound)
        {
            var spEvent = saveBatch.CachedSpEvents[this.Id];

            // Assign web to event
            spEvent.related_web = await saveBatch.SharePointLookupManager.GetWebOrCreateWebPlusSite(url, createIfNotFound);
        }
    }
}
