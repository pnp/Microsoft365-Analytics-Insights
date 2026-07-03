using Common.Entities;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using Tests.FakeDataGen.Seeding;

namespace Tests.FakeDataGen.Copilot
{
    /// <summary>
    /// Manages user creation and department assignment
    /// </summary>
    public class CopilotUserManager
    {
        private readonly Random _random;
        private readonly CopilotLicenseManager _licenseManager;

        public CopilotUserManager(Random random, CopilotLicenseManager licenseManager)
        {
            _random = random;
            _licenseManager = licenseManager;
        }

        /// <summary>
        /// Creates test users with departments and licenses
        /// </summary>
        public List<User> CreateTestUsers(AnalyticsEntitiesContext db, int count, int copilotLicensePercentage)
        {
            var users = new List<User>();
            var copilotLicense = db.LicenseTypes.FirstOrDefault(l => l.SKUID == CopilotActivityGeneratorConfig.COPILOT_LICENSE_SKU);
            var e5License = db.LicenseTypes.FirstOrDefault(l => l.SKUID == CopilotActivityGeneratorConfig.E5_LICENSE_SKU);
            var e3License = db.LicenseTypes.FirstOrDefault(l => l.SKUID == CopilotActivityGeneratorConfig.E3_LICENSE_SKU);

            Console.WriteLine($"Creating {count} test users with {copilotLicensePercentage}% having Copilot licenses...");

            // Look-up caches so each distinct metadata value is created once and the same instance
            // is reused (keeps FKs coherent and lets us group users by company for the hierarchy).
            var deptCache = new Dictionary<string, UserDepartment>(StringComparer.OrdinalIgnoreCase);
            var companyCache = new Dictionary<string, CompanyName>(StringComparer.OrdinalIgnoreCase);
            var jobCache = new Dictionary<string, UserJobTitle>(StringComparer.OrdinalIgnoreCase);
            var stateCache = new Dictionary<string, StateOrProvince>(StringComparer.OrdinalIgnoreCase);
            var countryCache = new Dictionary<string, CountryOrRegion>(StringComparer.OrdinalIgnoreCase);
            var officeCache = new Dictionary<string, UserOfficeLocation>(StringComparer.OrdinalIgnoreCase);
            var usageCache = new Dictionary<string, UserUsageLocation>(StringComparer.OrdinalIgnoreCase);

            // Create users with realistic, internally-consistent metadata spread across domains.
            for (int i = 0; i < count; i++)
            {
                var profile = SeedDataCatalogue.NextUserProfile(_random);
                var upn = SeedDataCatalogue.BuildUpn("testuser", i);
                var user = new User
                {
                    UserPrincipalName = upn,
                    Mail = upn,
                    AccountEnabled = profile.AccountEnabled,
                    AzureAdId = Guid.NewGuid().ToString(),
                    PostalCode = profile.PostalCode ?? string.Empty,
                    Department = GetOrCreateLookup(db, db.UserDepartments, deptCache, profile.Department),
                    CompanyName = GetOrCreateLookup(db, db.CompanyNames, companyCache, profile.Company),
                    JobTitle = GetOrCreateLookup(db, db.UserJobTitles, jobCache, profile.JobTitle),
                    StateOrProvince = GetOrCreateLookup(db, db.StateOrProvinces, stateCache, profile.StateOrProvince),
                    UserCountry = GetOrCreateLookup(db, db.CountryOrRegions, countryCache, profile.Country),
                    OfficeLocation = GetOrCreateLookup(db, db.UserOfficeLocations, officeCache, profile.OfficeLocation),
                    UsageLocation = GetOrCreateLookup(db, db.UserUsageLocations, usageCache, profile.UsageLocation)
                };
                db.users.Add(user);
                users.Add(user);
            }
            db.SaveChanges();

            // Assign licenses to users
            int usersWithCopilot = AssignLicensesToUsers(db, users, copilotLicense, e5License, e3License, copilotLicensePercentage);

            Console.WriteLine($"Assigned Copilot licenses to {usersWithCopilot}/{count} users ({(usersWithCopilot * 100.0 / count):F1}%)");

            AssignManagers(db, users);

            return users;
        }

        private int AssignLicensesToUsers(AnalyticsEntitiesContext db, List<User> users, LicenseType copilotLicense, LicenseType e5License, LicenseType e3License, int copilotLicensePercentage)
        {
            int usersWithCopilot = 0;

            for (int i = 0; i < users.Count; i++)
            {
                var user = users[i];
                bool shouldHaveCopilot = _random.Next(100) < copilotLicensePercentage;

                if (shouldHaveCopilot && copilotLicense != null)
                {
                    // User gets Copilot + E5
                    _licenseManager.AssignLicenseToUser(db, user, copilotLicense);
                    if (e5License != null)
                    {
                        _licenseManager.AssignLicenseToUser(db, user, e5License);
                    }
                    usersWithCopilot++;
                }
                else if (e3License != null)
                {
                    // User gets E3 only
                    _licenseManager.AssignLicenseToUser(db, user, e3License);
                }
            }
            db.SaveChanges();

            return usersWithCopilot;
        }

        /// <summary>
        /// Resolves (or lazily creates) a named lookup row, caching the instance so each distinct
        /// value is created once and shared across every user that references it.
        /// </summary>
        private static T GetOrCreateLookup<T>(AnalyticsEntitiesContext db, DbSet<T> set,
            Dictionary<string, T> cache, string name) where T : AbstractEFEntityWithName, new()
        {
            if (name == null) return null;
            if (cache.TryGetValue(name, out var cached)) return cached;

            var existing = set.FirstOrDefault(x => x.Name == name);
            if (existing == null)
            {
                existing = new T { Name = name };
                set.Add(existing);
                db.SaveChanges();
            }
            cache[name] = existing;
            return existing;
        }

        /// <summary>
        /// Gives the generated users a realistic reporting hierarchy: within each company a small
        /// fraction are managers (left top-level) and everyone else reports to one of them.
        /// </summary>
        private void AssignManagers(AnalyticsEntitiesContext db, List<User> users)
        {
            if (users.Count < 2) return;

            foreach (var group in users.GroupBy(u => u.CompanyName))
            {
                var list = group.ToList();
                if (list.Count < 2) continue;

                int managerCount = Math.Max(1, (int)Math.Round(list.Count * 0.1));
                managerCount = Math.Min(managerCount, list.Count - 1);
                var managers = list.Take(managerCount).ToList();
                for (int i = managerCount; i < list.Count; i++)
                {
                    list[i].ManagerId = managers[_random.Next(managers.Count)].ID;
                }
            }
            db.SaveChanges();
        }
    }
}
