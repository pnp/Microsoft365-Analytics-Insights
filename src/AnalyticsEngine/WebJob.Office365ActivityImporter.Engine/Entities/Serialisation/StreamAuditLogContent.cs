using Common.Entities;
using Common.Entities.Entities.AuditLog;
using Microsoft.Extensions.Logging;
using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    /// <summary>
    /// Stream-specific event.
    /// https://docs.microsoft.com/en-us/stream/audit-logs#actions-logged-in-stream
    /// </summary>
    public class StreamAuditLogContent : AbstractAuditLogContent
    {
        #region Props
        public string ResourceTitle { get; set; }
        public string ClientApplicationId { get; set; }

        #endregion

        public override async Task<bool> ProcessExtendedProperties(SaveSession session, CommonAuditEvent relatedAuditEvent, ILogger logger)
        {

#if DEBUG
            Console.WriteLine($"\nDEBUG: New Stream event: '{this.Operation}'.");
#endif
            var vidGuid = Common.Entities.Entities.StreamVideo.GetIdFromUrl(this.ObjectId);
            if (vidGuid != Guid.Empty)
            {
                var vid = await session.StreamLookupManager.GetCreateOrUpdateStreamVideo(vidGuid, ResourceTitle);
                var clientApp = await session.SharePointLookupManager.GetClientApp(ClientApplicationId);
                var streamEvent = await session.Database.StreamEvents
                    .Where(e => e.EventID == this.Id)
                    .SingleOrDefaultAsync();
                if (streamEvent == null)
                {
                    streamEvent = new StreamEventMetada { AuditEvent = relatedAuditEvent };
                    session.Database.StreamEvents.Add(streamEvent);
                }

                streamEvent.Video = vid;
                streamEvent.ClientApplication = clientApp;
            }
            return vidGuid != Guid.Empty;
        }
    }
}
