using Azure.Core;
using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Ensures user table info is upto-date from Graph
    /// </summary>
    public class UserMetadataUpdater : AbstractApiLoader
    {
        #region Constructor & Privates

        private readonly ManualGraphCallClient _httpClient;
        private readonly GraphServiceClient _graphServiceClient;
        private readonly OfficeLicenseNameResolver _officeLicenseNameResolver;
        private UserMetadataCache _userMetaCache;
        private readonly GraphUserLoader _userLoader;

        public UserMetadataUpdater(AnalyticsLogger telemetry, AppConfig settings, TokenCredential creds, ManualGraphCallClient manualGraphCallClient)
            : base(telemetry, settings)
        {
            this._graphServiceClient = new GraphServiceClient(creds);
            this._officeLicenseNameResolver = new OfficeLicenseNameResolver();

            // Override default
            _graphServiceClient.HttpProvider.OverallTimeout = TimeSpan.FromHours(1);
            _httpClient = manualGraphCallClient;


            IDeltaValueProvider deltaProvider = null;
            if (settings.ConnectionStrings.RedisConnectionString != null)
            {
                deltaProvider = new RedisProcessDeltaValueProvider(settings, telemetry);
                telemetry.LogInformation($"User import - using Redis for delta token cache.");
            }
            else
            {
                telemetry.LogInformation($"User import - no redis found configured, using in-process cache for delta token.");
                deltaProvider = new InProcessDeltaValueProvider(telemetry);
            }
            _userLoader = new GraphUserLoader(_httpClient, deltaProvider, _telemetry);
        }

        public GraphUserLoader GraphUserLoader => _userLoader;

        #endregion

        /// <summary>
        /// Main method
        /// </summary>
        public async Task InsertAndUpdateDatabaseUsersFromGraph()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Configuration.AutoDetectChangesEnabled = false;

                _userMetaCache = new UserMetadataCache(db);

                _telemetry.LogInformation($"{DateTime.Now.ToShortTimeString()} User import - start");


                // If we have no active users, assume new install so clear delta key
                var activeUserCount = await db.users.Where(u => u.AccountEnabled.HasValue && u.AccountEnabled.Value == true).CountAsync();
                if (activeUserCount == 0)
                {
                    await _userLoader.DeltaValueProvider.ClearDeltaToken();
                }

                // Load from Graph & update delta code once done
                var allActiveGraphUsers = await _userLoader.LoadAllActiveUsers();

                // Get SKUs from tenant
                IGraphServiceSubscribedSkusCollectionPage skus = null;
                try
                {
                    skus = await _graphServiceClient.SubscribedSkus.Request().GetAsync();
                }
                catch (ServiceException ex)
                {
                    if (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        _telemetry.LogError($"User import - couldn't load SKUs for org - {ex.Message}. Ensure 'Organization.Read.All' in granted.");
                    }
                    else
                    {
                        _telemetry.LogError(ex, $"User import - couldn't load SKUs for org - {ex.Message}");
                    }

                    // If we can't get tenant SKUs to find all users by, we can get SKUs per user instead, but this can be very slow.
                    _telemetry.LogWarning($"User import - will load SKUs directly from each user instead. This will be slow.");
                }

                var allDbUsers = await db.users.Include(u => u.LicenseLookups).ToListAsync();
                var graphMentionedExistingDbUsers = GetDbUsersFromGraphUsers(allActiveGraphUsers, allDbUsers);

                // Insert any user we've not seen so far
                var insertedDbUsers = await InsertMissingUsers(db, allActiveGraphUsers, graphMentionedExistingDbUsers, skus == null);
                var notInserted = allActiveGraphUsers.Where(
                    u => !string.IsNullOrEmpty(u.UserPrincipalName) &&
                        !insertedDbUsers.Where(i => i.UserPrincipalName.ToLower() == u.UserPrincipalName.ToLower()).Any()).ToList();


                // Check existing users again Graph updates
                _telemetry.LogInformation($"User import - updating {notInserted.Count.ToString("N0")} existing users...");
                foreach (var existingGraphUser in notInserted)
                {
                    var dbUser = graphMentionedExistingDbUsers.Where(u => u.UserPrincipalName.ToLower() == existingGraphUser.UserPrincipalName.ToLower()).SingleOrDefault();
                    await UpdateDbUserWithGraphData(db, existingGraphUser, allActiveGraphUsers, allDbUsers, dbUser, skus == null);
                }

                // Combine inserted & modified db users
                var allProcessedDbUsers = new List<Common.Entities.User>(insertedDbUsers);
                var notInsertDbUsers = GetDbUsersFromGraphUsers(notInserted, graphMentionedExistingDbUsers);
                allProcessedDbUsers.AddRange(notInsertDbUsers);

                // Can we update SKUs for users on batch (ie Organization.Read.All granted)?
                if (skus != null)
                {
                    await ProcessSKUsForAllUsers(skus, allProcessedDbUsers, db);
                    _telemetry.LogInformation($"User import - updated user license information from {skus.Count.ToString("N0")} tenant SKUs");
                }

                db.ChangeTracker.DetectChanges();
                await db.SaveChangesAsync();
                _telemetry.LogInformation($"{DateTime.Now.ToShortTimeString()} User import - inserted {insertedDbUsers.Count.ToString("N0")} new users and updated {notInserted.Count.ToString("N0")} from Graph API");
            }
        }

        private async Task ProcessSKUsForAllUsers(IGraphServiceSubscribedSkusCollectionPage skus, List<Common.Entities.User> graphFoundDbUsers, AnalyticsEntitiesContext db)
        {
            // Remove all license info from all users 1st
            db.UserLicenseTypeLookups.RemoveRange(graphFoundDbUsers.SelectMany(u => u.LicenseLookups));

            foreach (var sku in skus)
            {
                var req = _graphServiceClient.Users.Request()
                    .Select("userPrincipalName")
                    .Filter($"assignedLicenses/any(u:u/skuId eq {sku.SkuId})");

                // Recursively load users 
                var allUsersWithSku = new List<Microsoft.Graph.User>();
                int skuPage = 1;
                while (req != null)
                {
                    var usersWithSku = await req.GetAsync();
                    allUsersWithSku.AddRange(usersWithSku);
                    req = usersWithSku.NextPageRequest;
                    Console.WriteLine($"DEBUG: SKU {sku.SkuPartNumber} page {skuPage}");
                    skuPage++;
                }

                // Update all
                await AddSkuForUsers(graphFoundDbUsers, allUsersWithSku, sku, db);
            }

        }

        private async Task AddSkuForUsers(List<Common.Entities.User> graphFoundDbUsers, List<Microsoft.Graph.User> usersWithSku, SubscribedSku sku, AnalyticsEntitiesContext db)
        {
            var relevantDbUsers = new List<Common.Entities.User>();
            foreach (var graphUser in usersWithSku)
                foreach (var dbUser in graphFoundDbUsers)
                    if (graphUser.UserPrincipalName.ToLower() == dbUser.UserPrincipalName.ToLower())
                    {
                        relevantDbUsers.Add(dbUser);
                        break;
                    }

            _telemetry.LogInformation($"Found {relevantDbUsers.Count.ToString("N0")} users in SQL for SKU Part Number '{sku.SkuPartNumber}' from {usersWithSku.Count.ToString("N0")} Graph users.");

            var list = new List<UserLicenseTypeLookup>();
            int i = 0;
            foreach (var dbUser in relevantDbUsers)
            {
                var licence = await GetLicenseType(sku.SkuPartNumber);
                list.Add(new UserLicenseTypeLookup { License = licence, User = dbUser });

                if (i > 0 && i % 1000 == 0)
                {
                    Console.WriteLine($"User {i.ToString("N0")} / {relevantDbUsers.Count.ToString("N0")} processed for licenses.");
                }
            }
            db.UserLicenseTypeLookups.AddRange(list);
        }

        private async Task UpdateDbUserWithGraphData(AnalyticsEntitiesContext db, GraphUser graphUser, List<GraphUser> allGraphUsers, List<Common.Entities.User> allDbUsers, Common.Entities.User dbUser, bool readUserSkus)
        {
            UpdateDbUserFromGraphUser(dbUser, graphUser);

            var nameMaxLengthDepartment = StringUtils.EnsureMaxLength(graphUser.Department?.Trim(), 100);
            dbUser.Department = !string.IsNullOrEmpty(nameMaxLengthDepartment) ?
                await _userMetaCache.DepartmentCache.GetOrCreateNewResource(nameMaxLengthDepartment,
                    new UserDepartment { Name = nameMaxLengthDepartment }) : null;

            var nameMaxLengthJobTitle = StringUtils.EnsureMaxLength(graphUser.JobTitle?.Trim(), 100);
            dbUser.JobTitle = !string.IsNullOrEmpty(nameMaxLengthJobTitle) ?
                await _userMetaCache.JobTitleCache.GetOrCreateNewResource(nameMaxLengthJobTitle,
                    new UserJobTitle { Name = nameMaxLengthJobTitle }) : null;

            var nameMaxLengthOfficeLocation = StringUtils.EnsureMaxLength(graphUser.OfficeLocation?.Trim(), 100);
            dbUser.OfficeLocation = !string.IsNullOrEmpty(nameMaxLengthOfficeLocation) ?
                await _userMetaCache.OfficeLocationCache.GetOrCreateNewResource(nameMaxLengthOfficeLocation,
                    new UserOfficeLocation { Name = nameMaxLengthOfficeLocation }) : null;

            var nameMaxLengthUsageLocation = StringUtils.EnsureMaxLength(graphUser.UsageLocation?.Trim(), 100);
            dbUser.UsageLocation = !string.IsNullOrEmpty(nameMaxLengthUsageLocation) ?
                await _userMetaCache.UseageLocationCache.GetOrCreateNewResource(nameMaxLengthUsageLocation,
                    new UserUsageLocation { Name = nameMaxLengthUsageLocation }) : null;

            var nameMaxLengthCountry = StringUtils.EnsureMaxLength(graphUser.Country?.Trim(), 100);
            dbUser.UserCountry = !string.IsNullOrEmpty(nameMaxLengthCountry) ?
                await _userMetaCache.CountryOrRegionCache.GetOrCreateNewResource(nameMaxLengthCountry,
                    new CountryOrRegion { Name = nameMaxLengthCountry }) : null;

            var nameMaxLengthState = StringUtils.EnsureMaxLength(graphUser.State?.Trim(), 100);
            dbUser.StateOrProvince = !string.IsNullOrEmpty(nameMaxLengthState) ?
                await _userMetaCache.StateOrProvinceCache.GetOrCreateNewResource(nameMaxLengthState,
                    new StateOrProvince { Name = nameMaxLengthState }) : null;

            var nameMaxLengthCompany = StringUtils.EnsureMaxLength(graphUser.CompanyName?.Trim(), 100);
            dbUser.CompanyName = !string.IsNullOrEmpty(nameMaxLengthCompany) ?
                await _userMetaCache.CompanyNameCache.GetOrCreateNewResource(nameMaxLengthCompany,
                    new CompanyName { Name = nameMaxLengthCompany }) : null;

            if (graphUser.DefaultManagerInfo?.Id != null)
            {
                // Try getting manager from DB 1st
                var dbManager = allDbUsers.Where(u => !string.IsNullOrEmpty(u.AzureAdId) && new Guid(u.AzureAdId).Equals(new Guid(graphUser.DefaultManagerInfo.Id))).FirstOrDefault();
                if (dbManager == null)
                {
                    var graphManagerUser = allGraphUsers.FirstOrDefault(u => u.Id == graphUser.DefaultManagerInfo?.Id);

                    if (graphManagerUser != null)
                    {
                        // Got user from Graph cache; get DB user by UPN
                        var managerUpn = graphManagerUser.UserPrincipalName?.ToLower();

                        dbUser.Manager = !string.IsNullOrEmpty(managerUpn) ?
                            await _userMetaCache.UserCache.GetOrCreateNewResource(managerUpn,
                                new Common.Entities.User { UserPrincipalName = managerUpn }, true) : null;

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
            dbUser.LastUpdated = DateTime.Now;

            // This is only done per user if can't be done at tenant level (due to extra permission)
            if (readUserSkus)
            {
                // Get user service-plan from Graph
                // Service plan names - https://docs.microsoft.com/en-us/azure/active-directory/enterprise-users/licensing-service-plan-reference
                IUserLicenseDetailsCollectionPage userServicePlans = null;
                try
                {
                    userServicePlans = await _graphServiceClient.Users[graphUser.Id].LicenseDetails.Request()
                        .Select("skuPartNumber,skuId")
                        .GetAsync();
                }
                catch (ServiceException ex)
                {
                    _telemetry.LogError(ex, $"User import - couldn't load service-plans for user ID '{graphUser.Id}' - {ex.Message}");
                }

                if (userServicePlans != null)
                {
                    var allLicenses = new List<LicenseType>();
                    foreach (var userPlan in userServicePlans)
                    {
                        allLicenses.Add(await GetLicenseType(userPlan.SkuPartNumber));
                    }

                    // Remove old lookups (simpler) & re-add
                    db.UserLicenseTypeLookups.RemoveRange(dbUser.LicenseLookups.Where(l => l.IsSavedToDB));
                    foreach (var licence in allLicenses)
                    {
                        dbUser.LicenseLookups.Add(new UserLicenseTypeLookup { License = licence, User = dbUser });
                    }
                }
            }
        }

        private async Task<LicenseType> GetLicenseType(string skuPartNumber)
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


        private List<Common.Entities.User> GetDbUsersFromGraphUsers(List<GraphUser> allGraphUsers, List<Common.Entities.User> allDbUsers)
        {
            var users = new List<Common.Entities.User>();

            foreach (var graphUser in allGraphUsers)
            {
                // Do we have this graph user?
                var upn = graphUser.UserPrincipalName?.ToLower();
                if (!string.IsNullOrEmpty(upn))
                {
                    var dbUser = allDbUsers.Where(u => u.UserPrincipalName.ToLower() == upn).SingleOrDefault();
                    if (dbUser != null)
                    {
                        users.Add(dbUser);
                    }
                }
            }

            return users;
        }

        /// <summary>
        /// Inserts missing users into DB & calls UpdateDbUserWithGraphData
        /// </summary>
        private async Task<List<Common.Entities.User>> InsertMissingUsers(AnalyticsEntitiesContext db, List<GraphUser> allGraphUsers, List<Common.Entities.User> graphMentionedDbUsers, bool readUserSkus)
        {
            _telemetry.LogInformation($"User import - Inserting missing users...");
            var usersInserted = new List<Common.Entities.User>();

            // Build list of users to insert
            foreach (var graphUser in allGraphUsers)
            {
                // Do we have this graph user?
                var upn = graphUser.UserPrincipalName?.ToLower();
                if (!string.IsNullOrEmpty(upn) && !graphMentionedDbUsers.Where(u => u.UserPrincipalName.ToLower() == upn).Any())
                {
                    // Lookup manager will just add to cache but not to context
                    var dbUser = await _userMetaCache.UserCache.GetOrCreateNewResource(upn, UpdateDbUserFromGraphUser(new Common.Entities.User { UserPrincipalName = upn }, graphUser));
                    usersInserted.Add(dbUser);
                }
            }

            // Update too each user. 
            int i = 0;
            _telemetry.LogInformation($"User import - Loading metadata for {usersInserted.Count.ToString("N0")} new users...");

            foreach (var newDbUser in usersInserted)
            {
                var graphUser = allGraphUsers.Where(u => u.UserPrincipalName.ToLower() == newDbUser.UserPrincipalName).FirstOrDefault();
                await UpdateDbUserWithGraphData(db, graphUser, allGraphUsers, graphMentionedDbUsers, newDbUser, readUserSkus);

                if (i > 0 && i % 1000 == 0)
                {
                    Console.WriteLine($"New user {i}/{usersInserted.Count.ToString("N0")} processed for lookups.");
                }
                i++;
            }

            db.users.AddRange(usersInserted);

            Console.WriteLine($"User import - Saving {usersInserted.Count.ToString("N0")} new users to SQL...");
            await db.SaveChangesAsync();

            return usersInserted;
        }

        private Common.Entities.User UpdateDbUserFromGraphUser(Common.Entities.User dbUser, GraphUser graphUser)
        {
            dbUser.AccountEnabled = graphUser.AccountEnabled;
            dbUser.PostalCode = graphUser.PostalCode;
            dbUser.AzureAdId = graphUser.Id;
            dbUser.Mail = graphUser.Mail;

            return dbUser;
        }
    }
}
