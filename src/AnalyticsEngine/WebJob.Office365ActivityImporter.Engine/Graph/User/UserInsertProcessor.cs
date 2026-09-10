using Common.Entities;
using DataUtils;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Handles inserting new users into the database via two-phase approach:
    /// fast bulk insert (SqlBulkCopy), then metadata enrichment in batches.
    /// </summary>
    internal class UserInsertProcessor
    {
        private readonly AnalyticsLogger _logger;
        private readonly UserBatchProcessor _batchProcessor;
        private const int BULK_INSERT_BATCH_SIZE = 10000;
        private const int METADATA_BATCH_SIZE = 500;

        public UserInsertProcessor(AnalyticsLogger logger, UserBatchProcessor batchProcessor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _batchProcessor = batchProcessor ?? throw new ArgumentNullException(nameof(batchProcessor));
        }

        /// <summary>
        /// Inserts missing users into DB using two-phase approach: fast bulk insert, then metadata enrichment
        /// </summary>
        public async Task<List<Common.Entities.User>> InsertMissingUsers(
            AnalyticsEntitiesContext db,
            List<GraphUser> allGraphUsers,
            List<Common.Entities.User> graphMentionedDbUsers,
            bool readUserSkus,
            UserMetadataCache userMetaCache,
            UserDataMapper dataMapper,
            UserLicenseProcessor licenseProcessor,
            Func<AnalyticsEntitiesContext, GraphUser, List<GraphUser>, List<Common.Entities.User>, Common.Entities.User, bool, Dictionary<string, Common.Entities.User>, Task> updateAction)
        {
            _logger.LogInformation($"User import - Inserting missing users (two-phase: bulk insert + metadata enrichment)...");

            // Create HashSet for O(1) lookup of existing DB users.
            // OrdinalIgnoreCase comparer handles case so we don't need .ToLower() on the keys
            // (saves ~187k string allocations on a 200k-user tenant).
            var existingUpns = new HashSet<string>(
                graphMentionedDbUsers.Select(u => u.UserPrincipalName).Where(upn => !string.IsNullOrEmpty(upn)),
                StringComparer.OrdinalIgnoreCase);

            // Build list of users to insert - optimized with HashSet lookup
            var usersToInsert = new List<GraphUser>();
            foreach (var graphUser in allGraphUsers)
            {
                var upn = graphUser.UserPrincipalName;
                if (!string.IsNullOrEmpty(upn) && !existingUpns.Contains(upn))
                {
                    usersToInsert.Add(graphUser);
                    existingUpns.Add(upn); // Prevent duplicate UPNs from Graph
                }
            }

            _logger.LogInformation($"User import - Found {usersToInsert.Count.ToString("N0")} new users to insert");

            if (usersToInsert.Count == 0)
            {
                return new List<Common.Entities.User>();
            }

            // PHASE 1: Fast bulk insert with minimal data
            _logger.LogInformation($"User import - Phase 1: Starting bulk insert of {usersToInsert.Count.ToString("N0")} users...");
            await BulkInsertUsers(db, usersToInsert, BULK_INSERT_BATCH_SIZE);
            _logger.LogInformation($"User import - Phase 1: Bulk insert completed");

            // PHASE 2: Load inserted users and enrich with metadata
            _logger.LogInformation($"User import - Phase 2: Starting metadata enrichment for {usersToInsert.Count.ToString("N0")} new users (existing users will be updated separately)...");
            var insertedUserUpns = usersToInsert.Select(u => u.UserPrincipalName).ToList();
            var insertedDbUsers = await EnrichInsertedUsersWithMetadata(
                db,
                allGraphUsers,
                graphMentionedDbUsers,
                insertedUserUpns,
                readUserSkus,
                METADATA_BATCH_SIZE,
                userMetaCache,
                dataMapper,
                updateAction);

            _logger.LogInformation($"User import - Phase 2: Metadata enrichment completed for {insertedDbUsers.Count.ToString("N0")} new users");

            // Cleanup
            existingUpns.Clear();
            usersToInsert.Clear();
            insertedUserUpns.Clear();

            _logger.LogInformation($"User import - Completed inserting and enriching {insertedDbUsers.Count.ToString("N0")} new users");

            return insertedDbUsers;
        }

        /// <summary>
        /// Phase 1: Uses SqlBulkCopy for fast bulk insert of minimal user data
        /// </summary>
        private async Task BulkInsertUsers(AnalyticsEntitiesContext db, List<GraphUser> graphUsers, int batchSize)
        {
            var connectionString = db.Database.Connection.ConnectionString;
            var totalInserted = 0;

            // Process in batches to manage memory.
            // GetRange instead of Skip().Take() - Skip() walks past i elements every call,
            // so chunking N items in slices of K costs O(N^2/K). For 200k users in 10k batches
            // that's a 2M-step linear scan over the list head.
            for (int batchStart = 0; batchStart < graphUsers.Count; batchStart += batchSize)
            {
                var batchCount = Math.Min(batchSize, graphUsers.Count - batchStart);
                var batch = graphUsers.GetRange(batchStart, batchCount);
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
                _logger.LogInformation($"User import - Bulk inserted {totalInserted.ToString("N0")}/{graphUsers.Count.ToString("N0")} users to SQL");

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
            int batchSize,
            UserMetadataCache userMetaCache,
            UserDataMapper dataMapper,
            Func<AnalyticsEntitiesContext, GraphUser, List<GraphUser>, List<Common.Entities.User>, Common.Entities.User, bool, Dictionary<string, Common.Entities.User>, Task> updateAction)
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
            var enrichSw = Stopwatch.StartNew();
            for (int batchStart = 0; batchStart < insertedUserUpns.Count; batchStart += batchSize)
            {
                var batchCount = Math.Min(batchSize, insertedUserUpns.Count - batchStart);
                var batchUpns = insertedUserUpns.GetRange(batchStart, batchCount);

                // Load batch of newly inserted users from database WITH TRACKING for updates.
                // No LOWER() on the column - the default code-first collation is case-insensitive
                // (Latin1_General_CI_AS) so the comparison still matches mixed-case UPNs but the
                // predicate stays SARGable against the user_name index. At 200k users this turns
                // a clustered-index scan into an index seek per batch.
                var batchUsers = await db.users
                    .Where(u => batchUpns.Contains(u.UserPrincipalName))
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

                // Pre-populate cache with tracked entities from this batch to prevent duplicate inserts.
                // userMetaCache.UserCache uses OrdinalIgnoreCase so we don't need to lowercase the key.
                foreach (var trackedUser in batchUsers)
                {
                    if (!string.IsNullOrEmpty(trackedUser.UserPrincipalName))
                    {
                        await userMetaCache.UserCache.GetOrCreateNewResource(trackedUser.UserPrincipalName, trackedUser);
                    }
                }

                // Update metadata for each user
                //
                // Resolve this batch's managers in ONE query first. Without it the manager
                // resolution chain falls through to a per-user database lookup for every manager
                // that is not already in dbUsersByAadId - which, since that dictionary is seeded
                // from pre-existing users and then grows a batch at a time, means every manager
                // who happens to be inserted in a later batch than their report. Graph does not
                // order the delta by reporting line, so on a first import that is a large share of
                // everyone who has a manager (#371).
                var batchGraphUsers = new List<GraphUser>(batchUsers.Count);
                foreach (var dbUser in batchUsers)
                {
                    if (!string.IsNullOrEmpty(dbUser.UserPrincipalName) && graphUsersByUpn.TryGetValue(dbUser.UserPrincipalName, out var batchGraphUser))
                    {
                        batchGraphUsers.Add(batchGraphUser);
                    }
                }
                await dataMapper.PrefetchManagersForBatchAsync(batchGraphUsers);

                foreach (var dbUser in batchUsers)
                {
                    if (!string.IsNullOrEmpty(dbUser.UserPrincipalName) && graphUsersByUpn.TryGetValue(dbUser.UserPrincipalName, out var graphUser))
                    {
                        await updateAction(db, graphUser, allGraphUsers, new List<Common.Entities.User>(), dbUser, readUserSkus, dbUsersByAadId);
                    }
                }

                // Save batch
                db.ChangeTracker.DetectChanges();
                await db.SaveChangesAsync();

                enrichedUsers.AddRange(batchUsers);
                var percentDone = (double)enrichedUsers.Count / insertedUserUpns.Count * 100;
                var elapsedMs = enrichSw.ElapsedMilliseconds;
                var estimatedTotalMs = elapsedMs / percentDone * 100;
                var remainingMs = estimatedTotalMs - elapsedMs;
                var remaining = TimeSpan.FromMilliseconds(remainingMs);
                _logger.LogInformation($"User import - Enriched metadata for {enrichedUsers.Count.ToString("N0")}/{insertedUserUpns.Count.ToString("N0")} new users ({percentDone:F1}% done, estimated {remaining.Hours}h {remaining.Minutes}m {remaining.Seconds}s remaining)");

                // Clear change tracker to free memory after each batch
                _batchProcessor.DetachAllEntitiesExceptLookups(db);
            }

            // Cleanup
            graphUsersByUpn.Clear();
            dbUsersByAadId.Clear();

            return enrichedUsers;
        }
    }
}
