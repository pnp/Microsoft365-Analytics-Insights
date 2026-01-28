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
        private readonly AnalyticsLogger _telemetry;
        private readonly UserMetadataCache _userMetaCache;

        public UserDataMapper(AnalyticsLogger telemetry, UserMetadataCache userMetaCache)
        {
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
            _userMetaCache = userMetaCache ?? throw new ArgumentNullException(nameof(userMetaCache));
        }

        /// <summary>
        /// Update basic user properties from Graph user
        /// </summary>
        public Common.Entities.User UpdateDbUserFromGraphUser(Common.Entities.User dbUser, GraphUser graphUser)
        {
            dbUser.AccountEnabled = graphUser.AccountEnabled;
            dbUser.PostalCode = graphUser.PostalCode;
            dbUser.AzureAdId = graphUser.Id;
            dbUser.Mail = graphUser.Mail;

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
            UpdateDbUserFromGraphUser(dbUser, graphUser);

            // Update department
            var nameMaxLengthDepartment = StringUtils.EnsureMaxLength(graphUser.Department?.Trim(), 100);
            dbUser.Department = !string.IsNullOrEmpty(nameMaxLengthDepartment) ?
                await _userMetaCache.DepartmentCache.GetOrCreateNewResource(nameMaxLengthDepartment,
                    new UserDepartment { Name = nameMaxLengthDepartment }) : null;

            // Update job title
            var nameMaxLengthJobTitle = StringUtils.EnsureMaxLength(graphUser.JobTitle?.Trim(), 100);
            dbUser.JobTitle = !string.IsNullOrEmpty(nameMaxLengthJobTitle) ?
                await _userMetaCache.JobTitleCache.GetOrCreateNewResource(nameMaxLengthJobTitle,
                    new UserJobTitle { Name = nameMaxLengthJobTitle }) : null;

            // Update office location
            var nameMaxLengthOfficeLocation = StringUtils.EnsureMaxLength(graphUser.OfficeLocation?.Trim(), 100);
            dbUser.OfficeLocation = !string.IsNullOrEmpty(nameMaxLengthOfficeLocation) ?
                await _userMetaCache.OfficeLocationCache.GetOrCreateNewResource(nameMaxLengthOfficeLocation,
                    new UserOfficeLocation { Name = nameMaxLengthOfficeLocation }) : null;

            // Update usage location
            var nameMaxLengthUsageLocation = StringUtils.EnsureMaxLength(graphUser.UsageLocation?.Trim(), 100);
            dbUser.UsageLocation = !string.IsNullOrEmpty(nameMaxLengthUsageLocation) ?
                await _userMetaCache.UseageLocationCache.GetOrCreateNewResource(nameMaxLengthUsageLocation,
                    new UserUsageLocation { Name = nameMaxLengthUsageLocation }) : null;

            // Update country
            var nameMaxLengthCountry = StringUtils.EnsureMaxLength(graphUser.Country?.Trim(), 100);
            dbUser.UserCountry = !string.IsNullOrEmpty(nameMaxLengthCountry) ?
                await _userMetaCache.CountryOrRegionCache.GetOrCreateNewResource(nameMaxLengthCountry,
                    new CountryOrRegion { Name = nameMaxLengthCountry }) : null;

            // Update state
            var nameMaxLengthState = StringUtils.EnsureMaxLength(graphUser.State?.Trim(), 100);
            dbUser.StateOrProvince = !string.IsNullOrEmpty(nameMaxLengthState) ?
                await _userMetaCache.StateOrProvinceCache.GetOrCreateNewResource(nameMaxLengthState,
                    new StateOrProvince { Name = nameMaxLengthState }) : null;

            // Update company
            var nameMaxLengthCompany = StringUtils.EnsureMaxLength(graphUser.CompanyName?.Trim(), 100);
            dbUser.CompanyName = !string.IsNullOrEmpty(nameMaxLengthCompany) ?
                await _userMetaCache.CompanyNameCache.GetOrCreateNewResource(nameMaxLengthCompany,
                    new CompanyName { Name = nameMaxLengthCompany }) : null;

            // Update manager
            await UpdateUserManager(db, graphUser, allGraphUsers, dbUser, dbUsersByAadId, allDbUsers);

            dbUser.LastUpdated = DateTime.Now;
        }

        /// <summary>
        /// Update user's manager relationship
        /// </summary>
        private async Task UpdateUserManager(
            AnalyticsEntitiesContext db,
            GraphUser graphUser,
            List<GraphUser> allGraphUsers,
            Common.Entities.User dbUser,
            Dictionary<string, Common.Entities.User> dbUsersByAadId = null,
            List<Common.Entities.User> allDbUsers = null)
        {
            if (graphUser.DefaultManagerInfo?.Id != null)
            {
                // Try getting manager from DB using dictionary lookup if available
                Common.Entities.User dbManager = null;
                if (dbUsersByAadId != null && dbUsersByAadId.TryGetValue(graphUser.DefaultManagerInfo.Id, out dbManager))
                {
                    // Found manager using fast dictionary lookup
                    // CRITICAL: Ensure manager is tracked by EF before assignment
                    // If we assign a detached entity to a tracked entity's navigation property,
                    // EF will try to INSERT it, causing duplicate key errors
                    dbManager = EnsureUserIsTracked(db, dbManager, dbUsersByAadId);
                }
                else if (allDbUsers != null)
                {
                    // Fallback to LINQ query if dictionary not provided (for backwards compatibility)
                    dbManager = allDbUsers.Where(u => !string.IsNullOrEmpty(u.AzureAdId) &&
                        new Guid(u.AzureAdId).Equals(new Guid(graphUser.DefaultManagerInfo.Id))).FirstOrDefault();
                    
                    if (dbManager != null)
                    {
                        dbManager = EnsureUserIsTracked(db, dbManager, dbUsersByAadId);
                    }
                }

                if (dbManager == null)
                {
                    var graphManagerUser = allGraphUsers.FirstOrDefault(u => u.Id == graphUser.DefaultManagerInfo?.Id);

                    if (graphManagerUser != null)
                    {
                        // Got user from Graph cache; get DB user by UPN
                        var managerUpn = graphManagerUser.UserPrincipalName?.ToLower();

                        if (!string.IsNullOrEmpty(managerUpn))
                        {
                            // CRITICAL FIX: First try to find the manager in the database by UPN
                            // The AAD ID lookup might have failed due to mismatched/null AAD IDs,
                            // but the user might still exist in the database
                            dbManager = await db.users
                                .FirstOrDefaultAsync(u => u.UserPrincipalName.ToLower() == managerUpn);
                            
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
                        _telemetry.LogWarning($"Couldn't find manager with AAD ID {graphUser.DefaultManagerInfo?.Id} in Graph cache or DB");
                    }
                }
                else
                {
                    dbUser.Manager = dbManager;
                }
            }
        }

        /// <summary>
        /// Ensures a user entity is tracked by EF. If the entity is detached, either retrieves
        /// an already-tracked version from the context or uses Find() to get a tracked version.
        /// This prevents "Cannot insert duplicate key" errors when assigning navigation properties.
        /// </summary>
        private Common.Entities.User EnsureUserIsTracked(
            AnalyticsEntitiesContext db, 
            Common.Entities.User user,
            Dictionary<string, Common.Entities.User> dbUsersByAadId)
        {
            if (user == null)
            {
                return user;
            }

            // If the entity has no ID, try to find it by UPN in the database
            // This can happen if the entity was created from a template but actually exists in DB
            if (user.ID == 0 && !string.IsNullOrEmpty(user.UserPrincipalName))
            {
                var upnLower = user.UserPrincipalName.ToLower();
                var existingUser = db.users.FirstOrDefault(u => u.UserPrincipalName.ToLower() == upnLower);
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

            // No tracked entity found - use Find() which will return tracked entity from DB
            // This is more reliable than Attach() as it handles cases where the entity
            // might have been modified in the database since it was loaded
            var foundUser = db.users.Find(user.ID);
            
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
            // Create dictionary for O(1) lookup of DB users by UPN
            var dbUsersByUpn = allDbUsers
                .Where(u => !string.IsNullOrEmpty(u.UserPrincipalName))
                .ToDictionary(u => u.UserPrincipalName.ToLower(), u => u, StringComparer.OrdinalIgnoreCase);

            var users = new List<Common.Entities.User>();

            foreach (var graphUser in allGraphUsers)
            {
                var upn = graphUser.UserPrincipalName?.ToLower();
                if (!string.IsNullOrEmpty(upn) && dbUsersByUpn.TryGetValue(upn, out var dbUser))
                {
                    users.Add(dbUser);
                }
            }

            return users;
        }
    }
}
