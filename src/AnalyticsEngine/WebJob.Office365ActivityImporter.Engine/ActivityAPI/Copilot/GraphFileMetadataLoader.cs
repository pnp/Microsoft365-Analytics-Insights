using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;

namespace ActivityImporter.Engine.ActivityAPI.Copilot
{
    /// <summary>
    /// Populates file metadata from Graph API
    /// </summary>
    public class GraphFileMetadataLoader : ICopilotMetadataLoader
    {
        private readonly ISpoGraphClient _spoGraphClient;
        private readonly SiteGraphCache _siteGraphCache;
        private readonly UserGraphCache _userGraphCache;
        private readonly ILogger _logger;

        // Copilot context ids that resolved to nothing this session - don't waste Graph calls re-resolving them.
        private readonly ConcurrentDictionary<string, byte> _unresolvableContextIds = new ConcurrentDictionary<string, byte>();

        public GraphFileMetadataLoader(GraphServiceClient graphServiceClient, ILogger logger)
            : this(new GraphSpoClient(graphServiceClient), logger)
        {
        }

        public GraphFileMetadataLoader(ISpoGraphClient spoGraphClient, ILogger logger)
        {
            _spoGraphClient = spoGraphClient;
            _logger = logger;
            _siteGraphCache = new SiteGraphCache(spoGraphClient);
            _userGraphCache = new UserGraphCache(spoGraphClient);
        }

        public async Task<MeetingMetadata> GetMeetingInfo(string meetingId, string userGuid)
        {
            // Requires OnlineMeetings.Read.All and https://learn.microsoft.com/en-us/graph/cloud-communication-online-meeting-application-access-policy#configure-application-access-policy
            try
            {
                var meeting = await _spoGraphClient.GetOnlineMeetingAsync(userGuid, meetingId);

                return new MeetingMetadata(meeting);
            }
            catch (ODataError ex)
            {
                _logger.LogWarning(ex, "Error getting meeting info for meetingId {meetingId}", meetingId);
                return null;
            }
        }

        public async Task<SpoDocumentFileInfo> GetSpoFileInfo(string copilotDocContextId, string eventUpn)
        {
            // Skip anything that can't be a SharePoint/OneDrive file (securitycopilot.microsoft.com, local
            // Outlook attachment paths, other hosts) before doing any Graph work - these fail on every import.
            if (!StringUtils.IsResolvableSpoFileUrl(copilotDocContextId))
            {
                _logger.LogDebug("Copilot context '{ctx}' is not a resolvable SharePoint/OneDrive URL; skipping Graph lookup", copilotDocContextId);
                return null;
            }

            // Don't re-resolve a context we've already failed to resolve this session.
            if (_unresolvableContextIds.ContainsKey(copilotDocContextId))
            {
                _logger.LogDebug("Copilot context '{ctx}' was already unresolvable this session; skipping Graph lookup", copilotDocContextId);
                return null;
            }

            var result = await ResolveSpoFileInfoAsync(copilotDocContextId, eventUpn);
            if (result == null)
            {
                _unresolvableContextIds.TryAdd(copilotDocContextId, 0);
            }
            return result;
        }

