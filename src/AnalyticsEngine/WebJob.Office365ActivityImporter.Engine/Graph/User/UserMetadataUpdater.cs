using Azure.Core;
using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Data.SqlClient;
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

                // Pre-build dictionary for O(1) graph user lookups by AAD ID (avoids O(n) scans per user in manager resolution)
                _dataMapper.SetGraphUserLookup(allActiveGraphUsers);

                // Get SKUs from tenant
                var skus = await _userLoader.LoadTenantSkus();

                // Load DB user data without tracking
                var allDbUsers = await db.users.AsNoTracking().Include(u => u.LicenseLookups).ToListAsync();
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
        /// Inserts missing users into DB using two-phase approach: fast bulk insert, then metadata enrichment
        /// </summary>
        public async Task<List<Common.Entities.User>> InsertMissingUsers(AnalyticsEntitiesContext db, List<GraphUser> allGraphUsers, List<Common.Entities.User> graphMentionedDbUsers, bool readUserSkus)
        {
            const int BULK_INSERT_BATCH_SIZE = 10000;
            const int METADATA_BATCH_SIZE = 500;

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

            _telemetry.LogInformation($"User import - Inserting missing users (two-phase: bulk insert + metadata enrichment)...");

            // Create HashSet for O(1) lookup of existing DB users
            var existingUpns = new HashSet<string>(
                graphMentionedDbUsers.Select(u => u.UserPrincipalName?.ToLower()).Where(upn => !string.IsNullOrEmpty(upn)),
                StringComparer.OrdinalIgnoreCase);

            // Build list of users to insert - optimized with HashSet lookup
            var usersToInsert = new List<GraphUser>();
            foreach (var graphUser in allGraphUsers)
            {
                var upn = graphUser.UserPrincipalName?.ToLower();
                if (!string.IsNullOrEmpty(upn) && !existingUpns.Contains(upn))
                {
                    usersToInsert.Add(graphUser);
                    existingUpns.Add(upn); // Prevent duplicate UPNs from Graph
                }
            }

            _telemetry.LogInformation($"User import - Found {usersToInsert.Count.ToString("N0")} new users to insert");

            if (usersToInsert.Count == 0)
            {
                return new List<Common.Entities.User>();
            }

            // PHASE 1: Fast bulk insert with minimal data
            _telemetry.LogInformation($"User import - Phase 1: Starting bulk insert of {usersToInsert.Count.ToString("N0")} users...");
            await BulkInsertUsers(db, usersToInsert, BULK_INSERT_BATCH_SIZE);
            _telemetry.LogInformation($"User import - Phase 1: Bulk insert completed");

            // PHASE 2: Load inserted users and enrich with metadata
            _telemetry.LogInformation($"User import - Phase 2: Starting metadata enrichment...");
            var insertedUserUpns = usersToInsert.Select(u => u.UserPrincipalName.ToLower()).ToList();
            var insertedDbUsers = await EnrichInsertedUsersWithMetadata(
                db, 
                allGraphUsers, 
                graphMentionedDbUsers, 
                insertedUserUpns, 
                readUserSkus, 
                METADATA_BATCH_SIZE);

            _telemetry.LogInformation($"User import - Phase 2: Metadata enrichment completed for {insertedDbUsers.Count.ToString("N0")} users");

            // Cleanup
            existingUpns.Clear();
            usersToInsert.Clear();
            insertedUserUpns.Clear();

            _telemetry.LogInformation($"User import - Completed inserting and enriching {insertedDbUsers.Count.ToString("N0")} new users");

            return insertedDbUsers;
        }

        /// <summary>
        /// Phase 1: Uses SqlBulkCopy for fast bulk insert of minimal user data
        /// </summary>
        private async Task BulkInsertUsers(AnalyticsEntitiesContext db, List<GraphUser> graphUsers, int batchSize)
        {
            var connectionString = db.Database.Connection.ConnectionString;
            var totalInserted = 0;

            // Process in batches to manage memory
            for (int batchStart = 0; batchStart < graphUsers.Count; batchStart += batchSize)
            {
                var batch = graphUsers.Skip(batchStart).Take(batchSize).ToList();
                var dataTable = CreateUserDataTable(batch);

                using (var bulkCopy = new SqlBulkCopy(connectionString))
                {
                    bulkCopy.DestinationTableName = "dbo.users";
                    bulkCopy.BatchSize = batchSize;
                    bulkCopy.BulkCopyTimeout = 600; // 10 minutes

                    // Map only columns that exist in both GraphUser and the User table
                    bulkCopy.ColumnMappings.Add("UserPrincipalName", "user_name");
                    bulkCopy.ColumnMappings.Add("AzureAdId", "azure_ad_id");
                    bulkCopy.ColumnMappings.Add("AccountEnabled", "account_enabled");
                    bulkCopy.ColumnMappings.Add("Mail", "mail");
                    bulkCopy.ColumnMappings.Add("PostalCode", "postalcode");

                    await bulkCopy.WriteToServerAsync(dataTable);
                }

                totalInserted += batch.Count;
                _telemetry.LogInformation($"User import - Bulk inserted {totalInserted.ToString("N0")}/{graphUsers.Count.ToString("N0")} users to SQL");

                dataTable.Clear();
                dataTable.Dispose();
            }
        }

        /// <summary>
        /// Creates a DataTable from GraphUser list with minimal essential columns for bulk insert
        /// </summary>
        private DataTable CreateUserDataTable(List<GraphUser> graphUsers)
        {
            var dataTable = new DataTable();
            
            // Add only columns that exist in both GraphUser and the User database table
            dataTable.Columns.Add("UserPrincipalName", typeof(string));
            dataTable.Columns.Add("AzureAdId", typeof(string));
            dataTable.Columns.Add("AccountEnabled", typeof(bool));
            dataTable.Columns.Add("Mail", typeof(string));
            dataTable.Columns.Add("PostalCode", typeof(string));

            // Populate rows
            foreach (var graphUser in graphUsers)
            {
                var row = dataTable.NewRow();
                row["UserPrincipalName"] = graphUser.UserPrincipalName ?? (object)DBNull.Value;
                row["AzureAdId"] = graphUser.Id ?? (object)DBNull.Value;
                row["AccountEnabled"] = graphUser.AccountEnabled ?? false;
                row["Mail"] = graphUser.Mail ?? (object)DBNull.Value;
                row["PostalCode"] = graphUser.PostalCode ?? (object)DBNull.Value;

                dataTable.Rows.Add(row);
            }

            return dataTable;
        }

        /// <summary>
        /// Phase 2: Loads newly inserted users and enriches them with metadata (managers, licenses, etc.)
        /// </summary>
        private async Task<List<Common.Entities.User>> EnrichInsertedUsersWithMetadata(
            AnalyticsEntitiesContext db, 
            List<GraphUser> allGraphUsers, 
            List<Common.Entities.User> graphMentionedDbUsers,
            List<string> insertedUserUpns,
            bool readUserSkus,
            int batchSize)
        {
            var enrichedUsers = new List<Common.Entities.User>(insertedUserUpns.Count);

            // Create dictionary for fast Graph user lookup - pre-allocate capacity
            var graphUsersByUpn = new Dictionary<string, GraphUser>(allGraphUsers.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var graphUser in allGraphUsers)
            {
                if (!string.IsNullOrEmpty(graphUser.UserPrincipalName))
                {
                    graphUsersByUpn[graphUser.UserPrincipalName] = graphUser;
                }
            }

            // Build DB user dictionary for manager resolution from existing users
            // (newly inserted users added incrementally as each batch is loaded with tracking;
            // cross-batch managers resolved via DB fallback in UpdateUserManager)
            var dbUsersByAadId = new Dictionary<string, Common.Entities.User>(
                graphMentionedDbUsers.Count, StringComparer.OrdinalIgnoreCase);

            foreach (var user in graphMentionedDbUsers)
            {
                if (!string.IsNullOrEmpty(user.AzureAdId) && !dbUsersByAadId.ContainsKey(user.AzureAdId))
                {
                    dbUsersByAadId[user.AzureAdId] = user;
                }
            }

            // Process in batches - use GetRange for O(1) extraction
            for (int batchStart = 0; batchStart < insertedUserUpns.Count; batchStart += batchSize)
            {
                var batchCount = Math.Min(batchSize, insertedUserUpns.Count - batchStart);
                var batchUpns = insertedUserUpns.GetRange(batchStart, batchCount);

                // Load batch of newly inserted users from database WITH TRACKING for updates
                var batchUsers = await db.users
                    .Where(u => batchUpns.Contains(u.UserPrincipalName.ToLower()))
                    .Include(u => u.LicenseLookups)
                    .ToListAsync();

                // Update dbUsersByAadId with TRACKED entities from this batch
                foreach (var trackedUser in batchUsers)
                {
                    if (!string.IsNullOrEmpty(trackedUser.AzureAdId))
                    {
                        dbUsersByAadId[trackedUser.AzureAdId] = trackedUser;
                    }
                }

                // Pre-populate cache with tracked entities from this batch to prevent duplicate inserts
                foreach (var trackedUser in batchUsers)
                {
                    var upnLower = trackedUser.UserPrincipalName?.ToLower();
                    if (!string.IsNullOrEmpty(upnLower))
                    {
                        await _userMetaCache.UserCache.GetOrCreateNewResource(upnLower, trackedUser);
                    }
                }

                // Update metadata for each user
                foreach (var dbUser in batchUsers)
                {
                    var upnLower = dbUser.UserPrincipalName?.ToLower();
                    if (!string.IsNullOrEmpty(upnLower) && graphUsersByUpn.TryGetValue(upnLower, out var graphUser))
                    {
                        await UpdateDbUserWithGraphData(db, graphUser, allGraphUsers, new List<Common.Entities.User>(), dbUser, readUserSkus, dbUsersByAadId);
                    }
                }

                // Save batch
                db.ChangeTracker.DetectChanges();
                await db.SaveChangesAsync();

                enrichedUsers.AddRange(batchUsers);
                _telemetry.LogInformation($"User import - Enriched metadata for {enrichedUsers.Count.ToString("N0")}/{insertedUserUpns.Count.ToString("N0")} users");

                // Clear change tracker to free memory after each batch
                _batchProcessor.DetachAllEntitiesExceptLookups(db);
            }

            // Cleanup
            graphUsersByUpn.Clear();
            dbUsersByAadId.Clear();

            return enrichedUsers;
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
