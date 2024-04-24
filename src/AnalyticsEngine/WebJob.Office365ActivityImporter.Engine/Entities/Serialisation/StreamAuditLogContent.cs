using Common.Entities;
using Common.Entities.Entities.AuditLog;
using System;
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

        public override async Task<bool> ProcessExtendedProperties(SaveSession session, Office365Event relatedAuditEvent)
        {

#if DEBUG
            Console.WriteLine($"\nDEBUG: New Stream event: '{this.Operation}'.");
#endif
            var vidGuid = Common.Entities.Entities.StreamVideo.GetIdFromUrl(this.ObjectId);
            if (vidGuid != Guid.Empty)
            {
                var vid = await session.StreamLookupManager.GetCreateOrUpdateStreamVideo(vidGuid, ResourceTitle);
                var clientApp = await session.SharePointLookupManager.GetClientApp(ClientApplicationId);
                var newEvent = new StreamEventMetada { Video = vid, Event = relatedAuditEvent, ClientApplication = clientApp };

                session.Database.StreamEvents.Add(newEvent);
            }
            return vidGuid != Guid.Empty;
        }
    }
}
