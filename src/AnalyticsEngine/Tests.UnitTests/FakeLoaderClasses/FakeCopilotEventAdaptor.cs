using ActivityImporter.Engine.ActivityAPI.Copilot;
using System;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;

namespace UnitTests.FakeLoaderClasses
{
    public class FakeCopilotMetadataLoader : ICopilotMetadataLoader
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

    public class ReturnNullFilesAndMeetingsAdaptor : ICopilotMetadataLoader
    {
        public Task<MeetingMetadata> GetMeetingInfo(string meetingId, string userGuid)
        {
            return Task.FromResult<MeetingMetadata>(null);
        }
        public Task<SpoDocumentFileInfo> GetSpoFileInfo(string copilotId, string eventUpn)
        {
            return Task.FromResult<SpoDocumentFileInfo>(null);
        }
        public Task<string> GetUserIdFromUpn(string userPrincipalName)
        {
            return Task.FromResult("testId");
        }
    }

    /// <summary>
    /// Metadata loader that throws on every call — used to verify exception-handling paths.
    /// </summary>
    public class ThrowingCopilotMetadataLoader : ICopilotMetadataLoader
    {
        public Task<MeetingMetadata> GetMeetingInfo(string meetingId, string userGuid)
        {
            throw new InvalidOperationException("Simulated meeting info failure");
        }
        public Task<SpoDocumentFileInfo> GetSpoFileInfo(string copilotId, string eventUpn)
        {
            throw new InvalidOperationException("Simulated file info failure");
        }
        public Task<string> GetUserIdFromUpn(string userPrincipalName)
        {
            throw new InvalidOperationException("Simulated user lookup failure");
        }
    }

    /// <summary>
    /// Metadata loader that counts how many times each Graph resolution method is called, so a test can
    /// assert that disabling Copilot resource resolution makes NO Graph calls at all.
    /// </summary>
    public class RecordingCopilotMetadataLoader : ICopilotMetadataLoader
    {
        public int MeetingCalls;
        public int FileCalls;
        public int UserIdCalls;

        public Task<MeetingMetadata> GetMeetingInfo(string meetingId, string userGuid)
        {
            System.Threading.Interlocked.Increment(ref MeetingCalls);
            return Task.FromResult<MeetingMetadata>(null);
        }
        public Task<SpoDocumentFileInfo> GetSpoFileInfo(string copilotId, string eventUpn)
        {
            System.Threading.Interlocked.Increment(ref FileCalls);
            return Task.FromResult<SpoDocumentFileInfo>(null);
        }
        public Task<string> GetUserIdFromUpn(string userPrincipalName)
        {
            System.Threading.Interlocked.Increment(ref UserIdCalls);
            return Task.FromResult("testId");
        }
    }
}
