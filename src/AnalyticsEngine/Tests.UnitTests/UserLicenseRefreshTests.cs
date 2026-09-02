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
using SkuUser = Microsoft.Graph.Models.User;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the licence refresh introduced by issue #392.
    ///
    /// The old implementation deleted every <c>user_license_type_lookups</c> row for the whole user
    /// population and then refilled it SKU by SKU, with no transaction and no staging table. On a
    /// large tenant the refill took several minutes, and for that whole window every report joining
    /// the table saw a tenant missing most or all of its licences.
    ///
    /// The invariant these tests lock in is the one named in the issue: <b>at no point during an
    /// import is <c>user_license_type_lookups</c> observably missing licences for users who still
    /// hold them.</b>
    /// </summary>
    [TestClass]
    public class UserLicenseRefreshTests
    {
        #region Pure delta logic (no database)

        [TestMethod]
        public void UserLicenseAssignmentDelta_UnchangedState_ProducesNoWrites()
        {
            var current = new HashSet<UserLicenseAssignment>
            {
                new UserLicenseAssignment(1, 10),
                new UserLicenseAssignment(2, 10),
                new UserLicenseAssignment(2, 11),
            };
            var desired = new HashSet<UserLicenseAssignment>(current);

            var delta = UserLicenseAssignmentDelta.Between(current, desired);

            Assert.IsTrue(delta.IsEmpty, "An unchanged tenant must produce no writes at all.");
            Assert.AreEqual(0, delta.ToAdd.Count);
            Assert.AreEqual(0, delta.ToRemove.Count);
            Assert.AreEqual(3, delta.UnchangedCount, "Every existing assignment should be reported as already correct.");
        }

        [TestMethod]
        public void UserLicenseAssignmentDelta_AddsAndRemovesOnlyWhatChanged()
        {
            var current = new HashSet<UserLicenseAssignment>
            {
                new UserLicenseAssignment(1, 10),   // kept
                new UserLicenseAssignment(1, 11),   // licence taken away
                new UserLicenseAssignment(2, 10),   // kept
            };
            var desired = new HashSet<UserLicenseAssignment>
            {
                new UserLicenseAssignment(1, 10),
                new UserLicenseAssignment(2, 10),
                new UserLicenseAssignment(3, 12),   // new user + new licence
            };

            var delta = UserLicenseAssignmentDelta.Between(current, desired);

            CollectionAssert.AreEquivalent(
                new[] { new UserLicenseAssignment(3, 12) }, delta.ToAdd.ToArray(),
                "Only genuinely new assignments should be inserted.");
            CollectionAssert.AreEquivalent(
                new[] { new UserLicenseAssignment(1, 11) }, delta.ToRemove.ToArray(),
                "Only assignments Graph no longer reports should be deleted.");
            Assert.AreEqual(2, delta.UnchangedCount);
        }

        [TestMethod]
        public void UserLicenseAssignmentDelta_EmptyDesiredState_RemovesEverythingInScope()
        {
            // A tenant that genuinely holds no SKUs must still end up with an empty lookup table.
            var current = new HashSet<UserLicenseAssignment>
            {
                new UserLicenseAssignment(1, 10),
                new UserLicenseAssignment(2, 10),
            };

            var delta = UserLicenseAssignmentDelta.Between(current, new HashSet<UserLicenseAssignment>());

            Assert.AreEqual(0, delta.ToAdd.Count);
            Assert.AreEqual(2, delta.ToRemove.Count);
        }

        [TestMethod]
        public void UserLicenseAssignment_HasValueSemantics()
        {
            // The whole reconciliation is a set difference, so value equality is load-bearing.
            Assert.AreEqual(new UserLicenseAssignment(7, 3), new UserLicenseAssignment(7, 3));
            Assert.AreEqual(new UserLicenseAssignment(7, 3).GetHashCode(), new UserLicenseAssignment(7, 3).GetHashCode());
            Assert.AreNotEqual(new UserLicenseAssignment(7, 3), new UserLicenseAssignment(3, 7));

            var set = new HashSet<UserLicenseAssignment> { new UserLicenseAssignment(7, 3) };
            Assert.IsFalse(set.Add(new UserLicenseAssignment(7, 3)), "Duplicate assignments must collapse - the table has a UNIQUE index on (license_type_id, user_id).");
        }

        #endregion

        #region End-to-end: the table is never observably incomplete

        /// <summary>
        /// End-to-end regression test for issue #392. Two users each hold a licence from a previous
        /// import. A second import then runs in which user B's seat moves to a different SKU - so the
        /// refresh genuinely has work to do - and we probe the database on a SEPARATE connection every
        /// time the importer asks Graph for a SKU's users, i.e. from inside the window the old code
        /// left the table wiped.
        ///
        /// Pre-fix this fails with 0 licences observed, because <c>ProcessSKUsForAllUsers</c> deleted
        /// every row before the first <c>LoadUsersBySku</c> call and only refilled afterwards.
        /// This test covers the historical up-front wipe; the write window itself is covered by
        /// <see cref="UserLicenseRefresh_TableStaysCompleteBetweenTheAddAndTheRemove"/>.
        /// </summary>
        [TestMethod]
        public async Task UserLicenseRefresh_LicencesRemainVisibleThroughoutTheImport()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var tick = DateTime.Now.Ticks;
            var userAUpn = $"licencewindowA{tick}@test.com";
            var userBUpn = $"licencewindowB{tick}@test.com";
            var userAId = Guid.NewGuid().ToString();
            var userBId = Guid.NewGuid().ToString();

            var skuAId = Guid.NewGuid();
            var skuBId = Guid.NewGuid();
            const string skuAPart = "ENTERPRISEPACK";
            const string skuBPart = "ENTERPRISEPREMIUM";
            const string licenceAName = "Office 365 E3";
            const string licenceBName = "Office 365 E5";

            await RemoveTestUsers(userAUpn, userBUpn);

            var fakeLoader = BuildLoader(
                new[] { (userAUpn, userAId), (userBUpn, userBId) },
                new[] { (skuAId, skuAPart, new[] { userAUpn }), (skuBId, skuBPart, new[] { userBUpn }) });

            // ---- Run 1: establish one licence each. ----
            await new UserMetadataUpdater(logger, config, fakeLoader).InsertAndUpdateDatabaseFromExternalUsers();

            int userADbId, userBDbId;
            using (var db = new AnalyticsEntitiesContext())
            {
                userADbId = await GetUserId(db, userAUpn);
                userBDbId = await GetUserId(db, userBUpn);
                Assert.AreEqual(1, await CountLookups(db, userADbId), "Run 1 should give user A exactly one licence.");
                Assert.AreEqual(1, await CountLookups(db, userBDbId), "Run 1 should give user B exactly one licence.");
            }

            // ---- Run 2: user B's seat moves from SKU B to SKU A, so the refresh really does write. ----
            fakeLoader.SetFakeState(
                null,
                new List<SubscribedSku>
                {
                    new SubscribedSku { SkuId = skuAId, SkuPartNumber = skuAPart },
                    new SubscribedSku { SkuId = skuBId, SkuPartNumber = skuBPart }
                },
                new Dictionary<Guid, List<SkuUser>>
                {
                    { skuAId, new List<SkuUser> { new SkuUser { UserPrincipalName = userAUpn, Id = userAId }, new SkuUser { UserPrincipalName = userBUpn, Id = userBId } } },
                    { skuBId, new List<SkuUser>() }
                });

            var observations = new List<(Guid Sku, int UserA, int UserB)>();
            fakeLoader.OnLoadUsersBySku = async skuId =>
            {
                // Deliberately a separate context (and therefore a separate SQL connection): this is
                // what a report querying the database during an import sees.
                using (var probe = new AnalyticsEntitiesContext())
                {
                    observations.Add((skuId,
                        await CountLookups(probe, userADbId),
                        await CountLookups(probe, userBDbId)));
                }
            };

            await new UserMetadataUpdater(logger, config, fakeLoader).InsertAndUpdateDatabaseFromExternalUsers();

            Assert.AreEqual(2, observations.Count, "Both SKUs should have been walked, giving two probes inside the refresh window.");
            foreach (var observed in observations)
            {
                Assert.IsTrue(observed.UserA >= 1,
                    "REGRESSION (issue #392): user A's licence disappeared from user_license_type_lookups while the import was running. " +
                    "The refresh must reconcile the table in place, never delete-then-refill, or every report joining it reports a tenant " +
                    $"with no licences for the duration. Observed {observed.UserA}.");
                Assert.IsTrue(observed.UserB >= 1,
                    $"REGRESSION (issue #392): user B's licence disappeared from user_license_type_lookups while the import was running. Observed {observed.UserB}.");
            }

            using (var db = new AnalyticsEntitiesContext())
            {
                var a = await LoadLicenceNames(db, userADbId);
                var b = await LoadLicenceNames(db, userBDbId);
                CollectionAssert.AreEquivalent(new[] { licenceAName }, a, "User A should still hold exactly their own licence after run 2.");
                CollectionAssert.AreEquivalent(new[] { licenceAName }, b, "User B should hold ONLY the licence they moved to after run 2.");
                Assert.IsFalse(b.Contains(licenceBName), "User B's old licence should have been removed.");
            }

            await RemoveTestUsers(userAUpn, userBUpn);
        }

        /// <summary>
        /// The strongest form of the issue #392 invariant: probe the database on a separate connection
        /// at every point the refresh writes - before the insert, after the insert has COMMITTED, and
        /// either side of the delete - while a user swaps one SKU for another. The user must never be
        /// observed holding zero licences.
        ///
        /// This is deliberately stronger than asserting the recorder's call order: it fails for any
        /// implementation that momentarily empties the table during its write phase, including one
        /// that simply reorders the delete before the insert.
        /// </summary>
        [TestMethod]
        public async Task UserLicenseRefresh_TableStaysCompleteBetweenTheAddAndTheRemove()
        {
            var tick = DateTime.Now.Ticks;
            var upn = $"licenceprobe{tick}@test.com";
            await RemoveTestUsers(upn);

            var oldSkuId = Guid.NewGuid();
            var newSkuId = Guid.NewGuid();
            var oldSkus = new List<SubscribedSku> { new SubscribedSku { SkuId = oldSkuId, SkuPartNumber = "ENTERPRISEPACK" } };
            var newSkus = new List<SubscribedSku> { new SubscribedSku { SkuId = newSkuId, SkuPartNumber = "ENTERPRISEPREMIUM" } };

            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    var dbUser = await InsertUser(db, upn);
                    var users = new List<Common.Entities.User> { dbUser };

                    // Establish the starting licence.
                    var oldLoader = new FakeUserMetadataLoader(null, oldSkus,
                        new Dictionary<Guid, List<SkuUser>> { { oldSkuId, new List<SkuUser> { new SkuUser { UserPrincipalName = upn } } } });
                    await new UserLicenseProcessor(AnalyticsLogger.ConsoleOnlyTracer(), oldLoader, new UserMetadataCache(db))
                        .ProcessSKUsForAllUsers(oldSkus, users, db);
                    Assert.AreEqual(1, await CountLookups(db, dbUser.ID), "Setup should have left the user with one licence.");

                    // Swap the SKU, probing on a separate connection around every write.
                    var probe = new ProbingUserLicenseStore(
                        new SqlUserLicenseStore(db, AnalyticsLogger.ConsoleOnlyTracer()), dbUser.ID);
                    var newLoader = new FakeUserMetadataLoader(null, newSkus,
                        new Dictionary<Guid, List<SkuUser>> { { newSkuId, new List<SkuUser> { new SkuUser { UserPrincipalName = upn } } } });

                    await new UserLicenseProcessor(AnalyticsLogger.ConsoleOnlyTracer(), newLoader, new UserMetadataCache(db), _ => probe)
                        .ProcessSKUsForAllUsers(newSkus, users, db);

                    Assert.IsTrue(probe.Observations.Count >= 4,
                        $"Expected the refresh to write (insert then delete) so the probe has something to watch; got: {probe.Describe()}");

                    foreach (var observation in probe.Observations)
                    {
                        Assert.IsTrue(observation.LicenceCount >= 1,
                            "REGRESSION (issue #392): a reader on another connection saw the user holding NO licences at " +
                            $"'{observation.Point}'. The refresh must never leave user_license_type_lookups incomplete for a " +
                            $"user who still holds a licence. Full trace: {probe.Describe()}");
                    }

                    var beforeRemove = probe.Observations.First(o => o.Point == "before-remove");
                    Assert.AreEqual(2, beforeRemove.LicenceCount,
                        "Between the insert and the delete the user should transiently hold BOTH licences. Seeing 1 here means the " +
                        "delete ran before the insert, which is the ordering that produces a visible licence gap. Full trace: " + probe.Describe());
                }

                using (var verify = new AnalyticsEntitiesContext())
                {
                    var names = await LoadLicenceNames(verify, await GetUserId(verify, upn));
                    CollectionAssert.AreEquivalent(new[] { "Office 365 E5" }, names, "Only the current licence should remain after the swap.");
                }
            }
            finally
            {
                await RemoveTestUsers(upn);
            }
        }

        /// <summary>
        /// Graph reporting zero tenant SKUs is far more likely to be a transient failure or a lost
        /// 'Organization.Read.All' consent than a tenant that genuinely holds no licences. Wiping the
        /// whole table on that signal would recreate issue #392 in its most damaging form, so the
        /// refresh must decline to reconcile.
        /// </summary>
        [TestMethod]
        public async Task UserLicenseRefresh_EmptyTenantSkuList_LeavesLicencesUntouched()
        {
            var tick = DateTime.Now.Ticks;
            var upn = $"licenceemptyskus{tick}@test.com";
            await RemoveTestUsers(upn);

            var skuId = Guid.NewGuid();
            var skus = new List<SubscribedSku> { new SubscribedSku { SkuId = skuId, SkuPartNumber = "ENTERPRISEPACK" } };

            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    var dbUser = await InsertUser(db, upn);
                    var users = new List<Common.Entities.User> { dbUser };

                    var loader = new FakeUserMetadataLoader(null, skus,
                        new Dictionary<Guid, List<SkuUser>> { { skuId, new List<SkuUser> { new SkuUser { UserPrincipalName = upn } } } });
                    await new UserLicenseProcessor(AnalyticsLogger.ConsoleOnlyTracer(), loader, new UserMetadataCache(db))
                        .ProcessSKUsForAllUsers(skus, users, db);
                    Assert.AreEqual(1, await CountLookups(db, dbUser.ID));

                    var emptyLoader = new FakeUserMetadataLoader(null, new List<SubscribedSku>(), new Dictionary<Guid, List<SkuUser>>());
                    await new UserLicenseProcessor(AnalyticsLogger.ConsoleOnlyTracer(), emptyLoader, new UserMetadataCache(db))
                        .ProcessSKUsForAllUsers(new List<SubscribedSku>(), users, db);

                    Assert.AreEqual(1, await CountLookups(db, dbUser.ID),
                        "An empty tenant SKU list must leave existing licences alone. Treating it as the authoritative 'nobody holds " +
                        "anything' would delete every licence row in the tenant on a transient Graph blip.");
                }
            }
            finally
            {
                await RemoveTestUsers(upn);
            }
        }

        /// <summary>
        /// A single SKU reporting zero holders while the tenant's own SKU record says seats are
        /// consumed is Graph contradicting itself. Believing the empty answer would delete every
        /// assignment for that licence type - issue #392 scoped to one SKU, which on a tenant whose
        /// Copilot seats all sit on one SKU is indistinguishable from the original outage.
        /// </summary>
        [TestMethod]
        public async Task UserLicenseRefresh_SkuClaimsConsumedSeatsButReportsNoHolders_RemovalsHeldBack()
        {
            var tick = DateTime.Now.Ticks;
            var upn = $"licencecontradiction{tick}@test.com";
            await RemoveTestUsers(upn);

            var skuId = Guid.NewGuid();
            const string skuPart = "ENTERPRISEPACK";

            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    var dbUser = await InsertUser(db, upn);
                    var users = new List<Common.Entities.User> { dbUser };

                    var licensedSkus = new List<SubscribedSku> { new SubscribedSku { SkuId = skuId, SkuPartNumber = skuPart, ConsumedUnits = 1 } };
                    var loader = new FakeUserMetadataLoader(null, licensedSkus,
                        new Dictionary<Guid, List<SkuUser>> { { skuId, new List<SkuUser> { new SkuUser { UserPrincipalName = upn } } } });
                    await new UserLicenseProcessor(AnalyticsLogger.ConsoleOnlyTracer(), loader, new UserMetadataCache(db))
                        .ProcessSKUsForAllUsers(licensedSkus, users, db);
                    Assert.AreEqual(1, await CountLookups(db, dbUser.ID), "Setup should have left the user licensed.");

                    // Graph now says 1 seat is consumed but returns nobody holding the SKU.
                    var contradictorySkus = new List<SubscribedSku> { new SubscribedSku { SkuId = skuId, SkuPartNumber = skuPart, ConsumedUnits = 1 } };
                    var contradictoryLoader = new FakeUserMetadataLoader(null, contradictorySkus, new Dictionary<Guid, List<SkuUser>>());
                    await new UserLicenseProcessor(AnalyticsLogger.ConsoleOnlyTracer(), contradictoryLoader, new UserMetadataCache(db))
                        .ProcessSKUsForAllUsers(contradictorySkus, users, db);

                    Assert.AreEqual(1, await CountLookups(db, dbUser.ID),
                        "A SKU that claims consumed seats but lists no holders must NOT have its assignments deleted - the two answers " +
                        "cannot both be right, and deleting is the unrecoverable one.");

                    // With ConsumedUnits back to 0 the empty list is credible, so the seat really goes.
                    var releasedSkus = new List<SubscribedSku> { new SubscribedSku { SkuId = skuId, SkuPartNumber = skuPart, ConsumedUnits = 0 } };
                    var releasedLoader = new FakeUserMetadataLoader(null, releasedSkus, new Dictionary<Guid, List<SkuUser>>());
                    await new UserLicenseProcessor(AnalyticsLogger.ConsoleOnlyTracer(), releasedLoader, new UserMetadataCache(db))
                        .ProcessSKUsForAllUsers(releasedSkus, users, db);

                    Assert.AreEqual(0, await CountLookups(db, dbUser.ID),
                        "When Graph consistently reports the SKU has no consumed seats and no holders, the assignment must be removed.");
                }
            }
            finally
            {
                await RemoveTestUsers(upn);
            }
        }

        /// <summary>
        /// Documents why the duplicate-UPN tie-break in <c>ProcessSKUsForAllUsers</c> cannot be
        /// exercised end-to-end: <c>dbo.users</c> carries a UNIQUE index on <c>user_name</c>
        /// (<c>IX_users</c>), so two rows can never share a user-principal-name. The tie-break stays
        /// as a determinism guarantee - "last one wins" over an unordered result set would otherwise
        /// let the reconciliation add and delete the same licence on alternating cycles - but this
        /// test pins the constraint that makes it unreachable today, so a future migration that
        /// relaxed it would be noticed here.
        /// </summary>
        [TestMethod]
        public async Task UserLicenseRefresh_DuplicateUpnsAreImpossible_DatabaseEnforcesUniqueUserName()
        {
            var upn = $"licencedupe{DateTime.Now.Ticks}@test.com";
            await RemoveTestUsers(upn);

            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    await InsertUser(db, upn);
                }

                using (var second = new AnalyticsEntitiesContext())
                {
                    await Assert.ThrowsExceptionAsync<System.Data.Entity.Infrastructure.DbUpdateException>(
                        async () => await InsertUser(second, upn),
                        "dbo.users must keep its UNIQUE index on user_name (IX_users). If this ever stops throwing, " +
                        "duplicate UPNs have become possible and the licence refresh's tie-break needs a real test.");
                }
            }
            finally
            {
                await RemoveTestUsers(upn);
            }
        }

        #endregion

        #region Processor-level: what actually gets written

        /// <summary>
        /// A run where nothing changed must not write anything at all. The old implementation
        /// rewrote every row on every cycle, which is both the source of the outage window and,
        /// at 200k-user scale, hundreds of thousands of pointless inserts per import.
        /// </summary>
        [TestMethod]
        public async Task UserLicenseRefresh_NothingChanged_WritesNothing()
        {
            var tick = DateTime.Now.Ticks;
            var upn = $"licencesteady{tick}@test.com";
            await RemoveTestUsers(upn);

            var skuId = Guid.NewGuid();
            var skus = new List<SubscribedSku> { new SubscribedSku { SkuId = skuId, SkuPartNumber = "ENTERPRISEPACK" } };

            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    var dbUser = await InsertUser(db, upn);
                    var loader = new FakeUserMetadataLoader(null, skus,
                        new Dictionary<Guid, List<SkuUser>> { { skuId, new List<SkuUser> { new SkuUser { UserPrincipalName = upn } } } });

                    var recorder = new RecordingUserLicenseStore(new SqlUserLicenseStore(db, AnalyticsLogger.ConsoleOnlyTracer()));
                    var processor = new UserLicenseProcessor(
                        AnalyticsLogger.ConsoleOnlyTracer(), loader, new UserMetadataCache(db), _ => recorder);

                    // First pass writes the one missing assignment.
                    await processor.ProcessSKUsForAllUsers(skus, new List<Common.Entities.User> { dbUser }, db);
                    Assert.AreEqual(1, recorder.Added.Count, "The first refresh should insert the missing assignment.");
                    Assert.AreEqual(0, recorder.Removed.Count);

                    // Second pass, same Graph answer: nothing to do.
                    recorder.Reset();
                    await processor.ProcessSKUsForAllUsers(skus, new List<Common.Entities.User> { dbUser }, db);
                    Assert.AreEqual(0, recorder.Added.Count, "An unchanged tenant must not re-insert assignments that are already correct.");
                    Assert.AreEqual(0, recorder.Removed.Count, "An unchanged tenant must not delete any assignment.");
                }
            }
            finally
            {
                await RemoveTestUsers(upn);
            }
        }

        /// <summary>
        /// A user swapping one SKU for another must never be momentarily unlicensed, so the insert
        /// has to happen before the delete.
        /// </summary>
        [TestMethod]
        public async Task UserLicenseRefresh_LicenceSwap_AddsBeforeItRemoves()
        {
            var tick = DateTime.Now.Ticks;
            var upn = $"licenceswap{tick}@test.com";
            await RemoveTestUsers(upn);

            var oldSkuId = Guid.NewGuid();
            var newSkuId = Guid.NewGuid();
            var oldSkus = new List<SubscribedSku> { new SubscribedSku { SkuId = oldSkuId, SkuPartNumber = "ENTERPRISEPACK" } };
            var newSkus = new List<SubscribedSku> { new SubscribedSku { SkuId = newSkuId, SkuPartNumber = "ENTERPRISEPREMIUM" } };

            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    var dbUser = await InsertUser(db, upn);
                    var users = new List<Common.Entities.User> { dbUser };

                    var oldLoader = new FakeUserMetadataLoader(null, oldSkus,
                        new Dictionary<Guid, List<SkuUser>> { { oldSkuId, new List<SkuUser> { new SkuUser { UserPrincipalName = upn } } } });
                    var recorder = new RecordingUserLicenseStore(new SqlUserLicenseStore(db, AnalyticsLogger.ConsoleOnlyTracer()));

                    await new UserLicenseProcessor(AnalyticsLogger.ConsoleOnlyTracer(), oldLoader, new UserMetadataCache(db), _ => recorder)
                        .ProcessSKUsForAllUsers(oldSkus, users, db);

                    // Now the same user holds a different SKU instead.
                    recorder.Reset();
                    var newLoader = new FakeUserMetadataLoader(null, newSkus,
                        new Dictionary<Guid, List<SkuUser>> { { newSkuId, new List<SkuUser> { new SkuUser { UserPrincipalName = upn } } } });

                    await new UserLicenseProcessor(AnalyticsLogger.ConsoleOnlyTracer(), newLoader, new UserMetadataCache(db), _ => recorder)
                        .ProcessSKUsForAllUsers(newSkus, users, db);

                    Assert.AreEqual(1, recorder.Added.Count, "The new licence should be inserted.");
                    Assert.AreEqual(1, recorder.Removed.Count, "The licence the user no longer holds should be deleted.");
                    CollectionAssert.AreEqual(
                        new[] { "add", "remove" }, recorder.Operations.ToArray(),
                        "Additions must be applied before removals so a SKU swap never leaves the user momentarily unlicensed.");
                }

                using (var verify = new AnalyticsEntitiesContext())
                {
                    var names = await LoadLicenceNames(verify, await GetUserId(verify, upn));
                    CollectionAssert.AreEquivalent(new[] { "Office 365 E5" }, names, "Only the current licence should remain after the swap.");
                }
            }
            finally
            {
                await RemoveTestUsers(upn);
            }
        }

        /// <summary>
        /// The refresh must only ever delete rows for users it was given. Anyone else's licences are
        /// none of its business - that is what makes a scoped (non-full-population) call safe.
        /// </summary>
        [TestMethod]
        public async Task UserLicenseRefresh_LeavesOutOfScopeUsersAlone()
        {
            var tick = DateTime.Now.Ticks;
            var inScopeUpn = $"licencescopein{tick}@test.com";
            var outOfScopeUpn = $"licencescopeout{tick}@test.com";
            await RemoveTestUsers(inScopeUpn, outOfScopeUpn);

            var skuId = Guid.NewGuid();
            var skus = new List<SubscribedSku> { new SubscribedSku { SkuId = skuId, SkuPartNumber = "ENTERPRISEPACK" } };

            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    var inScope = await InsertUser(db, inScopeUpn);
                    var outOfScope = await InsertUser(db, outOfScopeUpn);

                    var bothLicensedLoader = new FakeUserMetadataLoader(null, skus,
                        new Dictionary<Guid, List<SkuUser>>
                        {
                            { skuId, new List<SkuUser> { new SkuUser { UserPrincipalName = inScopeUpn }, new SkuUser { UserPrincipalName = outOfScopeUpn } } }
                        });

                    await new UserLicenseProcessor(AnalyticsLogger.ConsoleOnlyTracer(), bothLicensedLoader, new UserMetadataCache(db))
                        .ProcessSKUsForAllUsers(skus, new List<Common.Entities.User> { inScope, outOfScope }, db);

                    Assert.AreEqual(1, await CountLookups(db, inScope.ID));
                    Assert.AreEqual(1, await CountLookups(db, outOfScope.ID));

                    // Now refresh with Graph reporting nobody licensed, but only the in-scope user supplied.
                    var noneLicensedLoader = new FakeUserMetadataLoader(null, skus, new Dictionary<Guid, List<SkuUser>>());
                    await new UserLicenseProcessor(AnalyticsLogger.ConsoleOnlyTracer(), noneLicensedLoader, new UserMetadataCache(db))
                        .ProcessSKUsForAllUsers(skus, new List<Common.Entities.User> { inScope }, db);

                    Assert.AreEqual(0, await CountLookups(db, inScope.ID), "The in-scope user's licence should have been removed.");
                    Assert.AreEqual(1, await CountLookups(db, outOfScope.ID),
                        "A user outside the refresh scope must keep their licences - the refresh only owns the rows for the users it is given.");
                }
            }
            finally
            {
                await RemoveTestUsers(inScopeUpn, outOfScopeUpn);
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Decorator that queries the licence count for one user on a SEPARATE connection around every
        /// write the refresh performs, so a test can assert what a concurrent reader would have seen.
        /// </summary>
        private class ProbingUserLicenseStore : IUserLicenseStore
        {
            private readonly IUserLicenseStore _inner;
            private readonly int _watchedUserId;

            public ProbingUserLicenseStore(IUserLicenseStore inner, int watchedUserId)
            {
                _inner = inner;
                _watchedUserId = watchedUserId;
            }

            public List<(string Point, int LicenceCount)> Observations { get; } = new List<(string, int)>();

            public string Describe() => string.Join(", ", Observations.Select(o => $"{o.Point}={o.LicenceCount}"));

            private async Task Probe(string point)
            {
                using (var reader = new AnalyticsEntitiesContext())
                {
                    Observations.Add((point, await CountLookups(reader, _watchedUserId)));
                }
            }

            public Task<HashSet<UserLicenseAssignment>> LoadAssignmentsFor(ICollection<int> userIds)
                => _inner.LoadAssignmentsFor(userIds);

            public async Task<int> AddAssignments(IReadOnlyList<UserLicenseAssignment> assignments)
            {
                if (assignments == null || assignments.Count == 0)
                {
                    return await _inner.AddAssignments(assignments);
                }

                await Probe("before-add");
                var added = await _inner.AddAssignments(assignments);
                await Probe("after-add");
                return added;
            }

            public async Task<int> RemoveAssignments(IReadOnlyList<UserLicenseAssignment> assignments)
            {
                if (assignments == null || assignments.Count == 0)
                {
                    return await _inner.RemoveAssignments(assignments);
                }

                await Probe("before-remove");
                var removed = await _inner.RemoveAssignments(assignments);
                await Probe("after-remove");
                return removed;
            }
        }

        /// <summary>
        /// Decorator that records what the refresh asked the store to write, and in what order.
        /// </summary>
        private class RecordingUserLicenseStore : IUserLicenseStore
        {
            private readonly IUserLicenseStore _inner;

            public RecordingUserLicenseStore(IUserLicenseStore inner)
            {
                _inner = inner;
            }

            public List<UserLicenseAssignment> Added { get; } = new List<UserLicenseAssignment>();
            public List<UserLicenseAssignment> Removed { get; } = new List<UserLicenseAssignment>();

            /// <summary>"add" / "remove" in call order, recorded only for calls that had work to do.</summary>
            public List<string> Operations { get; } = new List<string>();

            public void Reset()
            {
                Added.Clear();
                Removed.Clear();
                Operations.Clear();
            }

            public Task<HashSet<UserLicenseAssignment>> LoadAssignmentsFor(ICollection<int> userIds)
                => _inner.LoadAssignmentsFor(userIds);

            public async Task<int> AddAssignments(IReadOnlyList<UserLicenseAssignment> assignments)
            {
                if (assignments != null && assignments.Count > 0)
                {
                    Added.AddRange(assignments);
                    Operations.Add("add");
                }
                return await _inner.AddAssignments(assignments);
            }

            public async Task<int> RemoveAssignments(IReadOnlyList<UserLicenseAssignment> assignments)
            {
                if (assignments != null && assignments.Count > 0)
                {
                    Removed.AddRange(assignments);
                    Operations.Add("remove");
                }
                return await _inner.RemoveAssignments(assignments);
            }
        }

        private static FakeUserMetadataLoader BuildLoader(
            (string Upn, string AadId)[] users,
            (Guid SkuId, string PartNumber, string[] LicensedUpns)[] skus)
        {
            var graphUsers = users
                .Select(u => new GraphUser { UserPrincipalName = u.Upn, Id = u.AadId, AccountEnabled = true, Mail = u.Upn })
                .ToList();

            var skuList = skus.Select(s => new SubscribedSku { SkuId = s.SkuId, SkuPartNumber = s.PartNumber }).ToList();

            var usersBySku = new Dictionary<Guid, List<SkuUser>>();
            foreach (var sku in skus)
            {
                usersBySku[sku.SkuId] = sku.LicensedUpns
                    .Select(upn => new SkuUser { UserPrincipalName = upn, Id = users.First(u => u.Upn == upn).AadId })
                    .ToList();
            }

            return new FakeUserMetadataLoader(graphUsers, skuList, usersBySku);
        }

        private static async Task<Common.Entities.User> InsertUser(AnalyticsEntitiesContext db, string upn)
        {
            var user = new Common.Entities.User { UserPrincipalName = upn, AccountEnabled = true };
            db.users.Add(user);
            await db.SaveChangesAsync();
            return user;
        }

        private static async Task<int> GetUserId(AnalyticsEntitiesContext db, string upn)
        {
            var user = await db.users.AsNoTracking().FirstOrDefaultAsync(u => u.UserPrincipalName == upn);
            Assert.IsNotNull(user, $"Expected test user '{upn}' to exist in the database.");
            return user.ID;
        }

        private static Task<int> CountLookups(AnalyticsEntitiesContext db, int userId)
            => db.UserLicenseTypeLookups.AsNoTracking().CountAsync(l => l.UserId == userId);

        private static async Task<string[]> LoadLicenceNames(AnalyticsEntitiesContext db, int userId)
        {
            var names = await db.UserLicenseTypeLookups.AsNoTracking()
                .Where(l => l.UserId == userId)
                .Select(l => l.License.Name)
                .ToListAsync();
            return names.ToArray();
        }

        private static async Task RemoveTestUsers(params string[] upns)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var users = await db.users.Where(u => upns.Contains(u.UserPrincipalName)).ToListAsync();
                if (users.Count == 0)
                {
                    return;
                }

                var ids = users.Select(u => u.ID).ToList();
                var lookups = await db.UserLicenseTypeLookups.Where(l => ids.Contains(l.UserId)).ToListAsync();
                db.UserLicenseTypeLookups.RemoveRange(lookups);
                db.users.RemoveRange(users);
                await db.SaveChangesAsync();
            }
        }

        #endregion
    }
}
