using Common.Entities;
using Common.Entities.Entities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.FakeDataGen
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

            // Create users
            for (int i = 0; i < count; i++)
            {
                // Get or create a random department
                var departmentName = CopilotActivityGeneratorConfig.DepartmentNames[_random.Next(CopilotActivityGeneratorConfig.DepartmentNames.Length)];
                var department = GetOrCreateDepartment(db, departmentName);

                var upn = $"testuser{i}@contoso.com";
                var user = new User
                {
                    UserPrincipalName = upn,
                    Mail = upn,
                    Department = department,
                    AccountEnabled = true,
                    AzureAdId = Guid.NewGuid().ToString()
                };
                db.users.Add(user);
                users.Add(user);
            }
            db.SaveChanges();

            // Assign licenses to users
            int usersWithCopilot = AssignLicensesToUsers(db, users, copilotLicense, e5License, e3License, copilotLicensePercentage);

            Console.WriteLine($"Assigned Copilot licenses to {usersWithCopilot}/{count} users ({(usersWithCopilot * 100.0 / count):F1}%)");

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

        private UserDepartment GetOrCreateDepartment(AnalyticsEntitiesContext db, string departmentName)
        {
            var department = db.UserDepartments.FirstOrDefault(d => d.Name == departmentName);
            if (department == null)
            {
                department = new UserDepartment { Name = departmentName };
                db.UserDepartments.Add(department);
                db.SaveChanges();
            }
            return department;
        }
    }
}
