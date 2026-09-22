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
    /// The <c>/users/delta</c> query this product tracks users with, and the version stamp that pins it.
    /// </summary>
    /// <remarks>
    /// Microsoft Graph fixes the <c>$select</c> when a delta token is first minted: a stored token
    /// continues the cycle it was created for, so widening the selection later does NOT start returning
    /// the new property to a tenant that already has one. That makes every <c>$select</c> change a
    /// breaking change for existing deployments unless the stored token is invalidated with it.
    ///
    /// <para>
    /// <see cref="SelectVersion"/> is part of the delta-token cache key, so bumping it discards the
    /// stored token and the next import performs one full enumeration under the new selection. That is
    /// the only thing that makes a newly selected property arrive for users who have not otherwise
    /// changed - and those are the overwhelming majority on an established tenant.
    /// </para>
    ///
    /// <para>
    /// <b>Bump <see cref="SelectVersion"/> in the same change that edits <see cref="Select"/>.</b>
    /// Forgetting it does not fail anywhere: the import keeps running, the new column simply stays
    /// empty forever on every upgraded tenant while looking correct on a fresh install. v2 added
    /// <c>createdDateTime</c> for the Copilot Adoption seat-tenure proxy.
    /// </para>
    /// </remarks>
    public static class GraphUserDeltaQuery
    {
        /// <summary>Bump whenever <see cref="Select"/> changes. Part of the delta-token cache key.</summary>
        public const string SelectVersion = "v2";

        /// <summary>
        /// Properties tracked for user changes.
        /// </summary>
        /// <remarks>
        /// assignedLicenses / assignedPlans are here as defence-in-depth so that a user whose ONLY
        /// change is a licence assignment is still surfaced by /users/delta on subsequent runs. The
        /// primary correctness guarantee for licence counts comes from UserMetadataUpdater /
        /// UserLicenseProcessor processing the full DB user population each run, not just delta users.
        ///
        /// createdDateTime is Entra's immutable account-creation timestamp, used by Copilot Adoption as
        /// the seat-tenure proxy until real licence-assignment history exists.
        /// </remarks>
        public const string Select =
            "id,accountEnabled,createdDateTime,officeLocation,usageLocation,jobTitle,department,mail,"
            + "userPrincipalName,manager,companyName,postalCode,country,state,assignedLicenses,assignedPlans";
    }

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

        /// <summary>
        /// The extra Graph properties this tenant's configured user-org types need. Defaults to
        /// <see cref="GraphUserOrgSelection.None"/>, so a caller that never configures orgs issues
        /// exactly the request this product has always issued.
        /// </summary>
        private GraphUserOrgSelection _orgSelection = GraphUserOrgSelection.None;

        /// <summary>
        /// Set when a request carrying the org properties was rejected, so the retry - and the rest of
        /// the cycle - runs without them.
        /// </summary>
        private bool _orgSelectionRejected;

        /// <summary>
        /// Whether the most recent attempt actually sent a stored delta token.
        /// </summary>
        /// <remarks>
        /// Load-bearing for telling a dead token apart from an unusable <c>$select</c>: Graph answers
        /// 400 for both, and mistaking one for the other leaves organisation values permanently stale.
        /// </remarks>
        private bool _lastLoadUsedStoredToken;

        public GraphUserLoader(ManualGraphCallClient httpClient, IDeltaValueProvider deltaValueProvider, ILogger logger, GraphServiceClient graphServiceClient)
        {
            this._httpClient = httpClient;
            _deltaValueProvider = deltaValueProvider;
            this._logger = logger;
            this._graphServiceClient = graphServiceClient;
        }

        public IDeltaValueProvider DeltaValueProvider => _deltaValueProvider;

        /// <summary>
        /// Declares which org attributes this cycle should read.
        /// </summary>
        /// <remarks>
        /// This is the single place the <c>$select</c> and the delta-token cache key are derived from
        /// the same object. Pushing the qualifier into the provider here - rather than leaving the
        /// caller to do it - is what makes it impossible for the two to drift apart and have a token
        /// minted under one selection reused under another.
        /// </remarks>
        public void SetOrgSelection(GraphUserOrgSelection orgSelection)
        {
            _orgSelection = orgSelection ?? GraphUserOrgSelection.None;
            _orgSelectionRejected = false;
            _deltaValueProvider.SetKeyQualifier(_orgSelection.DeltaKeyQualifier);

            if (_orgSelection.UnparseableAttributeNames.Count > 0)
            {
                _logger.LogError(
                    $"User import - {_orgSelection.UnparseableAttributeNames.Count} configured organisation "
                    + "attribute(s) could not be understood and will be skipped. Re-save them on the User "
                    + "organisations page to correct them.");
            }

            if (!_orgSelection.IsEmpty)
            {
                _logger.LogInformation(
                    $"User import - also reading organisation attributes from Graph: {_orgSelection}.");
            }
        }

        /// <summary>Whether Graph rejected the configured org properties during this cycle.</summary>
        public bool OrgSelectionWasRejected => _orgSelectionRejected;

        public async Task<List<GraphUser>> LoadAllActiveUsers()
        {
            var results = await LoadWithOrgFallback().ConfigureAwait(false);

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
        /// Loads the users, distinguishing a stale delta token from an unusable <c>$select</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Graph answers a 400 both for a property it does not recognise <b>and</b> for a delta token
        /// it will not accept - an expired one, or one minted under a different query. Treating every
        /// 400 as "the administrator's attribute is wrong" was a genuine trap: the retry would drop
        /// the org properties, commit a fresh token under the <i>unqualified</i> key, and leave the
        /// stale token sitting on the qualified key forever. Every later cycle would pick that stale
        /// token up again, 400 again, and fall back again - so organisation values would never update
        /// again while the user import looked perfectly healthy.
        /// </para>
        /// <para>
        /// So a token is ruled out first. If one was sent, it is discarded and the <b>same</b>
        /// selection retried, which is a full enumeration and the correct response to a dead token.
        /// Only a request that carried no token can blame the selection.
        /// </para>
        /// </remarks>
        private async Task<List<GraphUser>> LoadWithOrgFallback()
        {
            try
            {
                return await LoadUsersPageByPage(_orgSelection).ConfigureAwait(false);
            }
            catch (GraphHttpException ex) when (!_orgSelection.IsEmpty)
            {
                if (_lastLoadUsedStoredToken)
                {
                    _logger.LogWarning(
                        $"User import - Microsoft Graph rejected the request (HTTP {(int)ex.StatusCode}) while a stored "
                        + "delta token was in use. Discarding the token and re-reading every user once, keeping the "
                        + "configured organisation attributes. If the attributes themselves are the problem, the retry "
                        + "will say so.");

                    await _deltaValueProvider.ClearDeltaToken().ConfigureAwait(false);

                    try
                    {
                        return await LoadUsersPageByPage(_orgSelection).ConfigureAwait(false);
                    }
                    catch (GraphHttpException retryEx)
                    {
                        return await FallBackWithoutOrgAttributes(retryEx).ConfigureAwait(false);
                    }
                }

                return await FallBackWithoutOrgAttributes(ex).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Drops the org properties and reloads, so the rest of the user import still completes.
        /// </summary>
        /// <remarks>
        /// Reached only once a stale delta token has been ruled out, so the selection really is the
        /// remaining suspect. The org properties are the only part of this query an administrator can
        /// edit at runtime, and Graph fails the WHOLE request over one of them - which would otherwise
        /// stop licences, managers and department metadata importing as well.
        ///
        /// The retry is deliberately not restricted to a 400. The fallback load is lenient (see
        /// <see cref="LoadUsersPageByPage"/>), which is exactly what this method did before org
        /// attributes existed, so falling back on any HTTP failure leaves behaviour identical to the
        /// old behaviour in every non-400 case instead of newly propagating a transient 500.
        /// </remarks>
        private async Task<List<GraphUser>> FallBackWithoutOrgAttributes(GraphHttpException ex)
        {
            _orgSelectionRejected = true;

            if (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                _logger.LogError(
                    $"User import - Microsoft Graph rejected the configured organisation attributes "
                    + $"({_orgSelection}). Organisation values will NOT be refreshed this cycle, but the rest of the "
                    + "user import will continue. Check those attributes still exist in the tenant on the User "
                    + "organisations page. Graph said: " + ex.Message);
            }
            else
            {
                _logger.LogWarning(
                    $"User import - the request carrying the configured organisation attributes ({_orgSelection}) "
                    + $"failed with HTTP {(int)ex.StatusCode}. Retrying without them so the rest of the user import "
                    + "can continue. Organisation values will NOT be refreshed this cycle.");
            }

            // The token key follows the selection, so falling back has to re-point the provider at the
            // unqualified key - otherwise this cycle would store a token under the org-qualified key
            // while querying without the org properties.
            _deltaValueProvider.SetKeyQualifier(GraphUserOrgSelection.None.DeltaKeyQualifier);

            return await LoadUsersPageByPage(GraphUserOrgSelection.None).ConfigureAwait(false);
        }

        private async Task<List<GraphUser>> LoadUsersPageByPage(GraphUserOrgSelection orgSelection)
        {
            // Cache delta using tenant ID
            var usersQueryDelta = await _deltaValueProvider.GetDeltaToken();
            _lastLoadUsedStoredToken = !string.IsNullOrEmpty(usersQueryDelta);
            var initialDeltaUrl = $"https://graph.microsoft.com:443/v1.0/users/delta" +
                $"?$select={orgSelection.BuildSelect(GraphUserDeltaQuery.Select)}" +
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
                },
                // Strict ONLY while org attributes are in play. LoadAllPagesPlusDeltaWithThrottleRetries
                // otherwise swallows a non-transient HTTP failure, logs a warning and returns the rows
                // gathered so far - so a 400 from an unrecognised $select property would come back as an
                // empty user list rather than an exception, the fallback above would never run, and the
                // import would quietly do nothing every cycle with only a warning to show for it.
                //
                // Left lenient when there are no org attributes, which is the behaviour every existing
                // deployment has today: this code path must not change for them.
                throwOnHttpError: !orgSelection.IsEmpty);

            if (string.IsNullOrEmpty(usersQueryDelta))
            {
                _logger.LogInformation($"User import - read {results.Count.ToString("N0")} users (all) from Graph API");
            }
            else
            {
                _logger.LogInformation($"User import - read {results.Count.ToString("N0")} updated users from Graph API, using last delta.");
            }

            return results;
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
