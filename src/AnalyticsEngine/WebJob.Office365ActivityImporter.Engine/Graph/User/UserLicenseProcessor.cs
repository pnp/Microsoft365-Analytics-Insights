using Common.Entities;
using DataUtils;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Handles all license and SKU processing operations for users
    /// </summary>
    internal class UserLicenseProcessor
    {
        private readonly AnalyticsLogger _logger;
        private readonly IOfficeLicenseNameResolver _officeLicenseNameResolver;
        private readonly IUserMetadataLoader _userLoader;
        private readonly UserMetadataCache _userMetaCache;
        private readonly Func<AnalyticsEntitiesContext, IUserLicenseStore> _licenseStoreFactory;
        public UserLicenseProcessor(
            AnalyticsLogger logger,
            IUserMetadataLoader userLoader,
            UserMetadataCache userMetaCache)
            : this(logger, userLoader, userMetaCache, new OfficeLicenseNameResolver(), null)
        {
        }
        public UserLicenseProcessor(
            AnalyticsLogger logger,
            IUserMetadataLoader userLoader,
            UserMetadataCache userMetaCache,
            Func<AnalyticsEntitiesContext, IUserLicenseStore> licenseStoreFactory)
            : this(logger, userLoader, userMetaCache, new OfficeLicenseNameResolver(), licenseStoreFactory)
        {
        }
        internal UserLicenseProcessor(
            AnalyticsLogger logger,
            IUserMetadataLoader userLoader,
            UserMetadataCache userMetaCache,
            IOfficeLicenseNameResolver officeLicenseNameResolver,
            Func<AnalyticsEntitiesContext, IUserLicenseStore> licenseStoreFactory = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userLoader = userLoader ?? throw new ArgumentNullException(nameof(userLoader));
            _userMetaCache = userMetaCache ?? throw new ArgumentNullException(nameof(userMetaCache));
            _officeLicenseNameResolver = officeLicenseNameResolver ?? throw new ArgumentNullException(nameof(officeLicenseNameResolver));
            _licenseStoreFactory = licenseStoreFactory ?? (db => new SqlUserLicenseStore(db, logger));
        }
        /// <summary>
        /// Reconciles <c>dbo.user_license_type_lookups</c> for the supplied users against the licence
        /// assignments the tenant's SKUs report in Graph.
        /// </summary>
        /// <remarks>
        /// This step used to DELETE every licence lookup for the whole user population and then refill
        /// it SKU by SKU. On a large tenant the refill took several minutes, during which every report
        /// joining the table saw a tenant missing most or all of its licences - "no Copilot licences
        /// found" on a tenant that plainly held seats. See issue #392.
        ///
        /// Instead we compute the difference between what is stored and what Graph reports, and write
        /// only that difference - additions first, so a user swapping one SKU for another is never
        /// momentarily unlicensed. Readers therefore only ever see a complete licence set, and in the
        /// steady state (where almost nothing changes between cycles) the write is close to a no-op
        /// instead of a full rebuild of hundreds of thousands of rows.
        ///
        /// Cost of that trade: both the stored and the wanted state are held in memory as sets of
        /// two ints while they are compared. At a 200k-user tenant holding a few licences each that
        /// is tens of MB - far cheaper than the hundreds of thousands of round-trip INSERTs per cycle
        /// it replaces, and the importer runs 64-bit (AnyCPU, Prefer32Bit=false).
        /// </remarks>
        public async Task ProcessSKUsForAllUsers(
            List<SubscribedSku> skus,
            List<Common.Entities.User> graphFoundDbUsers,
            AnalyticsEntitiesContext db)
        {
            if (skus == null) throw new ArgumentNullException(nameof(skus));
            if (graphFoundDbUsers == null) throw new ArgumentNullException(nameof(graphFoundDbUsers));
            if (db == null) throw new ArgumentNullException(nameof(db));
            // The users whose licence rows this refresh owns. Anything outside this set is left
            // strictly alone, so the refresh can never delete a licence it was not asked to manage.
            var scopeUserIds = new HashSet<int>();
            foreach (var user in graphFoundDbUsers)
            {
                if (user.IsSavedToDB)
                {
                    scopeUserIds.Add(user.ID);
                }
            }
            if (scopeUserIds.Count == 0)
            {
                _logger.LogInformation("User import - no saved users to refresh licences for; skipping licence refresh.");
                return;
            }
            // An empty SKU list is not a credible "this tenant holds no licences at all" - every
            // M365 tenant has at least one subscribed SKU - so it is almost always a Graph glitch or
            // a permissions change. Reconciling against it would delete every licence in the tenant,
            // which is the outage this whole class exists to prevent. Leave the table alone and say
            // so loudly. A user genuinely losing their last licence still reconciles correctly,
            // because the SKU remains in the tenant and simply stops listing that user. Issue #392.
            if (skus.Count == 0)
            {
                _logger.LogWarning("User import - Graph reported ZERO tenant SKUs. Existing licence data has been left untouched rather than deleted, because an empty SKU list is far more likely to be a transient Graph or permissions problem ('Organization.Read.All') than a tenant that genuinely holds no licences.");
                return;
            }
            // Build the UPN dictionary ONCE for the whole tenant and reuse for every SKU.
            // Previous code rebuilt this 200k-entry dictionary on every SKU iteration - 30 SKUs
            // * 200k users = 6M unnecessary string allocations. The OrdinalIgnoreCase comparer
            // makes .ToLower() on keys redundant.
            var dbUsersByUpn = new Dictionary<string, Common.Entities.User>(
                graphFoundDbUsers.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var u in graphFoundDbUsers)
            {
                if (string.IsNullOrEmpty(u.UserPrincipalName))
                {
                    continue;
                }
                // dbo.users has a UNIQUE index on user_name (IX_users), so a UPN collision should be
                // impossible - but "last one wins" would leave the winner up to row order, which has
                // no ORDER BY. If the constraint were ever relaxed that would make the reconciliation
                // add and delete the same licence on alternating cycles. Pick deterministically
                // instead: a saved row beats an unsaved one, then the lowest primary key wins.
                if (dbUsersByUpn.TryGetValue(u.UserPrincipalName, out var alreadyMapped) &&
                    PreferExistingMapping(alreadyMapped, u))
                {
                    continue;
                }
                dbUsersByUpn[u.UserPrincipalName] = u;
            }
            // Resolve every SKU to its LicenseType and persist any newly created ones FIRST: the
            // reconciliation below compares primary keys, so each LicenseType must already have one.
            var skuLicenseTypes = new List<KeyValuePair<SubscribedSku, LicenseType>>(skus.Count);
            foreach (var sku in skus)
            {
                skuLicenseTypes.Add(new KeyValuePair<SubscribedSku, LicenseType>(sku, await GetLicenseType(sku.SkuPartNumber)));
            }
            await db.SaveChangesAsync();
            var store = _licenseStoreFactory(db);
            var refresh = await store.StartRefresh(DateTime.UtcNow);
            var seatCountSnapshots = await SaveSubscribedSkuCapacityAsync(db, skuLicenseTypes, refresh.CompletedUtc);
            await store.CarryAssignmentsAcrossRenamedLicenceTypes(skuLicenseTypes.Select(pair => pair.Value.ID).ToList(), refresh);
            // Build the state Graph says the table should be in. Using a set also gives us the
            // de-duplication the UNIQUE index on (license_type_id, user_id) demands: two SKU part
            // numbers (e.g. RIGHTSMANAGEMENT and RIGHTSMANAGEMENT_CE) can resolve to the same
            // display name, and therefore to the same LicenseType.
            var desired = new HashSet<UserLicenseAssignment>();
            // Licence types whose SKU gave an answer we do not believe. Their existing rows are left
            // alone rather than deleted - see the contradiction check below.
            var untrustedLicenceTypeIds = new HashSet<int>();
            foreach (var pair in skuLicenseTypes)
            {
                // Load users with this SKU
                var allUsersWithSku = await _userLoader.LoadUsersBySku(pair.Key.SkuId.Value);
                // Graph contradicting itself: the tenant's own SKU record says seats are consumed,
                // but the query for who holds that SKU came back empty. Believing the empty answer
                // would delete every assignment for this licence type - issue #392 all over again,
                // just scoped to one SKU, which is indistinguishable from the original outage on a
                // tenant whose Copilot seats all sit on a single SKU. Keep what we have and say so.
                // (Only the strict contradiction is policed: a merely smaller-than-expected count is
                // normal, since ConsumedUnits and the user query settle at different times.)
                if (allUsersWithSku.Count == 0 && pair.Key.ConsumedUnits.GetValueOrDefault() > 0)
                {
                    untrustedLicenceTypeIds.Add(pair.Value.ID);
                    _logger.LogError($"User import - Graph reports {pair.Key.ConsumedUnits.GetValueOrDefault().ToString("N0")} consumed seat(s) for SKU '{pair.Key.SkuPartNumber}' but returned NO users holding it. Refusing to delete the existing '{pair.Value.Name}' assignments on the strength of that, because the two answers cannot both be right. They will reconcile on a later cycle once Graph is consistent.");
                }
                AddSkuAssignments(dbUsersByUpn, allUsersWithSku, pair.Key, pair.Value, desired);
                // Clear the SKU users list to free memory
                allUsersWithSku.Clear();
            }
            var current = await store.LoadAssignmentsFor(scopeUserIds);
            var delta = UserLicenseAssignmentDelta.Between(current, desired);
            var removals = delta.ToRemove;
            if (untrustedLicenceTypeIds.Count > 0)
            {
                // Two SKU part numbers can share a licence type, so suppressing by licence type can
                // hold back a removal that a sibling SKU legitimately justified. That errs towards
                // keeping data, which is the direction this whole change is about.
                removals = delta.ToRemove.Where(r => !untrustedLicenceTypeIds.Contains(r.LicenseTypeId)).ToList();
                var suppressed = delta.ToRemove.Count - removals.Count;
                if (suppressed > 0)
                {
                    _logger.LogWarning($"User import - held back {suppressed.ToString("N0")} licence removal(s) belonging to {untrustedLicenceTypeIds.Count.ToString("N0")} SKU(s) that reported no holders while claiming consumed seats.");
                }
            }
            _logger.LogInformation(
                $"User import - licence refresh across {skus.Count.ToString("N0")} SKU(s) for {scopeUserIds.Count.ToString("N0")} user(s): " +
                $"{delta.UnchangedCount.ToString("N0")} assignment(s) already correct, {delta.ToAdd.Count.ToString("N0")} to add, {removals.Count.ToString("N0")} to remove.");
            var keptCurrentAssignments = current.Where(existing => !removals.Contains(existing)).ToList();
            var seeded = await store.SeedCurrentAssignments(keptCurrentAssignments, refresh);
            var added = 0;
            var removed = 0;
            if (delta.ToAdd.Count > 0 || removals.Count > 0)
            {
                // Additions before removals. A user moving from one SKU to another then briefly holds
                // both licences rather than neither - a momentary superset is safe to report on, a
                // momentary gap is exactly the bug this replaced.
                added = await store.AddAssignments(delta.ToAdd, refresh);
                removed = await store.RemoveAssignments(removals, refresh);
            }
            var runId = await store.CompleteRefresh(refresh, seatCountSnapshots);
            _logger.LogInformation(
                $"User import - licence refresh complete: {added.ToString("N0")} assignment(s) added, {removed.ToString("N0")} removed" +
                $", {seeded.ToString("N0")} existing assignment(s) seeded into history" +
                (runId.HasValue ? $", history refresh run {runId.Value.ToString("N0")} recorded." : "."));
        }
        private async Task<List<LicenseSeatCountSnapshot>> SaveSubscribedSkuCapacityAsync(
            AnalyticsEntitiesContext db,
            List<KeyValuePair<SubscribedSku, LicenseType>> skuLicenseTypes,
            DateTime refreshedUtc)
        {
            var capacityByLicenceType = skuLicenseTypes
                .GroupBy(pair => pair.Value.ID)
                .Select(g => new LicenseSeatCountSnapshot
                {
                    LicenseTypeId = g.Key,
                    ConsumedUnits = g.Any(pair => pair.Key.ConsumedUnits != null) ? g.Sum(pair => pair.Key.ConsumedUnits) : (int?)null,
                    PrepaidEnabledUnits = g.Any(pair => pair.Key.PrepaidUnits?.Enabled != null) ? g.Sum(pair => pair.Key.PrepaidUnits?.Enabled) : (int?)null,
                    PrepaidWarningUnits = g.Any(pair => pair.Key.PrepaidUnits?.Warning != null) ? g.Sum(pair => pair.Key.PrepaidUnits?.Warning) : (int?)null,
                    PrepaidSuspendedUnits = g.Any(pair => pair.Key.PrepaidUnits?.Suspended != null) ? g.Sum(pair => pair.Key.PrepaidUnits?.Suspended) : (int?)null,
                })
                .ToList();
            var columnsExist = await db.Database.SqlQuery<int>(
                "SELECT CASE WHEN COL_LENGTH(N'dbo.license_types', N'prepaid_enabled_units') IS NULL " +
                "OR COL_LENGTH(N'dbo.license_types', N'subscribed_sku_refreshed_utc') IS NULL THEN 0 ELSE 1 END").SingleAsync();
            if (columnsExist == 0)
            {
                _logger.LogWarning("User import - subscribed SKU capacity columns are not present yet; purchased/unassigned Copilot seats will remain unknown until the database migration has run.");
                return capacityByLicenceType;
            }
            foreach (var capacity in capacityByLicenceType)
            {
                await db.Database.ExecuteSqlCommandAsync(
                    "UPDATE dbo.license_types " +
                    "SET prepaid_enabled_units = @enabled, prepaid_warning_units = @warning, prepaid_suspended_units = @suspended, subscribed_sku_refreshed_utc = @refreshedUtc " +
                    "WHERE id = @id",
                    new SqlParameter("@enabled", (object)capacity.PrepaidEnabledUnits ?? DBNull.Value),
                    new SqlParameter("@warning", (object)capacity.PrepaidWarningUnits ?? DBNull.Value),
                    new SqlParameter("@suspended", (object)capacity.PrepaidSuspendedUnits ?? DBNull.Value),
                    new SqlParameter("@refreshedUtc", refreshedUtc),
                    new SqlParameter("@id", capacity.LicenseTypeId));
            }
            await ZeroCapacityLeftBehindByRenamesAsync(db, skuLicenseTypes, capacityByLicenceType.Select(c => c.LicenseTypeId).ToList(), refreshedUtc);
            return capacityByLicenceType;
        }
        /// <summary>
        /// Zeroes the capacity of licence types that a current SKU has moved away from.
        /// </summary>
        /// <remarks>
        /// Licence types are filed by display name, so when a product's name changes - or a SKU gains a
        /// name for the first time, as every SKU a licensing-CSV refresh adds does - the next import files
        /// it under a new licence type, and the old one keeps the capacity it was last given. Copilot
        /// Adoption sums purchased seats across every Copilot seat type, so that stale figure would count
        /// the same seats twice: a tenant upgrading with Microsoft 365 E7, previously filed under its part
        /// number, would see its E7 seats purchased twice and all of the old copy reported as unassigned.
        /// Graph lists no subscription for the old licence type any more, so zero is its true value, not
        /// unknown - and unknown would blank the purchased-seat total.
        /// </remarks>
        private async Task ZeroCapacityLeftBehindByRenamesAsync(
            AnalyticsEntitiesContext db,
            List<KeyValuePair<SubscribedSku, LicenseType>> skuLicenseTypes,
            List<int> currentLicenceTypeIds,
            DateTime refreshedUtc)
        {
            var currentSkuPartNumbers = skuLicenseTypes
                .Select(pair => pair.Key.SkuPartNumber)
                .Where(sku => !string.IsNullOrWhiteSpace(sku))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (currentSkuPartNumbers.Count == 0 || currentLicenceTypeIds.Count == 0)
            {
                return;
            }
            var parameters = new List<SqlParameter> { new SqlParameter("@refreshedUtc", refreshedUtc) };
            var skuNames = new List<string>();
            for (var i = 0; i < currentSkuPartNumbers.Count; i++)
            {
                skuNames.Add("@sku" + i);
                parameters.Add(new SqlParameter("@sku" + i, currentSkuPartNumbers[i]));
            }
            var idNames = new List<string>();
            for (var i = 0; i < currentLicenceTypeIds.Count; i++)
            {
                idNames.Add("@id" + i);
                parameters.Add(new SqlParameter("@id" + i, currentLicenceTypeIds[i]));
            }
            // Only rows not already zero, so the log line means something and a steady state writes nothing.
            var zeroed = await db.Database.ExecuteSqlCommandAsync(
                "UPDATE dbo.license_types " +
                "SET prepaid_enabled_units = 0, prepaid_warning_units = 0, prepaid_suspended_units = 0, subscribed_sku_refreshed_utc = @refreshedUtc " +
                $"WHERE sku_id IN ({string.Join(", ", skuNames)}) AND id NOT IN ({string.Join(", ", idNames)}) " +
                "AND (prepaid_enabled_units IS NULL OR prepaid_enabled_units <> 0 " +
                "OR prepaid_warning_units IS NULL OR prepaid_warning_units <> 0 " +
                "OR prepaid_suspended_units IS NULL OR prepaid_suspended_units <> 0)",
                parameters.ToArray());
            if (zeroed > 0)
            {
                _logger.LogInformation($"User import - set purchased capacity to zero on {zeroed.ToString("N0")} licence type(s) whose SKU is now filed under a renamed licence type, so its seats are not counted twice.");
            }
        }
        public static async Task MarkSubscribedSkuCapacityUnavailableAsync(AnalyticsEntitiesContext db, AnalyticsLogger logger)
        {
            var columnsExist = await db.Database.SqlQuery<int>(
                "SELECT CASE WHEN COL_LENGTH(N'dbo.license_types', N'subscribed_sku_refreshed_utc') IS NULL THEN 0 ELSE 1 END").SingleAsync();
            if (columnsExist == 0) return;
            var cleared = await db.Database.ExecuteSqlCommandAsync(
                "UPDATE dbo.license_types SET prepaid_enabled_units = NULL, prepaid_warning_units = NULL, prepaid_suspended_units = NULL, subscribed_sku_refreshed_utc = NULL");
            logger?.LogWarning($"User import - tenant subscribedSkus were unavailable, so cleared purchased-seat capacity for {cleared.ToString("N0")} licence type(s). Copilot Adoption will show purchased/unassigned seats as unknown, not zero.");
        }
        /// <summary>
        /// Adds the assignments implied by one SKU to <paramref name="desired"/>. The
        /// <paramref name="dbUsersByUpn"/> dictionary is built once per import in
        /// <see cref="ProcessSKUsForAllUsers"/> and reused across all SKUs - rebuilding it per SKU at
        /// 200k users x ~30 SKUs costs millions of unnecessary string allocations.
        /// </summary>
        /// <returns>How many assignments this SKU contributed that were not already in the set.</returns>
        public int AddSkuAssignments(
            Dictionary<string, Common.Entities.User> dbUsersByUpn,
            List<Microsoft.Graph.Models.User> usersWithSku,
            SubscribedSku sku,
            LicenseType licence,
            HashSet<UserLicenseAssignment> desired)
        {
            if (dbUsersByUpn == null) throw new ArgumentNullException(nameof(dbUsersByUpn));
            if (usersWithSku == null) throw new ArgumentNullException(nameof(usersWithSku));
            if (licence == null) throw new ArgumentNullException(nameof(licence));
            if (desired == null) throw new ArgumentNullException(nameof(desired));
            if (licence.ID < 1)
            {
                // Without this we would silently stage assignments pointing at licence type 0.
                // Callers must persist newly created LicenseTypes before reconciling.
                throw new InvalidOperationException(
                    $"Licence type '{licence.Name}' has not been saved to the database yet, so it has no ID to assign users to.");
            }
            var matchedDbUsers = 0;
            var newAssignments = 0;
            var duplicatesSkipped = 0;
            foreach (var graphUser in usersWithSku)
            {
                // dbUsersByUpn is OrdinalIgnoreCase so we can look up with the original UPN
                // (no .ToLower() allocation per Graph user).
                if (string.IsNullOrEmpty(graphUser.UserPrincipalName) ||
                    !dbUsersByUpn.TryGetValue(graphUser.UserPrincipalName, out var dbUser))
                {
                    continue;
                }
                matchedDbUsers++;
                // A user that failed to insert has no primary key to point a lookup row at.
                if (!dbUser.IsSavedToDB)
                {
                    continue;
                }
                if (desired.Add(new UserLicenseAssignment(dbUser.ID, licence.ID)))
                {
                    newAssignments++;
                }
                else
                {
                    duplicatesSkipped++;
                }
            }
            _logger.LogInformation($"User import - Found {matchedDbUsers.ToString("N0")} users in SQL for SKU Part Number '{sku?.SkuPartNumber}' from {usersWithSku.Count.ToString("N0")} Graph users.");
            if (duplicatesSkipped > 0)
            {
                _logger.LogInformation($"User import - Skipped {duplicatesSkipped.ToString("N0")} duplicate license lookups for SKU '{sku?.SkuPartNumber}' (display-name '{licence.Name}' already assigned via another SKU).");
            }
            return newAssignments;
        }
        /// <summary>
        /// Deterministic tie-break when two database rows share a user-principal-name: keep a row
        /// that is actually saved over one that is not, then keep the lower primary key.
        /// </summary>
        private static bool PreferExistingMapping(Common.Entities.User existing, Common.Entities.User candidate)
        {
            if (existing.IsSavedToDB != candidate.IsSavedToDB)
            {
                return existing.IsSavedToDB;
            }
            return existing.ID <= candidate.ID;
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
                await db.SaveChangesAsync();
                // Dedupe by LicenseType display name: two SKU part numbers can resolve
                // to the same LicenseType, and the user_license_type_lookups table has
                // a UNIQUE index on (license_type_id, user_id).
                var desired = new HashSet<UserLicenseAssignment>();
                var addedLicenseNames = new HashSet<string>();
                foreach (var userPlan in userServicePlans)
                {
                    if (licenseTypesDict.TryGetValue(userPlan.SkuPartNumber, out var licence) &&
                        addedLicenseNames.Add(licence.Name))
                    {
                        desired.Add(new UserLicenseAssignment(dbUser.ID, licence.ID));
                    }
                }
                var store = _licenseStoreFactory(db);
                var refresh = await store.StartRefresh(DateTime.UtcNow);
                await store.CarryAssignmentsAcrossRenamedLicenceTypes(licenseTypesDict.Values.Select(l => l.ID).ToList(), refresh);
                var current = await store.LoadAssignmentsFor(new[] { dbUser.ID });
                var delta = UserLicenseAssignmentDelta.Between(current, desired);
                var keptCurrentAssignments = current.Where(existing => !delta.ToRemove.Contains(existing)).ToList();
                await store.SeedCurrentAssignments(keptCurrentAssignments, refresh);
                await store.AddAssignments(delta.ToAdd, refresh);
                await store.RemoveAssignments(delta.ToRemove, refresh);
                await store.CompleteRefresh(refresh, new List<LicenseSeatCountSnapshot>());
            }
        }
        /// <summary>
        /// Width of <c>license_types.name</c>: <see cref="AbstractEFEntityWithName.Name"/> is <c>[MaxLength(100)]</c>.
        /// </summary>
        internal const int LicenceTypeNameMaxLength = 100;
        /// <summary>
        /// Get or create license type from SKU part number
        /// </summary>
        public async Task<LicenseType> GetLicenseType(string skuPartNumber)
        {
            var productName = _officeLicenseNameResolver.GetDisplayNameFor(skuPartNumber);
            if (string.IsNullOrEmpty(productName))
            {
                _logger.LogWarning($"User import - unexpected SKU part-number '{skuPartNumber}'. Couldn't find a corresponding display-name.");
                // Set display name as SKU ID
                productName = skuPartNumber;
            }
            // Some of Microsoft's product names are longer than the column (the GCC High and DoD Power
            // Pages capacity packs run to 105 characters). One of those would fail validation on the
            // SaveChanges that follows and abort the licence refresh for the whole tenant, every cycle.
            // Truncate before the name is used as the cache key as well: the cache finds existing rows
            // by name, so a key longer than the stored name would never find its row again.
            productName = StringUtils.EnsureMaxLength(productName, LicenceTypeNameMaxLength);
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
