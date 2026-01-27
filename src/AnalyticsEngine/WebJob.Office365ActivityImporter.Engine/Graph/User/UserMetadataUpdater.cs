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

                // Get SKUs from tenant
                var skus = await _userLoader.LoadTenantSkus();

                // Load only essential DB user data without tracking
                var allDbUsers = await db.users.AsNoTracking().Include(u => u.LicenseLookups).ToListAsync();
                _telemetry.LogInformation($"User import - loaded {allDbUsers.Count.ToString("N0")} users from database");

                // Create lookup dictionaries for performance
                var dbUsersByUpn = allDbUsers
                    .Where(u => !string.IsNullOrEmpty(u.UserPrincipalName))
                    .ToDictionary(u => u.UserPrincipalName.ToLower(), u => u, StringComparer.OrdinalIgnoreCase);

                var dbUsersByAadId = allDbUsers
                    .Where(u => !string.IsNullOrEmpty(u.AzureAdId))
                    .GroupBy(u => u.AzureAdId)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var graphMentionedExistingDbUsers = _dataMapper.GetDbUsersFromGraphUsers(allActiveGraphUsers, allDbUsers);

                // Insert any user we've not seen so far
                var insertedDbUsers = await InsertMissingUsers(db, allActiveGraphUsers, graphMentionedExistingDbUsers, skus == null);
                
                // Identify users that need updating
                var notInsertedUpns = new HashSet<string>(
                    allActiveGraphUsers
                        .Where(u => !string.IsNullOrEmpty(u.UserPrincipalName) && 
                                    !insertedDbUsers.Any(i => i.UserPrincipalName.Equals(u.UserPrincipalName, StringComparison.OrdinalIgnoreCase)))
                        .Select(u => u.UserPrincipalName.ToLower()),
                    StringComparer.OrdinalIgnoreCase);

                // Clear the large list to free memory
                allDbUsers.Clear();
                allDbUsers = null;

                // Process existing users in batches using batch processor
                await _batchProcessor.ProcessExistingUsersInBatches(
                    db,
                    allActiveGraphUsers,
                    notInsertedUpns,
                    dbUsersByUpn,
                    dbUsersByAadId,
                    async (graphUser, dbUser) => await UpdateDbUserWithGraphData(db, graphUser, allActiveGraphUsers, new List<Common.Entities.User>(), dbUser, skus == null, dbUsersByAadId),
                    BATCH_SIZE);

                // Combine inserted & modified db users for SKU processing
                var allProcessedDbUsers = new List<Common.Entities.User>(insertedDbUsers);
                var notInsertDbUsers = allActiveGraphUsers
                    .Where(g => !string.IsNullOrEmpty(g.UserPrincipalName) && dbUsersByUpn.ContainsKey(g.UserPrincipalName.ToLower()))
                    .Select(g => dbUsersByUpn[g.UserPrincipalName.ToLower()])
                    .ToList();
                allProcessedDbUsers.AddRange(notInsertDbUsers);

                // Can we update SKUs for users on batch (ie Organization.Read.All granted)?
                if (skus != null)
                {
                    // Re-attach users to the context to ensure they're tracked properly
                    foreach (var user in allProcessedDbUsers)
                    {
                        if (db.Entry(user).State == EntityState.Detached)
                        {
                            db.users.Attach(user);
                        }
                        // Reload license lookups from database to get current state
                        db.Entry(user).Collection(u => u.LicenseLookups).Load();
                    }
                    
                    await _licenseProcessor.ProcessSKUsForAllUsers(skus, allProcessedDbUsers, db);
                    _telemetry.LogInformation($"User import - updated user license information from {skus.Count.ToString("N0")} tenant SKUs");
                    
                    db.ChangeTracker.DetectChanges();
                    await db.SaveChangesAsync();
                }

                // Final cleanup
                dbUsersByUpn.Clear();
                dbUsersByAadId.Clear();
                allActiveGraphUsers.Clear();
                allProcessedDbUsers.Clear();

                _telemetry.LogInformation($"{DateTime.Now.ToShortTimeString()} User import - inserted {insertedDbUsers.Count.ToString("N0")} new users and updated {notInsertedUpns.Count.ToString("N0")} from Graph API");
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
        /// Inserts missing users into DB & calls UpdateDbUserWithGraphData
        /// </summary>
        public async Task<List<Common.Entities.User>> InsertMissingUsers(AnalyticsEntitiesContext db, List<GraphUser> allGraphUsers, List<Common.Entities.User> graphMentionedDbUsers, bool readUserSkus)
        {
            const int BATCH_SIZE = 500;

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

            _telemetry.LogInformation($"User import - Inserting missing users...");
            var usersInserted = new List<Common.Entities.User>();

            // Create HashSet for O(1) lookup of existing DB users
            var existingUpns = new HashSet<string>(
                graphMentionedDbUsers.Select(u => u.UserPrincipalName?.ToLower()).Where(upn => !string.IsNullOrEmpty(upn)),
                StringComparer.OrdinalIgnoreCase);

            // Create dictionary for fast Graph user lookup
            var graphUsersByUpn = allGraphUsers
                .Where(g => !string.IsNullOrEmpty(g.UserPrincipalName))
                .ToDictionary(g => g.UserPrincipalName.ToLower(), g => g, StringComparer.OrdinalIgnoreCase);

            // Create dictionary for fast DB user lookup by Azure AD ID
            var dbUsersByAadId = graphMentionedDbUsers
                .Where(u => !string.IsNullOrEmpty(u.AzureAdId))
                .GroupBy(u => u.AzureAdId)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // Build list of users to insert - optimized with HashSet lookup
            var usersToInsert = new List<GraphUser>();
            foreach (var graphUser in allGraphUsers)
            {
                var upn = graphUser.UserPrincipalName?.ToLower();
                if (!string.IsNullOrEmpty(upn) && !existingUpns.Contains(upn))
                {
                    usersToInsert.Add(graphUser);
                }
            }

            _telemetry.LogInformation($"User import - Found {usersToInsert.Count.ToString("N0")} new users to insert");

            // Process in batches to reduce memory pressure
            for (int batchStart = 0; batchStart < usersToInsert.Count; batchStart += BATCH_SIZE)
            {
                var batch = usersToInsert.Skip(batchStart).Take(BATCH_SIZE).ToList();
                var batchInserted = new List<Common.Entities.User>();

                foreach (var graphUser in batch)
                {
                    var upn = graphUser.UserPrincipalName?.ToLower();
                    // Lookup manager will just add to cache but not to context
                    var dbUser = await _userMetaCache.UserCache.GetOrCreateNewResource(
                        upn,
                        _dataMapper.UpdateDbUserFromGraphUser(new Common.Entities.User { UserPrincipalName = upn }, graphUser));
                    batchInserted.Add(dbUser);
                }

                // Update metadata for each user in batch
                for (int i = 0; i < batchInserted.Count; i++)
                {
                    var newDbUser = batchInserted[i];
                    var upnLower = newDbUser.UserPrincipalName.ToLower();

                    if (graphUsersByUpn.TryGetValue(upnLower, out var graphUser))
                    {
                        await UpdateDbUserWithGraphData(db, graphUser, allGraphUsers, graphMentionedDbUsers, newDbUser, readUserSkus, dbUsersByAadId);
                    }
                }

                // Save batch and clear change tracker to free memory
                db.ChangeTracker.DetectChanges();
                await db.SaveChangesAsync();
                
                usersInserted.AddRange(batchInserted);
                _telemetry.LogInformation($"User import - Saved batch {usersInserted.Count.ToString("N0")}/{usersToInsert.Count.ToString("N0")} new users to SQL");

                // Clear change tracker to free memory after each batch, but preserve lookups
                _batchProcessor.DetachAllEntitiesExceptLookups(db);
                batchInserted.Clear();
            }

            // Cleanup
            existingUpns.Clear();
            graphUsersByUpn.Clear();
            dbUsersByAadId.Clear();
            usersToInsert.Clear();

            _telemetry.LogInformation($"User import - Completed inserting {usersInserted.Count.ToString("N0")} new users");

            return usersInserted;
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
