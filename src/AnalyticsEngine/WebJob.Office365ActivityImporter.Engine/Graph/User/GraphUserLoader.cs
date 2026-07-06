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
                // /subscribedSkus typically returns a small number of rows (<=100) so a single
                // GET is enough. We materialise into a List<T> so callers don't need to know
                // about Kiota response wrappers.
                var page = await _graphServiceClient.SubscribedSkus.GetAsync();
                return page?.Value ?? new List<SubscribedSku>();
            }
            catch (ODataError ex)
            {
                if (ex.ResponseStatusCode == (int)System.Net.HttpStatusCode.Forbidden)
                {
                    _logger.LogError($"User import - couldn't load SKUs for org - {ex.Message}. Ensure 'Organization.Read.All' in granted.");
                }
                else
                {
                    _logger.LogError(ex, $"User import - couldn't load SKUs for org - {ex.Message}");
                }

                // If we can't get tenant SKUs to find all users by, we can get SKUs per user instead, but this can be very slow.
                _logger.LogWarning($"User import - will load SKUs directly from each user instead. This will be slow.");
                return null;
            }
        }

        public async Task<List<Microsoft.Graph.Models.User>> LoadUsersBySku(Guid skuId)
        {
            // Per-iteration safety cap: at 200k-user scale a runaway nextLink could allocate
            // memory until OOM. 1M users per SKU is comfortably above any real tenant we expect
            // to see and will trip a warning instead of silently filling memory forever.
            const int MAX_USERS_PER_SKU = 1_000_000;
            var allUsersWithSku = new List<Microsoft.Graph.Models.User>();

            var firstPage = await _graphServiceClient.Users.GetAsync(rc =>
            {
                rc.QueryParameters.Select = new[] { "userPrincipalName" };
                rc.QueryParameters.Filter = $"assignedLicenses/any(u:u/skuId eq {skuId})";
            });

            if (firstPage == null)
            {
                return allUsersWithSku;
            }

            int loaded = 0;
            var iterator = PageIterator<Microsoft.Graph.Models.User, UserCollectionResponse>
                .CreatePageIterator(_graphServiceClient, firstPage, user =>
                {
                    allUsersWithSku.Add(user);
                    loaded++;
                    return loaded < MAX_USERS_PER_SKU;
                });

            await iterator.IterateAsync();

            if (iterator.State == PagingState.Paused)
            {
                _logger.LogWarning($"User import - hit MAX_USERS_PER_SKU ({MAX_USERS_PER_SKU:N0}) walking users for SKU {skuId}. Returning partial result of {allUsersWithSku.Count:N0} users.");
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
                return page?.Value ?? new List<LicenseDetails>();
            }
            catch (ODataError ex)
            {
                _logger.LogError(ex, $"User import - couldn't load service-plans for user ID '{userId}' - {ex.Message}");
                return null;
            }
        }
    }
}
