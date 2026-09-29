using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GraphUserDeltaQuery = Common.Entities.Redis.GraphUserDeltaQuery;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Graph API implementation of user metadata loader
    /// </summary>
    public class GraphUserLoader : IUserMetadataLoader
    {
        private readonly ManualGraphCallClient _httpClient;
        private readonly IDeltaValueProvider _deltaValueProvider;
        private readonly ILogger _logger;
        private readonly GraphServiceClient _graphServiceClient;

        // Buffer the delta token returned by Graph during the most recent
        // LoadAllActiveUsers call. We do NOT persist it to the underlying
        // IDeltaValueProvider until the caller explicitly commits via
        // CommitDeltaTokenAsync, which happens only after the entire user
        // import (insert + metadata update + license update) has succeeded.
        // This guarantees that a mid-import failure doesn't cause us to skip
        // the failed users on the next cycle.
        private string _pendingDeltaToken;
        private bool _hasPendingDeltaToken;

        // The stored token the buffered read continued from, or null when it read the full user list. Commit
        // uses it to notice the checkpoint being cleared while this import was running (see CommitDeltaTokenAsync).
        private string _pendingBaseToken;

        public GraphUserLoader(ManualGraphCallClient httpClient, IDeltaValueProvider deltaValueProvider, ILogger logger, GraphServiceClient graphServiceClient)
        {
            this._httpClient = httpClient;
            _deltaValueProvider = deltaValueProvider;
            this._logger = logger;
            this._graphServiceClient = graphServiceClient;
        }

        public IDeltaValueProvider DeltaValueProvider => _deltaValueProvider;

        /// <inheritdoc />
        public bool LastLoadReachedDeltaLink { get; private set; }

        public async Task<List<GraphUser>> LoadAllActiveUsers()
        {
            // Reset any previously buffered token, and the completeness flag, before a new load.
            _pendingDeltaToken = null;
            _hasPendingDeltaToken = false;
            _pendingBaseToken = null;
            LastLoadReachedDeltaLink = false;

            // Cache delta using tenant ID
            var usersQueryDelta = await _deltaValueProvider.GetDeltaToken();
            var read = await ReadDeltaRound(usersQueryDelta);

            // Graph refuses a token it considers too old, or has reset. Replaying it can never succeed and nothing
            // else ever clears it, so without this every later run read 0 users and still counted as a success
            // (issue #664). Only the initial request carries the token - later pages follow $skiptoken links - so
            // only a failure on page 1 can be a rejection of it.
            if (read.Failure != null && read.FailedPage == 1
                && GraphDeltaTokenRejection.IsRejectedDeltaToken(read.Failure, requestCarriedDeltaToken: !string.IsNullOrEmpty(usersQueryDelta)))
            {
                _logger.LogWarning($"User import - Microsoft Graph rejected the stored /users/delta token ({DescribeFailure(read.Failure)}): it has expired, or Graph has reset synchronisation. " +
                    "Discarding the token and reading the full user list again in this run, which also catches up on every change made while it was unusable.");

                try
                {
                    // ClearDeltaToken rather than overwriting the key: the Redis store also forgets its in-process
                    // fallback copy, which would otherwise hand the dead token back the next time a read fails.
                    await _deltaValueProvider.ClearDeltaToken();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"User import - couldn't discard the rejected /users/delta token: {ex.Message}. " +
                        "Not reading the full user list in this run, so the run is incomplete; the next cycle will try again.");
                    return new List<GraphUser>();
                }

                // Once, and with no $deltatoken at all. Never $deltatoken=latest: that means "sync from now" and would
                // skip every change made while the import was stuck. A failure of this read cannot be taken for a
                // token rejection, because it carries no token, so the recovery can never loop.
                usersQueryDelta = null;
                read = await ReadDeltaRound(null);
            }

            if (read.Failure != null)
            {
                var tokenOutcome = string.IsNullOrEmpty(usersQueryDelta)
                    ? "No delta token was in use, so the next run reads the full user list again."
                    : "The stored delta token was kept, because this failure does not mean it has expired; the next run resumes from it.";
                _logger.LogWarning($"User import - the /users/delta read stopped on page {read.FailedPage:N0} ({DescribeFailure(read.Failure)}) after {read.Users.Count:N0} user(s). {tokenOutcome}");
            }
            else if (!_hasPendingDeltaToken)
            {
                _logger.LogWarning("User import - the /users/delta read ended without an @odata.deltaLink, so there is no new delta token to save.");
            }
            else
            {
                LastLoadReachedDeltaLink = true;
                _pendingBaseToken = usersQueryDelta;
            }

            var results = read.Users;
            if (string.IsNullOrEmpty(usersQueryDelta))
            {
                _logger.LogInformation($"User import - read {results.Count.ToString("N0")} users (all) from Graph API");
            }
            else
            {
                _logger.LogInformation($"User import - read {results.Count.ToString("N0")} updated users from Graph API, using last delta.");
            }

            // Graph for some reason gives duplicates; filter that out.
            // HashSet pre-allocated to results.Count avoids the per-Grouping allocation that
            // GroupBy + First would do - at 200k users that's ~200k fewer allocations.
            var seenUpns = new HashSet<string>(results.Count, StringComparer.OrdinalIgnoreCase);
            var allGraphUsers = new List<GraphUser>(results.Count);
            foreach (var u in results)
            {
                if (string.IsNullOrEmpty(u.UserPrincipalName))
                {
                    continue;
                }
                if (seenUpns.Add(u.UserPrincipalName))
                {
                    allGraphUsers.Add(u);
                }
            }

            var allActiveGraphUsers = allGraphUsers.Where(u => u.AccountEnabled.HasValue && u.AccountEnabled.Value).ToList();

            return allActiveGraphUsers;
        }

        /// <summary>
        /// One pass over <c>/users/delta</c>, from <paramref name="deltaToken"/> or, when it is empty, from the
        /// start. Buffers the new token if the pass reaches an <c>@odata.deltaLink</c>, and records the failure
        /// that ended it early, if one did.
        /// </summary>
        private async Task<DeltaReadRound> ReadDeltaRound(string deltaToken)
        {
            var url = $"https://graph.microsoft.com:443/v1.0/users/delta" +
                $"?$select={GraphUserDeltaQuery.Select}" +
                "&$expand=manager";
            if (!string.IsNullOrEmpty(deltaToken))
            {
                url += $"&$deltatoken={deltaToken}";
            }

            var round = new DeltaReadRound();
            round.Users = await _httpClient.LoadAllPagesPlusDeltaWithThrottleRetries<GraphUser>(url, _logger,
                (deltaLink) =>
                {
                    // Buffer the new delta in memory. It will only be persisted to
                    // the underlying provider when CommitDeltaTokenAsync is called
                    // after the rest of the import succeeds.
                    _pendingDeltaToken = StringUtils.ExtractCodeFromGraphUrl(deltaLink);
                    _hasPendingDeltaToken = true;
                    return Task.CompletedTask;
                },
                // Lenient paging, deliberately: a failure part-way still returns the users read so far, and the
                // rest of the import - the licence refresh in particular, which does not use this token - still
                // runs. What must not happen is the failure passing for "nothing changed", so it is recorded
                // here and the load reports itself incomplete.
                onPageFailed: (ex, page) =>
                {
                    round.Failure = ex;
                    round.FailedPage = page;
                });

            return round;
        }

        /// <summary>
        /// The HTTP status and Graph error code, without the URL: the URL carries the delta token, and
        /// <see cref="ManualGraphCallClient"/> has already logged it in full.
        /// </summary>
        private static string DescribeFailure(Exception failure)
        {
            var graphError = failure as GraphHttpException;
            if (graphError == null)
            {
                return failure.Message;
            }

            return $"HTTP {(int)graphError.StatusCode} ({graphError.StatusCode}), Graph error code '{graphError.GraphErrorCode ?? "unknown"}'";
        }

        private sealed class DeltaReadRound
        {
            public List<GraphUser> Users { get; set; }
            public Exception Failure { get; set; }
            public int FailedPage { get; set; }
        }

        /// <inheritdoc />
        public async Task<bool> CommitDeltaTokenAsync()
        {
            if (!_hasPendingDeltaToken)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(_pendingBaseToken))
            {
                // This run continued from a stored checkpoint. If that checkpoint has gone since, it was cleared
                // while the run was in progress - by an admin on the web portal's User import page, or by hand in
                // Redis - to ask for a full re-read. Saving this run's token would quietly undo that request, so
                // it is withheld and the next run reads the full user list, as the clear intended. A failed read
                // here falls back to the checkpoint this run started from, so a Redis blip still saves as before.
                var stored = await _deltaValueProvider.GetDeltaToken();
                if (string.IsNullOrEmpty(stored))
                {
                    _logger.LogWarning("User import - the stored /users/delta checkpoint was cleared while this import was running, so this run's new checkpoint is not being saved. " +
                        "The next run reads the full user list, as the clear intended.");
                    _pendingDeltaToken = null;
                    _hasPendingDeltaToken = false;
                    _pendingBaseToken = null;
                    return false;
                }
            }

            await _deltaValueProvider.SetDeltaToken(_pendingDeltaToken);
            _pendingDeltaToken = null;
            _hasPendingDeltaToken = false;
            _pendingBaseToken = null;
            return true;
        }

        public async Task<List<SubscribedSku>> LoadTenantSkus()
        {
            try
            {
                var page = await _graphServiceClient.SubscribedSkus.GetAsync();

                if (page?.Value == null)
                {
                    // A 200 with no `value` collection is NOT the same answer as "this tenant has no
                    // SKUs". The licence refresh treats the SKU list as the authority on who holds
                    // what, so a spuriously empty list would have it remove every licence in the
                    // tenant. Throw rather than returning null: null is the signal for the *403
                    // permissions* case below, which routes into the per-user fallback - and that
                    // fallback loads every user's licence lookups and makes one Graph call per user,
                    // which at 200k users is both enormously slow and documented as OOM-prone. A
                    // missing `value` on a 200 is transient, so failing the cycle and retrying is
                    // both cheaper and safer. See issue #392.
                    throw new InvalidOperationException(
                        "User import - the tenant SKU response contained no 'value' collection. Aborting rather than treating it as 'this tenant has no SKUs', which would delete every licence assignment in the database.");
                }

                // /subscribedSkus is a paginated collection. Reading only the first page would hand
                // the refresh a SKU list that silently omits everything on page 2 onwards, and every
                // assignment for those licence types would then be deleted - the same failure as an
                // empty list, just partial. Walk every page and refuse to return an incomplete one.
                var allSkus = new List<SubscribedSku>();
                var iterator = PageIterator<SubscribedSku, SubscribedSkuCollectionResponse>
                    .CreatePageIterator(_graphServiceClient, page, sku =>
                    {
                        allSkus.Add(sku);
                        return true;
                    });

                await iterator.IterateAsync();

                if (iterator.State != PagingState.Complete)
                {
                    throw new InvalidOperationException(
                        $"User import - could not read the complete list of tenant SKUs (paging ended in state '{iterator.State}' after {allSkus.Count.ToString("N0")} SKU(s)). Aborting rather than reconciling licences against a partial SKU list, which would delete every assignment for the SKUs that were missed.");
                }

                return allSkus;
            }
            catch (ODataError ex)
            {
                if (ex.ResponseStatusCode != (int)System.Net.HttpStatusCode.Forbidden)
                {
                    // Only a 403 justifies the per-user fallback: that is a persistent consent
                    // problem ('Organization.Read.All' not granted) where per-user licence calls are
                    // the only way to make progress. Anything else - 429 throttling, 500, 503 - is
                    // transient, and falling back would turn one cheap failed request into one Graph
                    // call per user (200k on a large tenant, while we are already being throttled),
                    // down a path whose own comment warns it can run out of memory. Fail the cycle
                    // and retry instead; the delta token is not committed, so nothing is lost. This
                    // also keeps first-page failures consistent with page 2+, which throw
                    // ServiceException from the PageIterator and already abort. See issue #392.
                    _logger.LogError(ex, $"User import - couldn't load SKUs for org - {ex.Message}");
                    throw;
                }

                _logger.LogError($"User import - couldn't load SKUs for org - {ex.Message}. Ensure 'Organization.Read.All' in granted.");

                // If we can't get tenant SKUs to find all users by, we can get SKUs per user instead, but this can be very slow.
                _logger.LogWarning($"User import - will load SKUs directly from each user instead. This will be slow.");
                return null;
            }
        }

        public async Task<List<Microsoft.Graph.Models.User>> LoadUsersBySku(Guid skuId)
        {
            // Per-iteration safety cap: at 200k-user scale a runaway nextLink could allocate
            // memory until OOM. 1M users per SKU is comfortably above any real tenant we expect
            // to see and will fail the import rather than silently filling memory forever.
            const int MAX_USERS_PER_SKU = 1_000_000;
            var allUsersWithSku = new List<Microsoft.Graph.Models.User>();

            var firstPage = await _graphServiceClient.Users.GetAsync(rc =>
            {
                rc.QueryParameters.Select = new[] { "userPrincipalName" };
                rc.QueryParameters.Filter = $"assignedLicenses/any(u:u/skuId eq {skuId})";
            });

            // The licence refresh removes any assignment this list does not report, so an
            // incomplete answer must NEVER be passed off as a complete one - it would delete
            // licences that users still hold. Fail the import instead: the delta token is only
            // committed on success, so the next cycle retries with nothing destroyed. Issue #392.
            if (firstPage == null)
            {
                throw new InvalidOperationException(
                    $"User import - Graph returned no response listing users for SKU {skuId}. Aborting rather than treating it as 'nobody holds this SKU', which would delete every assignment for it.");
            }

            int loaded = 0;
            var iterator = PageIterator<Microsoft.Graph.Models.User, UserCollectionResponse>
                .CreatePageIterator(_graphServiceClient, firstPage, user =>
                {
                    // Check BEFORE adding, so a result of exactly MAX_USERS_PER_SKU is a complete
                    // result rather than a false "truncated" that would fail every cycle forever.
                    // Pausing here means a genuine (cap + 1)th user exists.
                    if (loaded >= MAX_USERS_PER_SKU)
                    {
                        return false;
                    }
                    allUsersWithSku.Add(user);
                    loaded++;
                    return true;
                });

            await iterator.IterateAsync();

            if (iterator.State == PagingState.Paused)
            {
                throw new InvalidOperationException(
                    $"User import - hit MAX_USERS_PER_SKU ({MAX_USERS_PER_SKU:N0}) walking users for SKU {skuId}; the result is truncated at {allUsersWithSku.Count:N0} users. Aborting rather than reconciling against a partial list, which would delete the licences of every user past the cap.");
            }

            _logger.LogDebug($"SKU {skuId} loaded {allUsersWithSku.Count:N0} users");

            return allUsersWithSku;
        }

        public async Task<List<LicenseDetails>> LoadUserLicenseDetails(string userId)
        {
            try
            {
                var page = await _graphServiceClient.Users[userId].LicenseDetails.GetAsync(rc =>
                {
                    rc.QueryParameters.Select = new[] { "skuPartNumber", "skuId" };
                });

                if (page?.Value == null)
                {
                    // Same trap as the tenant SKU list, one user at a time: ProcessUserLicenses
                    // deletes this user's existing lookups and re-adds from whatever comes back, so
                    // treating a missing 'value' collection as "this user holds no licences" quietly
                    // deletes their licences. Returning null is the established "couldn't read it"
                    // signal that ProcessUserLicenses already skips on, so their existing licences
                    // are retained. Note this does NOT force a re-read: the import still commits the
                    // /users/delta token, so a user whose licence call failed keeps their previous
                    // (now possibly stale) licences until they next appear in a delta or the delta
                    // token is cleared. Stale beats deleted - that is the whole point of #392 - but
                    // it is not self-correcting on the next cycle.
                    _logger.LogError($"User import - the licence-details response for user ID '{userId}' contained no 'value' collection. Keeping that user's existing licences rather than treating it as 'no licences'; they will not be re-read until the user next changes in Graph.");
                    return null;
                }

                return page.Value;
            }
            catch (ODataError ex)
            {
                _logger.LogError(ex, $"User import - couldn't load service-plans for user ID '{userId}' - {ex.Message}");
                return null;
            }
        }
    }
}
