using Common.Entities;
using System;
using System.Linq;

namespace Tests.FakeDataGen
{
    /// <summary>
    /// Manages license creation and assignment for test users
    /// </summary>
    public class CopilotLicenseManager
    {
        /// <summary>
        /// Ensures that all required license types exist in the database
        /// </summary>
        public void EnsureLicensesExist(AnalyticsEntitiesContext db)
        {
            // Check if licenses already exist
            var licenseCount = db.LicenseTypes.Count();
            if (licenseCount > 0)
            {
                Console.WriteLine($"Found {licenseCount} existing license types in database.");
                return;
            }

            Console.WriteLine("No licenses found. Creating test license types...");

            // Create license types
            var licenses = new[]
            {
                new LicenseType { Name = "Microsoft 365 Copilot", SKUID = CopilotActivityGeneratorConfig.COPILOT_LICENSE_SKU },
                new LicenseType { Name = "Office 365 E5", SKUID = CopilotActivityGeneratorConfig.E5_LICENSE_SKU },
                new LicenseType { Name = "Office 365 E3", SKUID = CopilotActivityGeneratorConfig.E3_LICENSE_SKU },
                new LicenseType { Name = "Microsoft 365 Business Premium", SKUID = CopilotActivityGeneratorConfig.BUSINESS_PREMIUM_SKU },
                new LicenseType { Name = "Exchange Online Plan 1", SKUID = CopilotActivityGeneratorConfig.EXCHANGE_ONLINE_SKU }
            };

            foreach (var license in licenses)
            {
                db.LicenseTypes.Add(license);
            }
            db.SaveChanges();

            Console.WriteLine($"Created {licenses.Length} license types.");
        }

        /// <summary>
        /// Assigns a license to a user
        /// </summary>
        public void AssignLicenseToUser(AnalyticsEntitiesContext db, User user, LicenseType license)
        {
            // Check if user already has this license
            var existingLookup = db.UserLicenseTypeLookups
                .FirstOrDefault(l => l.UserId == user.ID && l.LicenseTypeId == license.ID);

            if (existingLookup == null)
            {
                var lookup = new UserLicenseTypeLookup
                {
                    User = user,
                    License = license
                };
                db.UserLicenseTypeLookups.Add(lookup);
            }
        }
    }
}
