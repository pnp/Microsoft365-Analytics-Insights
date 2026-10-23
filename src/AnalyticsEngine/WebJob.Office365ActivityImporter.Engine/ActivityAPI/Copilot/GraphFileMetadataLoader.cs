using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;

namespace ActivityImporter.Engine.ActivityAPI.Copilot
{
    /// <summary>
    /// A file or meeting lookup that Microsoft Graph failed transiently (throttling, a server error) rather than
    /// answered. Distinct from a <c>null</c> result, which means the file or meeting genuinely can't be resolved,
    /// so a caller can retry instead of saving the interaction as if it had nothing to enrich.
    /// </summary>
    public class CopilotEnrichmentTransientException : Exception
    {
        public CopilotEnrichmentTransientException(string message) : base(message)
        {
        }

        public CopilotEnrichmentTransientException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

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

        // Online-meeting ids that Graph has already told us cannot be resolved this run. The id already includes
        // the user component used to construct it, so this suppresses repeats for the same (user, meeting) attempt.
        private readonly ConcurrentDictionary<string, byte> _unresolvableMeetingIds =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // 1 once we've logged the "no application access policy" explanation for this run.
        private int _meetingAccessPolicyWarningLogged;

        // 1 once we've logged the generic 403 / 404 meeting lookup explanations for this run.
        private int _meetingForbiddenWarningLogged;
        private int _meetingNotFoundWarningLogged;

        private const int MaxDocumentLibrariesToSearch = 50;

        // Retrying a file lookup that Graph failed transiently. Only GetSpoFileInfo does: the prewarm calls it OUTSIDE
        // the save lock. The save, inside the lock, calls GetSpoFileInfoSingleAttempt and never waits. The budget is
        // this loader's, and the shared loader lives for the whole import run.

        /// <summary>Tries per file lookup in <see cref="GetSpoFileInfo"/>, the first included.</summary>
        internal const int TransientRetryMaxAttempts = 3;

        /// <summary>Retries this loader may spend in its life (the run); after that a transient failure isn't retried.</summary>
        internal const int TransientRetryBudgetPerRun = 50;

        /// <summary>Back-off before retry n is n times this.</summary>
        internal static readonly TimeSpan TransientRetryBaseDelay = TimeSpan.FromSeconds(2);

        /// <summary>How a retry waits. Replaceable so tests don't sleep, and can see every wait.</summary>
        internal Func<TimeSpan, Task> RetryDelay { get; set; } = Task.Delay;

        private int _transientRetriesSpent;
        private int _transientRetryBudgetSpentLogged;

        /// <summary>Retries spent so far on file lookups that Graph failed transiently, out of <see cref="TransientRetryBudgetPerRun"/>.</summary>
        public int TransientRetriesSpent => Volatile.Read(ref _transientRetriesSpent);

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

        public Task<MeetingMetadata> GetMeetingInfo(string meetingId, string userGuid)
            => GetMeetingInfo(meetingId, userGuid, requireDefiniteResult: false);

