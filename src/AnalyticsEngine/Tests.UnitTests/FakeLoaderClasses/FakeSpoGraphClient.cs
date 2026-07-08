using ActivityImporter.Engine.ActivityAPI.Copilot;
using Microsoft.Graph.Models;
using System;
using System.Threading.Tasks;

namespace Tests.UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory fake of <see cref="ISpoGraphClient"/> so the resolution logic in GraphFileMetadataLoader can
    /// be unit tested without a live Graph client. Each call is counted, and each response is overridable per test.
    /// </summary>
    public class FakeSpoGraphClient : ISpoGraphClient
    {
        public int GetOnlineMeetingCalls { get; private set; }
        public int GetUserDriveCalls { get; private set; }
        public int GetSiteDriveCalls { get; private set; }
        public int GetSiteCalls { get; private set; }
        public int GetListItemByIdCalls { get; private set; }
        public int FindListItemByWebUrlCalls { get; private set; }
        public int GetUserCalls { get; private set; }

        public int TotalCalls => GetOnlineMeetingCalls + GetUserDriveCalls + GetSiteDriveCalls + GetSiteCalls
            + GetListItemByIdCalls + FindListItemByWebUrlCalls + GetUserCalls;

        private static Drive DriveWithIds => new Drive { SharePointIds = new SharepointIds { SiteId = "site-guid", ListId = "list-guid" } };

        public Func<string, string, OnlineMeeting> OnGetOnlineMeeting = (userId, meetingId) =>
            new OnlineMeeting { Id = meetingId, Subject = "unit test meeting", CreationDateTime = DateTimeOffset.UtcNow };
        public Func<string, Drive> OnGetUserDrive = upn => DriveWithIds;
        public Func<string, Drive> OnGetSiteDrive = siteIdentifier => DriveWithIds;
        public Func<string, Site> OnGetSite = siteIdentifier => new Site { Id = siteIdentifier, WebUrl = "https://contoso.sharepoint.com/sites/x" };
        public Func<string, string, string, ListItem> OnGetListItemById = (siteId, listId, itemId) =>
            new ListItem { WebUrl = "https://contoso.sharepoint.com/sites/x/Shared Documents/file.docx" };
        public Func<string, string, string, ListItem> OnFindListItemByWebUrl = (siteId, listId, webUrl) => new ListItem { WebUrl = webUrl };
        public Func<string, User> OnGetUser = userId => new User { Id = "user-guid" };

        public Task<OnlineMeeting> GetOnlineMeetingAsync(string userId, string meetingId)
        {
            GetOnlineMeetingCalls++;
            return Task.FromResult(OnGetOnlineMeeting(userId, meetingId));
        }

        public Task<Drive> GetUserDriveAsync(string upn)
        {
            GetUserDriveCalls++;
            return Task.FromResult(OnGetUserDrive(upn));
        }

        public Task<Drive> GetSiteDriveAsync(string siteIdentifier)
        {
            GetSiteDriveCalls++;
            return Task.FromResult(OnGetSiteDrive(siteIdentifier));
        }

        public Task<Site> GetSiteAsync(string siteIdentifier)
        {
            GetSiteCalls++;
            return Task.FromResult(OnGetSite(siteIdentifier));
        }

        public Task<ListItem> GetListItemByIdAsync(string siteId, string listId, string itemId)
        {
            GetListItemByIdCalls++;
            return Task.FromResult(OnGetListItemById(siteId, listId, itemId));
        }

        public Task<ListItem> FindListItemByWebUrlAsync(string siteId, string listId, string webUrl)
        {
            FindListItemByWebUrlCalls++;
            return Task.FromResult(OnFindListItemByWebUrl(siteId, listId, webUrl));
        }

        public Task<User> GetUserAsync(string userId)
        {
            GetUserCalls++;
            return Task.FromResult(OnGetUser(userId));
        }
    }
}
