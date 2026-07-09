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
        // Case-insensitive so a failed prewarm (keyed off the raw event UserId) isn't re-attempted in the serial
        // pass under a differently-cased UPN (UPNs are case-insensitive).
        private readonly ConcurrentDictionary<string, byte> _unresolvableContextIds =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // Positive cache of resolved file info, keyed by copilot doc context id. Run-scoped: the same file
        // context recurs across many audit batches, so caching avoids repeat Graph resolution every batch.
        private readonly ConcurrentDictionary<string, SpoDocumentFileInfo> _fileInfoByContext =
            new ConcurrentDictionary<string, SpoDocumentFileInfo>(StringComparer.OrdinalIgnoreCase);

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

            // Cache key: a personal OneDrive ("-my") file is resolved through the *event user's* own drive, so
            // the result is user-specific - key those per (context, upn). A shared-site file is resolved
            // independently of the user, so key those by context alone (dedupes across all users who touched it).
            var cacheKey = FileCacheKey(copilotDocContextId, eventUpn);

            // Already resolved this context earlier in the run? Return the cached result (no Graph call).
            if (_fileInfoByContext.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            // Don't re-resolve a context we've already failed to resolve this run.
            if (_unresolvableContextIds.ContainsKey(cacheKey))
            {
                _logger.LogDebug("Copilot context '{ctx}' was already unresolvable this run; skipping Graph lookup", copilotDocContextId);
                return null;
            }

            var result = await ResolveSpoFileInfoAsync(copilotDocContextId, eventUpn);
            if (result == null)
            {
                _unresolvableContextIds.TryAdd(cacheKey, 0);
            }
            else
            {
                _fileInfoByContext.TryAdd(cacheKey, result);
            }
            return result;
        }

        // Personal OneDrive ("-my") files depend on the event user's drive, so include the upn in the key;
        // shared-site files don't, so key by context alone.
        private static string FileCacheKey(string copilotDocContextId, string eventUpn)
            => StringUtils.IsMySiteUrl(copilotDocContextId) ? copilotDocContextId + "\n" + (eventUpn ?? string.Empty) : copilotDocContextId;

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
                // We might have a direct URL as the copilot context ID. Resolve it straight to a driveItem via
                // the Graph /shares endpoint (one call) instead of paging the whole document library to URL-match.
                // Example: https://contoso-my.sharepoint.com/personal/alex_contoso_onmicrosoft_com/Documents/MyDoc.docx
                try
                {
                    var driveItem = await _spoGraphClient.GetDriveItemByUrlAsync(copilotDocContextId);
                    if (driveItem != null)
                    {
                        return new SpoDocumentFileInfo(driveItem, site);
                    }
                }
                catch (ODataError ex)
                {
                    _logger.LogWarning(ex, "Error resolving driveItem for copilotDocContextId {copilotDocContextId}", copilotDocContextId);
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
