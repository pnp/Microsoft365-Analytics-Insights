using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Graph.Models;
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
    /// Database-backed regression test for the manager prefetch added in #371.
    ///
    /// The prefetch (<c>ManagerPrefetchCache</c> / <c>SqlUserLookupStore</c>) loads a batch's
    /// managers with tracking at the start of the batch, before the batch's own users are attached.
    /// EF identity resolution therefore lets a prefetched entity win over the <c>AsNoTracking</c>
    /// snapshot the pipeline was about to attach - which is fine only for as long as the prefetched
    /// entity is at least as complete.
    ///
    /// It is not automatically: <c>UserMetadataUpdater</c> loads its snapshots with
    /// <c>Include(u =&gt; u.LicenseLookups)</c> on the per-user licence path, proxies are disabled
    /// and <c>User.LicenseLookups</c> is a plain non-virtual list, so there is no lazy load to fill
    /// it in afterwards. A prefetch without that <c>Include</c> hands
    /// <c>UserLicenseProcessor.ProcessUserLicenses</c> an empty licence collection, it deletes none
    /// of the user's existing rows before re-adding them, and <c>SaveChanges</c> fails on the unique
    /// <c>(license_type_id, user_id)</c> index.
    ///
    /// Found by review, not by the pre-existing suite - none of its licence tests happened to make a
    /// user the manager of another user in the same batch.
    /// </summary>
    [TestClass]
    public class UserManagerPrefetchLicenceTests
    {
        [TestMethod]
        public async Task ManagerPrefetch_ManagerIsAlsoUpdatedInTheSameBatch_KeepsItsLicenceGraph()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            // Guid, not DateTime.Now.Ticks: Ticks only advances every ~15ms on .NET Framework, so
            // two fixtures starting close together can collide.
            var tag = Guid.NewGuid().ToString("N");
            var managerUpn = $"prefetchmanager{tag}@test.com";
            var reportUpn = $"prefetchreport{tag}@test.com";
            var managerAadId = Guid.NewGuid().ToString();
            var reportAadId = Guid.NewGuid().ToString();

            await RemoveTestUsers(managerUpn, reportUpn);
            try
            {
                var manager = new GraphUser
                {
                    UserPrincipalName = managerUpn,
                    Id = managerAadId,
                    AccountEnabled = true,
                    Mail = managerUpn,
                };
                var report = new GraphUser
                {
                    UserPrincipalName = reportUpn,
                    Id = reportAadId,
                    AccountEnabled = true,
                    Mail = reportUpn,
                    // This is what puts the manager into the batch's prefetch set.
                    ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = managerAadId } },
                };

                // fakeSkus: null forces the per-user licence path, which is the one that reads
                // dbUser.LicenseLookups. Both users hold the same licence on both runs, so the
                // second run must delete and re-add rather than add a duplicate.
                var licences = new Dictionary<string, List<LicenseDetails>>
                {
                    { managerAadId, new List<LicenseDetails> { new LicenseDetails { SkuId = Guid.NewGuid(), SkuPartNumber = "ENTERPRISEPACK" } } },
                    { reportAadId, new List<LicenseDetails> { new LicenseDetails { SkuId = Guid.NewGuid(), SkuPartNumber = "ENTERPRISEPACK" } } },
                };

                var fakeLoader = new FakeUserMetadataLoader(
                    new List<GraphUser> { manager, report },
                    fakeSkus: null,
                    fakeUsersBySku: null,
                    fakeLicenseDetails: licences);

                // Run 1 inserts both users and gives each a licence.
                await new UserMetadataUpdater(logger, config, fakeLoader).InsertAndUpdateDatabaseFromExternalUsers();

                using (var db = new AnalyticsEntitiesContext())
                {
                    Assert.AreEqual(1, await CountLicences(db, managerUpn), "Precondition: run 1 gives the manager one licence.");
                    Assert.AreEqual(1, await CountLicences(db, reportUpn), "Precondition: run 1 gives the report one licence.");
                }

                // Run 2 takes the existing-user path, where the batch's users are attached one at a
                // time from AsNoTracking snapshots - so this is the run where a prefetched manager
                // can shadow one. Pre-fix this throws a duplicate-key DbUpdateException.
                await new UserMetadataUpdater(logger, config, fakeLoader).InsertAndUpdateDatabaseFromExternalUsers();

                using (var db = new AnalyticsEntitiesContext())
                {
                    Assert.AreEqual(1, await CountLicences(db, managerUpn),
                        "The manager is prefetched AND updated in the same batch; its existing licence row must be replaced, not duplicated.");
                    Assert.AreEqual(1, await CountLicences(db, reportUpn),
                        "The report is not prefetched and must be unaffected.");

                    var reportRow = await db.users.AsNoTracking().FirstOrDefaultAsync(u => u.UserPrincipalName == reportUpn);
                    var managerRow = await db.users.AsNoTracking().FirstOrDefaultAsync(u => u.UserPrincipalName == managerUpn);
                    Assert.IsNotNull(reportRow);
                    Assert.IsNotNull(managerRow);
                    Assert.AreEqual(managerRow.ID, reportRow.ManagerId,
                        "The manager relationship must still be resolved through the prefetch.");
                }
            }
            finally
            {
                await RemoveTestUsers(managerUpn, reportUpn);
            }
        }

        private static async Task<int> CountLicences(AnalyticsEntitiesContext db, string upn)
        {
            return await db.UserLicenseTypeLookups.CountAsync(l => l.User.UserPrincipalName == upn);
        }

        /// <summary>
        /// Deletes on the exact UPNs recorded before the first write - never a substring LIKE, which
        /// can either reach another fixture's rows or silently match nothing and leak every run. The
        /// worktree's catalogs are not reset between runs.
        /// </summary>
        private static async Task RemoveTestUsers(params string[] upns)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var users = await db.users
                    .Include(u => u.LicenseLookups)
                    .Where(u => upns.Contains(u.UserPrincipalName))
                    .ToListAsync();

                if (users.Count == 0)
                {
                    return;
                }

                // Clear the manager self-reference first: the FK would otherwise block the delete.
                foreach (var user in users)
                {
                    user.ManagerId = null;
                    user.Manager = null;
                }
                await db.SaveChangesAsync();

                foreach (var user in users)
                {
                    db.UserLicenseTypeLookups.RemoveRange(user.LicenseLookups);
                }
                db.users.RemoveRange(users);
                await db.SaveChangesAsync();
            }
        }
    }
}
