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
            // Process license removals in batches to reduce memory pressure
            _telemetry.LogInformation($"User import - removing old license lookups for {graphFoundDbUsers.Count.ToString("N0")} users");

            for (int i = 0; i < graphFoundDbUsers.Count; i += DEFAULT_BATCH_SIZE)
            {
                var batch = graphFoundDbUsers.Skip(i).Take(DEFAULT_BATCH_SIZE).ToList();
                var licenseLookupsToRemove = batch.SelectMany(u => u.LicenseLookups.Where(l => l.IsSavedToDB)).ToList();

                if (licenseLookupsToRemove.Any())
                {
                    db.UserLicenseTypeLookups.RemoveRange(licenseLookupsToRemove);
                }
            }

            await db.SaveChangesAsync();

            foreach (var sku in skus)
            {
                // Load users with this SKU
                var allUsersWithSku = await _userLoader.LoadUsersBySku(sku.SkuId.Value);

                // Update all
                await AddSkuForUsers(graphFoundDbUsers, allUsersWithSku, sku, db);

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
            AnalyticsEntitiesContext db)
        {
            // Create dictionary for O(1) lookup of DB users by UPN
            var dbUsersByUpn = graphFoundDbUsers
                .Where(u => !string.IsNullOrEmpty(u.UserPrincipalName))
                .ToDictionary(u => u.UserPrincipalName.ToLower(), u => u, StringComparer.OrdinalIgnoreCase);

            var relevantDbUsers = new List<Common.Entities.User>();
            foreach (var graphUser in usersWithSku)
            {
                if (!string.IsNullOrEmpty(graphUser.UserPrincipalName) &&
                    dbUsersByUpn.TryGetValue(graphUser.UserPrincipalName.ToLower(), out var dbUser))
                {
                    relevantDbUsers.Add(dbUser);
                }
            }

            _telemetry.LogInformation($"User import - Found {relevantDbUsers.Count.ToString("N0")} users in SQL for SKU Part Number '{sku.SkuPartNumber}' from {usersWithSku.Count.ToString("N0")} Graph users.");

            // Get license type once for all users
            var licence = await GetLicenseType(sku.SkuPartNumber);

            // Process in batches
            for (int i = 0; i < relevantDbUsers.Count; i += SKU_BATCH_SIZE)
            {
                var batch = relevantDbUsers.Skip(i).Take(SKU_BATCH_SIZE).ToList();
                var list = new List<UserLicenseTypeLookup>(batch.Count);

                foreach (var dbUser in batch)
                {
                    list.Add(new UserLicenseTypeLookup { License = licence, User = dbUser });
                }

                db.UserLicenseTypeLookups.AddRange(list);

                if ((i + batch.Count) % 5000 == 0 || i + batch.Count >= relevantDbUsers.Count)
                {
                    _telemetry.LogInformation($"User {(i + batch.Count).ToString("N0")} / {relevantDbUsers.Count.ToString("N0")} processed for licenses.");
                }

                // Clear batch list to free memory
                list.Clear();
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
                foreach (var userPlan in userServicePlans)
                {
                    if (licenseTypesDict.TryGetValue(userPlan.SkuPartNumber, out var licence))
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
