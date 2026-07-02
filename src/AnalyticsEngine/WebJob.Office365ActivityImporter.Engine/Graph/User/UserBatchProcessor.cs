using Common.Entities;
using DataUtils;
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
    /// Handles batch processing operations for user data to optimize memory usage and database operations
    /// </summary>
    internal class UserBatchProcessor
    {
        private readonly AnalyticsLogger _logger;
        private const int DEFAULT_BATCH_SIZE = 500;

        public UserBatchProcessor(AnalyticsLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Process existing users in batches to reduce memory pressure
        /// </summary>
        public async Task<int> ProcessExistingUsersInBatches(
            AnalyticsEntitiesContext db,
            List<GraphUser> allActiveGraphUsers,
            HashSet<string> userUpnsToProcess,
            Dictionary<string, Common.Entities.User> dbUsersByUpn,
            Dictionary<string, Common.Entities.User> dbUsersByAadId,
            Func<GraphUser, Common.Entities.User, Task> updateAction,
            int batchSize = DEFAULT_BATCH_SIZE)
        {
            _logger.LogInformation($"User import - updating {userUpnsToProcess.Count.ToString("N0")} existing users in batches...");

            int processedCount = 0;
            // userUpnsToProcess is OrdinalIgnoreCase so we no longer need .ToLower() per Graph user.
            var batchedGraphUsers = allActiveGraphUsers
                .Where(u => !string.IsNullOrEmpty(u.UserPrincipalName) && userUpnsToProcess.Contains(u.UserPrincipalName))
                .ToList();

            for (int i = 0; i < batchedGraphUsers.Count; i += batchSize)
            {
                var batchCount = Math.Min(batchSize, batchedGraphUsers.Count - i);
                var batch = batchedGraphUsers.GetRange(i, batchCount);

                // CRITICAL: Ensure all entities in the dictionaries that might be referenced
                // by this batch are properly attached BEFORE processing
                // This prevents "Cannot insert duplicate key" errors when assigning navigation properties
                var referencedUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var graphUser in batch)
                {
                    // Collect all Azure AD IDs that might be referenced (managers, etc.)
                    if (graphUser.DefaultManagerInfo?.Id != null)
                    {
                        referencedUserIds.Add(graphUser.DefaultManagerInfo.Id);
                    }
                }


                // Attach any detached users that will be referenced in this batch
                foreach (var aadId in referencedUserIds)
                {
                    if (dbUsersByAadId.TryGetValue(aadId, out var referencedUser))
                    {
                        var trackedUser = GetOrAttachUser(db, referencedUser);
                        // Update dictionary with tracked entity
                        if (trackedUser != referencedUser)
                        {
                            dbUsersByAadId[aadId] = trackedUser;
                        }
                    }
                }

                foreach (var existingGraphUser in batch)
                {
                    var upn = existingGraphUser.UserPrincipalName;
                    if (!string.IsNullOrEmpty(upn) && dbUsersByUpn.TryGetValue(upn, out var dbUser))
                    {
                        // Get tracked version of the user (or attach if not tracked)
                        var trackedUser = GetOrAttachUser(db, dbUser);

                        // Update dictionary with tracked entity
                        if (trackedUser != dbUser)
                        {
                            dbUsersByUpn[upn] = trackedUser;
                            if (!string.IsNullOrEmpty(trackedUser.AzureAdId))
                            {
                                dbUsersByAadId[trackedUser.AzureAdId] = trackedUser;
                            }
                        }

                        await updateAction(existingGraphUser, trackedUser);
                    }
                }

                // Save batch and clear change tracker to free memory
                db.ChangeTracker.DetectChanges();
                await db.SaveChangesAsync();

                processedCount += batch.Count;
                _logger.LogInformation($"User import - processed batch {processedCount.ToString("N0")}/{batchedGraphUsers.Count.ToString("N0")} existing users");

                // Clear change tracker to release memory, but preserve lookups
                DetachAllEntitiesExceptLookups(db);
            }

            return processedCount;
        }

        /// <summary>
        /// Gets a tracked version of the user entity. If the entity is detached, checks if another
        /// entity with the same ID is already tracked and returns that. Otherwise attaches the entity.
        /// This prevents "Attaching an entity failed because another entity of the same type already 
        /// has the same primary key value" errors.
        /// </summary>
        private Common.Entities.User GetOrAttachUser(AnalyticsEntitiesContext db, Common.Entities.User user)
        {
            if (user == null)
            {
                return null;
            }

            var entry = db.Entry(user);

            // If already tracked, return as-is
            if (entry.State != EntityState.Detached)
            {
                return user;
            }

            // Check if another entity with the same ID is already tracked
            if (user.ID > 0)
            {
                var alreadyTracked = db.ChangeTracker.Entries<Common.Entities.User>()
                    .FirstOrDefault(e => e.Entity.ID == user.ID && e.State != EntityState.Detached);

                if (alreadyTracked != null)
                {
                    return alreadyTracked.Entity;
                }
            }

            // No tracked entity found - safe to attach
            try
            {
                return db.users.Attach(user);
            }
            catch (InvalidOperationException)
            {
                // Another entity with same key was added between our check and attach
                // Try to find it again
                var tracked = db.ChangeTracker.Entries<Common.Entities.User>()
                    .FirstOrDefault(e => e.Entity.ID == user.ID && e.State != EntityState.Detached);

                if (tracked != null)
                {
                    return tracked.Entity;
                }

                // If still can't find, try Find() as last resort
                if (user.ID > 0)
                {
                    var found = db.users.Find(user.ID);
                    if (found != null)
                    {
                        return found;
                    }
                }

                throw; // Re-throw if we truly can't resolve
            }
        }

        /// <summary>
        /// Detach all entities from the change tracker to free memory
        /// </summary>
        public void DetachAllEntities(AnalyticsEntitiesContext db)
        {
            foreach (var entry in db.ChangeTracker.Entries().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }

        /// <summary>
        /// Detach all entities except lookup entities to free memory while preserving lookup cache consistency.
        /// This prevents FK constraint violations when processing users in batches.
        /// </summary>
        public void DetachAllEntitiesExceptLookups(AnalyticsEntitiesContext db)
        {
            var lookupTypes = new HashSet<Type>
            {
                typeof(UserDepartment),
                typeof(UserJobTitle),
                typeof(UserOfficeLocation),
                typeof(UserUsageLocation),
                typeof(CountryOrRegion),
                typeof(StateOrProvince),
                typeof(CompanyName),
                typeof(LicenseType)
            };

            foreach (var entry in db.ChangeTracker.Entries().ToList())
            {
                if (!lookupTypes.Contains(entry.Entity.GetType()))
                {
                    entry.State = EntityState.Detached;
                }
            }
        }

        /// <summary>
        /// Detach specific entity type from the change tracker
        /// </summary>
        public void DetachEntities<T>(AnalyticsEntitiesContext db) where T : class
        {
            foreach (var entry in db.ChangeTracker.Entries<T>().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }

        #region Bulk SQL Update

        /// <summary>
        /// Bulk update existing users using raw SQL for maximum performance.
        /// Replaces per-entity EF tracking with temp table + SQL UPDATE JOIN.
        /// Pre-warms all lookup caches then builds a DataTable with resolved FK IDs,
        /// bulk-copies to a temp table, and executes a single UPDATE ... FROM JOIN.
        /// </summary>
        public async Task<int> BulkUpdateExistingUsers(
            AnalyticsEntitiesContext db,
            List<GraphUser> allActiveGraphUsers,
            HashSet<string> userUpnsToProcess,
            Dictionary<string, Common.Entities.User> dbUsersByUpn,
            Dictionary<string, Common.Entities.User> dbUsersByAadId,
            Dictionary<string, GraphUser> graphUsersByAadId,
            UserMetadataCache userMetaCache)
        {
            var graphUsersToUpdate = new List<GraphUser>(userUpnsToProcess.Count);
            foreach (var u in allActiveGraphUsers)
            {
                if (!string.IsNullOrEmpty(u.UserPrincipalName) &&
                    userUpnsToProcess.Contains(u.UserPrincipalName))
                {
                    graphUsersToUpdate.Add(u);
                }
            }

            if (graphUsersToUpdate.Count == 0)
                return 0;

            _logger.LogInformation($"User import - bulk updating {graphUsersToUpdate.Count.ToString("N0")} existing users...");

            // Pre-warm all lookup caches so every value has a DB ID
            var lookupMaps = await PreWarmLookupCaches(db, graphUsersToUpdate, userMetaCache);

            // Save new lookup entities to DB so their IDs are populated
            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync();

            var connectionString = db.Database.Connection.ConnectionString;
            int totalProcessed = 0;
            const int BULK_BATCH_SIZE = 50000;

            for (int i = 0; i < graphUsersToUpdate.Count; i += BULK_BATCH_SIZE)
            {
                var batchCount = Math.Min(BULK_BATCH_SIZE, graphUsersToUpdate.Count - i);
                var batch = graphUsersToUpdate.GetRange(i, batchCount);

                using (var dataTable = BuildUpdateDataTable(batch, lookupMaps, dbUsersByAadId, dbUsersByUpn, graphUsersByAadId))
                {
                    await ExecuteBulkUpdate(connectionString, dataTable);
                }

                totalProcessed += batchCount;
                _logger.LogInformation($"User import - bulk updated {totalProcessed.ToString("N0")}/{graphUsersToUpdate.Count.ToString("N0")} existing users");
            }

            return totalProcessed;
        }

        /// <summary>
        /// Collects every unique lookup value from graph users and ensures it exists
        /// in both the EF cache and DB. Returns entity-reference maps keyed by normalised name.
        /// </summary>
        private async Task<LookupEntityMaps> PreWarmLookupCaches(
            AnalyticsEntitiesContext db,
            List<GraphUser> graphUsers,
            UserMetadataCache cache)
        {
            var maps = new LookupEntityMaps();

            // Collect unique normalised names (single pass)
            var deptSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var titleSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var officeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usageSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var countrySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stateSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var companySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var u in graphUsers)
            {
                AddNormalized(u.Department, deptSet);
                AddNormalized(u.JobTitle, titleSet);
                AddNormalized(u.OfficeLocation, officeSet);
                AddNormalized(u.UsageLocation, usageSet);
                AddNormalized(u.Country, countrySet);
                AddNormalized(u.State, stateSet);
                AddNormalized(u.CompanyName, companySet);
            }

            // Pre-warm each cache and store entity reference (ID populated after SaveChanges)
            foreach (var n in deptSet)
                maps.Departments[n] = await cache.DepartmentCache.GetOrCreateNewResource(n, new UserDepartment { Name = n });
            foreach (var n in titleSet)
                maps.JobTitles[n] = await cache.JobTitleCache.GetOrCreateNewResource(n, new UserJobTitle { Name = n });
            foreach (var n in officeSet)
                maps.OfficeLocations[n] = await cache.OfficeLocationCache.GetOrCreateNewResource(n, new UserOfficeLocation { Name = n });
            foreach (var n in usageSet)
                maps.UsageLocations[n] = await cache.UseageLocationCache.GetOrCreateNewResource(n, new UserUsageLocation { Name = n });
            foreach (var n in countrySet)
                maps.Countries[n] = await cache.CountryOrRegionCache.GetOrCreateNewResource(n, new CountryOrRegion { Name = n });
            foreach (var n in stateSet)
                maps.StatesOrProvinces[n] = await cache.StateOrProvinceCache.GetOrCreateNewResource(n, new StateOrProvince { Name = n });
            foreach (var n in companySet)
                maps.CompanyNames[n] = await cache.CompanyNameCache.GetOrCreateNewResource(n, new CompanyName { Name = n });

            _logger.LogInformation(
                $"User import - pre-warmed lookup caches: {deptSet.Count} departments, {titleSet.Count} titles, " +
                $"{officeSet.Count} offices, {usageSet.Count} usage locations, {countrySet.Count} countries, " +
                $"{stateSet.Count} states, {companySet.Count} companies");

            return maps;
        }

        private static void AddNormalized(string raw, HashSet<string> set)
        {
            var name = StringUtils.EnsureMaxLength(raw?.Trim(), 100);
            if (!string.IsNullOrEmpty(name))
                set.Add(name);
        }

        private DataTable BuildUpdateDataTable(
            List<GraphUser> graphUsers,
            LookupEntityMaps maps,
            Dictionary<string, Common.Entities.User> dbUsersByAadId,
            Dictionary<string, Common.Entities.User> dbUsersByUpn,
            Dictionary<string, GraphUser> graphUsersByAadId)
        {
            var dt = new DataTable();
            dt.Columns.Add("id", typeof(int));
            dt.Columns.Add("azure_ad_id", typeof(string));
            dt.Columns.Add("account_enabled", typeof(bool));
            dt.Columns.Add("mail", typeof(string));
            dt.Columns.Add("postalcode", typeof(string));
            dt.Columns.Add("department_id", typeof(int));
            dt.Columns.Add("job_title_id", typeof(int));
            dt.Columns.Add("office_location_id", typeof(int));
            dt.Columns.Add("usage_location_id", typeof(int));
            dt.Columns.Add("country_or_region_id", typeof(int));
            dt.Columns.Add("state_or_province_id", typeof(int));
            dt.Columns.Add("company_name_id", typeof(int));
            dt.Columns.Add("manager_id", typeof(int));
            dt.Columns.Add("last_updated", typeof(DateTime));

            var now = DateTime.Now;

            foreach (var graphUser in graphUsers)
            {
                var upn = graphUser.UserPrincipalName;
                if (string.IsNullOrEmpty(upn))
                    continue;

                // Find the DB user to get the PK
                if (!dbUsersByUpn.TryGetValue(upn, out var dbUser) || dbUser.ID == 0)
                    continue;

                var row = dt.NewRow();
                row["id"] = dbUser.ID;
                row["azure_ad_id"] = (object)graphUser.Id ?? DBNull.Value;
                row["account_enabled"] = graphUser.AccountEnabled.HasValue ? (object)graphUser.AccountEnabled.Value : DBNull.Value;
                row["mail"] = (object)graphUser.Mail ?? DBNull.Value;
                row["postalcode"] = (object)graphUser.PostalCode ?? DBNull.Value;
                row["department_id"] = ResolveLookupId(graphUser.Department, maps.Departments);
                row["job_title_id"] = ResolveLookupId(graphUser.JobTitle, maps.JobTitles);
                row["office_location_id"] = ResolveLookupId(graphUser.OfficeLocation, maps.OfficeLocations);
                row["usage_location_id"] = ResolveLookupId(graphUser.UsageLocation, maps.UsageLocations);
                row["country_or_region_id"] = ResolveLookupId(graphUser.Country, maps.Countries);
                row["state_or_province_id"] = ResolveLookupId(graphUser.State, maps.StatesOrProvinces);
                row["company_name_id"] = ResolveLookupId(graphUser.CompanyName, maps.CompanyNames);
                row["manager_id"] = ResolveManagerId(graphUser, dbUsersByAadId, dbUsersByUpn, graphUsersByAadId);
                row["last_updated"] = now;

                dt.Rows.Add(row);
            }

            return dt;
        }

        private static object ResolveLookupId<T>(string rawValue, Dictionary<string, T> map) where T : AbstractEFEntityWithName
        {
            var name = StringUtils.EnsureMaxLength(rawValue?.Trim(), 100);
            if (!string.IsNullOrEmpty(name) && map.TryGetValue(name, out var entity) && entity.ID > 0)
                return entity.ID;
            return DBNull.Value;
        }

        private static object ResolveManagerId(
            GraphUser graphUser,
            Dictionary<string, Common.Entities.User> dbUsersByAadId,
            Dictionary<string, Common.Entities.User> dbUsersByUpn,
            Dictionary<string, GraphUser> graphUsersByAadId)
        {
            if (graphUser.DefaultManagerInfo?.Id == null)
                return DBNull.Value;

            var mgrAadId = graphUser.DefaultManagerInfo.Id;

            if (dbUsersByAadId.TryGetValue(mgrAadId, out var manager) && manager.ID > 0)
                return manager.ID;

            if (graphUsersByAadId != null &&
                graphUsersByAadId.TryGetValue(mgrAadId, out var mgrGraph) &&
                !string.IsNullOrEmpty(mgrGraph.UserPrincipalName) &&
                dbUsersByUpn.TryGetValue(mgrGraph.UserPrincipalName, out var mgrByUpn) && mgrByUpn.ID > 0)
            {
                return mgrByUpn.ID;
            }

            return DBNull.Value;
        }

        private static async Task ExecuteBulkUpdate(string connectionString, DataTable dataTable)
        {
            if (dataTable.Rows.Count == 0)
                return;

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var cmd = new SqlCommand(CREATE_TEMP_TABLE_SQL, connection))
                {
                    cmd.CommandTimeout = 600;
                    await cmd.ExecuteNonQueryAsync();
                }

                using (var bulkCopy = new SqlBulkCopy(connection))
                {
                    bulkCopy.DestinationTableName = "#user_updates";
                    bulkCopy.BatchSize = 10000;
                    bulkCopy.BulkCopyTimeout = 600;

                    bulkCopy.ColumnMappings.Add("id", "id");
                    bulkCopy.ColumnMappings.Add("azure_ad_id", "azure_ad_id");
                    bulkCopy.ColumnMappings.Add("account_enabled", "account_enabled");
                    bulkCopy.ColumnMappings.Add("mail", "mail");
                    bulkCopy.ColumnMappings.Add("postalcode", "postalcode");
                    bulkCopy.ColumnMappings.Add("department_id", "department_id");
                    bulkCopy.ColumnMappings.Add("job_title_id", "job_title_id");
                    bulkCopy.ColumnMappings.Add("office_location_id", "office_location_id");
                    bulkCopy.ColumnMappings.Add("usage_location_id", "usage_location_id");
                    bulkCopy.ColumnMappings.Add("country_or_region_id", "country_or_region_id");
                    bulkCopy.ColumnMappings.Add("state_or_province_id", "state_or_province_id");
                    bulkCopy.ColumnMappings.Add("company_name_id", "company_name_id");
                    bulkCopy.ColumnMappings.Add("manager_id", "manager_id");
                    bulkCopy.ColumnMappings.Add("last_updated", "last_updated");

                    await bulkCopy.WriteToServerAsync(dataTable);
                }

                using (var cmd = new SqlCommand(UPDATE_FROM_TEMP_SQL, connection))
                {
                    cmd.CommandTimeout = 600;
                    await cmd.ExecuteNonQueryAsync();
                }

                using (var cmd = new SqlCommand("DROP TABLE #user_updates", connection))
                {
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        private const string CREATE_TEMP_TABLE_SQL = @"
            CREATE TABLE #user_updates (
                id              INT          NOT NULL,
                azure_ad_id     NVARCHAR(450) NULL,
                account_enabled BIT           NULL,
                mail            NVARCHAR(450) NULL,
                postalcode      NVARCHAR(50)  NULL,
                department_id   INT           NULL,
                job_title_id    INT           NULL,
                office_location_id INT        NULL,
                usage_location_id  INT        NULL,
                country_or_region_id INT      NULL,
                state_or_province_id INT      NULL,
                company_name_id INT           NULL,
                manager_id      INT           NULL,
                last_updated    DATETIME      NOT NULL
            )";

        private const string UPDATE_FROM_TEMP_SQL = @"
            UPDATE u
            SET u.azure_ad_id            = t.azure_ad_id,
                u.account_enabled        = t.account_enabled,
                u.mail                   = t.mail,
                u.postalcode             = t.postalcode,
                u.department_id          = t.department_id,
                u.job_title_id           = t.job_title_id,
                u.office_location_id     = t.office_location_id,
                u.usage_location_id      = t.usage_location_id,
                u.country_or_region_id   = t.country_or_region_id,
                u.state_or_province_id   = t.state_or_province_id,
                u.company_name_id        = t.company_name_id,
                u.manager_id             = t.manager_id,
                u.last_updated           = t.last_updated
            FROM dbo.users u
            INNER JOIN #user_updates t ON u.id = t.id";

        /// <summary>
        /// Holds entity-reference dictionaries for each lookup type.
        /// Entity IDs are valid after the context has been saved.
        /// </summary>
        internal class LookupEntityMaps
        {
            public Dictionary<string, UserDepartment> Departments = new Dictionary<string, UserDepartment>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, UserJobTitle> JobTitles = new Dictionary<string, UserJobTitle>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, UserOfficeLocation> OfficeLocations = new Dictionary<string, UserOfficeLocation>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, UserUsageLocation> UsageLocations = new Dictionary<string, UserUsageLocation>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, CountryOrRegion> Countries = new Dictionary<string, CountryOrRegion>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, StateOrProvince> StatesOrProvinces = new Dictionary<string, StateOrProvince>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, CompanyName> CompanyNames = new Dictionary<string, CompanyName>(StringComparer.OrdinalIgnoreCase);
        }

        #endregion
    }
}
