using Common.Entities.UserOrgs;
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
using GraphUserDeltaQuery = Common.Entities.State.GraphUserDeltaQuery;

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
                // The names are printed, not just counted. An admin reading this log has to know WHICH
                // organisation type to go and re-save, and every extensionAttributeN collapses to the
                // same $select fragment - so the fragment list identifies nothing.
                _logger.LogError(
                    $"User import - {_orgSelection.UnparseableAttributeNames.Count} configured organisation "
                    + "attribute(s) could not be understood and will be skipped: "
                    + string.Join(", ", _orgSelection.UnparseableAttributeNames)
                    + ". Re-save them on the User organisations page to correct them.");
            }

            if (!_orgSelection.IsEmpty)
            {
                _logger.LogInformation(
                    $"User import - also reading organisation attributes from Graph: {_orgSelection}.");
            }
        }

        public async Task ClearStoredDeltaTokensAsync()
        {
            await _deltaValueProvider.ClearDeltaToken().ConfigureAwait(false);

            // The unqualified key too: it is where FallBackWithoutOrgAttributes resumes from.
            var qualifier = _orgSelection.DeltaKeyQualifier;
            if (!string.IsNullOrEmpty(qualifier))
            {
                _deltaValueProvider.SetKeyQualifier(GraphUserOrgSelection.None.DeltaKeyQualifier);
                try
                {
                    await _deltaValueProvider.ClearDeltaToken().ConfigureAwait(false);
                }
                finally
                {
                    _deltaValueProvider.SetKeyQualifier(qualifier);
                }
            }
        }

        /// <summary>Whether Graph rejected the configured org properties during this cycle.</summary>
        public bool OrgSelectionWasRejected => _orgSelectionRejected;

        public async Task<List<GraphUser>> LoadAllActiveUsers()
        {
            // Reset any previously buffered token, and the completeness flag, before a new load.
            _pendingDeltaToken = null;
            _hasPendingDeltaToken = false;
            _pendingBaseToken = null;
            LastLoadReachedDeltaLink = false;

            var read = await ReadWithTokenRecovery(_orgSelection);

            // The request carrying the organisation attributes failed, and not because of a dead token -
            // ReadWithTokenRecovery has already ruled that out. Any such failure is worth one attempt without
            // them, because the alternative is that licences, managers and department metadata stop importing
            // too. It also keeps a partial org-carrying read away from the organisation merge, which only ever
            // sees a complete one.
            if (read != null && read.Failure != null && !_orgSelection.IsEmpty)
            {
                read = await FallBackWithoutOrgAttributes(read.Failure);
            }

            if (read == null)
            {
                // A rejected token could not be discarded; ReadWithTokenRecovery has logged why.
                return new List<GraphUser>();
            }

            var usersQueryDelta = read.DeltaToken;
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
        /// Reads <c>/users/delta</c> under one selection from the key's stored token, recovering by itself from
        /// a token Graph refuses: the token is discarded and the full user list read once, with the same selection.
        /// </summary>
        /// <returns>The read, or <c>null</c> when a rejected token could not be discarded.</returns>
        /// <remarks>
        /// Applies to every selection a cycle tries, not just the first. The without-organisations fallback
        /// switches to the unqualified key, which on a tenant that has had organisations configured for a while
        /// holds a token nobody has written since before the feature was enabled - so it is precisely the read
        /// most likely to meet a dead token.
        /// </remarks>
        private async Task<DeltaReadRound> ReadWithTokenRecovery(GraphUserOrgSelection selection)
        {
            // Cache delta using tenant ID
            var usersQueryDelta = await _deltaValueProvider.GetDeltaToken();
            var read = await ReadDeltaRound(selection, usersQueryDelta);

            // Graph refuses a token it considers too old, or has reset. Replaying it can never succeed and nothing
            // else ever clears it, so without this every later run read 0 users and still counted as a success
            // (issue #664). Only the initial request carries the token - later pages follow $skiptoken links - so
            // only a failure on page 1 can be a rejection of it.
            var requestCarriedDeltaToken = !string.IsNullOrEmpty(usersQueryDelta);
            if (read.Failure != null && read.FailedPage == 1)
            {
                if (GraphDeltaTokenRejection.IsRejectedDeltaToken(read.Failure, requestCarriedDeltaToken))
                {
                    _logger.LogWarning($"User import - Microsoft Graph rejected the stored /users/delta token ({DescribeFailure(read.Failure)}): it has expired, or Graph has reset synchronisation. " +
                        "Discarding the token and reading the full user list again in this run, which also catches up on every change made while it was unusable.");
                }
                else if (IsAmbiguousOrgCycleRejection(read.Failure, requestCarriedDeltaToken))
                {
                    _logger.LogWarning($"User import - Microsoft Graph refused a request that carried the stored /users/delta token ({DescribeFailure(read.Failure)}). " +
                        "With organisation attributes configured that is how Graph answers a dead token as well as an attribute it does not recognise, so the token is ruled out first: " +
                        "discarding it and reading the full user list again in this run. This cycle will take longer than usual.");
                }
                else
                {
                    return read;
                }

                try
                {
                    // ClearDeltaToken rather than overwriting the key: the persisted store also forgets its in-process
                    // fallback copy, which would otherwise hand the dead token back the next time a read fails.
                    await _deltaValueProvider.ClearDeltaToken();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"User import - couldn't discard the rejected /users/delta token: {ex.Message}. " +
                        "Not reading the full user list in this run, so the run is incomplete; the next cycle will try again.");
                    return null;
                }

                // Once, and with no $deltatoken at all. Never $deltatoken=latest: that means "sync from now" and would
                // skip every change made while the import was stuck. A failure of this read cannot be taken for a
                // token rejection, because it carries no token, so the recovery can never loop.
                read = await ReadDeltaRound(selection, null);
            }

            return read;
        }

        /// <summary>
        /// Whether, in a cycle that reads organisation attributes, a refused request that carried a token must be
        /// treated as a dead token even though <see cref="GraphDeltaTokenRejection"/> does not recognise its shape.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Graph answers a 400 both for a property it does not recognise <b>and</b> for a delta token it will not
        /// accept - an expired one, or one minted under a different query - and not always with an error code the
        /// narrow test knows. Treating such a 400 as "the administrator's attribute is wrong" was a genuine trap:
        /// the fallback would drop the org properties, commit a fresh token under the <i>unqualified</i> key, and
        /// leave the stale token sitting on the qualified key forever. Every later cycle would pick it up again, 400
        /// again, and fall back again - so organisation values would never update again while the user import looked
        /// perfectly healthy. So the token is ruled out first: only a request that carried no token can blame the
        /// selection.
        /// </para>
        /// <para>
        /// Keyed on the cycle's configured selection, not the attempt's, so it also covers the fallback read. A
        /// deployment with no organisation types keeps <see cref="GraphDeltaTokenRejection"/>'s rule exactly, and with
        /// it the guarantee that a transient fault never costs a full re-read of the tenant (issue #664).
        /// </para>
        /// </remarks>
        private bool IsAmbiguousOrgCycleRejection(Exception failure, bool requestCarriedDeltaToken)
        {
            if (!requestCarriedDeltaToken || _orgSelection.IsEmpty)
            {
                return false;
            }

            var graphError = failure as GraphHttpException;
            return graphError != null
                && (graphError.StatusCode == System.Net.HttpStatusCode.BadRequest
                    || graphError.StatusCode == System.Net.HttpStatusCode.Gone);
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
        /// Falling back is deliberately not restricted to a 400: any failure of the org-carrying request
        /// is worth one attempt without it, because the alternative is importing nothing at all. The
        /// fallback goes through <see cref="ReadWithTokenRecovery"/> because it switches to the
        /// unqualified key, which is the one most likely to be holding a token nobody has written
        /// since before organisations were configured. If even that fails, the read is reported
        /// incomplete exactly as it is for a deployment with no organisation types, and no token is
        /// committed.
        /// </remarks>
        private async Task<DeltaReadRound> FallBackWithoutOrgAttributes(Exception failure)
        {
            _orgSelectionRejected = true;

            var graphError = failure as GraphHttpException;
            if (graphError != null && graphError.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                _logger.LogError(
                    $"User import - Microsoft Graph rejected the configured organisation attributes "
                    + $"({_orgSelection}). Organisation values will NOT be refreshed this cycle, but the rest of the "
                    + "user import will continue. Check those attributes still exist in the tenant on the User "
                    + $"organisations page. Graph said: {DescribeFailure(failure)}.");
            }
            else
            {
                _logger.LogWarning(
                    $"User import - the request carrying the configured organisation attributes ({_orgSelection}) "
                    + $"failed ({DescribeFailure(failure)}). Retrying without them so the rest of the user import "
                    + "can continue. Organisation values will NOT be refreshed this cycle.");
            }

            // The token key follows the selection, so falling back has to re-point the provider at the
            // unqualified key - otherwise this cycle would store a token under the org-qualified key
            // while querying without the org properties.
            _deltaValueProvider.SetKeyQualifier(GraphUserOrgSelection.None.DeltaKeyQualifier);

            return await ReadWithTokenRecovery(GraphUserOrgSelection.None);
        }

        /// <summary>
        /// One pass over <c>/users/delta</c> under <paramref name="selection"/>, from <paramref name="deltaToken"/>
        /// or, when it is empty, from the start. Buffers the new token if the pass reaches an
        /// <c>@odata.deltaLink</c>, and records the failure that ended it early, if one did.
        /// </summary>
        private async Task<DeltaReadRound> ReadDeltaRound(GraphUserOrgSelection selection, string deltaToken)
        {
            var url = $"https://graph.microsoft.com:443/v1.0/users/delta" +
                $"?$select={selection.BuildSelect(GraphUserDeltaQuery.Select)}" +
                "&$expand=manager";
            if (!string.IsNullOrEmpty(deltaToken))
            {
                url += $"&$deltatoken={deltaToken}";
            }

            // A round abandoned for a retry or a fallback must never leave a token behind for the next one to commit:
            // it would be saved under whatever key and selection are in force by then.
            _pendingDeltaToken = null;
            _hasPendingDeltaToken = false;

            var round = new DeltaReadRound { DeltaToken = deltaToken };
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
            /// <summary>The stored token the round continued from, or null when it read the full user list.</summary>
            public string DeltaToken { get; set; }
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
                // the state table - to ask for a full re-read. Saving this run's token would quietly undo that request, so
                // it is withheld and the next run reads the full user list, as the clear intended. A failed read
                // here falls back to the checkpoint this run started from, so a storage blip still saves as before.
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

        /// <summary>
        /// The page size requested when listing a SKU's holders: 999 is the documented maximum <c>$top</c> for
        /// <c>GET /users</c>. <c>/users/delta</c> has no documented page-size option, so this applies here only.
        /// </summary>
        private const int UsersBySkuPageSize = 999;

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

                // Without $top Graph pages /users 100 at a time, so a SKU with N holders costs N / 100
                // sequential requests, every cycle. Graph keeps the page size in each @odata.nextLink,
                // which the iterator below follows unchanged. Issue #707.
                rc.QueryParameters.Top = UsersBySkuPageSize;
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
