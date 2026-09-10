using Common.Entities;
using DataUtils;
using System;
using System.Collections.Generic;
using System.Data.Entity;
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
        /// <param name="prepareBatch">
        /// Called once with each batch before it is processed, so the caller can resolve everything
        /// the batch will need in bulk - in production that is <c>UserDataMapper</c> prefetching the
        /// batch's managers in a single query instead of one query per user (#371). Optional; pass
        /// null to skip.
        /// </param>
        public async Task<int> ProcessExistingUsersInBatches(
            AnalyticsEntitiesContext db,
            List<GraphUser> allActiveGraphUsers,
            HashSet<string> userUpnsToProcess,
            Dictionary<string, Common.Entities.User> dbUsersByUpn,
            Dictionary<string, Common.Entities.User> dbUsersByAadId,
            Func<GraphUser, Common.Entities.User, Task> updateAction,
            Func<List<GraphUser>, Task> prepareBatch,
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

                if (prepareBatch != null)
                {
                    await prepareBatch(batch);
                }

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

                // If still can't find, load the row as a last resort. Find() cannot Include the
                // licence graph; on the per-user licence path that could hand
                // ProcessUserLicenses a tracked User with an empty LicenseLookups list.
                if (user.ID > 0)
                {
                    var found = db.users
                        .Include(u => u.LicenseLookups)
                        .FirstOrDefault(u => u.ID == user.ID);
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
        /// <param name="bulkUpdateWriter">
        /// Where each resolved batch is written. In production this is
        /// <see cref="SqlUserBulkUpdateWriter"/>, which is the original <c>SqlBulkCopy</c> code
        /// relocated unchanged; injecting it lets the batching and foreign-key resolution above be
        /// tested without a SQL Server (#371).
        /// </param>
        public async Task<int> BulkUpdateExistingUsers(
            AnalyticsEntitiesContext db,
            List<GraphUser> allActiveGraphUsers,
            HashSet<string> userUpnsToProcess,
            Dictionary<string, Common.Entities.User> dbUsersByUpn,
            Dictionary<string, Common.Entities.User> dbUsersByAadId,
            Dictionary<string, GraphUser> graphUsersByAadId,
            UserMetadataCache userMetaCache,
            IUserBulkUpdateWriter bulkUpdateWriter)
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

            int totalProcessed = 0;
            const int BULK_BATCH_SIZE = 50000;

            for (int i = 0; i < graphUsersToUpdate.Count; i += BULK_BATCH_SIZE)
            {
                var batchCount = Math.Min(BULK_BATCH_SIZE, graphUsersToUpdate.Count - i);
                var batch = graphUsersToUpdate.GetRange(i, batchCount);

                // Read per batch, exactly as the inlined table builder used to: on a tenant large
                // enough to need more than one batch the later batches carry a later last_updated.
                // Local time, not UTC - see the note on UserBulkUpdateRules.BuildUpdateTable.
                var now = DateTime.Now;

                using (var dataTable = UserBulkUpdateRules.BuildUpdateTable(
                    batch, lookupMaps, dbUsersByAadId, dbUsersByUpn, graphUsersByAadId, now))
                {
                    await bulkUpdateWriter.ExecuteAsync(dataTable);
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
            // Same normalisation the mapping rule applies, so the pre-warmed cache keys and the
            // foreign keys resolved later cannot drift apart (#371).
            var name = UserMetadataMappingRules.NormaliseLookupName(raw);
            if (name != null)
                set.Add(name);
        }

        #endregion
    }
}
