using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;
using System;
using System.Collections.Generic;
using System.Threading;
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

        /// <summary>A bounded page of a site's document library drives (with SharePointIds populated).</summary>
        Task<IReadOnlyList<Drive>> GetSiteDocumentLibraryDrivesAsync(string siteIdentifier, int maxDrives);

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
    /// A client that can issue the same requests under a stricter policy: no SDK retries, and a cancellation token that
    /// ends a request in flight. The loader uses it for lookups made while the save holds its SQL lock.
    /// </summary>
    public interface IBoundableSpoGraphClient
    {
        ISpoGraphClient WithRequestPolicy(bool disableSdkRetries, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Live Microsoft Graph implementation of <see cref="ISpoGraphClient"/>. Deliberately contains no logic
    /// beyond issuing the Graph request so that all testable behaviour stays in the loader.
    /// </summary>
    public class GraphSpoClient : ISpoGraphClient, IBoundableSpoGraphClient
    {
        private readonly GraphServiceClient _graphServiceClient;
        private readonly bool _disableSdkRetries;
        private readonly CancellationToken _cancellationToken;

        public GraphSpoClient(GraphServiceClient graphServiceClient)
            : this(graphServiceClient, false, CancellationToken.None)
        {
        }

        private GraphSpoClient(GraphServiceClient graphServiceClient, bool disableSdkRetries, CancellationToken cancellationToken)
        {
            _graphServiceClient = graphServiceClient;
            _disableSdkRetries = disableSdkRetries;
            _cancellationToken = cancellationToken;
        }

        public ISpoGraphClient WithRequestPolicy(bool disableSdkRetries, CancellationToken cancellationToken)
            => new GraphSpoClient(_graphServiceClient, disableSdkRetries, cancellationToken);

        // Kiota's RetryHandler reads a per-request RetryHandlerOption before its own default, so MaxRetry = 0 turns off its
        // 429/503/504 retries (and their Retry-After waits) for this request only.
        private void Apply<T>(RequestConfiguration<T> rc) where T : class, new()
        {
            if (_disableSdkRetries)
            {
                rc.Options.Add(new RetryHandlerOption { MaxRetry = 0 });
            }
        }

        public Task<OnlineMeeting> GetOnlineMeetingAsync(string userId, string meetingId)
            => _graphServiceClient.Users[userId].OnlineMeetings[meetingId].GetAsync(Apply, _cancellationToken);

        public Task<Drive> GetUserDriveAsync(string upn)
            => _graphServiceClient.Users[upn].Drive.GetAsync(rc => { rc.QueryParameters.Select = new[] { "SharePointIds" }; Apply(rc); }, _cancellationToken);

        public Task<Drive> GetSiteDriveAsync(string siteIdentifier)
            => _graphServiceClient.Sites[siteIdentifier].Drive.GetAsync(rc => { rc.QueryParameters.Select = new[] { "SharePointIds" }; Apply(rc); }, _cancellationToken);

        public async Task<IReadOnlyList<Drive>> GetSiteDocumentLibraryDrivesAsync(string siteIdentifier, int maxDrives)
        {
            var drives = await _graphServiceClient.Sites[siteIdentifier].Drives.GetAsync(rc =>
            {
                rc.QueryParameters.Select = new[] { "id", "name", "sharePointIds", "webUrl" };
                rc.QueryParameters.Top = maxDrives;
                Apply(rc);
            }, _cancellationToken);
            return drives?.Value ?? new List<Drive>();
        }

        public Task<Site> GetSiteAsync(string siteIdentifier)
            => _graphServiceClient.Sites[siteIdentifier].GetAsync(Apply, _cancellationToken);

        public Task<ListItem> GetListItemByIdAsync(string siteId, string listId, string itemId)
            => _graphServiceClient.Sites[siteId].Lists[listId].Items[itemId].GetAsync(rc => { rc.QueryParameters.Expand = new[] { "fields" }; Apply(rc); }, _cancellationToken);

        public Task<DriveItem> GetDriveItemByUrlAsync(string url)
        {
            // https://learn.microsoft.com/en-us/graph/api/shares-get - encode the URL as an unpadded base64url
            // "u!" share id, then read its driveItem. Resolves any SPO/OneDrive URL in one call.
            var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(url));
            var shareId = "u!" + base64.TrimEnd('=').Replace('/', '_').Replace('+', '-');
            return _graphServiceClient.Shares[shareId].DriveItem.GetAsync(Apply, _cancellationToken);
        }

        public Task<User> GetUserAsync(string userId)
            => _graphServiceClient.Users[userId].GetAsync(Apply, _cancellationToken);
    }
}
