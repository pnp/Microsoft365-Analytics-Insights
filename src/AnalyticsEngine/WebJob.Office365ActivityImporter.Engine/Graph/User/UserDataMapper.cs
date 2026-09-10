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
    /// Handles mapping and updating of user data between Graph and database entities
    /// </summary>
    internal class UserDataMapper
    {
        private readonly AnalyticsLogger _logger;
        private readonly UserMetadataCache _userMetaCache;
        private readonly ManagerPrefetchCache _managerPrefetch;
        private Dictionary<string, GraphUser> _graphUsersByAadId;

        /// <summary>
        /// Constructor without a lookup store: manager resolution falls back to the original
        /// per-user database query. Kept so existing call sites and tests do not have to change.
        /// </summary>
        public UserDataMapper(AnalyticsLogger logger, UserMetadataCache userMetaCache)
        {
            // Deliberately not chained to the overload below: a constructor initialiser runs before
            // the body, so chaining would move the store's own argument checks ahead of these and
            // change which ParamName a caller sees.
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userMetaCache = userMetaCache ?? throw new ArgumentNullException(nameof(userMetaCache));
            _managerPrefetch = new ManagerPrefetchCache(null);
        }

        /// <param name="userLookupStore">
        /// Used by <see cref="PrefetchManagersForBatchAsync"/> to resolve a whole batch's managers in
        /// one query instead of one query per user (#371).
        /// </param>
        public UserDataMapper(AnalyticsLogger logger, UserMetadataCache userMetaCache, IUserLookupStore userLookupStore)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userMetaCache = userMetaCache ?? throw new ArgumentNullException(nameof(userMetaCache));
            if (userLookupStore == null) throw new ArgumentNullException(nameof(userLookupStore));
            _managerPrefetch = new ManagerPrefetchCache(userLookupStore);
        }

        /// <summary>
        /// The pre-built dictionary for O(1) graph user lookups by AAD ID.
        /// </summary>
        public Dictionary<string, GraphUser> GraphUsersByAadId => _graphUsersByAadId;

        /// <summary>
        /// Pre-builds a dictionary for O(1) graph user lookups by AAD ID.
        /// Call once before processing batches to avoid O(n) linear scans per user.
        /// </summary>
        public void SetGraphUserLookup(List<GraphUser> allGraphUsers)
        {
            _graphUsersByAadId = new Dictionary<string, GraphUser>(allGraphUsers.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var graphUser in allGraphUsers)
            {
                if (!string.IsNullOrEmpty(graphUser.Id))
                {
                    _graphUsersByAadId[graphUser.Id] = graphUser;
                }
            }
        }

        /// <summary>
        /// Loads every manager the given batch might have to look up by UPN, in a single query, so
        /// the resolution chain below does not have to go to the database once per user.
        /// </summary>
        /// <remarks>
        /// This is the fix for the N+1 called out in #371. The database-by-UPN branch of
        /// <see cref="UpdateUserManager"/> is reached whenever a manager cannot be resolved from the
        /// in-memory dictionaries - during insert enrichment, a manager inserted in a later batch
        /// than their report. One chunked query per batch replaces those round trips.
        ///
        /// Call once per batch, before processing it: the entities returned are tracked by the
        /// import's context and every batch ends by detaching them, so a cache kept across batches
        /// would hand out detached entities. Each call therefore replaces the previous contents.
        /// A no-op when no lookup store was supplied, which leaves the original per-user query in
        /// place.
        /// </remarks>
        public async Task PrefetchManagersForBatchAsync(IEnumerable<GraphUser> batch)
        {
            await _managerPrefetch.LoadForBatchAsync(batch, _graphUsersByAadId);
        }

        /// <summary>
        /// Update basic user properties from Graph user
        /// </summary>
        public Common.Entities.User UpdateDbUserFromGraphUser(Common.Entities.User dbUser, GraphUser graphUser)
        {
            return ApplyDirectFields(dbUser, UserMetadataMappingRules.BuildPlan(graphUser));
        }

        /// <summary>Copies the fields that live directly on the users row (no lookup table involved).</summary>
        private static Common.Entities.User ApplyDirectFields(Common.Entities.User dbUser, UserMetadataChangePlan plan)
        {
            dbUser.AccountEnabled = plan.AccountEnabled;
            dbUser.PostalCode = plan.PostalCode;
            dbUser.AzureAdId = plan.AzureAdId;
            dbUser.Mail = plan.Mail;

            return dbUser;
        }

        /// <summary>
        /// Update all user metadata including department, job title, location, manager, etc.
        /// </summary>
        public async Task UpdateUserMetadata(
            AnalyticsEntitiesContext db,
            GraphUser graphUser,
            List<GraphUser> allGraphUsers,
            Common.Entities.User dbUser,
            Dictionary<string, Common.Entities.User> dbUsersByAadId = null,
            List<Common.Entities.User> allDbUsers = null)
        {
            var plan = UserMetadataMappingRules.BuildPlan(graphUser);
            ApplyDirectFields(dbUser, plan);

            // Update department
            // Note: Both navigation property AND FK must be set explicitly.
            // When entities are loaded with AsNoTracking() and later attached, navigation properties
            // are null (not loaded via Include), so setting them to null is a no-op. The FK column
            // retains its original DB value. Explicitly setting the FK to null ensures EF detects the change.
            if (plan.DepartmentName != null)
            {
                dbUser.Department = await _userMetaCache.DepartmentCache.GetOrCreateNewResource(plan.DepartmentName,
                    new UserDepartment { Name = plan.DepartmentName });
            }
            else
            {
                dbUser.Department = null;
                dbUser.DepartmentId = null;
            }

            // Update job title
            if (plan.JobTitleName != null)
            {
                dbUser.JobTitle = await _userMetaCache.JobTitleCache.GetOrCreateNewResource(plan.JobTitleName,
                    new UserJobTitle { Name = plan.JobTitleName });
            }
            else
            {
                dbUser.JobTitle = null;
                dbUser.JobTitleId = null;
            }

            // Update office location
            if (plan.OfficeLocationName != null)
            {
                dbUser.OfficeLocation = await _userMetaCache.OfficeLocationCache.GetOrCreateNewResource(plan.OfficeLocationName,
                    new UserOfficeLocation { Name = plan.OfficeLocationName });
            }
            else
            {
                dbUser.OfficeLocation = null;
                dbUser.OfficeLocationId = null;
            }

            // Update usage location
            if (plan.UsageLocationName != null)
            {
                dbUser.UsageLocation = await _userMetaCache.UseageLocationCache.GetOrCreateNewResource(plan.UsageLocationName,
                    new UserUsageLocation { Name = plan.UsageLocationName });
            }
            else
            {
                dbUser.UsageLocation = null;
                dbUser.UsageLocationId = null;
            }

            // Update country
            if (plan.CountryName != null)
            {
                dbUser.UserCountry = await _userMetaCache.CountryOrRegionCache.GetOrCreateNewResource(plan.CountryName,
                    new CountryOrRegion { Name = plan.CountryName });
            }
            else
            {
                dbUser.UserCountry = null;
                dbUser.UserCountryId = null;
            }

            // Update state
            if (plan.StateOrProvinceName != null)
            {
                dbUser.StateOrProvince = await _userMetaCache.StateOrProvinceCache.GetOrCreateNewResource(plan.StateOrProvinceName,
                    new StateOrProvince { Name = plan.StateOrProvinceName });
            }
            else
            {
                dbUser.StateOrProvince = null;
                dbUser.StateOrProvinceId = null;
            }

            // Update company
            if (plan.CompanyName != null)
            {
                dbUser.CompanyName = await _userMetaCache.CompanyNameCache.GetOrCreateNewResource(plan.CompanyName,
                    new CompanyName { Name = plan.CompanyName });
            }
            else
            {
                dbUser.CompanyName = null;
                dbUser.CompanyNameId = null;
            }

            // Update manager
            await UpdateUserManager(db, plan.ManagerAadId, allGraphUsers, dbUser, dbUsersByAadId, allDbUsers);

            dbUser.LastUpdated = DateTime.Now;
        }

        /// <summary>
        /// Update user's manager relationship
        /// </summary>
        /// <param name="managerAadId">
        /// The manager's Entra object id from <see cref="UserMetadataMappingRules.BuildPlan"/>, or null
        /// when Graph reported no manager. Taking it as a parameter keeps the resolution chain readable
        /// and means the mapping rule is the single place that reads it off the Graph user.
        /// </param>
        private async Task UpdateUserManager(
            AnalyticsEntitiesContext db,
            string managerAadId,
            List<GraphUser> allGraphUsers,
            Common.Entities.User dbUser,
            Dictionary<string, Common.Entities.User> dbUsersByAadId = null,
            List<Common.Entities.User> allDbUsers = null)
        {
            if (managerAadId != null)
            {
                // Try getting manager from DB using dictionary lookup if available
                Common.Entities.User dbManager = null;
                if (dbUsersByAadId != null && dbUsersByAadId.TryGetValue(managerAadId, out dbManager))
                {
                    // Found manager using fast dictionary lookup
                    // CRITICAL: Ensure manager is tracked by EF before assignment
                    // If we assign a detached entity to a tracked entity's navigation property,
                    // EF will try to INSERT it, causing duplicate key errors
                    dbManager = await EnsureUserIsTrackedAsync(db, dbManager, dbUsersByAadId);
                }
                else if (allDbUsers != null)
                {
                    // Fallback to LINQ query if dictionary not provided (for backwards compatibility)
                    dbManager = allDbUsers.Where(u => !string.IsNullOrEmpty(u.AzureAdId) &&
                        new Guid(u.AzureAdId).Equals(new Guid(managerAadId))).FirstOrDefault();

                    if (dbManager != null)
                    {
                        dbManager = await EnsureUserIsTrackedAsync(db, dbManager, dbUsersByAadId);
                    }
                }

                if (dbManager == null)
                {
                    // Use pre-built dictionary for O(1) lookup instead of O(n) list scan
                    GraphUser graphManagerUser = null;
                    if (_graphUsersByAadId != null)
                    {
                        _graphUsersByAadId.TryGetValue(managerAadId, out graphManagerUser);
                    }
                    else
                    {
                        graphManagerUser = allGraphUsers.FirstOrDefault(u => u.Id == managerAadId);
                    }

                    if (graphManagerUser != null)
                    {
                        // Got user from Graph cache; get DB user by UPN
                        var managerUpn = graphManagerUser.UserPrincipalName;

                        if (!string.IsNullOrEmpty(managerUpn))
                        {
                            // CRITICAL FIX: First try to find the manager in the database by UPN
                            // The AAD ID lookup might have failed due to mismatched/null AAD IDs,
                            // but the user might still exist in the database.
                            //
                            // Served from the batch prefetch when one was loaded
                            // (PrefetchManagersForBatchAsync), which is what stops this being a
                            // query per user at 200k-user scale (#371). The prefetch runs on the
                            // same context, so the entity is tracked exactly as the query below
                            // would return it. The query is still the fallback: it covers managers
                            // created part-way through the batch, and the case where no lookup
                            // store was supplied at all.
                            //
                            // Drop LOWER() from the column predicate - the default code-first
                            // collation (Latin1_General_CI_AS) is case-insensitive, so leaving
                            // the column un-lowered keeps the predicate SARGable and lets the
                            // index on user_name be used.
                            if (!_managerPrefetch.TryGet(managerUpn, out dbManager))
                            {
                                dbManager = await db.users
                                    .Include(u => u.LicenseLookups)
                                    .FirstOrDefaultAsync(u => u.UserPrincipalName == managerUpn);
                            }

                            if (dbManager != null)
                            {
                                // Manager exists in DB - use it (already tracked from FirstOrDefaultAsync)
                                // Update dictionary for future lookups if AAD ID is available
                                if (!string.IsNullOrEmpty(dbManager.AzureAdId) && dbUsersByAadId != null)
                                {
                                    dbUsersByAadId[dbManager.AzureAdId] = dbManager;
                                }
                                dbUser.Manager = dbManager;
                            }
                            else
                            {
                                // Manager truly doesn't exist in DB - use cache to create
                                dbUser.Manager = await _userMetaCache.UserCache.GetOrCreateNewResource(managerUpn,
                                    new Common.Entities.User { UserPrincipalName = graphManagerUser.UserPrincipalName }, true);
                            }
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"Couldn't find manager with AAD ID {managerAadId} in Graph cache or DB");
                    }
                }
                else
                {
                    dbUser.Manager = dbManager;
                }
            }
            else
            {
                // No manager info from Graph - clear the manager relationship
                dbUser.Manager = null;
                dbUser.ManagerId = null;
            }
        }

        /// <summary>
        /// Ensures a user entity is tracked by EF. If the entity is detached, either retrieves
        /// an already-tracked version from the context or uses Find() to get a tracked version.
        /// This prevents "Cannot insert duplicate key" errors when assigning navigation properties.
        /// </summary>
        private async Task<Common.Entities.User> EnsureUserIsTrackedAsync(
            AnalyticsEntitiesContext db,
            Common.Entities.User user,
            Dictionary<string, Common.Entities.User> dbUsersByAadId)
        {
            if (user == null)
            {
                return user;
            }

            // If the entity has no ID, try to find it by UPN in the database
            // This can happen if the entity was created from a template but actually exists in DB.
            // No LOWER() on the column - CI collation handles case-insensitive matching and
            // keeps the predicate SARGable against the user_name index.
            if (user.ID == 0 && !string.IsNullOrEmpty(user.UserPrincipalName))
            {
                var upn = user.UserPrincipalName;
                // Served from the batch prefetch where possible, for the same reason as the manager
                // branch above: this ran once per user with an unsaved template entity (#371).
                if (!_managerPrefetch.TryGet(upn, out var existingUser))
                {
                    existingUser = await db.users
                        .Include(u => u.LicenseLookups)
                        .FirstOrDefaultAsync(u => u.UserPrincipalName == upn);
                }
                if (existingUser != null)
                {
                    // Found the user in DB - use the tracked version
                    if (!string.IsNullOrEmpty(existingUser.AzureAdId) && dbUsersByAadId != null)
                    {
                        dbUsersByAadId[existingUser.AzureAdId] = existingUser;
                    }
                    return existingUser;
                }
                // User doesn't exist in DB - return as-is (will be inserted)
                return user;
            }

            if (user.ID == 0)
            {
                // No ID and no UPN - can't resolve, return as-is
                return user;
            }

            var entry = db.Entry(user);

            // If already tracked (Added, Modified, Unchanged), return as-is
            if (entry.State != EntityState.Detached)
            {
                return user;
            }

            // Check if there's already a tracked entity with the same ID in the context
            var trackedUser = db.ChangeTracker.Entries<Common.Entities.User>()
                .FirstOrDefault(e => e.Entity.ID == user.ID && e.State != EntityState.Detached)?.Entity;

            if (trackedUser != null)
            {
                // Update dictionary with tracked entity for future lookups
                if (!string.IsNullOrEmpty(trackedUser.AzureAdId) && dbUsersByAadId != null)
                {
                    dbUsersByAadId[trackedUser.AzureAdId] = trackedUser;
                }
                return trackedUser;
            }

            // No tracked entity found - query the tracked entity from DB with the same licence
            // graph as the per-user licence path. Find() cannot Include navigation properties, and
            // an empty User.LicenseLookups list would stop ProcessUserLicenses deleting stored rows
            // before it re-adds the current Graph answer.
            var foundUser = await db.users
                .Include(u => u.LicenseLookups)
                .FirstOrDefaultAsync(u => u.ID == user.ID);

            if (foundUser != null)
            {
                // Update dictionary with tracked entity for future lookups
                if (!string.IsNullOrEmpty(foundUser.AzureAdId) && dbUsersByAadId != null)
                {
                    dbUsersByAadId[foundUser.AzureAdId] = foundUser;
                }
                return foundUser;
            }

            // Fallback: try to attach the original entity
            // This should rarely happen, but handles edge cases
            try
            {
                db.users.Attach(user);
                return user;
            }
            catch
            {
                // If attach fails, return original (will likely fail later, but with clearer error)
                return user;
            }
        }

        /// <summary>
        /// Get database users that match Graph users by UPN
        /// </summary>
        public List<Common.Entities.User> GetDbUsersFromGraphUsers(
            List<GraphUser> allGraphUsers,
            List<Common.Entities.User> allDbUsers)
        {
            // Create dictionary for O(1) lookup of DB users by UPN.
            // OrdinalIgnoreCase comparer handles case so we don't need .ToLower() on the keys -
            // saves ~187k string allocations per import on a 200k-user tenant.
            var dbUsersByUpn = allDbUsers
                .Where(u => !string.IsNullOrEmpty(u.UserPrincipalName))
                .ToDictionary(u => u.UserPrincipalName, u => u, StringComparer.OrdinalIgnoreCase);

            var users = new List<Common.Entities.User>();

            foreach (var graphUser in allGraphUsers)
            {
                var upn = graphUser.UserPrincipalName;
                if (!string.IsNullOrEmpty(upn) && dbUsersByUpn.TryGetValue(upn, out var dbUser))
                {
                    users.Add(dbUser);
                }
            }

            return users;
        }
    }
}
