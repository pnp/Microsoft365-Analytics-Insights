using Microsoft.Graph;
using System;
using System.IO;
using System.Web;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot
{
    public class SpoDocumentFileInfo
    {
        public SpoDocumentFileInfo()
        {
        }

        public SpoDocumentFileInfo(BaseItem spItem, Site site)
        {
            if (spItem == null)
            {
                throw new ArgumentNullException(nameof(spItem));
            }

            this.SiteUrl = site?.WebUrl ?? throw new ArgumentNullException(nameof(site));
            this.Url = spItem.WebUrl ?? throw new ArgumentNullException(nameof(spItem));
            this.Filename = HttpUtility.UrlDecode(Path.GetFileName(spItem.WebUrl)) ?? throw new ArgumentNullException(nameof(spItem));
            this.Extension = Path.GetExtension(spItem.WebUrl)?.Replace(".", "") ?? throw new ArgumentOutOfRangeException(nameof(spItem), "No extension found");
        }

        public string Url { get; set; } = null;
        public string SiteUrl { get; set; } = null;
        public string Filename { get; set; } = null;
        public string Extension { get; set; } = null;
    }

    public class MeetingMetadata
    {
        public MeetingMetadata()
        {
        }

        public MeetingMetadata(OnlineMeeting meeting)
        {
            if (meeting == null)
            {
                throw new ArgumentNullException(nameof(meeting));
            }

            this.MeetingId = meeting.Id ?? throw new ArgumentNullException(nameof(meeting));
            this.CreatedUTC = meeting.CreationDateTime.HasValue ? meeting.CreationDateTime.Value.UtcDateTime : throw new ArgumentNullException(nameof(meeting.CreationDateTime));
            this.Subject = meeting.Subject ?? throw new ArgumentNullException(nameof(meeting.Subject));
        }

        public string MeetingId { get; set; } = null;
        public string Subject { get; set; } = null;
        public DateTime CreatedUTC { get; set; } = DateTime.MinValue;
    }
}
