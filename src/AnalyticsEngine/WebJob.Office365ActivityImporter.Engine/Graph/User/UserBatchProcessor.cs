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
                var batch = batchedGraphUsers.Skip(i).Take(batchSize).ToList();

                foreach (var existingGraphUser in batch)
                {
                    var upn = existingGraphUser.UserPrincipalName?.ToLower();
                    if (!string.IsNullOrEmpty(upn) && dbUsersByUpn.TryGetValue(upn, out var dbUser))
                    {
                        // Attach the user to context for tracking
                        var trackedUser = db.users.Attach(dbUser);
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
