using Common.Entities;
using DataUtils;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
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



        public override async Task<bool> ProcessExtendedProperties(SaveSession saveBatch, CommonAuditEvent relatedAuditEvent, ILogger logger)
        {
            // Is there a site/web for this event (SP file events only)?
            if (!string.IsNullOrEmpty(this.SiteUrl))
            {
                await AssignWeb(saveBatch, this.SiteUrl, true, logger);     // The web's own address: that web, created if new
                return true;
            }
            else
            {
                if (StringUtils.IsValidAbsoluteUrl(StringUtils.ConvertSharePointUrl(this.ObjectId)))
                {
                    await AssignWeb(saveBatch, this.ObjectId, false, logger);   // An item's address: the known web it is in, if any. Nothing is created
                    return true;
                }
                else
                    return false;
            }
        }

        async Task AssignWeb(SaveSession saveBatch, string url, bool createIfNotFound, ILogger logger)
        {
            try
            {
                var spEvent = saveBatch.CachedSpEvents[this.Id];

                // Assign web to event
                spEvent.related_web = await saveBatch.SharePointLookupManager.GetWebOrCreateWebPlusSite(url, createIfNotFound);
            }
            catch (KeyNotFoundException)
            {
                logger.LogWarning($"Could not find SP event for audit-log content ID {this.Id} in cached SP events when assigning web for URL {url}");
            }
        }
    }
}
