using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
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

        // Site-scoped cache of document-library drives used only after cheaper default-library and /shares
        // resolution fail for a Doc.aspx?sourcedoc= link. Run-scoped and bounded by MaxDocumentLibrariesToSearch.
        private readonly ConcurrentDictionary<string, IReadOnlyList<Drive>> _documentLibraryDrivesBySite =
            new ConcurrentDictionary<string, IReadOnlyList<Drive>>(StringComparer.OrdinalIgnoreCase);

        // Users for whom Graph has told us there's no Teams application access policy for this app. The grant is
        // per-user (or global), so this is cached per user rather than tenant-wide, and only for this run.
        private readonly ConcurrentDictionary<string, byte> _usersWithoutMeetingAccessPolicy =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // 1 once we've logged the "no application access policy" explanation for this run.
        private int _meetingAccessPolicyWarningLogged;

        private const int MaxDocumentLibrariesToSearch = 50;

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

            // A tenant that hasn't granted the application access policy rejects *every* online-meeting read for
            // that user, so once we've seen it don't keep calling Graph for the same user this run.
            if (userGuid != null && _usersWithoutMeetingAccessPolicy.ContainsKey(userGuid))
            {
                _logger.LogDebug("Skipping meeting lookup for meetingId {meetingId}: no Teams application access policy for this app on the user", meetingId);
                return null;
            }

            try
            {
                var meeting = await _spoGraphClient.GetOnlineMeetingAsync(userGuid, meetingId);

                return new MeetingMetadata(meeting);
            }
            catch (ODataError ex) when (IsMissingApplicationAccessPolicy(ex))
            {
                // Expected tenant-configuration condition rather than a product fault, and it hits every
                // meeting-context Copilot event. Log the actionable explanation once per import run (without the
                // exception object, so it doesn't dominate exception telemetry) and skip enrichment quietly.
                if (userGuid != null)
                {
                    _usersWithoutMeetingAccessPolicy.TryAdd(userGuid, 0);
                }

                if (Interlocked.Exchange(ref _meetingAccessPolicyWarningLogged, 1) == 0)
                {
                    _logger.LogWarning("Copilot meeting enrichment is unavailable in this tenant: Microsoft Graph reports no Teams application "
                        + "access policy for this application, so online-meeting details can't be read and meeting metadata will be skipped. "
                        + "To enable it, grant the importer application an access policy with the Teams PowerShell cmdlets "
                        + "New-CsApplicationAccessPolicy / Grant-CsApplicationAccessPolicy - see "
                        + "https://learn.microsoft.com/graph/cloud-communication-online-meeting-application-access-policy. "
                        + "Further occurrences this import run are logged at debug level only.");
                }
                else
                {
                    _logger.LogDebug("Skipping meeting info for meetingId {meetingId}: no Teams application access policy for this app on the user", meetingId);
                }

                return null;
            }
            catch (ODataError ex)
            {
                _logger.LogWarning(ex, "Error getting meeting info for meetingId {meetingId}", meetingId);
                return null;
            }
        }

        /// <summary>
        /// Is this the "No application access policy found for this app ... on the user" 403 that Graph returns when
        /// the tenant hasn't run New-CsApplicationAccessPolicy / Grant-CsApplicationAccessPolicy for the importer app?
        /// </summary>
        internal static bool IsMissingApplicationAccessPolicy(ODataError ex)
        {
            if (ex == null) return false;

            // Some Graph/Kiota paths leave the status code unset (0), so don't require it to be exactly 403 - the
            // message text is the reliable discriminator; just make sure we never swallow a non-forbidden error.
            if (ex.ResponseStatusCode != (int)HttpStatusCode.Forbidden && ex.ResponseStatusCode != 0) return false;

            var message = ex.Error?.Message ?? ex.Message ?? string.Empty;
            return message.IndexOf("application access policy", StringComparison.OrdinalIgnoreCase) >= 0;
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

            // File resolution is based only on the context URL: OneDrive URLs are resolved through the owner's
            // personal site named in the URL, not through the Copilot user's drive.
            var cacheKey = FileCacheKey(copilotDocContextId);

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

            var result = await ResolveSpoFileInfoAsync(copilotDocContextId);
            if (result.FileInfo == null)
            {
                if (result.CacheAsUnresolvable)
                {
                    _unresolvableContextIds.TryAdd(cacheKey, 0);
                }
            }
            else
            {
                _fileInfoByContext.TryAdd(cacheKey, result.FileInfo);
            }
            return result.FileInfo;
        }

        private static string FileCacheKey(string copilotDocContextId) => copilotDocContextId;

        // Example: https://contoso-my.sharepoint.com/personal/alex_contoso_com/_layouts/15/Doc.aspx?sourcedoc=%7B00000000-0000-0000-0000-000000000000%7D&file=Presentation.pptx&action=edit&mobileredirect=true
        private async Task<FileResolutionResult> ResolveSpoFileInfoAsync(string copilotDocContextId)
        {
            var siteUrl = StringUtils.GetSiteUrl(copilotDocContextId);
            if (siteUrl == null) return FileResolutionResult.Unresolvable;

            var siteResult = await GetSiteFromSiteUrl(siteUrl);
            if (siteResult.ShouldStop)
            {
                return FileResolutionResult.FromFailure(siteResult.CacheAsUnresolvable);
            }

            var site = siteResult.Site;
            var driveItemId = StringUtils.GetDriveItemId(copilotDocContextId);
            if (driveItemId == null)
            {
                return await ResolveDriveItemByUrlAsync(copilotDocContextId, site);
            }

            var spSiteId = site.Id;
            var defaultDriveResult = await GetSpoInfoFromSiteUrl(siteUrl);
            var sawTransientFailure = defaultDriveResult.ShouldStop && !defaultDriveResult.CacheAsUnresolvable;

            if (defaultDriveResult.Drive != null)
            {
                var defaultListId = defaultDriveResult.Drive.SharePointIds?.ListId;
                if (!string.IsNullOrEmpty(defaultListId))
                {
                    var defaultListResult = await TryGetListItemByIdAsync(spSiteId, defaultListId, driveItemId, site, copilotDocContextId);
                    if (defaultListResult.FileInfo != null)
                    {
                        return defaultListResult;
                    }
                    sawTransientFailure |= !defaultListResult.CacheAsUnresolvable;
                }
            }

            var sharesResult = await ResolveDriveItemByUrlAsync(copilotDocContextId, site);
            if (sharesResult.FileInfo != null)
            {
                return sharesResult;
            }
            sawTransientFailure |= !sharesResult.CacheAsUnresolvable;

            var librarySearchResult = await ResolveDriveItemFromSiteLibrariesAsync(spSiteId, defaultDriveResult.Drive?.SharePointIds?.ListId, driveItemId, site, copilotDocContextId);
            if (librarySearchResult.FileInfo != null)
            {
                return librarySearchResult;
            }
            sawTransientFailure |= !librarySearchResult.CacheAsUnresolvable;

            return FileResolutionResult.FromFailure(!sawTransientFailure);
        }

        public async Task<string> GetUserIdFromUpn(string userPrincipalName)
        {
            var user = await _userGraphCache.GetResource(userPrincipalName);
            return user.Id ?? throw new Exception($"No user ID found on user in Graph by upn {userPrincipalName}");
        }

        private async Task<FileResolutionResult> ResolveDriveItemByUrlAsync(string copilotDocContextId, Site site)
        {
            // Resolve the URL directly via /shares without first loading a drive. This is one Graph call and
            // works for plain SharePoint/OneDrive file URLs, including files in another user's OneDrive.
            try
            {
                var driveItem = await _spoGraphClient.GetDriveItemByUrlAsync(copilotDocContextId);
                if (driveItem != null)
                {
                    return FileResolutionResult.Resolved(new SpoDocumentFileInfo(driveItem, site));
                }
            }
            catch (ODataError ex) when (IsExpectedFileResolutionFailure(ex))
            {
                _logger.LogDebug("Graph could not resolve Copilot context '{ctx}' through /shares (status {status}, code {code}); trying any remaining bounded fallbacks",
                    copilotDocContextId, ex.ResponseStatusCode, ex.Error?.Code);
                return FileResolutionResult.Unresolvable;
            }
            catch (ODataError ex) when (IsTransientFileResolutionFailure(ex))
            {
                _logger.LogWarning("Transient Graph error resolving Copilot context '{ctx}' through /shares (status {status}, code {code}); it will not be cached as unresolvable",
                    copilotDocContextId, ex.ResponseStatusCode, ex.Error?.Code);
                return FileResolutionResult.TransientFailure;
            }
            catch (ODataError ex)
            {
                _logger.LogWarning(ex, "Unexpected error resolving driveItem for copilotDocContextId {copilotDocContextId}", copilotDocContextId);
                return FileResolutionResult.TransientFailure;
            }

            _logger.LogDebug("Graph /shares returned no driveItem for Copilot context '{ctx}'", copilotDocContextId);
            return FileResolutionResult.Unresolvable;
        }

        private async Task<FileResolutionResult> TryGetListItemByIdAsync(string spSiteId, string spListId, string driveItemId, Site site, string copilotDocContextId)
        {
            try
            {
                var item = await _spoGraphClient.GetListItemByIdAsync(spSiteId, spListId, driveItemId);
                return FileResolutionResult.Resolved(new SpoDocumentFileInfo(item, site));
            }
            catch (ODataError ex) when (IsExpectedFileResolutionFailure(ex))
            {
                _logger.LogDebug("Graph could not resolve Copilot context '{ctx}' as list item {itemId} in list {listId} (status {status}, code {code})",
                    copilotDocContextId, driveItemId, spListId, ex.ResponseStatusCode, ex.Error?.Code);
                return FileResolutionResult.Unresolvable;
            }
            catch (ODataError ex) when (IsTransientFileResolutionFailure(ex))
            {
                _logger.LogWarning("Transient Graph error resolving Copilot context '{ctx}' as list item {itemId} in list {listId} (status {status}, code {code}); it will not be cached as unresolvable",
                    copilotDocContextId, driveItemId, spListId, ex.ResponseStatusCode, ex.Error?.Code);
                return FileResolutionResult.TransientFailure;
            }
            catch (ODataError ex)
            {
                _logger.LogWarning(ex, "Unexpected error getting file info for copilotDocContextId {copilotDocContextId}", copilotDocContextId);
                return FileResolutionResult.TransientFailure;
            }
        }

        private async Task<FileResolutionResult> ResolveDriveItemFromSiteLibrariesAsync(string spSiteId, string defaultListId, string driveItemId, Site site, string copilotDocContextId)
        {
            var drivesResult = await GetDocumentLibraryDrivesAsync(spSiteId);
            if (drivesResult.ShouldStop)
            {
                return FileResolutionResult.FromFailure(drivesResult.CacheAsUnresolvable);
            }

            var sawTransientFailure = false;
            foreach (var drive in drivesResult.Drives)
            {
                var listId = drive.SharePointIds?.ListId;
                if (string.IsNullOrEmpty(listId) || string.Equals(listId, defaultListId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var result = await TryGetListItemByIdAsync(spSiteId, listId, driveItemId, site, copilotDocContextId);
                if (result.FileInfo != null)
                {
                    return result;
                }
                sawTransientFailure |= !result.CacheAsUnresolvable;
            }

            return FileResolutionResult.FromFailure(!sawTransientFailure);
        }

        private async Task<DocumentLibraryDrivesResult> GetDocumentLibraryDrivesAsync(string spSiteId)
        {
            if (_documentLibraryDrivesBySite.TryGetValue(spSiteId, out var cached))
            {
                return DocumentLibraryDrivesResult.Found(cached);
            }

            try
            {
                var drives = await _spoGraphClient.GetSiteDocumentLibraryDrivesAsync(spSiteId, MaxDocumentLibrariesToSearch);
                _documentLibraryDrivesBySite.TryAdd(spSiteId, drives);
                return DocumentLibraryDrivesResult.Found(drives);
            }
            catch (ODataError ex) when (IsExpectedFileResolutionFailure(ex))
            {
                _logger.LogDebug("Graph could not enumerate document libraries for site {siteId} (status {status}, code {code})",
                    spSiteId, ex.ResponseStatusCode, ex.Error?.Code);
                _documentLibraryDrivesBySite.TryAdd(spSiteId, Array.Empty<Drive>());
                return DocumentLibraryDrivesResult.Found(Array.Empty<Drive>());
            }
            catch (ODataError ex) when (IsTransientFileResolutionFailure(ex))
            {
                _logger.LogWarning("Transient Graph error enumerating document libraries for site {siteId} (status {status}, code {code}); contexts depending on this fallback will not be cached as unresolvable",
                    spSiteId, ex.ResponseStatusCode, ex.Error?.Code);
                return DocumentLibraryDrivesResult.TransientFailure;
            }
            catch (ODataError ex)
            {
                _logger.LogWarning(ex, "Unexpected error enumerating document libraries for site {siteId}", spSiteId);
                return DocumentLibraryDrivesResult.TransientFailure;
            }
        }

        private async Task<SiteResolutionResult> GetSiteFromSiteUrl(string siteUrl)
        {
            var siteAddress = StringUtils.GetHostAndSiteRelativeUrl(siteUrl);
            if (siteAddress == null)
            {
                // Possibly a Teams reference
                return SiteResolutionResult.Unresolvable;
            }

            try
            {
                var site = await _siteGraphCache.GetResource(siteAddress);
                return SiteResolutionResult.Found(site);
            }
            catch (ODataError ex) when (IsExpectedFileResolutionFailure(ex))
            {
                _logger.LogDebug("Graph could not resolve SharePoint site {siteUrl} (status {status}, code {code})",
                    siteUrl, ex.ResponseStatusCode, ex.Error?.Code);
                return SiteResolutionResult.Unresolvable;
            }
            catch (ODataError ex) when (IsTransientFileResolutionFailure(ex))
            {
                _logger.LogWarning("Transient Graph error resolving SharePoint site {siteUrl} (status {status}, code {code}); context will not be cached as unresolvable",
                    siteUrl, ex.ResponseStatusCode, ex.Error?.Code);
                return SiteResolutionResult.TransientFailure;
            }
            catch (ODataError ex)
            {
                _logger.LogWarning(ex, "Unexpected error getting site info for site {siteUrl}", siteUrl);
                return SiteResolutionResult.TransientFailure;
            }
        }

        private async Task<DriveResolutionResult> GetSpoInfoFromSiteUrl(string siteUrl)
        {
            var siteAddress = StringUtils.GetHostAndSiteRelativeUrl(siteUrl);
            if (siteAddress == null)
            {
                return DriveResolutionResult.Unresolvable;
            }

            // Get drive ID from site ID
            Drive siteDrive = null;
            try
            {
                siteDrive = await _spoGraphClient.GetSiteDriveAsync(siteAddress);
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
                catch (ODataError ex) when (IsExpectedFileResolutionFailure(ex))
                {
                    _logger.LogDebug("Graph could not resolve SharePoint site {siteUrl} while loading its default drive (status {status}, code {code})",
                        siteUrl, ex.ResponseStatusCode, ex.Error?.Code);
                    return DriveResolutionResult.Unresolvable;
                }
                catch (ODataError ex) when (IsTransientFileResolutionFailure(ex))
                {
                    _logger.LogWarning("Transient Graph error resolving SharePoint site {siteUrl} while loading its default drive (status {status}, code {code}); context will not be cached as unresolvable",
                        siteUrl, ex.ResponseStatusCode, ex.Error?.Code);
                    return DriveResolutionResult.TransientFailure;
                }
                catch (ODataError ex)
                {
                    _logger.LogWarning(ex, "Unexpected error getting site info for site {siteUrl}", siteUrl);
                    return DriveResolutionResult.TransientFailure;
                }
                if (site != null)
                {
                    try
                    {
                        // Try one more time using site ID
                        siteDrive = await _spoGraphClient.GetSiteDriveAsync(site.Id);
                    }
                    catch (ODataError ex) when (IsExpectedFileResolutionFailure(ex))
                    {
                        _logger.LogDebug("Graph could not resolve default drive for site {siteId} (status {status}, code {code})",
                            site.Id, ex.ResponseStatusCode, ex.Error?.Code);
                    }
                    catch (ODataError ex) when (IsTransientFileResolutionFailure(ex))
                    {
                        _logger.LogWarning("Transient Graph error resolving default drive for site {siteId} (status {status}, code {code}); context will not be cached as unresolvable",
                            site.Id, ex.ResponseStatusCode, ex.Error?.Code);
                        return DriveResolutionResult.TransientFailure;
                    }
                    catch (ODataError ex)
                    {
                        _logger.LogWarning(ex, "Unexpected error resolving default drive for site {siteId}", site.Id);
                        return DriveResolutionResult.TransientFailure;
                    }

                    if (siteDrive == null)
                    {
                        // Site exists but no drive for some reason
                        _logger.LogDebug("No default drive found for site ID {siteId}", site.Id);
                        return DriveResolutionResult.Unresolvable;
                    }
                    else
                    {
                        return DriveResolutionResult.Found(siteDrive);
                    }
                }
                else
                {
                    // We can't find the site. Bug in the URL parsing?
                    _logger.LogDebug("No site found for site {siteUrl}", siteUrl);
                    return DriveResolutionResult.Unresolvable;
                }
            }
            else
            {
                return DriveResolutionResult.Found(siteDrive);
            }
        }

        private static bool IsExpectedFileResolutionFailure(ODataError ex)
        {
            if (ex == null) return false;
            return ex.ResponseStatusCode == (int)HttpStatusCode.NotFound
                || ex.ResponseStatusCode == (int)HttpStatusCode.Forbidden;
        }

        private static bool IsTransientFileResolutionFailure(ODataError ex)
        {
            if (ex == null) return false;
            return ex.ResponseStatusCode == 429
                || ex.ResponseStatusCode >= 500;
        }

        private sealed class FileResolutionResult
        {
            public SpoDocumentFileInfo FileInfo { get; private set; }
            public bool CacheAsUnresolvable { get; private set; }

            public static FileResolutionResult Unresolvable { get; } = new FileResolutionResult { CacheAsUnresolvable = true };
            public static FileResolutionResult TransientFailure { get; } = new FileResolutionResult { CacheAsUnresolvable = false };
            public static FileResolutionResult Resolved(SpoDocumentFileInfo fileInfo) => new FileResolutionResult { FileInfo = fileInfo, CacheAsUnresolvable = false };
            public static FileResolutionResult FromFailure(bool cacheAsUnresolvable) => cacheAsUnresolvable ? Unresolvable : TransientFailure;
        }

        private sealed class SiteResolutionResult
        {
            public Site Site { get; private set; }
            public bool CacheAsUnresolvable { get; private set; }
            public bool ShouldStop => Site == null;

            public static SiteResolutionResult Unresolvable { get; } = new SiteResolutionResult { CacheAsUnresolvable = true };
            public static SiteResolutionResult TransientFailure { get; } = new SiteResolutionResult { CacheAsUnresolvable = false };
            public static SiteResolutionResult Found(Site site) => new SiteResolutionResult { Site = site };
        }

        private sealed class DriveResolutionResult
        {
            public Drive Drive { get; private set; }
            public bool CacheAsUnresolvable { get; private set; }
            public bool ShouldStop => Drive == null;

            public static DriveResolutionResult Unresolvable { get; } = new DriveResolutionResult { CacheAsUnresolvable = true };
            public static DriveResolutionResult TransientFailure { get; } = new DriveResolutionResult { CacheAsUnresolvable = false };
            public static DriveResolutionResult Found(Drive drive) => new DriveResolutionResult { Drive = drive };
        }

        private sealed class DocumentLibraryDrivesResult
        {
            public IReadOnlyList<Drive> Drives { get; private set; }
            public bool CacheAsUnresolvable { get; private set; }
            public bool ShouldStop => Drives == null;

            public static DocumentLibraryDrivesResult TransientFailure { get; } = new DocumentLibraryDrivesResult { CacheAsUnresolvable = false };
            public static DocumentLibraryDrivesResult Found(IReadOnlyList<Drive> drives) => new DocumentLibraryDrivesResult { Drives = drives ?? Array.Empty<Drive>() };
        }
    }
}
