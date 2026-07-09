using Microsoft.Graph;
using Microsoft.Graph.Models;
using System;
using System.Threading.Tasks;

namespace ActivityImporter.Engine.ActivityAPI.Copilot
{
    /// <summary>
    /// Thin abstraction over the individual Microsoft Graph calls needed to resolve Copilot file/meeting
    /// context metadata. Exists so the resolution *logic* in <see cref="GraphFileMetadataLoader"/> (which
    /// branch to take, early-rejects, caching) can be unit-tested against a fake instead of a live Graph
    /// client. The real implementation (<see cref="GraphSpoClient"/>) is a pass-through - all decision making
    /// lives in the loader.
    /// </summary>
    public interface ISpoGraphClient
    {
        Task<OnlineMeeting> GetOnlineMeetingAsync(string userId, string meetingId);

        /// <summary>A user's OneDrive (with SharePointIds populated).</summary>
        Task<Drive> GetUserDriveAsync(string upn);

        /// <summary>A site's default document library drive (with SharePointIds populated). Accepts a site id or a host:/path address.</summary>
        Task<Drive> GetSiteDriveAsync(string siteIdentifier);

        /// <summary>A site by id or by host:/path address.</summary>
        Task<Site> GetSiteAsync(string siteIdentifier);

        /// <summary>A single list item by its id (fields expanded).</summary>
        Task<ListItem> GetListItemByIdAsync(string siteId, string listId, string itemId);

        /// <summary>
        /// Resolve a full SharePoint/OneDrive URL directly to its driveItem via the Graph /shares endpoint,
        /// or null if it can't be resolved. One call - avoids paging an entire document library to URL-match.
        /// </summary>
        Task<DriveItem> GetDriveItemByUrlAsync(string url);

        Task<User> GetUserAsync(string userId);
    }

    /// <summary>
    /// Live Microsoft Graph implementation of <see cref="ISpoGraphClient"/>. Deliberately contains no logic
    /// beyond issuing the Graph request so that all testable behaviour stays in the loader.
    /// </summary>
    public class GraphSpoClient : ISpoGraphClient
    {
        private readonly GraphServiceClient _graphServiceClient;

        public GraphSpoClient(GraphServiceClient graphServiceClient)
        {
            _graphServiceClient = graphServiceClient;
        }

        public Task<OnlineMeeting> GetOnlineMeetingAsync(string userId, string meetingId)
            => _graphServiceClient.Users[userId].OnlineMeetings[meetingId].GetAsync();

        public Task<Drive> GetUserDriveAsync(string upn)
            => _graphServiceClient.Users[upn].Drive.GetAsync(rc => { rc.QueryParameters.Select = new[] { "SharePointIds" }; });

        public Task<Drive> GetSiteDriveAsync(string siteIdentifier)
            => _graphServiceClient.Sites[siteIdentifier].Drive.GetAsync(rc => { rc.QueryParameters.Select = new[] { "SharePointIds" }; });

        public Task<Site> GetSiteAsync(string siteIdentifier)
            => _graphServiceClient.Sites[siteIdentifier].GetAsync();

        public Task<ListItem> GetListItemByIdAsync(string siteId, string listId, string itemId)
            => _graphServiceClient.Sites[siteId].Lists[listId].Items[itemId].GetAsync(rc => { rc.QueryParameters.Expand = new[] { "fields" }; });

        public Task<DriveItem> GetDriveItemByUrlAsync(string url)
        {
            // https://learn.microsoft.com/en-us/graph/api/shares-get - encode the URL as an unpadded base64url
            // "u!" share id, then read its driveItem. Resolves any SPO/OneDrive URL in one call.
            var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(url));
            var shareId = "u!" + base64.TrimEnd('=').Replace('/', '_').Replace('+', '-');
            return _graphServiceClient.Shares[shareId].DriveItem.GetAsync();
        }

        public Task<User> GetUserAsync(string userId)
            => _graphServiceClient.Users[userId].GetAsync();
    }
}