        internal async Task<MeetingMetadata> GetMeetingInfo(string meetingId, string userGuid, bool requireDefiniteResult)
        {
            // Requires OnlineMeetings.Read.All and https://learn.microsoft.com/en-us/graph/cloud-communication-online-meeting-application-access-policy#configure-application-access-policy

            // A tenant that hasn't granted the application access policy rejects *every* online-meeting read for
            // that user, so once we've seen it don't keep calling Graph for the same user this run.
            if (userGuid != null && _usersWithoutMeetingAccessPolicy.ContainsKey(userGuid))
            {
                _logger.LogDebug("Skipping meeting lookup for meetingId {meetingId}: no Teams application access policy for this app on the user", meetingId);
                return null;
            }

            if (meetingId != null && _unresolvableMeetingIds.ContainsKey(meetingId))
            {
                _logger.LogDebug("Skipping meeting lookup for meetingId {meetingId}: it already failed with an expected Graph response this run", meetingId);
                return null;
            }

            try
            {
                var meeting = await _spoGraphClient.GetOnlineMeetingAsync(userGuid, meetingId);

                return new MeetingMetadata(meeting);
            }
            catch (ApiException ex) when (IsMissingApplicationAccessPolicy(ex))
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
            catch (ApiException ex) when (IsMeetingForbidden(ex))
            {
                if (meetingId != null)
                {
                    _unresolvableMeetingIds.TryAdd(meetingId, 0);
                }

                if (Interlocked.Exchange(ref _meetingForbiddenWarningLogged, 1) == 0)
                {
                    _logger.LogWarning("Copilot meeting enrichment could not read one online meeting because Microsoft Graph returned HTTP 403. "
                        + "Meeting metadata will be skipped for affected events. Likely causes are: the Copilot audit event belongs to an attendee "
                        + "but the online-meeting id requires the organiser id; OnlineMeetings.Read.All has not been consented for the runtime app; "
                        + "or the Teams application access policy does not cover the user whose meeting is being read. Further 403 meeting lookup "
                        + "failures this import run are logged at debug level only.");
                }
                else
                {
                    _logger.LogDebug("Skipping meeting info for meetingId {meetingId}: Graph returned HTTP 403 for this online-meeting lookup", meetingId);
                }

                return null;
            }
            catch (ApiException ex) when (IsMeetingNotFound(ex))
            {
                if (meetingId != null)
                {
                    _unresolvableMeetingIds.TryAdd(meetingId, 0);
                }

                if (Interlocked.Exchange(ref _meetingNotFoundWarningLogged, 1) == 0)
                {
                    _logger.LogWarning("Copilot meeting enrichment could not find one online meeting because Microsoft Graph returned HTTP 404. "
                        + "Meeting metadata will be skipped for affected events. This can happen when an attendee uses Copilot in a meeting they "
                        + "did not organise and the audit context does not include the organiser id needed to build the online-meeting id. "
                        + "Further 404 meeting lookup failures this import run are logged at debug level only.");
                }
                else
                {
                    _logger.LogDebug("Skipping meeting info for meetingId {meetingId}: Graph returned HTTP 404 for this online-meeting lookup", meetingId);
                }

                return null;
            }
            catch (ApiException ex) when (IsTransientFileResolutionFailure(ex))
            {
                // Throttling or a server error that outlasted the Graph SDK's own retries. Not "this meeting has
                // no details": the save counts the interaction it has to save without them.
                throw new CopilotEnrichmentTransientException(
                    $"Microsoft Graph failed transiently reading an online meeting (HTTP {ex.ResponseStatusCode}).", ex);
            }
            catch (ApiException ex)
            {
                _logger.LogWarning(ex, "Error getting meeting info for meetingId {meetingId}", meetingId);
                if (requireDefiniteResult) throw;
                return null;
            }
        }

        /// <summary>
        /// Is this the "No application access policy found for this app ... on the user" 403 that Graph returns when
        /// the tenant hasn't run New-CsApplicationAccessPolicy / Grant-CsApplicationAccessPolicy for the importer app?
        /// </summary>
        internal static bool IsMissingApplicationAccessPolicy(ApiException ex)
        {
            if (ex == null) return false;

            // Some Graph/Kiota paths leave the status code unset (0), so don't require it to be exactly 403 - the
            // message text is the reliable discriminator; just make sure we never swallow a non-forbidden error.
            if (ex.ResponseStatusCode != (int)HttpStatusCode.Forbidden && ex.ResponseStatusCode != 0) return false;

            var message = (ex as ODataError)?.Error?.Message ?? ex.Message ?? string.Empty;
            return message.IndexOf("application access policy", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool IsMeetingForbidden(ApiException ex)
            => ex?.ResponseStatusCode == (int)HttpStatusCode.Forbidden && !IsMissingApplicationAccessPolicy(ex);

        internal static bool IsMeetingNotFound(ApiException ex)
            => ex?.ResponseStatusCode == (int)HttpStatusCode.NotFound;

        /// <summary>
        /// Resolves a Copilot file context, retrying a lookup that Graph failed transiently (throttling, a server error or
        /// a dropped connection) with a short back-off, within the run's <see cref="TransientRetryBudgetPerRun"/>. The
        /// prewarm calls this, outside the save lock, so a lookup that recovers is cached before the save needs it.
        /// Gives up with <see cref="CopilotEnrichmentTransientException"/>.
        /// </summary>
        public async Task<SpoDocumentFileInfo> GetSpoFileInfo(string copilotDocContextId, string eventUpn)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await GetSpoFileInfoSingleAttempt(copilotDocContextId, eventUpn);
                }
                catch (Exception ex) when (attempt < TransientRetryMaxAttempts && IsRetryableGraphFailure(ex))
                {
                    if (!TryTakeTransientRetry())
                    {
                        throw;
                    }
                }
                await RetryDelay(TimeSpan.FromTicks(TransientRetryBaseDelay.Ticks * attempt));
            }
        }