        // Example: https://m365cp123890-my.sharepoint.com/personal/sambetts_m365cp123890_onmicrosoft_com/_layouts/15/Doc.aspx?sourcedoc=%7B0D86F64F-8435-430C-8979-FF46C00F7ACB%7D&file=Presentation.pptx&action=edit&mobileredirect=true
        private async Task<SpoDocumentFileInfo> ResolveSpoFileInfoAsync(string copilotDocContextId, string eventUpn)
        {
            var siteUrl = StringUtils.GetSiteUrl(copilotDocContextId);
            if (siteUrl == null) return null;

            Drive drive = null;
            if (StringUtils.IsMySiteUrl(siteUrl))
            {
                drive = await GetSpoInfoFromMySiteUrl(eventUpn);
            }
            else
            {
                drive = await GetSpoInfoFromSiteUrl(siteUrl);
            }
            if (drive == null)
            {
                return null;
            }

            // Get site ID from url
            // https://learn.microsoft.com/en-us/graph/api/drive-get?view=graph-rest-beta&tabs=http
            var spSiteId = drive.SharePointIds?.SiteId;
            if (string.IsNullOrEmpty(spSiteId))
            {
                throw new ArgumentOutOfRangeException("SharePointIds.SiteId");
            }
            var spListId = drive.SharePointIds?.ListId;
            if (string.IsNullOrEmpty(spListId))
            {
                throw new ArgumentOutOfRangeException("SharePointIds.ListId");
            }
            var driveItemId = StringUtils.GetDriveItemId(copilotDocContextId);

            var site = await _siteGraphCache.GetResourceOrNullIfNotExists(spSiteId);
            if (driveItemId != null)
            {
                try
                {
                    var item = await _spoGraphClient.GetListItemByIdAsync(spSiteId, spListId, driveItemId);
                    return new SpoDocumentFileInfo(item, site);
                }
                catch (ODataError ex)
                {
                    _logger.LogWarning(ex, "Error getting file info for copilotDocContextId {copilotDocContextId}", copilotDocContextId);
                    return null;
                }
            }
            else
            {
                // We might have a direct URL as the copilot context ID, so search the list for a matching item.
                // Example: https://contoso-my.sharepoint.com/personal/alex_contoso_onmicrosoft_com/Documents/MyDoc.docx
                try
                {
                    var matchedItem = await _spoGraphClient.FindListItemByWebUrlAsync(spSiteId, spListId, copilotDocContextId);
                    if (matchedItem != null)
                    {
                        return new SpoDocumentFileInfo(matchedItem, site);
                    }
                }
                catch (ODataError ex)
                {
                    _logger.LogWarning(ex, "Error getting items info for list {spListId} on site {siteUrl}", spListId, siteUrl);
                    return null;
                }

                _logger.LogWarning("No driveItemId found in copilotDocContextId {copilotDocContextId}", copilotDocContextId);
                return null;
            }
        }

        public async Task<string> GetUserIdFromUpn(string userPrincipalName)
        {
            var user = await _userGraphCache.GetResource(userPrincipalName);
            return user.Id ?? throw new Exception($"No user ID found on user in Graph by upn {userPrincipalName}");
        }

        private async Task<Drive> GetSpoInfoFromMySiteUrl(string eventUpn)
        {
            // Needs Files.Read.All
            try
            {
                return await _spoGraphClient.GetUserDriveAsync(eventUpn)
                    ?? throw new ArgumentOutOfRangeException(eventUpn);
            }
            catch (ODataError ex)
            {
                _logger.LogWarning(ex, $"Error {ex.ResponseStatusCode} getting drive info for user {eventUpn}", eventUpn);
                return null;
            }
        }

        private async Task<Drive> GetSpoInfoFromSiteUrl(string siteUrl)
        {
            var siteAddress = StringUtils.GetHostAndSiteRelativeUrl(siteUrl);
            if (siteAddress == null)
            {
                // Possibly a Teams reference
                return null;
            }

            // Get drive ID from site ID
            Drive siteDrive = null;
            try
            {
                siteDrive = await _spoGraphClient.GetSiteDriveAsync(siteAddress)
                    ?? throw new ArgumentOutOfRangeException(siteAddress);
            }
            catch (ODataError)
            {
                // We can't get the drive via the site address, for some reason. Most of the time we can, but sometimes it doesn't work...
                // Load just the site and then try getting the drive using the loaded site ID
            }

            if (siteDrive == null)
            {
                Site site = null;
                try
                {
                    site = await _spoGraphClient.GetSiteAsync(siteAddress) ?? throw new ArgumentOutOfRangeException(siteAddress);
                }
                catch (ODataError ex)
                {
                    _logger.LogWarning(ex, "Error getting site info for site {siteUrl}", siteUrl);
                    return null;
                }
                if (site != null)
                {
                    try
                    {
                        // Try one more time using site ID
                        siteDrive = await _spoGraphClient.GetSiteDriveAsync(site.Id)
                            ?? throw new ArgumentOutOfRangeException(siteAddress);
                    }
                    catch (ODataError)
                    {
                        // Ignore. Handle logging below
                    }

                    if (siteDrive == null)
                    {
                        // Site exists but no drive for some reason
                        _logger.LogWarning($"No drive found for site ID {site.Id}");
                        return null;
                    }
                    else
                    {
                        return siteDrive;
                    }
                }
                else
                {
                    // We can't find the site. Bug in the URL parsing?
                    _logger.LogError("No site found for site {siteUrl}", siteUrl);
                    return null;
                }
            }
            else
            {
                return siteDrive;
            }
        }
    }
}
