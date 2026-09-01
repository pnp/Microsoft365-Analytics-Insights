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

        public GraphUserLoader(ManualGraphCallClient httpClient, IDeltaValueProvider deltaValueProvider, ILogger logger, GraphServiceClient graphServiceClient)
        {
            this._httpClient = httpClient;
            _deltaValueProvider = deltaValueProvider;
            this._logger = logger;
            this._graphServiceClient = graphServiceClient;
        }

        public IDeltaValueProvider DeltaValueProvider => _deltaValueProvider;

        public async Task<List<GraphUser>> LoadAllActiveUsers()
        {
            // Cache delta using tenant ID
            var usersQueryDelta = await _deltaValueProvider.GetDeltaToken();
            // assignedLicenses / assignedPlans are added to $select as defence-in-depth
            // so that a user whose ONLY change is a licence assignment will still be
            // surfaced by /users/delta on subsequent runs. The primary correctness
            // guarantee for licence counts now comes from UserMetadataUpdater /
            // UserLicenseProcessor processing the full DB user population each run
            // (not just delta users) - selecting these fields here is a belt-and-
            // braces measure: without them, Graph would not flag a user as changed
            // when only their licence assignments were updated, even though those
            // are tracked properties on the underlying user object.
            var initialDeltaUrl = $"https://graph.microsoft.com:443/v1.0/users/delta" +
                "?$select=id,accountEnabled,officeLocation,usageLocation,jobTitle,department,mail,userPrincipalName,manager,companyName,postalCode,country,state,assignedLicenses,assignedPlans" +
                "&$expand=manager";
            if (!string.IsNullOrEmpty(usersQueryDelta))
            {
                initialDeltaUrl += $"&$deltatoken={usersQueryDelta}";
            }

            // Reset any previously buffered token before a new load.
            _pendingDeltaToken = null;
            _hasPendingDeltaToken = false;

            var results = await _httpClient.LoadAllPagesPlusDeltaWithThrottleRetries<GraphUser>(initialDeltaUrl, _logger,
                (deltaLink) =>
                {
                    // Buffer the new delta in memory. It will only be persisted to
                    // the underlying provider when CommitDeltaTokenAsync is called
                    // after the rest of the import succeeds.
                    _pendingDeltaToken = StringUtils.ExtractCodeFromGraphUrl(deltaLink);
                    _hasPendingDeltaToken = true;
                    return Task.CompletedTask;
                });


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

        public async Task CommitDeltaTokenAsync()
        {
            if (_hasPendingDeltaToken)
            {
                await _deltaValueProvider.SetDeltaToken(_pendingDeltaToken);
                _pendingDeltaToken = null;
                _hasPendingDeltaToken = false;
            }
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