        /// <summary>
        /// The same lookup, made once and without waiting, for a caller holding the save lock. A transient failure throws
        /// <see cref="CopilotEnrichmentTransientException"/>, and is not cached, so the next lookup tries again.
        /// </summary>
        public Task<SpoDocumentFileInfo> GetSpoFileInfoSingleAttempt(string copilotDocContextId, string eventUpn)
            => GetSpoFileInfoSingleAttempt(copilotDocContextId, eventUpn, requireDefiniteResult: false);

        internal async Task<SpoDocumentFileInfo> GetSpoFileInfoSingleAttempt(string copilotDocContextId, string eventUpn, bool requireDefiniteResult)
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
                else if (result.Failure == ResolutionFailure.Transient)
                {
                    // Throttling or a server error rather than an answer, so not "no such file": GetSpoFileInfo retries
                    // it (outside the save lock), and the save counts the interaction if it goes without its file.
                    throw new CopilotEnrichmentTransientException("Microsoft Graph failed transiently resolving a Copilot file context.");
                }
                else if (requireDefiniteResult && result.Failure == ResolutionFailure.Unexpected)
                {
                    throw new InvalidOperationException("Microsoft Graph did not give a definite file-resolution result; durable enrichment must be retried.");
                }
                // Otherwise an unexpected Graph error, already logged with its exception: not cached, so a later
                // batch tries again, but not retried now - waiting a few seconds doesn't change a 400.
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
                return FileResolutionResult.FromFailure(siteResult.Failure);
            }

            var site = siteResult.Site;
            var driveItemId = StringUtils.GetDriveItemId(copilotDocContextId);
            if (driveItemId == null)
            {
                return await ResolveDriveItemByUrlAsync(copilotDocContextId, site);
            }

            var spSiteId = site.Id;
            var defaultDriveResult = await GetSpoInfoFromSiteUrl(siteUrl);
            var failure = defaultDriveResult.ShouldStop ? defaultDriveResult.Failure : ResolutionFailure.Unresolvable;

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
                    failure = Worst(failure, defaultListResult.Failure);
                }
            }

            var sharesResult = await ResolveDriveItemByUrlAsync(copilotDocContextId, site);
            if (sharesResult.FileInfo != null)
            {
                return sharesResult;
            }
            failure = Worst(failure, sharesResult.Failure);

            var librarySearchResult = await ResolveDriveItemFromSiteLibrariesAsync(spSiteId, defaultDriveResult.Drive?.SharePointIds?.ListId, driveItemId, site, copilotDocContextId);
            if (librarySearchResult.FileInfo != null)
            {
                return librarySearchResult;
            }
            failure = Worst(failure, librarySearchResult.Failure);

            return FileResolutionResult.FromFailure(failure);
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
            catch (ApiException ex) when (IsExpectedFileResolutionFailure(ex))
            {
                _logger.LogDebug("Graph could not resolve Copilot context '{ctx}' through /shares (status {status}, code {code}); trying any remaining bounded fallbacks",
                    copilotDocContextId, ex.ResponseStatusCode, GraphErrorCode(ex));
                return FileResolutionResult.Unresolvable;
            }
            catch (ApiException ex) when (IsTransientFileResolutionFailure(ex))
            {
                _logger.LogWarning("Transient Graph error resolving Copilot context '{ctx}' through /shares (status {status}, code {code}); it will not be cached as unresolvable",
                    copilotDocContextId, ex.ResponseStatusCode, GraphErrorCode(ex));
                return FileResolutionResult.TransientFailure;
            }
            catch (ApiException ex)
            {
                _logger.LogWarning(ex, "Unexpected error resolving driveItem for copilotDocContextId {copilotDocContextId}", copilotDocContextId);
                return FileResolutionResult.UnexpectedFailure;
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
            catch (ApiException ex) when (IsExpectedFileResolutionFailure(ex))
            {
                _logger.LogDebug("Graph could not resolve Copilot context '{ctx}' as list item {itemId} in list {listId} (status {status}, code {code})",
                    copilotDocContextId, driveItemId, spListId, ex.ResponseStatusCode, GraphErrorCode(ex));
                return FileResolutionResult.Unresolvable;
            }
            catch (ApiException ex) when (IsTransientFileResolutionFailure(ex))
            {
                _logger.LogWarning("Transient Graph error resolving Copilot context '{ctx}' as list item {itemId} in list {listId} (status {status}, code {code}); it will not be cached as unresolvable",
                    copilotDocContextId, driveItemId, spListId, ex.ResponseStatusCode, GraphErrorCode(ex));
                return FileResolutionResult.TransientFailure;
            }
            catch (ApiException ex)
            {
                _logger.LogWarning(ex, "Unexpected error getting file info for copilotDocContextId {copilotDocContextId}", copilotDocContextId);
                return FileResolutionResult.UnexpectedFailure;
            }
        }

        private async Task<FileResolutionResult> ResolveDriveItemFromSiteLibrariesAsync(string spSiteId, string defaultListId, string driveItemId, Site site, string copilotDocContextId)
        {
            var drivesResult = await GetDocumentLibraryDrivesAsync(spSiteId);
            if (drivesResult.ShouldStop)
            {
                return FileResolutionResult.FromFailure(drivesResult.Failure);
            }

            var failure = ResolutionFailure.Unresolvable;
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
                failure = Worst(failure, result.Failure);
            }

            return FileResolutionResult.FromFailure(failure);
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
            catch (ApiException ex) when (IsExpectedFileResolutionFailure(ex))
            {
                _logger.LogDebug("Graph could not enumerate document libraries for site {siteId} (status {status}, code {code})",
                    spSiteId, ex.ResponseStatusCode, GraphErrorCode(ex));
                _documentLibraryDrivesBySite.TryAdd(spSiteId, Array.Empty<Drive>());
                return DocumentLibraryDrivesResult.Found(Array.Empty<Drive>());
            }
            catch (ApiException ex) when (IsTransientFileResolutionFailure(ex))
            {
                _logger.LogWarning("Transient Graph error enumerating document libraries for site {siteId} (status {status}, code {code}); contexts depending on this fallback will not be cached as unresolvable",
                    spSiteId, ex.ResponseStatusCode, GraphErrorCode(ex));
                return DocumentLibraryDrivesResult.TransientFailure;
            }
            catch (ApiException ex)
            {
                _logger.LogWarning(ex, "Unexpected error enumerating document libraries for site {siteId}", spSiteId);
                return DocumentLibraryDrivesResult.UnexpectedFailure;
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
            catch (ApiException ex) when (IsExpectedFileResolutionFailure(ex))
            {
                _logger.LogDebug("Graph could not resolve SharePoint site {siteUrl} (status {status}, code {code})",
                    siteUrl, ex.ResponseStatusCode, GraphErrorCode(ex));
                return SiteResolutionResult.Unresolvable;
            }
            catch (ApiException ex) when (IsTransientFileResolutionFailure(ex))
            {
                _logger.LogWarning("Transient Graph error resolving SharePoint site {siteUrl} (status {status}, code {code}); context will not be cached as unresolvable",
                    siteUrl, ex.ResponseStatusCode, GraphErrorCode(ex));
                return SiteResolutionResult.TransientFailure;
            }
            catch (ApiException ex)
            {
                _logger.LogWarning(ex, "Unexpected error getting site info for site {siteUrl}", siteUrl);
                return SiteResolutionResult.UnexpectedFailure;
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
            catch (ApiException)
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
                catch (ApiException ex) when (IsExpectedFileResolutionFailure(ex))
                {
                    _logger.LogDebug("Graph could not resolve SharePoint site {siteUrl} while loading its default drive (status {status}, code {code})",
                        siteUrl, ex.ResponseStatusCode, GraphErrorCode(ex));
                    return DriveResolutionResult.Unresolvable;
                }
                catch (ApiException ex) when (IsTransientFileResolutionFailure(ex))
                {
                    _logger.LogWarning("Transient Graph error resolving SharePoint site {siteUrl} while loading its default drive (status {status}, code {code}); context will not be cached as unresolvable",
                        siteUrl, ex.ResponseStatusCode, GraphErrorCode(ex));
                    return DriveResolutionResult.TransientFailure;
                }
                catch (ApiException ex)
                {
                    _logger.LogWarning(ex, "Unexpected error getting site info for site {siteUrl}", siteUrl);
                    return DriveResolutionResult.UnexpectedFailure;
                }
                if (site != null)
                {
                    try
                    {
                        // Try one more time using site ID
                        siteDrive = await _spoGraphClient.GetSiteDriveAsync(site.Id);
                    }
                    catch (ApiException ex) when (IsExpectedFileResolutionFailure(ex))
                    {
                        _logger.LogDebug("Graph could not resolve default drive for site {siteId} (status {status}, code {code})",
                            site.Id, ex.ResponseStatusCode, GraphErrorCode(ex));
                    }
                    catch (ApiException ex) when (IsTransientFileResolutionFailure(ex))
                    {
                        _logger.LogWarning("Transient Graph error resolving default drive for site {siteId} (status {status}, code {code}); context will not be cached as unresolvable",
                            site.Id, ex.ResponseStatusCode, GraphErrorCode(ex));
                        return DriveResolutionResult.TransientFailure;
                    }
                    catch (ApiException ex)
                    {
                        _logger.LogWarning(ex, "Unexpected error resolving default drive for site {siteId}", site.Id);
                        return DriveResolutionResult.UnexpectedFailure;
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

        // Graph errors are classified by status code on Kiota's ApiException rather than on ODataError, its subclass:
        // an error response whose body is empty or can't be parsed - common for throttling and gateway errors -
        // arrives as a plain ApiException, which every ODataError-only handler here used to let escape unclassified.

        private static bool IsExpectedFileResolutionFailure(ApiException ex)
        {
            if (ex == null) return false;
            return ex.ResponseStatusCode == (int)HttpStatusCode.NotFound
                || ex.ResponseStatusCode == (int)HttpStatusCode.Forbidden;
        }

        private static bool IsTransientFileResolutionFailure(ApiException ex)
        {
            if (ex == null) return false;
            return ex.ResponseStatusCode == 429
                || ex.ResponseStatusCode >= 500;
        }

        /// <summary>Graph's error code, for the log, when the response had a body to read it from.</summary>
        private static string GraphErrorCode(ApiException ex) => (ex as ODataError)?.Error?.Code;

        /// <summary>
        /// Worth retrying: Graph throttling or a server error, or a request that couldn't connect.
        /// <see cref="CopilotEnrichmentTransientException"/> is how a lookup here reports giving up on one of those.
        /// </summary>
        internal static bool IsRetryableGraphFailure(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is CopilotEnrichmentTransientException || e is HttpRequestException)
                {
                    return true;
                }
                if (e is ApiException apiError && IsTransientFileResolutionFailure(apiError))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Takes one retry from the run's budget. Once it is spent, says so once: under sustained throttling, retrying
        /// every lookup for the rest of the run would only add load on a tenant that is already being throttled.
        /// </summary>
        private bool TryTakeTransientRetry()
        {
            int spent;
            do
            {
                spent = Volatile.Read(ref _transientRetriesSpent);
                if (spent >= TransientRetryBudgetPerRun)
                {
                    if (Interlocked.Exchange(ref _transientRetryBudgetSpentLogged, 1) == 0)
                    {
                        _logger.LogWarning($"Copilot file enrichment: the {TransientRetryBudgetPerRun} retries this import run allows for file lookups " +
                            "that Microsoft Graph failed transiently (throttling or server errors) are spent, so for the rest of the run such a " +
                            "lookup is tried once, and an interaction whose file can't be read then is saved without its file details.");
                    }
                    return false;
                }
            }
            while (Interlocked.CompareExchange(ref _transientRetriesSpent, spent + 1, spent) != spent);
            return true;
        }

        /// <summary>
        /// Why a lookup produced nothing. Ordered so the worst of several lookups' outcomes is the largest: a context
        /// that failed transiently on any path might still resolve, so that outcome wins.
        /// </summary>
        private enum ResolutionFailure
        {
            None = 0,

            /// <summary>Graph answered that it isn't there or can't be seen (404/403). Cached for the run.</summary>
            Unresolvable = 1,

            /// <summary>
            /// Any other Graph error. Not cached, since it may be specific to this attempt, but not retried either:
            /// waiting a few seconds doesn't change a 400.
            /// </summary>
            Unexpected = 2,

            /// <summary>Throttling or a server error (429/5xx). Not cached, and reported so the caller can retry.</summary>
            Transient = 3,
        }

        private static ResolutionFailure Worst(ResolutionFailure a, ResolutionFailure b) => a > b ? a : b;

        private sealed class FileResolutionResult
        {
            public SpoDocumentFileInfo FileInfo { get; private set; }
            public ResolutionFailure Failure { get; private set; }
            public bool CacheAsUnresolvable => Failure == ResolutionFailure.Unresolvable;

            public static FileResolutionResult Unresolvable { get; } = new FileResolutionResult { Failure = ResolutionFailure.Unresolvable };
            public static FileResolutionResult TransientFailure { get; } = new FileResolutionResult { Failure = ResolutionFailure.Transient };
            public static FileResolutionResult UnexpectedFailure { get; } = new FileResolutionResult { Failure = ResolutionFailure.Unexpected };
            public static FileResolutionResult Resolved(SpoDocumentFileInfo fileInfo) => new FileResolutionResult { FileInfo = fileInfo };
            public static FileResolutionResult FromFailure(ResolutionFailure failure)
                => failure == ResolutionFailure.Transient ? TransientFailure
                : failure == ResolutionFailure.Unexpected ? UnexpectedFailure
                : Unresolvable;
        }

        private sealed class SiteResolutionResult
        {
            public Site Site { get; private set; }
            public ResolutionFailure Failure { get; private set; }
            public bool ShouldStop => Site == null;

            public static SiteResolutionResult Unresolvable { get; } = new SiteResolutionResult { Failure = ResolutionFailure.Unresolvable };
            public static SiteResolutionResult TransientFailure { get; } = new SiteResolutionResult { Failure = ResolutionFailure.Transient };
            public static SiteResolutionResult UnexpectedFailure { get; } = new SiteResolutionResult { Failure = ResolutionFailure.Unexpected };
            public static SiteResolutionResult Found(Site site) => new SiteResolutionResult { Site = site };
        }

        private sealed class DriveResolutionResult
        {
            public Drive Drive { get; private set; }
            public ResolutionFailure Failure { get; private set; }
            public bool ShouldStop => Drive == null;

            public static DriveResolutionResult Unresolvable { get; } = new DriveResolutionResult { Failure = ResolutionFailure.Unresolvable };
            public static DriveResolutionResult TransientFailure { get; } = new DriveResolutionResult { Failure = ResolutionFailure.Transient };
            public static DriveResolutionResult UnexpectedFailure { get; } = new DriveResolutionResult { Failure = ResolutionFailure.Unexpected };
            public static DriveResolutionResult Found(Drive drive) => new DriveResolutionResult { Drive = drive };
        }

        private sealed class DocumentLibraryDrivesResult
        {
            public IReadOnlyList<Drive> Drives { get; private set; }
            public ResolutionFailure Failure { get; private set; }
            public bool ShouldStop => Drives == null;

            public static DocumentLibraryDrivesResult TransientFailure { get; } = new DocumentLibraryDrivesResult { Failure = ResolutionFailure.Transient };
            public static DocumentLibraryDrivesResult UnexpectedFailure { get; } = new DocumentLibraryDrivesResult { Failure = ResolutionFailure.Unexpected };
            public static DocumentLibraryDrivesResult Found(IReadOnlyList<Drive> drives) => new DocumentLibraryDrivesResult { Drives = drives ?? Array.Empty<Drive>() };
        }
    }
}
