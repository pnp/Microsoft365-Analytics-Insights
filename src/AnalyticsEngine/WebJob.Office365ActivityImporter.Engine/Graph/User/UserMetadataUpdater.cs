using Azure.Core;
using Common.Entities;
using Common.Entities.Config;
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
        private UserBatchProcessor _batchProcessor;
        private UserInsertProcessor _insertProcessor;
        private UserLicenseProcessor _licenseProcessor;
        private UserDataMapper _dataMapper;

        public UserMetadataUpdater(AnalyticsLogger logger, AppConfig settings, TokenCredential creds, ManualGraphCallClient manualGraphCallClient)
            : base(logger, settings)
        {
            IDeltaValueProvider deltaProvider = null;
            if (!string.IsNullOrEmpty(settings.ConnectionStrings.RedisConnectionString))
            {
                deltaProvider = new RedisProcessDeltaValueProvider(settings, logger);
                logger.LogInformation($"User import - using Redis for delta token cache.");
            }
            else
            {
                logger.LogInformation($"User import - no redis found configured, using in-process cache for delta token.");
                deltaProvider = new InProcessDeltaValueProvider(logger);
            }

            // v4 used graphClient.HttpProvider.OverallTimeout = 1h directly. HttpProvider is gone
            // in v5+, so we build a HttpClient with the desired timeout and inject it into the
            // GraphServiceClient. /users/delta over a 200k-tenant can comfortably exceed the
            // default 100s timeout - the explicit 1h timeout is load-bearing.
            var graphServiceClient = GraphServiceClientFactory.CreateWithTimeout(creds, TimeSpan.FromHours(1));

            _userLoader = new GraphUserLoader(manualGraphCallClient, deltaProvider, _logger, graphServiceClient);
            _contextFactory = DefaultAnalyticsDbContextFactory.Instance;
            InitializeHelpers();
        }

        /// <summary>
        /// Constructor with injectable user loader for testing and alternate implementations
        /// </summary>
        public UserMetadataUpdater(AnalyticsLogger logger, AppConfig settings, IUserMetadataLoader userLoader)
            : this(logger, settings, userLoader, DefaultAnalyticsDbContextFactory.Instance)
        {
        }

        /// <summary>
        /// Constructor with an injectable user loader and database context factory (#372).
        /// </summary>
        public UserMetadataUpdater(AnalyticsLogger logger, AppConfig settings, IUserMetadataLoader userLoader, IAnalyticsDbContextFactory contextFactory)
            : base(logger, settings)
        {
            _userLoader = userLoader;
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
            InitializeHelpers();
        }

        private void InitializeHelpers()
        {
            _batchProcessor = new UserBatchProcessor(_logger);
            _insertProcessor = new UserInsertProcessor(_logger, _batchProcessor);
        }

        public IUserMetadataLoader UserLoader => _userLoader;

        #endregion

        /// <summary>
        /// Main method
        /// </summary>
        public async Task InsertAndUpdateDatabaseFromExternalUsers()
        {
            const int BATCH_SIZE = 500;
            var phaseResults = new UserImportPhaseResults();

            using (var db = _contextFactory.Create())
            {
                db.Configuration.AutoDetectChangesEnabled = false;

                _userMetaCache = new UserMetadataCache(db);
                _licenseProcessor = new UserLicenseProcessor(_logger, _userLoader, _userMetaCache);
                _dataMapper = new UserDataMapper(_logger, _userMetaCache, new SqlUserLookupStore(db));

                _logger.LogInformation($"{DateTime.Now.ToShortTimeString()} User import - start");

                // If we have no active users, assume new install so clear delta key
                var activeUserCount = await db.users.Where(u => u.AccountEnabled.HasValue && u.AccountEnabled.Value == true).CountAsync();
                if (activeUserCount == 0)
                {
                    await _userLoader.DeltaValueProvider.ClearDeltaToken();
                }

                // Load from Graph & update delta code once done
                var allActiveGraphUsers = await _userLoader.LoadAllActiveUsers();
                _logger.LogInformation($"User import - loaded {allActiveGraphUsers.Count.ToString("N0")} users from Graph");

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

                // Single pass to populate both dictionaries
                foreach (var user in allDbUsers)
                {
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
                var insertedDbUsers = await InsertMissingUsers(db, allActiveGraphUsers, graphMentionedExistingDbUsers, skus == null);
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
                            new SqlUserBulkUpdateWriter(db.Database.Connection.ConnectionString));
                    }
                    else
                    {
                        existingUsersUpdated = await _batchProcessor.ProcessExistingUsersInBatches(
                            db,
                            allActiveGraphUsers,
                            notInsertedUpns,
                            dbUsersByUpn,
                            dbUsersByAadId,
                            async (graphUser, dbUser) => await UpdateDbUserWithGraphData(db, graphUser, allActiveGraphUsers, new List<Common.Entities.User>(), dbUser, true, dbUsersByAadId),
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
                            if (u.ID > 0)
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
                if (UserImportCommitPolicy.ShouldCommitDelta(phaseResults))
                {
                    await _userLoader.CommitDeltaTokenAsync();
                }
                else
                {
                    _logger.LogWarning("User import - NOT committing the Graph delta token: at least one import phase did not complete. " +
                        "The same users will be reprocessed on the next cycle rather than being skipped.");
                }

                // Final cleanup
                dbUsersByUpn.Clear();
                dbUsersByAadId.Clear();
                allActiveGraphUsers.Clear();
                allDbUsersForLicenseRefresh?.Clear();
                allDbUsers?.Clear();
            }
        }

        private async Task UpdateDbUserWithGraphData(AnalyticsEntitiesContext db, GraphUser graphUser, List<GraphUser> allGraphUsers, List<Common.Entities.User> allDbUsers, Common.Entities.User dbUser, bool readUserSkus, Dictionary<string, Common.Entities.User> dbUsersByAadId = null)
        {
            await _dataMapper.UpdateUserMetadata(db, graphUser, allGraphUsers, dbUser, dbUsersByAadId, allDbUsers);

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
        public async Task<List<Common.Entities.User>> InsertMissingUsers(AnalyticsEntitiesContext db, List<GraphUser> allGraphUsers, List<Common.Entities.User> graphMentionedDbUsers, bool readUserSkus)
        {
            // Ensure cache and helpers are initialized (for direct method calls from tests)
            if (_userMetaCache == null)
            {
                _userMetaCache = new UserMetadataCache(db);
            }
            if (_dataMapper == null)
            {
                _dataMapper = new UserDataMapper(_logger, _userMetaCache, new SqlUserLookupStore(db));
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
                    await UpdateDbUserWithGraphData(ctx, graphUser, allGraph, allDb, dbUser, readSkus, dbByAadId));
        }

        /// <summary>
        /// Get database users that match Graph users by UPN (public wrapper for testing)
        /// </summary>
        public List<Common.Entities.User> GetDbUsersFromGraphUsers(List<GraphUser> allGraphUsers, List<Common.Entities.User> allDbUsers)
        {
            // Ensure mapper is initialized
            if (_dataMapper == null && _userMetaCache != null)
            {
                _dataMapper = new UserDataMapper(_logger, _userMetaCache);
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
                _dataMapper = new UserDataMapper(_logger, _userMetaCache);
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
