using Azure.Core;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.UserScope;
using DataUtils;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Ensures user table info is upto-date from Graph
    /// </summary>
    public class UserMetadataUpdater : AbstractApiLoader
    {
        #region Constructor & Privates

        private UserMetadataCache _userMetaCache;
        private readonly IUserMetadataLoader _userLoader;
        private readonly IAnalyticsDbContextFactory _contextFactory;
        private readonly IClock _clock;
        private UserBatchProcessor _batchProcessor;
        private UserInsertProcessor _insertProcessor;
        private UserLicenseProcessor _licenseProcessor;
        private UserDataMapper _dataMapper;
        private IUserImportScopeProvider _userScopeProvider;
        private IImportLastRunStore _lastRunStore;
        private IUserImportScopeMarkerStore _scopeMarkerStore;

        /// <summary>
        /// Cadence key for the full re-read that picks up people who joined the <c>UserGroupsFilter</c> groups after
        /// the directory was last read in full. Stored in the process-lifetime last-run store, like the section gates.
        /// </summary>
        public const string ScopeCatchUpLastRunKey = "UserImportScopeCatchUpLastRun";

        /// <summary>At most one scope catch-up re-read per this many hours.</summary>
        public const int ScopeCatchUpIntervalHours = 24;

        /// <param name="userScopeProvider">
        /// The process-lifetime <c>UserGroupsFilter</c> scope: only people in it are written to the users table.
        /// Null means unfiltered.
        /// </param>
        /// <param name="lastRunStore">
        /// Process-lifetime store that rate-limits the scope catch-up re-read. Without one no catch-up is attempted.
        /// </param>
        public UserMetadataUpdater(AnalyticsLogger logger, AppConfig settings, TokenCredential creds, ManualGraphCallClient manualGraphCallClient, IClock clock = null,
            IUserImportScopeProvider userScopeProvider = null, IImportLastRunStore lastRunStore = null)
            : base(logger, settings)
        {
            _clock = clock ?? SystemClock.Instance;
            _userScopeProvider = userScopeProvider;
            _lastRunStore = lastRunStore;
            IDeltaValueProvider deltaProvider = null;
            if (!string.IsNullOrEmpty(settings.ConnectionStrings.RedisConnectionString))
            {
                deltaProvider = new RedisProcessDeltaValueProvider(settings, logger);
                _scopeMarkerStore = new RedisUserImportScopeMarkerStore(settings);
                logger.LogInformation($"User import - using Redis for delta token cache.");
            }
            else
            {
                logger.LogInformation($"User import - no redis found configured, using in-process cache for delta token.");
                deltaProvider = new InProcessDeltaValueProvider(logger);
            }

            // v4 used graphClient.HttpProvider.OverallTimeout = 1h directly. HttpProvider is gone
            // in v5+, so we build a HttpClient that keeps Kiota's service-directed Retry-After
            // handling but caps any single stalled HTTP request. /users/delta over a 200k-user
            // tenant can legitimately take longer than the framework default 100s across all pages;
            // the budget is therefore per request, not per full tenant traversal.
            var graphServiceClient = GraphServiceClientFactory.CreateForUserImport(creds, _logger);

            _userLoader = new GraphUserLoader(manualGraphCallClient, deltaProvider, _logger, graphServiceClient);
            _contextFactory = DefaultAnalyticsDbContextFactory.Instance;
            InitializeHelpers();
        }

        /// <summary>
        /// Constructor with injectable user loader for testing and alternate implementations
        /// </summary>
        public UserMetadataUpdater(AnalyticsLogger logger, AppConfig settings, IUserMetadataLoader userLoader, IClock clock = null)
            : this(logger, settings, userLoader, DefaultAnalyticsDbContextFactory.Instance, clock)
        {
        }

        /// <summary>
        /// Constructor with an injectable user loader and database context factory (#372).
        /// </summary>
        public UserMetadataUpdater(AnalyticsLogger logger, AppConfig settings, IUserMetadataLoader userLoader, IAnalyticsDbContextFactory contextFactory, IClock clock = null)
            : base(logger, settings)
        {
            _userLoader = userLoader;
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
            _clock = clock ?? SystemClock.Instance;
            InitializeHelpers();
        }

        private void InitializeHelpers()
        {
            _batchProcessor = new UserBatchProcessor(_logger, _clock);
            _insertProcessor = new UserInsertProcessor(_logger, _batchProcessor);
        }

        public IUserMetadataLoader UserLoader => _userLoader;

        /// <summary>
        /// Applies <c>UserGroupsFilter</c> to this updater: only people in the scope are written, and people who
        /// joined its groups after the directory was last read in full are caught up by an occasional full re-read.
        /// </summary>
        /// <param name="scopeMarkerStore">
        /// Where the filter the stored delta token was taken under is recorded, so a changed filter triggers a full
        /// read. Null when the token does not outlive the import (see <see cref="RedisUserImportScopeMarkerStore"/>).
        /// </param>
        public UserMetadataUpdater WithUserScope(IUserImportScopeProvider userScopeProvider, IImportLastRunStore lastRunStore,
            IUserImportScopeMarkerStore scopeMarkerStore = null)
        {
            _userScopeProvider = userScopeProvider;
            _lastRunStore = lastRunStore;
            _scopeMarkerStore = scopeMarkerStore;
            return this;
        }

        #endregion

        /// <summary>
        /// Main method
        /// </summary>
        /// <returns>
        /// True when the cycle completed and the new Graph delta token was committed. False when it did not -
        /// above all when the <c>/users/delta</c> read stopped before its <c>@odata.deltaLink</c> (issue #664) -
        /// so the import section is not recorded as done and is retried on the next cycle. A failure in any
        /// database phase still throws, exactly as before.
        /// </returns>
        public async Task<bool> InsertAndUpdateDatabaseFromExternalUsers()
        {
            const int BATCH_SIZE = 500;
            var phaseResults = new UserImportPhaseResults();

            using (var db = _contextFactory.Create())
            {
                db.Configuration.AutoDetectChangesEnabled = false;

                _userMetaCache = new UserMetadataCache(db);
                _licenseProcessor = new UserLicenseProcessor(_logger, _userLoader, _userMetaCache);
                _dataMapper = new UserDataMapper(_logger, _userMetaCache, new SqlUserLookupStore(db), _clock);
                var importCycleLastUpdatedUtc = _clock.UtcNow;

                // UserGroupsFilter: only people in the scope are written to the users table - their metadata, manager
                // link and licences. Everyone else is left out entirely.
                var userScope = _userScopeProvider == null ? UserImportScope.Unfiltered : await _userScopeProvider.GetScopeAsync();
                _dataMapper.UserScope = userScope;

                _logger.LogInformation($"{DateTime.Now.ToShortTimeString()} User import - start");

                // If we have no active users, assume new install so clear delta key
                var activeUserCount = await db.users.Where(u => u.AccountEnabled.HasValue && u.AccountEnabled.Value == true).CountAsync();
                var deltaTokenCleared = false;
                if (activeUserCount == 0)
                {
                    await _userLoader.DeltaValueProvider.ClearDeltaToken();
                    deltaTokenCleared = true;
                }

                // A stored delta token only returns people who changed since it was taken, so one taken under a
                // different UserGroupsFilter would never return the people the new filter lets in.
                deltaTokenCleared |= await ClearDeltaTokenIfUserScopeChangedAsync(alreadyCleared: deltaTokenCleared);

                if (!deltaTokenCleared && userScope.IsFiltered)
                {
                    await ClearDeltaTokenForScopeCatchUpIfDueAsync(db, userScope);
                }

                // Load from Graph & update delta code once done
                var allActiveGraphUsers = await _userLoader.LoadAllActiveUsers();
                var deltaReadCompleted = _userLoader.LastLoadReachedDeltaLink;
                _logger.LogInformation($"User import - loaded {allActiveGraphUsers.Count.ToString("N0")} users from Graph");

                if (userScope.IsFiltered)
                {
                    var readFromGraph = allActiveGraphUsers.Count;
                    allActiveGraphUsers = SelectGraphUsersInScope(allActiveGraphUsers, userScope);
                    _logger.LogInformation($"User import - {allActiveGraphUsers.Count:N0} of the {readFromGraph:N0} users read are in UserGroupsFilter; only they are written.");
                }

                // Pre-build dictionary for O(1) graph user lookups by AAD ID (avoids O(n) scans per user in manager resolution)
                _dataMapper.SetGraphUserLookup(allActiveGraphUsers);

                // Get SKUs from tenant
                var skus = await _userLoader.LoadTenantSkus();

                // Load DB user data without tracking.
                // Only include license lookups when we'll need per-user license processing
                // (i.e. tenant-level SKUs unavailable). Including them unconditionally loads
                // hundreds of thousands of extra entities that are never read in the common
                // path and can cause an out-of-memory crash before the existing-user metadata
                // update runs.
                var allDbUsers = skus == null
                    ? await db.users.AsNoTracking().Include(u => u.LicenseLookups).ToListAsync()
                    : await db.users.AsNoTracking().ToListAsync();
                _logger.LogInformation($"User import - loaded {allDbUsers.Count.ToString("N0")} users from database");

                // Create lookup dictionaries for performance - pre-allocate capacity
                var dbUsersByUpn = new Dictionary<string, Common.Entities.User>(allDbUsers.Count, StringComparer.OrdinalIgnoreCase);
                var dbUsersByAadId = new Dictionary<string, Common.Entities.User>(allDbUsers.Count, StringComparer.OrdinalIgnoreCase);

                // Single pass to populate both dictionaries. Under UserGroupsFilter, people outside the scope are left
                // out: these maps are also where managers are resolved, and a manager outside the scope must resolve
                // to no manager rather than to their (still unpurged) row.
                foreach (var user in allDbUsers)
                {
                    if (userScope.IsFiltered && !IsDbUserInScope(user, userScope))
                    {
                        continue;
                    }
                    if (!string.IsNullOrEmpty(user.UserPrincipalName))
                    {
                        dbUsersByUpn[user.UserPrincipalName] = user;
                    }
                    if (!string.IsNullOrEmpty(user.AzureAdId) && !dbUsersByAadId.ContainsKey(user.AzureAdId))
                    {
                        dbUsersByAadId[user.AzureAdId] = user;
                    }
                }

                var graphMentionedExistingDbUsers = _dataMapper.GetDbUsersFromGraphUsers(allActiveGraphUsers, allDbUsers);

                // Insert any user we've not seen so far
                var insertedDbUsers = await InsertMissingUsers(db, allActiveGraphUsers, graphMentionedExistingDbUsers, skus == null, importCycleLastUpdatedUtc);
                phaseResults.InsertPhaseSucceeded = true;
                _logger.LogInformation($"User import - Insert phase completed. {insertedDbUsers.Count.ToString("N0")} new users inserted.");

                // Reload newly inserted users WITH TRACKING and update dictionaries
                // This ensures they're properly tracked when used as managers in ProcessExistingUsersInBatches
                if (insertedDbUsers.Count > 0)
                {
                    _logger.LogInformation($"User import - Reloading {insertedDbUsers.Count.ToString("N0")} newly inserted users with tracking for manager relationships...");

                    // Collect UPNs as Graph delivers them. SQL Server's default code-first
                    // collation (Latin1_General_CI_AS) is case-insensitive, so we no longer
                    // need to lowercase here. The reload query below compares without LOWER()
                    // to stay SARGable against the user_name index - critical at 200k-user scale
                    // where a non-SARGable predicate forces a full clustered-index scan.
                    var insertedUpns = new List<string>(insertedDbUsers.Count);
                    foreach (var user in insertedDbUsers)
                    {
                        if (!string.IsNullOrEmpty(user.UserPrincipalName))
                        {
                            insertedUpns.Add(user.UserPrincipalName);
                        }
                    }

                    // Load with tracking in reasonable batches to avoid memory issues
                    const int RELOAD_BATCH_SIZE = 1000;
                    var reloadedUsers = new List<Common.Entities.User>(insertedDbUsers.Count);

                    for (int i = 0; i < insertedUpns.Count; i += RELOAD_BATCH_SIZE)
                    {
                        var batchCount = Math.Min(RELOAD_BATCH_SIZE, insertedUpns.Count - i);
                        var batchUpns = insertedUpns.GetRange(i, batchCount);
                        // No LOWER() on the column - the CI collation handles case-insensitive
                        // matching and keeps the predicate SARGable.
                        var batchReloaded = await db.users
                            .Where(u => batchUpns.Contains(u.UserPrincipalName))
                            .ToListAsync();
                        reloadedUsers.AddRange(batchReloaded);
                    }

                    // Update lookup dictionaries with TRACKED entities.
                    // dbUsersByUpn was built with StringComparer.OrdinalIgnoreCase so we can
                    // key by the original UPN without lowering it - the comparer handles case.
                    foreach (var insertedUser in reloadedUsers)
                    {
                        if (!string.IsNullOrEmpty(insertedUser.UserPrincipalName))
                        {
                            dbUsersByUpn[insertedUser.UserPrincipalName] = insertedUser;
                            await _userMetaCache.UserCache.GetOrCreateNewResource(insertedUser.UserPrincipalName, insertedUser);
                        }

                        if (!string.IsNullOrEmpty(insertedUser.AzureAdId))
                        {
                            dbUsersByAadId[insertedUser.AzureAdId] = insertedUser;
                        }
                    }

                    insertedDbUsers = reloadedUsers;
                }

                // Identify users that need updating - use HashSet for O(1) lookup instead of Any().
                // Both sets are OrdinalIgnoreCase so we keep the original UPN casing and let
                // the comparer handle case-insensitivity (cheaper than .ToLower() at 200k scale).
                var insertedUpnSet = new HashSet<string>(
                    insertedDbUsers.Where(u => !string.IsNullOrEmpty(u.UserPrincipalName)).Select(u => u.UserPrincipalName),
                    StringComparer.OrdinalIgnoreCase);

                var notInsertedUpns = new HashSet<string>(allActiveGraphUsers.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var graphUser in allActiveGraphUsers)
                {
                    if (!string.IsNullOrEmpty(graphUser.UserPrincipalName) && !insertedUpnSet.Contains(graphUser.UserPrincipalName))
                    {
                        notInsertedUpns.Add(graphUser.UserPrincipalName);
                    }
                }

                // NOTE: we deliberately do NOT clear allDbUsers here yet when
                // tenant-level SKUs are available. The licence refresh step below
                // (ProcessSKUsForAllUsers) MUST iterate over the entire DB user
                // population - not just the users returned by the current Graph
                // delta - otherwise users whose only change in Graph is a licence
                // assignment will never have their user_license_type_lookups
                // rows refreshed. With a persisted delta token (e.g. Redis) this
                // causes licence counts to drift downward run after run until
                // they no longer match the tenant's actual licence assignments.
                // When SKUs are not available the per-user path inside
                // UpdateDbUserWithGraphData handles licences as part of the
                // per-user Graph call, so we can free the list early in that
                // branch to save memory.
                if (skus == null)
                {
                    allDbUsers.Clear();
                    allDbUsers = null;
                }

                // Update existing users.
                // When tenant-level SKUs are available we can use the fast bulk-SQL
                // path (no per-user Graph calls needed for licenses).
                // When SKUs are NOT available we must fall back to the EF-per-entity
                // path because each user needs individual Graph license queries.
                _logger.LogInformation($"User import - Starting metadata update for {notInsertedUpns.Count.ToString("N0")} existing users...");
                int existingUsersUpdated = 0;
                try
                {
                    if (skus != null)
                    {
                        existingUsersUpdated = await _batchProcessor.BulkUpdateExistingUsers(
                            db,
                            allActiveGraphUsers,
                            notInsertedUpns,
                            dbUsersByUpn,
                            dbUsersByAadId,
                            _dataMapper.GraphUsersByAadId,
                            _userMetaCache,
                            new SqlUserBulkUpdateWriter(db.Database.Connection.ConnectionString),
                            importCycleLastUpdatedUtc);
                    }
                    else
                    {
                        existingUsersUpdated = await _batchProcessor.ProcessExistingUsersInBatches(
                            db,
                            allActiveGraphUsers,
                            notInsertedUpns,
                            dbUsersByUpn,
                            dbUsersByAadId,
                            async (graphUser, dbUser) => await UpdateDbUserWithGraphData(db, graphUser, allActiveGraphUsers, new List<Common.Entities.User>(), dbUser, true, dbUsersByAadId, importCycleLastUpdatedUtc),
                            // Resolve the whole batch's managers in one query rather than one per
                            // user - see UserDataMapper.PrefetchManagersForBatchAsync (#371).
                            batch => _dataMapper.PrefetchManagersForBatchAsync(batch),
                            BATCH_SIZE);
                    }
                    phaseResults.UpdatePhaseSucceeded = true;
                    _logger.LogInformation($"User import - Completed metadata update for {existingUsersUpdated.ToString("N0")} existing users");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"User import - ERROR updating existing users: {ex.Message}");
                    throw;
                }

                // Build the user list passed to ProcessSKUsForAllUsers.
                //
                // IMPORTANT: this MUST cover every user in the database, not just
                // the users returned by the current Graph delta response. The
                // licence refresh step reconciles user_license_type_lookups rows
                // for the supplied users against the per-SKU Graph queries; any
                // user not in the supplied list keeps their stale rows forever and
                // any new licence assignment for them is never written. When the
                // delta token is persisted (Redis) the delta response shrinks to
                // only users with metadata changes, so scoping the licence refresh
                // to delta users causes the tenant-wide licence counts to drift
                // downward over time.
                //
                // The supplied list is also the ONLY set of users whose licence rows
                // the refresh is allowed to delete, so passing the whole population
                // is what makes removals correct as well as additions.
                //
                // We build the list from allDbUsers (every existing DB user
                // loaded at the start of this run) plus insertedDbUsers (users
                // freshly created in this run), de-duplicated by primary key
                // because the refresh turns it into a UPN-keyed dictionary.
                List<Common.Entities.User> allDbUsersForLicenseRefresh = null;
                if (skus != null)
                {
                    var combinedById = new Dictionary<int, Common.Entities.User>(
                        (allDbUsers?.Count ?? 0) + insertedDbUsers.Count);

                    if (allDbUsers != null)
                    {
                        foreach (var u in allDbUsers)
                        {
                            // Licences are directory metadata too: not refreshed for anyone outside UserGroupsFilter.
                            if (u.ID > 0 && (!userScope.IsFiltered || IsDbUserInScope(u, userScope)))
                            {
                                combinedById[u.ID] = u;
                            }
                        }
                    }

                    foreach (var u in insertedDbUsers)
                    {
                        if (u.ID > 0)
                        {
                            combinedById[u.ID] = u;
                        }
                    }

                    allDbUsersForLicenseRefresh = new List<Common.Entities.User>(combinedById.Values);
                    _logger.LogInformation($"User import - licence refresh will cover {allDbUsersForLicenseRefresh.Count.ToString("N0")} DB users (entire population, not just delta).");
                }

                // Can we update SKUs for users on batch (ie Organization.Read.All granted)?
                if (skus != null)
                {
                    // No re-attach loop needed: the licence refresh works in FK IDs
                    // (UserId / LicenseTypeId) rather than navigation properties, so
                    // the entities do not need to be tracked by EF.
                    await _licenseProcessor.ProcessSKUsForAllUsers(skus, allDbUsersForLicenseRefresh, db);
                    _logger.LogInformation($"User import - updated user license information from {skus.Count.ToString("N0")} tenant SKUs");

                    db.ChangeTracker.DetectChanges();
                    await db.SaveChangesAsync();
                    phaseResults.LicenceRefreshSucceeded = true;
                }
                else
                {
                    await UserLicenseProcessor.MarkSubscribedSkuCapacityUnavailableAsync(db, _logger);

                    // No separate licence phase runs at all when tenant SKUs are unavailable:
                    // ProcessUserLicenses already ran per user inside the update phase above, so
                    // there is no outstanding licence work that committing the delta could skip.
                    // Set inside the branch rather than after it so that a catch added around the
                    // work above would leave the flag false, exactly as it would for the other two
                    // phases.
                    phaseResults.LicenceRefreshSucceeded = true;
                }

                _logger.LogInformation($"{DateTime.Now.ToShortTimeString()} User import - complete. Inserted {insertedDbUsers.Count.ToString("N0")} new users, updated metadata for {existingUsersUpdated.ToString("N0")} existing users (from {allActiveGraphUsers.Count.ToString("N0")} Graph users)");

                // All insert/metadata/license work succeeded. Now and ONLY now is it
                // safe to persist the new Graph delta token. If any of the previous
                // steps had thrown, control would have left this method via the
                // exception and the previously-persisted delta would still be in
                // effect, so the failed users will be retried on the next import
                // cycle instead of being skipped because Graph already considers
                // them "seen".
                //
                // The check is explicit rather than implied by that control flow so the
                // guarantee survives someone adding a catch: see UserImportCommitPolicy (#372).
                bool cycleCompleted;
                if (!deltaReadCompleted)
                {
                    // The read never reached an @odata.deltaLink, so there is no new token and
                    // CommitDeltaTokenAsync would quietly do nothing. Say so, and report the run as
                    // incomplete so it is neither stamped as done nor announced as a finished section:
                    // otherwise a read that fails on every run looks exactly like a tenant in which
                    // nothing changes (#664).
                    _logger.LogWarning("User import - NOT moving the user checkpoint forward: the Graph /users/delta read did not reach its end (no @odata.deltaLink), " +
                        "so this run's user list is incomplete and there is no new delta token to save. The users that were read have been saved; " +
                        "the next cycle reads again from the stored checkpoint, or reads the full user list if there is none.");
                    cycleCompleted = false;
                }
                else if (UserImportCommitPolicy.ShouldCommitDelta(phaseResults))
                {
                    // False when the loader withheld the new token because the checkpoint was cleared while this
                    // run was in progress (an admin asking for a full re-read); it has logged why. Reporting the run
                    // as not done keeps the cadence gate open, so that re-read happens on the next cycle.
                    cycleCompleted = await _userLoader.CommitDeltaTokenAsync();
                }
                else
                {
                    _logger.LogWarning("User import - NOT committing the Graph delta token: at least one import phase did not complete. " +
                        "The same users will be reprocessed on the next cycle rather than being skipped.");
                    cycleCompleted = false;
                }

                // Final cleanup
                dbUsersByUpn.Clear();
                dbUsersByAadId.Clear();
                allActiveGraphUsers.Clear();
                allDbUsersForLicenseRefresh?.Clear();
                allDbUsers?.Clear();

                return cycleCompleted;
            }
        }

        /// <summary>The Graph users in the <c>UserGroupsFilter</c> scope, matched on object id, UPN or mail.</summary>
        internal static List<GraphUser> SelectGraphUsersInScope(List<GraphUser> graphUsers, UserImportScope userScope)
        {
            if (graphUsers == null || userScope == null || !userScope.IsFiltered)
            {
                return graphUsers;
            }
            return graphUsers.Where(u => u != null && userScope.IsAnyInScope(u.Id, u.UserPrincipalName, u.Mail)).ToList();
        }

        /// <summary>True when a users-table row belongs to someone in the <c>UserGroupsFilter</c> scope.</summary>
        internal static bool IsDbUserInScope(Common.Entities.User user, UserImportScope userScope)
            => user != null && userScope.IsAnyInScope(user.AzureAdId, user.UserPrincipalName, user.Mail);

        /// <summary>
        /// Discards the stored <c>/users/delta</c> token when it was taken under a different <c>UserGroupsFilter</c>
        /// from the one set now, so this cycle reads the whole directory (filtered to the new scope as always).
        /// </summary>
        /// <remarks>
        /// A token only returns people who changed after it was taken, and while a filter is set everyone outside it
        /// is left out of the users table. So once the filter is removed or changed, the people it newly lets in would
        /// never be imported unless they happened to change. Changing the setting restarts the web job, which is why
        /// the filter the token belongs to is stored beside it rather than remembered in memory.
        /// </remarks>
        /// <returns>True when the token was discarded (or already had been) because the filter changed.</returns>
        private async Task<bool> ClearDeltaTokenIfUserScopeChangedAsync(bool alreadyCleared)
        {
            if (_scopeMarkerStore == null)
            {
                return false;
            }

            var fingerprint = (_userScopeProvider?.Filter ?? new UserGroupsFilterModel()).Fingerprint;
            try
            {
                var stored = await _scopeMarkerStore.GetFingerprintAsync() ?? string.Empty;
                if (string.Equals(stored, fingerprint, StringComparison.Ordinal))
                {
                    return false;
                }

                if (!alreadyCleared)
                {
                    _logger.LogInformation("User import - UserGroupsFilter has changed since the stored /users/delta checkpoint was taken, and an " +
                        "incremental read would never return the people it now includes who have not changed since. Reading the full user list this cycle.");
                    await _userLoader.DeltaValueProvider.ClearDeltaToken();
                }

                // Recorded now rather than once the read completes: with the token gone, every cycle reads the full
                // list until one completes, whatever this record says.
                await _scopeMarkerStore.SetFingerprintAsync(fingerprint);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"User import - could not check whether UserGroupsFilter has changed since the last full read of the directory ({ex.Message}). " +
                    "Will check again next cycle.");
                return false;
            }
        }

        /// <summary>
        /// People added to the <c>UserGroupsFilter</c> groups after the directory was last read in full are never
        /// returned by an incremental <c>/users/delta</c> read - their user object did not change, only the group did -
        /// so they would never reach the users table. When any enabled member is missing from it, discard the delta
        /// token so this cycle reads the full user list (filtered to the scope as always). Rate-limited to once per
        /// <see cref="ScopeCatchUpIntervalHours"/>, so a member who can never be imported cannot cause a full read
        /// every cycle.
        /// </summary>
        private async Task ClearDeltaTokenForScopeCatchUpIfDueAsync(AnalyticsEntitiesContext db, UserImportScope userScope)
        {
            if (_lastRunStore == null || userScope.EnabledMemberObjectIds.Count == 0)
            {
                return;
            }

            try
            {
                if (string.IsNullOrEmpty(await _userLoader.DeltaValueProvider.GetDeltaToken()))
                {
                    return;     // Already reading the full list.
                }

                var knownObjectIds = new HashSet<Guid>();
                foreach (var id in await db.users.Where(u => u.AzureAdId != null && u.AzureAdId != "").Select(u => u.AzureAdId).ToListAsync())
                {
                    if (Guid.TryParse(id, out var objectId))
                    {
                        knownObjectIds.Add(objectId);
                    }
                }

                var missing = userScope.EnabledMemberObjectIds.Count(id => !knownObjectIds.Contains(id));
                if (missing == 0)
                {
                    return;
                }

                var lastCatchUp = await _lastRunStore.GetLastRunUtc(ScopeCatchUpLastRunKey);
                if (!ImportCadenceGate.ShouldRun(lastCatchUp, ScopeCatchUpIntervalHours, force: false, nowUtc: _clock.UtcNow))
                {
                    _logger.LogInformation($"User import - {missing:N0} enabled member(s) of the UserGroupsFilter group(s) are not in the users table yet. " +
                        $"The next full re-read to add them is due after {lastCatchUp?.AddHours(ScopeCatchUpIntervalHours):u} UTC.");
                    return;
                }

                _logger.LogInformation($"User import - {missing:N0} enabled member(s) of the UserGroupsFilter group(s) are not in the users table - usually " +
                    "people added to the group(s) since the directory was last read in full, whom an incremental read never returns. " +
                    "Reading the full user list this cycle to add them.");
                await _userLoader.DeltaValueProvider.ClearDeltaToken();
                await _lastRunStore.SetLastRunUtc(ScopeCatchUpLastRunKey, _clock.UtcNow);
            }
            catch (Exception ex)
            {
                // A missed catch-up only delays adding new members; it must not stop the import.
                _logger.LogWarning($"User import - could not check for UserGroupsFilter members missing from the users table ({ex.Message}). " +
                    "Will check again next cycle.");
            }
        }

        private async Task UpdateDbUserWithGraphData(AnalyticsEntitiesContext db, GraphUser graphUser, List<GraphUser> allGraphUsers, List<Common.Entities.User> allDbUsers, Common.Entities.User dbUser, bool readUserSkus, Dictionary<string, Common.Entities.User> dbUsersByAadId = null, DateTime? lastUpdatedUtc = null)
        {
            await _dataMapper.UpdateUserMetadata(db, graphUser, allGraphUsers, dbUser, dbUsersByAadId, allDbUsers, lastUpdatedUtc);

            // This is only done per user if can't be done at tenant level (due to extra permission)
            if (readUserSkus)
            {
                await _licenseProcessor.ProcessUserLicenses(db, graphUser, dbUser);
            }
        }

        /// <summary>
        /// Inserts missing users into DB using two-phase approach: fast bulk insert, then metadata enrichment.
        /// Delegates to UserInsertProcessor for the heavy lifting.
        /// </summary>
        public async Task<List<Common.Entities.User>> InsertMissingUsers(AnalyticsEntitiesContext db, List<GraphUser> allGraphUsers, List<Common.Entities.User> graphMentionedDbUsers, bool readUserSkus, DateTime? importCycleLastUpdatedUtc = null)
        {
            // Ensure cache and helpers are initialized (for direct method calls from tests)
            if (_userMetaCache == null)
            {
                _userMetaCache = new UserMetadataCache(db);
            }
            if (_dataMapper == null)
            {
                _dataMapper = new UserDataMapper(_logger, _userMetaCache, new SqlUserLookupStore(db), _clock);
            }
            if (_licenseProcessor == null)
            {
                _licenseProcessor = new UserLicenseProcessor(_logger, _userLoader, _userMetaCache);
            }

            return await _insertProcessor.InsertMissingUsers(
                db,
                allGraphUsers,
                graphMentionedDbUsers,
                readUserSkus,
                _userMetaCache,
                _dataMapper,
                _licenseProcessor,
                async (ctx, graphUser, allGraph, allDb, dbUser, readSkus, dbByAadId) =>
                    await UpdateDbUserWithGraphData(ctx, graphUser, allGraph, allDb, dbUser, readSkus, dbByAadId, importCycleLastUpdatedUtc));
        }

        /// <summary>
        /// Get database users that match Graph users by UPN (public wrapper for testing)
        /// </summary>
        public List<Common.Entities.User> GetDbUsersFromGraphUsers(List<GraphUser> allGraphUsers, List<Common.Entities.User> allDbUsers)
        {
            // Ensure mapper is initialized
            if (_dataMapper == null && _userMetaCache != null)
            {
                _dataMapper = new UserDataMapper(_logger, _userMetaCache, _clock);
            }

            if (_dataMapper != null)
            {
                return _dataMapper.GetDbUsersFromGraphUsers(allGraphUsers, allDbUsers);
            }
            else
            {
                // Fallback implementation
                var dbUsersByUpn = allDbUsers
                    .Where(u => !string.IsNullOrEmpty(u.UserPrincipalName))
                    .ToDictionary(u => u.UserPrincipalName.ToLower(), u => u, StringComparer.OrdinalIgnoreCase);

                var users = new List<Common.Entities.User>();
                foreach (var graphUser in allGraphUsers)
                {
                    var upn = graphUser.UserPrincipalName?.ToLower();
                    if (!string.IsNullOrEmpty(upn) && dbUsersByUpn.TryGetValue(upn, out var dbUser))
                    {
                        users.Add(dbUser);
                    }
                }
                return users;
            }
        }

        /// <summary>
        /// Update basic user properties from Graph user (internal method for backward compatibility)
        /// </summary>
        internal Common.Entities.User UpdateDbUserFromGraphUser(Common.Entities.User dbUser, GraphUser graphUser)
        {
            // Ensure mapper is initialized
            if (_dataMapper == null && _userMetaCache != null)
            {
                _dataMapper = new UserDataMapper(_logger, _userMetaCache, _clock);
            }

            // If mapper is available, use it; otherwise do direct mapping
            if (_dataMapper != null)
            {
                return _dataMapper.UpdateDbUserFromGraphUser(dbUser, graphUser);
            }
            else
            {
                // Fallback for edge cases where mapper isn't initialized. Uses the same extracted
                // mapping rule as UserDataMapper so the two copies cannot drift apart (#371).
                var plan = UserMetadataMappingRules.BuildPlan(graphUser);
                dbUser.AccountEnabled = plan.AccountEnabled;
                dbUser.PostalCode = plan.PostalCode;
                dbUser.AzureAdId = plan.AzureAdId;
                dbUser.Mail = plan.Mail;
                return dbUser;
            }
        }
    }
}
