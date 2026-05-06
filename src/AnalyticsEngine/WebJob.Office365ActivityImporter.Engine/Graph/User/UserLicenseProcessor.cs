using Common.Entities;
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
    /// Handles all license and SKU processing operations for users
    /// </summary>
    internal class UserLicenseProcessor
    {
        private readonly AnalyticsLogger _telemetry;
        private readonly OfficeLicenseNameResolver _officeLicenseNameResolver;
        private readonly IUserMetadataLoader _userLoader;
        private readonly UserMetadataCache _userMetaCache;
        private const int DEFAULT_BATCH_SIZE = 500;
        private const int SKU_BATCH_SIZE = 1000;

        public UserLicenseProcessor(
            AnalyticsLogger telemetry,
            IUserMetadataLoader userLoader,
            UserMetadataCache userMetaCache)
        {
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
            _userLoader = userLoader ?? throw new ArgumentNullException(nameof(userLoader));
            _userMetaCache = userMetaCache ?? throw new ArgumentNullException(nameof(userMetaCache));
            _officeLicenseNameResolver = new OfficeLicenseNameResolver();
        }

        /// <summary>
        /// Process SKUs for all users in batches
        /// </summary>
        public async Task ProcessSKUsForAllUsers(
            IGraphServiceSubscribedSkusCollectionPage skus,
            List<Common.Entities.User> graphFoundDbUsers,
            AnalyticsEntitiesContext db)
        {
            // Remove all existing license lookups for these users via direct SQL for performance.
            // EF RemoveRange generates individual DELETE statements per entity which is extremely
            // slow for large user counts (10+ hours for ~187K users). A single SQL DELETE is instant.
            _telemetry.LogInformation($"User import - removing old license lookups for {graphFoundDbUsers.Count.ToString("N0")} users");

            // Detach all tracked license lookups from EF before the SQL delete so the
            // change-tracker doesn't try to re-process rows that no longer exist.
            foreach (var entry in db.ChangeTracker.Entries<UserLicenseTypeLookup>().ToList())
            {
                entry.State = EntityState.Detached;
            }

            var userIds = graphFoundDbUsers.Where(u => u.IsSavedToDB).Select(u => u.ID).ToList();

            if (userIds.Count > 0)
            {
                const int SQL_BATCH_SIZE = 10000;
                for (int i = 0; i < userIds.Count; i += SQL_BATCH_SIZE)
                {
                    var batchIds = userIds.Skip(i).Take(SQL_BATCH_SIZE).ToList();
                    var idList = string.Join(",", batchIds);
                    await db.Database.ExecuteSqlCommandAsync(
                        $"DELETE FROM dbo.user_license_type_lookups WHERE user_id IN ({idList})");
                }
            }

            // Clear in-memory license collections so new lookups are added cleanly
            foreach (var user in graphFoundDbUsers)
            {
                if (user.LicenseLookups != null)
                    user.LicenseLookups.Clear();
            }

            // Track which (LicenseType-display-name, user_id) pairs have already been queued
            // in this import run so we never try to insert two rows that would violate the
            // IX_license_type_id_user_id unique index. Two SKU part numbers (e.g.
            // RIGHTSMANAGEMENT and RIGHTSMANAGEMENT_CE) can resolve to the same display name
            // and therefore the same LicenseType, so without this guard the second SKU's
            // SaveChanges fails with "Cannot insert duplicate key row in object
            // 'dbo.user_license_type_lookups'".
            var assignedLicenses = new HashSet<(string licenseName, int userId)>();

            foreach (var sku in skus)
            {
                // Load users with this SKU
                var allUsersWithSku = await _userLoader.LoadUsersBySku(sku.SkuId.Value);

                // Update all
                await AddSkuForUsers(graphFoundDbUsers, allUsersWithSku, sku, db, assignedLicenses);

                // Clear the SKU users list to free memory
                allUsersWithSku.Clear();

                // Periodically save and clear change tracker
                await db.SaveChangesAsync();
                foreach (var entry in db.ChangeTracker.Entries<UserLicenseTypeLookup>().ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }

        /// <summary>
        /// Add SKU licenses for specific users
        /// </summary>
        public async Task AddSkuForUsers(
            List<Common.Entities.User> graphFoundDbUsers,
            List<Microsoft.Graph.User> usersWithSku,
            SubscribedSku sku,
            AnalyticsEntitiesContext db,
            HashSet<(string licenseName, int userId)> assignedLicenses = null)
        {
            // Create dictionary for O(1) lookup of DB users by UPN
            var dbUsersByUpn = graphFoundDbUsers
                .Where(u => !string.IsNullOrEmpty(u.UserPrincipalName))
                .ToDictionary(u => u.UserPrincipalName.ToLowerInvariant(), u => u, StringComparer.OrdinalIgnoreCase);

            var relevantDbUsers = new List<Common.Entities.User>();
            foreach (var graphUser in usersWithSku)
            {
                if (!string.IsNullOrEmpty(graphUser.UserPrincipalName) &&
                    dbUsersByUpn.TryGetValue(graphUser.UserPrincipalName.ToLowerInvariant(), out var dbUser))
                {
                    relevantDbUsers.Add(dbUser);
                }
            }

            _telemetry.LogInformation($"User import - Found {relevantDbUsers.Count.ToString("N0")} users in SQL for SKU Part Number '{sku.SkuPartNumber}' from {usersWithSku.Count.ToString("N0")} Graph users.");

            // Get license type once for all users
            var licence = await GetLicenseType(sku.SkuPartNumber);

            int duplicatesSkipped = 0;

            // Process in batches
            for (int i = 0; i < relevantDbUsers.Count; i += SKU_BATCH_SIZE)
            {
                var batchCount = Math.Min(SKU_BATCH_SIZE, relevantDbUsers.Count - i);
                var batch = relevantDbUsers.GetRange(i, batchCount);
                var list = new List<UserLicenseTypeLookup>(batch.Count);

                foreach (var dbUser in batch)
                {
                    // Two different SKU part numbers can resolve to the same product
                    // display name (and therefore the same LicenseType). The
                    // user_license_type_lookups table has a UNIQUE index on
                    // (license_type_id, user_id), so we must skip duplicates here
                    // instead of letting SaveChanges throw.
                    if (assignedLicenses != null &&
                        !assignedLicenses.Add((licence.Name, dbUser.ID)))
                    {
                        duplicatesSkipped++;
                        continue;
                    }

                    // Use FK ID directly so the User entity does not need to be
                    // tracked by EF, avoiding the costly re-attach loop for large
                    // user counts.  The License navigation property is kept because
                    // the LicenseType may have just been created (ID == 0) and EF
                    // must resolve its ID at save time.
                    list.Add(new UserLicenseTypeLookup { License = licence, UserId = dbUser.ID });
                }

                db.UserLicenseTypeLookups.AddRange(list);

                if ((i + batch.Count) % 5000 == 0 || i + batch.Count >= relevantDbUsers.Count)
                {
                    _telemetry.LogInformation($"User {(i + batch.Count).ToString("N0")} / {relevantDbUsers.Count.ToString("N0")} processed for licenses.");
                }

                // Clear batch list to free memory
                list.Clear();
            }

            if (duplicatesSkipped > 0)
            {
                _telemetry.LogInformation($"User import - Skipped {duplicatesSkipped.ToString("N0")} duplicate license lookups for SKU '{sku.SkuPartNumber}' (display-name '{licence.Name}' already assigned via another SKU).");
            }

            // Clear dictionaries and lists to free memory
            dbUsersByUpn.Clear();
            relevantDbUsers.Clear();
        }

        /// <summary>
        /// Process user-specific licenses when tenant-level SKUs are not available
        /// </summary>
        public async Task ProcessUserLicenses(
            AnalyticsEntitiesContext db,
            GraphUser graphUser,
            Common.Entities.User dbUser)
        {
            // Get user service-plan from Graph
            var userServicePlans = await _userLoader.LoadUserLicenseDetails(graphUser.Id);

            if (userServicePlans != null)
            {
                // Batch load all license types first to reduce repeated awaits
                var skuPartNumbers = userServicePlans.Select(p => p.SkuPartNumber).Distinct().ToList();
                var licenseTypesDict = new Dictionary<string, LicenseType>();
                foreach (var skuPartNumber in skuPartNumbers)
                {
                    var licenseType = await GetLicenseType(skuPartNumber);
                    licenseTypesDict[skuPartNumber] = licenseType;
                }

                // Remove old lookups & re-add
                db.UserLicenseTypeLookups.RemoveRange(dbUser.LicenseLookups.Where(l => l.IsSavedToDB));

                // Dedupe by LicenseType display name: two SKU part numbers can resolve
                // to the same LicenseType, and the user_license_type_lookups table has
                // a UNIQUE index on (license_type_id, user_id).
                var addedLicenseNames = new HashSet<string>();
                foreach (var userPlan in userServicePlans)
                {
                    if (licenseTypesDict.TryGetValue(userPlan.SkuPartNumber, out var licence) &&
                        addedLicenseNames.Add(licence.Name))
                    {
                        dbUser.LicenseLookups.Add(new UserLicenseTypeLookup { License = licence, User = dbUser });
                    }
                }
            }
        }

        /// <summary>
        /// Get or create license type from SKU part number
        /// </summary>
        public async Task<LicenseType> GetLicenseType(string skuPartNumber)
        {
            var productName = _officeLicenseNameResolver.GetDisplayNameFor(skuPartNumber);
            if (string.IsNullOrEmpty(productName))
            {
                _telemetry.LogWarning($"User import - unexpected SKU part-number '{skuPartNumber}'. Couldn't find a corresponding display-name.");

                // Set display name as SKU ID
                productName = skuPartNumber;
            }

            var thisLicense = await _userMetaCache.LicenseTypeCache.GetOrCreateNewResource(productName,
                new LicenseType
                {
                    Name = productName,
                    SKUID = skuPartNumber
                });
            return thisLicense;
        }
    }
}
