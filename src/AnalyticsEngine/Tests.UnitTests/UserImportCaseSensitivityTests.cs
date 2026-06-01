using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Graph;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Regression tests for the perf refactor that removed <c>.ToLower()</c> from
    /// EF queries against <c>user_name</c> (LOWER() on the column makes the predicate
    /// non-SARGable, forcing a clustered-index scan instead of a seek - a real bottleneck
    /// at 200k-user scale).
    ///
    /// The removal is only safe because the default code-first SQL Server collation for
    /// this codebase (<c>Latin1_General_CI_AS</c>) is case-insensitive, so the column
    /// comparison still matches mixed-case UPNs. These tests prove that end to end by
    /// running the user importer with UPN casing that differs between:
    ///   - the Graph payload (typically the canonical mixed-case UPN)
    ///   - the existing row in the database (which might be lowercase from a legacy import)
    ///   - the lookup we issue inside <see cref="UserMetadataUpdater"/> when reloading
    ///     just-inserted users.
    ///
    /// If a future EF migration changes the collation away from CI, these tests will
    /// fail and surface the regression before it hits production.
    /// </summary>
    [TestClass]
    public class UserImportCaseSensitivityTests
    {
        [TestMethod]
        public async Task UserImport_GraphUpperCaseUpn_MatchesExistingLowerCaseDbUser()
        {
            // Pre-existing DB user has lowercase UPN; Graph returns same UPN in MixedCase.
            // Without LOWER() on the column, the EF reload query relies on the CI collation
            // for the match - this asserts that path still works.
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var ticks = DateTime.Now.Ticks;
            var aadId = Guid.NewGuid().ToString();
            var lowerCaseUpn = $"caseuser{ticks}@test.com";
            var mixedCaseUpn = $"CaseUser{ticks}@TEST.com";

            // Seed the DB with the lowercase row (no licences, just the user)
            using (var seedDb = new AnalyticsEntitiesContext())
            {
                seedDb.users.Add(new Common.Entities.User
                {
                    UserPrincipalName = lowerCaseUpn,
                    AzureAdId = aadId,
                    AccountEnabled = true,
                    Mail = lowerCaseUpn,
                });
                await seedDb.SaveChangesAsync();
            }

            try
            {
                // Graph returns the same user but with mixed-case UPN.
                // Use PostalCode for the metadata-update assertion because UserMetadataUpdater's
                // direct property mapping sets it (no navigation property indirection).
                var graphUser = new GraphUser
                {
                    UserPrincipalName = mixedCaseUpn,
                    Id = aadId,
                    AccountEnabled = true,
                    Mail = mixedCaseUpn,
                    PostalCode = "ZZ-PERF-TEST",
                };

                var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser });
                var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);
                await updater.InsertAndUpdateDatabaseFromExternalUsers();

                using (var verifyDb = new AnalyticsEntitiesContext())
                {
                    // Should still resolve the existing row (no duplicate inserted),
                    // and the metadata update should have stuck.
                    var matches = await verifyDb.users
                        .Where(u => u.AzureAdId == aadId)
                        .ToListAsync();

                    Assert.AreEqual(1, matches.Count,
                        "Mixed-case Graph UPN should resolve the existing lowercase DB row, not create a duplicate.");
                    Assert.AreEqual("ZZ-PERF-TEST", matches[0].PostalCode,
                        "Metadata update should have flowed through the matched DB row.");
                }
            }
            finally
            {
                using (var cleanupDb = new AnalyticsEntitiesContext())
                {
                    var rows = await cleanupDb.users
                        .Where(u => u.AzureAdId == aadId)
                        .ToListAsync();
                    cleanupDb.users.RemoveRange(rows);
                    await cleanupDb.SaveChangesAsync();
                }
            }
        }

        [TestMethod]
        public async Task UserImport_LicenseAssignment_MatchesMixedCaseGraphUpnToDbUser()
        {
            // ProcessSKUsForAllUsers builds a single dbUsersByUpn dictionary
            // (OrdinalIgnoreCase) once per import. AddSkuForUsers then looks up
            // each Graph user's UPN against that dictionary. The Graph payload for
            // a SKU member can have different casing than the inserted user row,
            // so the dictionary lookup must be case-insensitive.
            //
            // Validates the H1+H3 refactor end-to-end: dropping .ToLower() from
            // the dictionary key build AND the per-Graph-user lookup, and reusing
            // the same dictionary across SKUs.
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var ticks = DateTime.Now.Ticks;
            var userId = Guid.NewGuid().ToString();
            // Graph delivers the UPN one way for the user payload and another for the SKU member.
            var graphUserUpn = $"licCaseUser{ticks}@TEST.com";   // mixed case in user payload
            var skuMemberUpn = $"liccaseuser{ticks}@test.com";   // lowercase in SKU member payload
            var skuId = Guid.NewGuid();
            var skuPartNumber = "ENTERPRISEPACK";
            var licenseName = "Office 365 E3";

            // Clean any leftover state from previous runs of this test
            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existing = await cleanupDb.users
                    .Include(u => u.LicenseLookups)
                    .Where(u => u.UserPrincipalName == graphUserUpn || u.UserPrincipalName == skuMemberUpn)
                    .ToListAsync();
                foreach (var u in existing)
                {
                    cleanupDb.UserLicenseTypeLookups.RemoveRange(u.LicenseLookups);
                    cleanupDb.users.Remove(u);
                }
                await cleanupDb.SaveChangesAsync();
            }

            try
            {
                var graphUser = new GraphUser
                {
                    UserPrincipalName = graphUserUpn,
                    Id = userId,
                    AccountEnabled = true,
                    Mail = graphUserUpn,
                };

                var skus = new GraphServiceSubscribedSkusCollectionPage
                {
                    new SubscribedSku { SkuId = skuId, SkuPartNumber = skuPartNumber }
                };

                // Note: SKU-member payload uses the OTHER casing
                var usersWithSku = new List<Microsoft.Graph.User>
                {
                    new Microsoft.Graph.User { UserPrincipalName = skuMemberUpn, Id = userId }
                };
                var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.User>>
                {
                    { skuId, usersWithSku }
                };

                var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, skus, fakeUsersBySku);
                var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);
                await updater.InsertAndUpdateDatabaseFromExternalUsers();

                using (var verifyDb = new AnalyticsEntitiesContext())
                {
                    // Use AzureAdId as the canonical identifier; case-insensitive UPN match
                    // means we should find exactly one row regardless of which casing won
                    // the insert race.
                    var dbUser = await verifyDb.users
                        .Include(u => u.LicenseLookups.Select(l => l.License))
                        .FirstOrDefaultAsync(u => u.AzureAdId == userId);

                    Assert.IsNotNull(dbUser, "User should have been inserted from Graph payload.");
                    Assert.AreEqual(1, dbUser.LicenseLookups.Count,
                        "License lookup must succeed even when Graph SKU-member UPN casing differs from the inserted user's UPN casing.");
                    Assert.AreEqual(licenseName, dbUser.LicenseLookups[0].License.Name);
                }
            }
            finally
            {
                using (var cleanupDb = new AnalyticsEntitiesContext())
                {
                    var rows = await cleanupDb.users
                        .Include(u => u.LicenseLookups)
                        .Where(u => u.AzureAdId == userId)
                        .ToListAsync();
                    foreach (var u in rows)
                    {
                        cleanupDb.UserLicenseTypeLookups.RemoveRange(u.LicenseLookups);
                        cleanupDb.users.Remove(u);
                    }
                    await cleanupDb.SaveChangesAsync();
                }
            }
        }

        [TestMethod]
        public async Task UserImport_TwoSkusOnSameUser_HoistedDictionaryStillResolves()
        {
            // The H3 refactor moved the dbUsersByUpn dictionary out of AddSkuForUsers
            // (where it was rebuilt per SKU - O(n*skus)) and into ProcessSKUsForAllUsers
            // (built once and reused). This test runs an import with TWO SKUs covering
            // the same user to verify the second SKU iteration still resolves the user
            // against the hoisted dictionary.
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var ticks = DateTime.Now.Ticks;
            var userId = Guid.NewGuid().ToString();
            var userUpn = $"hoistedDictUser{ticks}@test.com";

            var sku1Id = Guid.NewGuid(); var sku1Part = "ENTERPRISEPACK"; var lic1 = "Office 365 E3";
            var sku2Id = Guid.NewGuid(); var sku2Part = "ENTERPRISEPREMIUM"; var lic2 = "Office 365 E5";

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existing = await cleanupDb.users.Include(u => u.LicenseLookups)
                    .Where(u => u.UserPrincipalName == userUpn).ToListAsync();
                foreach (var u in existing)
                {
                    cleanupDb.UserLicenseTypeLookups.RemoveRange(u.LicenseLookups);
                    cleanupDb.users.Remove(u);
                }
                var leftover = await cleanupDb.LicenseTypes.Where(l => l.Name == lic1 || l.Name == lic2).ToListAsync();
                cleanupDb.LicenseTypes.RemoveRange(leftover);
                await cleanupDb.SaveChangesAsync();
            }

            try
            {
                var graphUser = new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn };
                var skus = new GraphServiceSubscribedSkusCollectionPage
                {
                    new SubscribedSku { SkuId = sku1Id, SkuPartNumber = sku1Part },
                    new SubscribedSku { SkuId = sku2Id, SkuPartNumber = sku2Part },
                };
                var skuMember = new Microsoft.Graph.User { UserPrincipalName = userUpn, Id = userId };
                var fakeUsersBySku = new Dictionary<Guid, List<Microsoft.Graph.User>>
                {
                    { sku1Id, new List<Microsoft.Graph.User> { skuMember } },
                    { sku2Id, new List<Microsoft.Graph.User> { skuMember } },
                };

                var fakeLoader = new FakeUserMetadataLoader(new List<GraphUser> { graphUser }, skus, fakeUsersBySku);
                var updater = new UserMetadataUpdater(telemetry, config, fakeLoader);
                await updater.InsertAndUpdateDatabaseFromExternalUsers();

                using (var verifyDb = new AnalyticsEntitiesContext())
                {
                    var dbUser = await verifyDb.users
                        .Include(u => u.LicenseLookups.Select(l => l.License))
                        .FirstOrDefaultAsync(u => u.UserPrincipalName == userUpn);

                    Assert.IsNotNull(dbUser);
                    Assert.AreEqual(2, dbUser.LicenseLookups.Count,
                        "Both SKUs should resolve against the single hoisted dbUsersByUpn dictionary.");
                    Assert.IsTrue(dbUser.LicenseLookups.Any(l => l.License.Name == lic1));
                    Assert.IsTrue(dbUser.LicenseLookups.Any(l => l.License.Name == lic2));
                }
            }
            finally
            {
                using (var cleanupDb = new AnalyticsEntitiesContext())
                {
                    var rows = await cleanupDb.users.Include(u => u.LicenseLookups)
                        .Where(u => u.UserPrincipalName == userUpn).ToListAsync();
                    foreach (var u in rows)
                    {
                        cleanupDb.UserLicenseTypeLookups.RemoveRange(u.LicenseLookups);
                        cleanupDb.users.Remove(u);
                    }
                    await cleanupDb.SaveChangesAsync();
                }
            }
        }
    }
}
