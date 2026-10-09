using Common.Entities;
using Common.Entities.LookupCaches;
using DataUtils;
using DataUtils.Sql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Text;
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

        /// <summary>Two parameters each, so well under SQL Server's 2,100-parameter limit.</summary>
        private const int CREATED_DATES_PER_STATEMENT = 500;

        /// <summary>
        /// Bulk-copy attempts per batch. Each failed attempt leaves out the users another import has created since
        /// the user list was loaded, so a further attempt only fails if yet another one appears in the meantime.
        /// </summary>
        internal const int MaxBulkInsertAttemptsPerBatch = 3;

        /// <summary>
        /// Test seam: runs before each bulk-copy attempt with the users about to be written, so a test can create
        /// one of them first, exactly as a concurrent import would.
        /// </summary>
        internal Func<IReadOnlyList<GraphUser>, Task> BeforeBulkInsertAttemptAsync { get; set; }

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
        private Task BulkInsertUsers(AnalyticsEntitiesContext db, List<GraphUser> graphUsers, int batchSize)
        {
            return BulkInsertUsers(db.Database.Connection.ConnectionString, graphUsers, batchSize);
        }

        /// <summary>
        /// Bulk-inserts <paramref name="graphUsers"/> into <c>dbo.users</c> over one connection, leaving out any
        /// that another import creates in the meantime (see <see cref="BulkInsertBatch"/>).
        /// </summary>
        /// <remarks>
        /// The connection is created through <see cref="AzureSqlTokenAuth.CreateConnection"/> and handed
        /// to <see cref="SqlBulkCopy"/> already open, exactly as <see cref="SqlUserBulkUpdateWriter"/> does.
        /// <c>new SqlBulkCopy(connectionString)</c> opens a private connection of its own that neither EF's
        /// <c>AzureSqlAccessTokenInterceptor</c> nor this helper ever sees, so on an Entra-only Azure SQL
        /// server - whose connection string carries no login - it connected with no access token and failed
        /// with <c>Login failed for user ''</c> on every import cycle, taking every later Graph section down
        /// with it (#609). SQL-authentication connection strings are left untouched by the helper.
        /// Internal, and keyed on the connection string rather than a context, so the token path can be
        /// tested without building an EF model.
        /// </remarks>
        internal async Task BulkInsertUsers(string connectionString, List<GraphUser> graphUsers, int batchSize)
        {
            var totalInserted = 0;
            var totalCreatedMeanwhile = 0;

            using (var connection = AzureSqlTokenAuth.CreateConnection(connectionString))
            {
                await connection.OpenAsync();

                // Process in batches to manage memory.
                // GetRange instead of Skip().Take() - Skip() walks past i elements every call,
                // so chunking N items in slices of K costs O(N^2/K). For 200k users in 10k batches
                // that's a 2M-step linear scan over the list head.
                for (int batchStart = 0; batchStart < graphUsers.Count; batchStart += batchSize)
                {
                    var batchCount = Math.Min(batchSize, graphUsers.Count - batchStart);
                    var batch = graphUsers.GetRange(batchStart, batchCount);

                    var inserted = await BulkInsertBatch(connection, batch, batchSize);

                    totalInserted += inserted;
                    totalCreatedMeanwhile += batchCount - inserted;
                    _logger.LogInformation(totalCreatedMeanwhile == 0
                        ? $"User import - Bulk inserted {totalInserted.ToString("N0")}/{graphUsers.Count.ToString("N0")} users to SQL"
                        : $"User import - Bulk inserted {totalInserted.ToString("N0")}/{graphUsers.Count.ToString("N0")} users to SQL ({totalCreatedMeanwhile.ToString("N0")} already created by another import)");
                }
            }
        }

        /// <summary>
        /// Writes one batch, leaving out any of its users that another import has created since the user list was
        /// loaded, and returns how many it inserted (#714).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>dbo.users.user_name</c> is unique (<c>IX_users</c>), and other imports create users too: the usage
        /// reports, the Copilot per-user report, the audit merges, the Teams call processor and the App Insights
        /// importer. A full directory read can hold the user list for many minutes before this insert, so one of its
        /// users can be created in between. A bulk-copy batch is all-or-nothing, so that single duplicate used to fail
        /// the whole batch, the user import, and every Graph section after it in that cycle.
        /// </para>
        /// <para>
        /// On a duplicate-key error the database is asked which of the batch's users exist now, by the comparison
        /// <c>IX_users</c> itself uses, and the batch is written again without them. Their rows are kept, not written
        /// over: phase 2 reloads every user of this insert by UPN, so they still get their Graph metadata, and
        /// <see cref="FillAccountCreatedDates"/> gives them the one column phase 2 cannot write. At most
        /// <see cref="MaxBulkInsertAttemptsPerBatch"/> attempts. A duplicate-key error that none of the batch's users
        /// explains, and every other error, is rethrown unchanged.
        /// </para>
        /// </remarks>
        private async Task<int> BulkInsertBatch(SqlConnection connection, List<GraphUser> batch, int batchSize)
        {
            for (var attempt = 1; ; attempt++)
            {
                if (BeforeBulkInsertAttemptAsync != null)
                {
                    await BeforeBulkInsertAttemptAsync(batch);
                }

                try
                {
                    await WriteBatch(connection, batch, batchSize);
                    return batch.Count;
                }
                catch (Exception ex) when (attempt < MaxBulkInsertAttemptsPerBatch && SqlDuplicateKey.IsViolation(ex))
                {
                    var existingIds = await ExistingUserIds.FindAsync(connection, batch.Select(u => u.UserPrincipalName).ToList());
                    var notYetCreated = new List<GraphUser>(batch.Count);
                    for (var i = 0; i < batch.Count; i++)
                    {
                        if (!existingIds[i].HasValue)
                        {
                            notYetCreated.Add(batch[i]);
                        }
                    }

                    var createdMeanwhile = batch.Count - notYetCreated.Count;
                    if (createdMeanwhile == 0)
                    {
                        // None of the batch's users exists, so this is not the race above.
                        throw;
                    }

                    await FillAccountCreatedDates(connection, batch, existingIds);
                    _logger.LogInformation($"User import - {createdMeanwhile.ToString("N0")} of the {batch.Count.ToString("N0")} users in this bulk-insert batch were created by another import since the user list was loaded, so they are not inserted again (they still get their Graph metadata). Inserting the other {notYetCreated.Count.ToString("N0")}.");

                    batch = notYetCreated;
                    if (batch.Count == 0)
                    {
                        return 0;
                    }
                }
            }
        }

        /// <summary>
        /// Gives the users another import created meanwhile the account-creation date this insert would have written,
        /// where they have none. Never overwrites a value.
        /// </summary>
        /// <remarks>
        /// Phase 2 writes every other column the bulk copy does, but <c>created_utc</c> is not in the EF model.
        /// Without this it would stay NULL until the existing-user update next sees the user - for a delta import,
        /// not until the account changes - and Copilot Adoption reads it as the account-age proxy for the reclaim
        /// grace period, which is exactly what the new accounts that meet this race need.
        /// </remarks>
        private static async Task FillAccountCreatedDates(SqlConnection connection, List<GraphUser> batch, int?[] existingIds)
        {
            var dates = new List<KeyValuePair<int, DateTime>>();
            for (var i = 0; i < batch.Count; i++)
            {
                if (existingIds[i].HasValue && batch[i].CreatedDateTime.HasValue)
                {
                    dates.Add(new KeyValuePair<int, DateTime>(existingIds[i].Value, batch[i].CreatedDateTime.Value));
                }
            }

            for (var offset = 0; offset < dates.Count; offset += CREATED_DATES_PER_STATEMENT)
            {
                var count = Math.Min(CREATED_DATES_PER_STATEMENT, dates.Count - offset);
                using (var command = new SqlCommand { Connection = connection, CommandTimeout = 600 })
                {
                    var sql = new StringBuilder("UPDATE u SET created_utc = v.created_utc FROM dbo.users AS u INNER JOIN (VALUES ");
                    for (var i = 0; i < count; i++)
                    {
                        if (i > 0)
                        {
                            sql.Append(',');
                        }
                        sql.Append("(@i").Append(i).Append(",@c").Append(i).Append(')');
                        command.Parameters.Add("@i" + i, SqlDbType.Int).Value = dates[offset + i].Key;
                        command.Parameters.Add("@c" + i, SqlDbType.DateTime2).Value = dates[offset + i].Value;
                    }
                    sql.Append(") AS v(id, created_utc) ON u.id = v.id WHERE u.created_utc IS NULL;");

                    command.CommandText = sql.ToString();
                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        private async Task WriteBatch(SqlConnection connection, List<GraphUser> batch, int batchSize)
        {
            using (var dataTable = CreateUserDataTable(batch))
            using (var bulkCopy = new SqlBulkCopy(connection))
            {
                bulkCopy.DestinationTableName = "dbo.users";
                bulkCopy.BatchSize = batchSize;
                bulkCopy.BulkCopyTimeout = 600; // 10 minutes

                // Map only columns that exist in both GraphUser and the User table
                bulkCopy.ColumnMappings.Add("UserPrincipalName", "user_name");
                bulkCopy.ColumnMappings.Add("AzureAdId", "azure_ad_id");
                bulkCopy.ColumnMappings.Add("AccountEnabled", "account_enabled");
                bulkCopy.ColumnMappings.Add("CreatedDateTime", "created_utc");
                bulkCopy.ColumnMappings.Add("Mail", "mail");
                bulkCopy.ColumnMappings.Add("PostalCode", "postalcode");

                await bulkCopy.WriteToServerAsync(dataTable);
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
            dataTable.Columns.Add("CreatedDateTime", typeof(DateTime));
            dataTable.Columns.Add("Mail", typeof(string));
            dataTable.Columns.Add("PostalCode", typeof(string));

            // Populate rows
            foreach (var graphUser in graphUsers)
            {
                var row = dataTable.NewRow();
                row["UserPrincipalName"] = graphUser.UserPrincipalName ?? (object)DBNull.Value;
                row["AzureAdId"] = graphUser.Id ?? (object)DBNull.Value;
                row["AccountEnabled"] = graphUser.AccountEnabled ?? false;
                row["CreatedDateTime"] = graphUser.CreatedDateTime ?? (object)DBNull.Value;
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

                var batchUsers = await new SqlUserLookupStore(db).GetUsersByUpnAsync(batchUpns);

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
                // Resolve this batch's managers in bulk first. Without it the manager
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
