using ActivityImporter.Engine.ActivityAPI.Copilot;
using System;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;

namespace UnitTests.FakeLoaderClasses
{
    public class FakeCopilotEventAdaptor : ICopilotMetadataLoader
    {
        public Task<MeetingMetadata> GetMeetingInfo(string meetingId, string userGuid)
        {
            return Task.FromResult(new MeetingMetadata
            {
                MeetingId = "test",
                CreatedUTC = DateTime.UtcNow,
                Subject = "unit test meeting"
            });
        }

        public Task<SpoDocumentFileInfo> GetSpoFileInfo(string copilotId, string eventUpn)
        {
#pragma warning disable CS8619 // Nullability of reference types in value doesn't match target type.
            return Task.FromResult(new SpoDocumentFileInfo
            {
                Extension = "docx",
                Filename = "test",
                Url = "https://test.sharepoint.com/sites/test/Shared%20Documents/General/test.docx",
                SiteUrl = "https://test.sharepoint.com/sites/test"
            });
#pragma warning restore CS8619 // Nullability of reference types in value doesn't match target type.
        }

        public Task<string> GetUserIdFromUpn(string userPrincipalName)
        {
            return Task.FromResult("testId");
        }
    }
}
