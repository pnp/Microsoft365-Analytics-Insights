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
        private readonly AnalyticsLogger _telemetry;
        private const int DEFAULT_BATCH_SIZE = 500;

        public UserBatchProcessor(AnalyticsLogger telemetry)
        {
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
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
            _telemetry.LogInformation($"User import - updating {userUpnsToProcess.Count.ToString("N0")} existing users in batches...");

            int processedCount = 0;
            var batchedGraphUsers = allActiveGraphUsers
                .Where(u => !string.IsNullOrEmpty(u.UserPrincipalName) && userUpnsToProcess.Contains(u.UserPrincipalName.ToLower()))
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
                    var upn = existingGraphUser.UserPrincipalName?.ToLower();
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
                _telemetry.LogInformation($"User import - processed batch {processedCount.ToString("N0")}/{batchedGraphUsers.Count.ToString("N0")} existing users");

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
    }
}
