using Azure.Core;
using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Graph;
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
        private UserBatchProcessor _batchProcessor;
        private UserInsertProcessor _insertProcessor;
        private UserLicenseProcessor _licenseProcessor;
        private UserDataMapper _dataMapper;

        public UserMetadataUpdater(AnalyticsLogger telemetry, AppConfig settings, TokenCredential creds, ManualGraphCallClient manualGraphCallClient)
            : base(telemetry, settings)
        {
            IDeltaValueProvider deltaProvider = null;
            if (!string.IsNullOrEmpty(settings.ConnectionStrings.RedisConnectionString))
            {
                deltaProvider = new RedisProcessDeltaValueProvider(settings, telemetry);
                telemetry.LogInformation($"User import - using Redis for delta token cache.");
            }
            else
            {
                telemetry.LogInformation($"User import - no redis found configured, using in-process cache for delta token.");
                deltaProvider = new InProcessDeltaValueProvider(telemetry);
            }

            var graphServiceClient = new GraphServiceClient(creds);
            graphServiceClient.HttpProvider.OverallTimeout = TimeSpan.FromHours(1);

            _userLoader = new GraphUserLoader(manualGraphCallClient, deltaProvider, _telemetry, graphServiceClient);
            InitializeHelpers();
        }

        /// <summary>
        /// Constructor with injectable user loader for testing and alternate implementations
        /// </summary>
        public UserMetadataUpdater(AnalyticsLogger telemetry, AppConfig settings, IUserMetadataLoader userLoader)
            : base(telemetry, settings)
        {
            _userLoader = userLoader;
            InitializeHelpers();
        }

        private void InitializeHelpers()
        {
            _batchProcessor = new UserBatchProcessor(_telemetry);
            _insertProcessor = new UserInsertProcessor(_telemetry, _batchProcessor);
        }

        public IUserMetadataLoader UserLoader => _userLoader;

        #endregion

        /// <summary>
        /// Main method
        /// </summary>
        public async Task InsertAndUpdateDatabaseFromExternalUsers()
        {
            const int BATCH_SIZE = 500;

            using (var db = new AnalyticsEntitiesContext())
            {
                db.Configuration.AutoDetectChangesEnabled = false;

                _userMetaCache = new UserMetadataCache(db);
                _licenseProcessor = new UserLicenseProcessor(_telemetry, _userLoader, _userMetaCache);
                _dataMapper = new UserDataMapper(_telemetry, _userMetaCache);

                _telemetry.LogInformation($"{DateTime.Now.ToShortTimeString()} User import - start");

                // If we have no active users, assume new install so clear delta key
                var activeUserCount = await db.users.Where(u => u.AccountEnabled.HasValue && u.AccountEnabled.Value == true).CountAsync();
                if (activeUserCount == 0)
                {
                    await _userLoader.DeltaValueProvider.ClearDeltaToken();
                }

                // Load from Graph & update delta code once done
                var allActiveGraphUsers = await _userLoader.LoadAllActiveUsers();
                _telemetry.LogInformation($"User import - loaded {allActiveGraphUsers.Count.ToString("N0")} users from Graph");

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
                _telemetry.LogInformation($"User import - loaded {allDbUsers.Count.ToString("N0")} users from database");

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
                _telemetry.LogInformation($"User import - Insert phase completed. {insertedDbUsers.Count.ToString("N0")} new users inserted.");

                // Reload newly inserted users WITH TRACKING and update dictionaries
                // This ensures they're properly tracked when used as managers in ProcessExistingUsersInBatches
                if (insertedDbUsers.Count > 0)
                {
                    _telemetry.LogInformation($"User import - Reloading {insertedDbUsers.Count.ToString("N0")} newly inserted users with tracking for manager relationships...");

                    // Pre-compute lowercase UPNs for SQL query matching
                    var insertedUpns = new List<string>(insertedDbUsers.Count);
                    foreach (var user in insertedDbUsers)
                    {
                        if (!string.IsNullOrEmpty(user.UserPrincipalName))
                        {
                            insertedUpns.Add(user.UserPrincipalName.ToLower());
                        }
                    }

                    // Load with tracking in reasonable batches to avoid memory issues
                    const int RELOAD_BATCH_SIZE = 1000;
                    var reloadedUsers = new List<Common.Entities.User>(insertedDbUsers.Count);

                    for (int i = 0; i < insertedUpns.Count; i += RELOAD_BATCH_SIZE)
                    {
                        var batchCount = Math.Min(RELOAD_BATCH_SIZE, insertedUpns.Count - i);
                        var batchUpns = insertedUpns.GetRange(i, batchCount);
                        var batchReloaded = await db.users
                            .Where(u => batchUpns.Contains(u.UserPrincipalName.ToLower()))
                            .ToListAsync();
                        reloadedUsers.AddRange(batchReloaded);
                    }

                    // Update lookup dictionaries with TRACKED entities
                    foreach (var insertedUser in reloadedUsers)
                    {
                        var upnLower = insertedUser.UserPrincipalName?.ToLower();
                        if (!string.IsNullOrEmpty(upnLower))
                        {
                            dbUsersByUpn[upnLower] = insertedUser;
                            await _userMetaCache.UserCache.GetOrCreateNewResource(upnLower, insertedUser);
                        }

                        if (!string.IsNullOrEmpty(insertedUser.AzureAdId))
                        {
                            dbUsersByAadId[insertedUser.AzureAdId] = insertedUser;
                        }
                    }

                    insertedDbUsers = reloadedUsers;
                }

                // Identify users that need updating - use HashSet for O(1) lookup instead of Any()
                var insertedUpnSet = new HashSet<string>(
                    insertedDbUsers.Where(u => !string.IsNullOrEmpty(u.UserPrincipalName)).Select(u => u.UserPrincipalName.ToLower()),
                    StringComparer.OrdinalIgnoreCase);

                var notInsertedUpns = new HashSet<string>(allActiveGraphUsers.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var graphUser in allActiveGraphUsers)
                {
                    if (!string.IsNullOrEmpty(graphUser.UserPrincipalName) && !insertedUpnSet.Contains(graphUser.UserPrincipalName.ToLower()))
                    {
                        notInsertedUpns.Add(graphUser.UserPrincipalName.ToLower());
                    }
                }

                // Clear the large list to free memory
                allDbUsers.Clear();
                allDbUsers = null;

                // Update existing users.
                // When tenant-level SKUs are available we can use the fast bulk-SQL
                // path (no per-user Graph calls needed for licenses).
                // When SKUs are NOT available we must fall back to the EF-per-entity
                // path because each user needs individual Graph license queries.
                _telemetry.LogInformation($"User import - Starting metadata update for {notInsertedUpns.Count.ToString("N0")} existing users...");
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
                            _userMetaCache);
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
                            BATCH_SIZE);
                    }
                    _telemetry.LogInformation($"User import - Completed metadata update for {existingUsersUpdated.ToString("N0")} existing users");
                }
                catch (Exception ex)
                {
                    _telemetry.LogError($"User import - ERROR updating existing users: {ex.Message}");
                    throw;
                }

                // Combine inserted & modified db users for SKU processing
                var allProcessedDbUsers = new List<Common.Entities.User>(insertedDbUsers.Count + notInsertedUpns.Count);
                allProcessedDbUsers.AddRange(insertedDbUsers);

                // Get existing (non-inserted) users that were updated
                foreach (var graphUser in allActiveGraphUsers)
                {
                    var upn = graphUser.UserPrincipalName;
                    if (!string.IsNullOrEmpty(upn) && 
                        dbUsersByUpn.TryGetValue(upn, out var dbUser) &&
                        !insertedUpnSet.Contains(upn.ToLower()))
                    {
                        allProcessedDbUsers.Add(dbUser);
                    }
                }

                // Can we update SKUs for users on batch (ie Organization.Read.All granted)?
                if (skus != null)
                {
                    // No re-attach loop needed: AddSkuForUsers now uses FK IDs (UserId)
                    // instead of the User navigation property, so the entities do not
                    // need to be tracked by EF.
                    await _licenseProcessor.ProcessSKUsForAllUsers(skus, allProcessedDbUsers, db);
                    _telemetry.LogInformation($"User import - updated user license information from {skus.Count.ToString("N0")} tenant SKUs");

                    db.ChangeTracker.DetectChanges();
                    await db.SaveChangesAsync();
                }

                _telemetry.LogInformation($"{DateTime.Now.ToShortTimeString()} User import - complete. Inserted {insertedDbUsers.Count.ToString("N0")} new users, updated metadata for {existingUsersUpdated.ToString("N0")} existing users (from {allActiveGraphUsers.Count.ToString("N0")} Graph users)");

                // Final cleanup
                dbUsersByUpn.Clear();
                dbUsersByAadId.Clear();
                allActiveGraphUsers.Clear();
                allProcessedDbUsers.Clear();
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
                _dataMapper = new UserDataMapper(_telemetry, _userMetaCache);
            }
            if (_licenseProcessor == null)
            {
                _licenseProcessor = new UserLicenseProcessor(_telemetry, _userLoader, _userMetaCache);
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
                _dataMapper = new UserDataMapper(_telemetry, _userMetaCache);
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
                _dataMapper = new UserDataMapper(_telemetry, _userMetaCache);
            }

            // If mapper is available, use it; otherwise do direct mapping
            if (_dataMapper != null)
            {
                return _dataMapper.UpdateDbUserFromGraphUser(dbUser, graphUser);
            }
            else
            {
                // Fallback for edge cases where mapper isn't initialized
                dbUser.AccountEnabled = graphUser.AccountEnabled;
                dbUser.PostalCode = graphUser.PostalCode;
                dbUser.AzureAdId = graphUser.Id;
                dbUser.Mail = graphUser.Mail;
                return dbUser;
            }
        }
    }
}
