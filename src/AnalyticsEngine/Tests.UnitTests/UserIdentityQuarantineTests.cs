using Common.Entities;
using Common.Entities.Config;
using Common.Entities.State;
using Common.Entities.UserOrgs;
using DataUtils;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// C-1 / C-2 / C-3 / C-4: matching Graph identities to users rows without ever stopping the user import - the rename
    /// planner, the quarantine that carries unmatched users from cycle to cycle, and the manager-presence rule.
    /// </summary>
    [TestClass]
    public class UserIdentityQuarantineTests
    {
        private const string A = "00000000-0000-0000-0000-00000000000a";
        private const string B = "00000000-0000-0000-0000-00000000000b";
        private const string C = "00000000-0000-0000-0000-00000000000c";

        #region Plan

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        public void Plan_NewNameOnARowWithoutAnObjectId_IsAdoptedNotAConflict(string placeholderObjectId)
        {
            var plan = UserRenameProcessor.Plan(
                new[] { Graph(A, "new@contoso.com") },
                new[] { Row(1, "old@contoso.com", A), Row(2, "new@contoso.com", placeholderObjectId) });

            Assert.AreEqual(0, plan.Conflicts.Count);
            Assert.AreEqual(0, plan.Renames.Count, "The metadata update adopts the row by name.");
            Assert.AreEqual(1, plan.PlaceholdersAdopted);
        }

        [TestMethod]
        public void Plan_SeveralRowsCarryTheObjectId_RenamesTheNewest()
        {
            var plan = UserRenameProcessor.Plan(
                new[] { Graph(A, "current@contoso.com") },
                new[] { Row(9, "second@contoso.com", A), Row(4, "first@contoso.com", A) });

            var rename = plan.Renames.Single();
            Assert.AreEqual(9, rename.Row.ID);
            Assert.AreEqual("current@contoso.com", rename.NewUpn);
            CollectionAssert.AreEqual(new[] { 4, 9 }, rename.RowsWithObjectId.ToArray());
        }

        [TestMethod]
        public void Plan_RenameChain_RenamesTheUserWhoseNameIsFreedAfterTheOneWhoFreesIt()
        {
            // A: one -> two, B: two -> three, C: three -> four. Listed in the worst order.
            var plan = UserRenameProcessor.Plan(
                new[] { Graph(A, "two@contoso.com"), Graph(B, "three@contoso.com"), Graph(C, "four@contoso.com") },
                new[] { Row(1, "one@contoso.com", A), Row(2, "two@contoso.com", B), Row(3, "three@contoso.com", C) });

            Assert.AreEqual(0, plan.Conflicts.Count);
            Assert.AreEqual(0, plan.Renames.Single(r => r.ObjectId == C).Round);
            Assert.AreEqual(1, plan.Renames.Single(r => r.ObjectId == B).Round);
            Assert.AreEqual(2, plan.Renames.Single(r => r.ObjectId == A).Round);
        }

        [TestMethod]
        public void Plan_Swap_QuarantinesBoth_WithTheRowsInvolved()
        {
            var plan = UserRenameProcessor.Plan(
                new[] { Graph(A, "two@contoso.com"), Graph(B, "one@contoso.com") },
                new[] { Row(1, "one@contoso.com", A), Row(2, "two@contoso.com", B) });

            Assert.AreEqual(0, plan.Renames.Count);
            Assert.AreEqual(UserIdentityConflictKind.DestinationOtherObjectId, plan.Conflicts[A].Kind);
            Assert.AreEqual(2, plan.Conflicts[A].DestinationRowId);
            CollectionAssert.AreEqual(new[] { 1 }, plan.Conflicts[A].SourceRowIds.ToArray());
            Assert.AreEqual(1, plan.Conflicts[B].DestinationRowId);
        }

        [TestMethod]
        public void Plan_ConflictingGraphNames_AndOverLongNames_AreQuarantined_AndNobodyElse()
        {
            var plan = UserRenameProcessor.Plan(
                new[]
                {
                    Graph(A, "a1@contoso.com"), Graph(A, "a2@contoso.com"),
                    Graph(B, "shared@contoso.com"), Graph(C, "shared@contoso.com"),
                    Graph("00000000-0000-0000-0000-00000000000d", new string('d', 251) + "@contoso.com"),
                    Graph("00000000-0000-0000-0000-00000000000e", "e.new@contoso.com"),
                },
                new[] { Row(1, "a@contoso.com", A), Row(4, "d@contoso.com", "00000000-0000-0000-0000-00000000000d"), Row(5, "e@contoso.com", "00000000-0000-0000-0000-00000000000e") });

            Assert.AreEqual(UserIdentityConflictKind.ConflictingGraphNames, plan.Conflicts[A].Kind);
            Assert.AreEqual(UserIdentityConflictKind.ConflictingGraphNames, plan.Conflicts[B].Kind);
            Assert.AreEqual(UserIdentityConflictKind.ConflictingGraphNames, plan.Conflicts[C].Kind);
            Assert.AreEqual(UserIdentityConflictKind.UpnTooLong, plan.Conflicts["00000000-0000-0000-0000-00000000000d"].Kind);
            Assert.AreEqual("e.new@contoso.com", plan.Renames.Single().NewUpn, "Everyone else is renamed as usual.");
        }

        #endregion

        #region Manager presence (C-4)

        [TestMethod]
        public void ManagerAnnotation_ParsedFromGraph_DistinguishesAbsentFromRemoved()
        {
            const string user = "\"id\":\"" + A + "\",\"userPrincipalName\":\"a@contoso.com\",\"accountEnabled\":true";
            var absent = JsonConvert.DeserializeObject<GraphUser>("{" + user + "}");
            var empty = JsonConvert.DeserializeObject<GraphUser>("{" + user + ",\"manager@delta\":[]}");
            var set = JsonConvert.DeserializeObject<GraphUser>("{" + user + ",\"manager@delta\":[{\"@odata.type\":\"#microsoft.graph.user\",\"id\":\"" + B + "\"}]}");
            var removed = JsonConvert.DeserializeObject<GraphUser>("{" + user + ",\"manager@delta\":[{\"@odata.type\":\"#microsoft.graph.user\",\"id\":\"" + B + "\",\"@removed\":{\"reason\":\"deleted\"}}]}");

            Assert.IsFalse(absent.ManagerReported);
            Assert.IsTrue(empty.ManagerReported);
            Assert.IsNull(empty.DefaultManagerInfo);
            Assert.AreEqual(B, UserMetadataMappingRules.BuildPlan(set).ManagerAadId);
            Assert.IsTrue(removed.ManagerReported);
            Assert.IsNull(UserMetadataMappingRules.BuildPlan(removed).ManagerAadId, "A link reported as removed means no manager.");
        }

        [TestMethod]
        public void Classify_TheManagerComesFromTheLatestEntryThatReportedIt()
        {
            var managerChanged = new GraphUser { Id = A, UserPrincipalName = "a@contoso.com", AccountEnabled = true, ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = B } } };
            var thenDisabled = new GraphUser { Id = A, UserPrincipalName = "a@contoso.com", AccountEnabled = false };

            var incremental = UserDeltaReadRules.Classify(new List<GraphUser> { managerChanged, thenDisabled }).Deactivations.DisabledUsers.Single();
            Assert.IsTrue(incremental.ManagerReported, "The earlier entry's manager change is not lost by the later entry leaving it out.");
            Assert.AreEqual(B, incremental.DefaultManagerInfo.Id);
            Assert.IsFalse(thenDisabled.ManagerReported, "The entries themselves are not modified.");

            var unreported = UserDeltaReadRules.Classify(new List<GraphUser> { thenDisabled }).Deactivations.DisabledUsers.Single();
            Assert.IsFalse(unreported.ManagerReported);

            var fullRead = UserDeltaReadRules.Classify(new List<GraphUser> { thenDisabled }, completeDirectoryRead: true, initialRound: true).Deactivations.DisabledUsers.Single();
            Assert.IsTrue(fullRead.ManagerReported, "In the initial round no annotation means no manager.");
            Assert.IsNull(fullRead.DefaultManagerInfo);
        }

        #endregion

        #region Store and merge

        [TestMethod]
        public async Task Store_PagesRoundTrip_AndAShorterSetRemovesThePagesItNoLongerNeeds()
        {
            var keyValues = new InMemoryKeyValueStore();
            var store = new UserIdentityQuarantineStore(keyValues, Guid.Empty);
            var since = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var many = Enumerable.Range(0, UserIdentityQuarantineStore.MaxEntriesPerPage * 2 + 10)
                .Select(i => Entry(Guid.NewGuid().ToString(), since)).ToList();
            many[0].Profile.Department = "Πωλήσεις";
            many[0].Profile.AdditionalProperties["employeeType"] = "Συνεργάτης";
            many[0].Profile.ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = B } };

            await store.SaveAsync(many);
            var loaded = await store.LoadAsync();

            Assert.AreEqual(many.Count, loaded.Count);
            var first = loaded.Single(e => e.ObjectId == many[0].ObjectId);
            Assert.AreEqual("Πωλήσεις", first.Profile.Department);
            Assert.AreEqual("Συνεργάτης", (string)first.Profile.AdditionalProperties["employeeType"]);
            Assert.IsTrue(first.Profile.ManagerReported);
            Assert.AreEqual(B, first.Profile.DefaultManagerInfo.Id);
            Assert.AreEqual(since, first.SinceUtc);
            Assert.IsFalse(loaded.Single(e => e.ObjectId == many[1].ObjectId).Profile.ManagerReported, "An unreported manager stays unreported.");
            Assert.IsTrue(await keyValues.ExistsAsync(UserIdentityQuarantineStore.PageKey(Guid.Empty, 2)));

            await store.SaveAsync(many.Take(3).ToList());
            Assert.AreEqual(3, (await store.LoadAsync()).Count);
            Assert.IsFalse(await keyValues.ExistsAsync(UserIdentityQuarantineStore.PageKey(Guid.Empty, 1)));
            Assert.IsFalse(await keyValues.ExistsAsync(UserIdentityQuarantineStore.PageKey(Guid.Empty, 2)));

            await store.SaveAsync(new List<QuarantinedUserIdentity>());
            Assert.AreEqual(0, (await store.LoadAsync()).Count);
            Assert.IsFalse(await keyValues.ExistsAsync(UserIdentityQuarantineStore.HeaderKey(Guid.Empty)));
        }

        /// <summary>
        /// A save that fails after writing some of its pages must leave the stored set exactly as it was. Pages used to be
        /// overwritten in place, so the old header then read new pages beside old ones: users who moved to a later page were
        /// in neither, and a quarantined user whose checkpoint had already moved was lost.
        /// </summary>
        [TestMethod]
        public async Task Store_ASaveThatFailsPartWay_LeavesTheStoredSetWhole_AndTheNextSaveConverges()
        {
            var keyValues = new FaultyKeyValueStore();
            var store = new UserIdentityQuarantineStore(keyValues, Guid.Empty);
            var since = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var stored = Enumerable.Range(0, UserIdentityQuarantineStore.MaxEntriesPerPage + 50)
                .Select(i => Entry("a0000000-0000-0000-0000-" + i.ToString("D12"), since)).ToList();
            await store.SaveAsync(stored);

            // Ten users who sort first push every stored user along by ten, across the page boundary.
            var next = Enumerable.Range(0, 10).Select(i => Entry("00000000-0000-0000-0000-" + i.ToString("D12"), since)).Concat(stored).ToList();
            var pageWrites = 0;
            keyValues.FailSet = key => key != UserIdentityQuarantineStore.HeaderKey(Guid.Empty) && ++pageWrites == 2;

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => store.SaveAsync(next));

            CollectionAssert.AreEquivalent(stored.Select(e => e.ObjectId).ToList(), (await store.LoadAsync()).Select(e => e.ObjectId).ToList(),
                "The failed save changed nothing that is read.");

            // It failed after the header: the new set is read, and the next save tidies up after both.
            keyValues.FailSet = null;
            keyValues.FailDelete = _ => true;
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => store.SaveAsync(next));
            CollectionAssert.AreEquivalent(next.Select(e => e.ObjectId).ToList(), (await store.LoadAsync()).Select(e => e.ObjectId).ToList());

            keyValues.FailDelete = null;
            var smaller = next.Take(3).ToList();
            await store.SaveAsync(smaller);
            CollectionAssert.AreEquivalent(smaller.Select(e => e.ObjectId).ToList(), (await store.LoadAsync()).Select(e => e.ObjectId).ToList());
            Assert.AreEqual(2, keyValues.Keys.Count, "The header and the one page it points at; every page of the earlier sets is gone.");
        }

        /// <summary>A set stored by the first version of the store, whose header names no slot, still loads and is replaced cleanly.</summary>
        [TestMethod]
        public async Task Store_ASetWrittenBeforeSlots_StillLoads_AndIsReplacedCleanly()
        {
            var keyValues = new FaultyKeyValueStore();
            var since = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var legacy = Enumerable.Range(0, UserIdentityQuarantineStore.MaxEntriesPerPage + 1)
                .Select(i => Entry("a0000000-0000-0000-0000-" + i.ToString("D12"), since)).ToList();
            await keyValues.SetStringAsync(UserIdentityQuarantineStore.HeaderKey(Guid.Empty), "{\"pages\":2,\"count\":" + legacy.Count + "}");
            await keyValues.SetStringAsync("IdentityQuarantine-" + Guid.Empty.ToString("D") + ":0",
                JsonConvert.SerializeObject(legacy.Take(UserIdentityQuarantineStore.MaxEntriesPerPage).ToList(), UserIdentityQuarantineStore.Json));
            await keyValues.SetStringAsync("IdentityQuarantine-" + Guid.Empty.ToString("D") + ":1",
                JsonConvert.SerializeObject(legacy.Skip(UserIdentityQuarantineStore.MaxEntriesPerPage).ToList(), UserIdentityQuarantineStore.Json));
            var store = new UserIdentityQuarantineStore(keyValues, Guid.Empty);

            CollectionAssert.AreEquivalent(legacy.Select(e => e.ObjectId).ToList(), (await store.LoadAsync()).Select(e => e.ObjectId).ToList());

            await store.SaveAsync(legacy.Take(2).ToList());
            Assert.AreEqual(2, (await store.LoadAsync()).Count);
            Assert.AreEqual(2, keyValues.Keys.Count, "The header and the new page; the first version's pages are gone.");
        }

        [TestMethod]
        public void Merge_ANewerEntryReplacesTheStoredProfile_KeepingAManagerChangeItDoesNotRepeat()
        {
            var stored = Entry(A, DateTime.UtcNow);
            stored.Profile.ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = B } };
            var fresh = Graph(A, "a@contoso.com");
            fresh.Department = "Πωλήσεις";
            var active = new List<GraphUser> { fresh };

            var merge = UserIdentityQuarantineRules.Merge(new[] { stored }, active, new List<GraphUser>(), GraphUserDeactivations.None, u => true);

            Assert.AreEqual(1, active.Count);
            Assert.AreEqual("Πωλήσεις", active[0].Department, "The newer profile wins...");
            Assert.AreEqual(B, active[0].DefaultManagerInfo?.Id, "...but not over a manager change it does not repeat.");
            Assert.AreEqual(1, merge.Superseded);
            Assert.AreEqual(1, merge.Retried);
        }

        [TestMethod]
        public void Merge_DeletedOrOutOfScopeUsersAreDropped_AndAStaleNameIsKeptAside()
        {
            var deleted = Entry(A, DateTime.UtcNow);
            var outOfScope = Entry(B, DateTime.UtcNow);
            var staleName = Entry(C, DateTime.UtcNow);
            staleName.Profile.UserPrincipalName = "taken@contoso.com";
            var active = new List<GraphUser> { Graph("00000000-0000-0000-0000-00000000000f", "taken@contoso.com") };
            var deactivations = new GraphUserDeactivations(new GraphUser[0], new string[0], new[] { A });

            var merge = UserIdentityQuarantineRules.Merge(new[] { deleted, outOfScope, staleName }, active, new List<GraphUser>(), deactivations, u => u.Id != B);

            Assert.AreEqual(2, merge.Dropped);
            Assert.AreEqual(C, merge.KeptUnchanged.Single().ObjectId, "Someone in this read has the name now, so this profile is out of date.");
            Assert.AreEqual(1, active.Count, "Nothing stored is written this cycle.");
        }

        #endregion

        #region Through the whole import

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public async Task ReusedName_EnabledStaleDestination_RecoversFromQuarantineDuringEmptyDelta_WithoutMovingHistory(bool bulkMetadata, bool missingSource)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var oldId = Guid.NewGuid().ToString();
            var newId = Guid.NewGuid().ToString();
            var desired = "reused-" + suffix + "@contoso.com";
            var prior = "reused-" + suffix + "@contoso.onmicrosoft.com";
            int oldRow, newRow, historyRow;
            using (var db = new AnalyticsEntitiesContext())
            {
                var old = new User { UserPrincipalName = desired, AzureAdId = oldId, AccountEnabled = true, Mail = desired };
                var current = new User { UserPrincipalName = prior, AzureAdId = newId, AccountEnabled = true, Mail = desired };
                var history = new User { UserPrincipalName = "history-" + suffix + "@contoso.com", Manager = old };
                db.users.AddRange(missingSource ? new[] { old, history } : new[] { old, current, history });
                await db.SaveChangesAsync();
                oldRow = old.ID; newRow = current.ID; historyRow = history.ID;
            }
            var owner = Graph(newId, desired);
            owner.Department = "Contoso Sales";
            owner.CreatedDateTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var loader = new FakeUserMetadataLoader(new List<GraphUser>(), bulkMetadata ? new List<Microsoft.Graph.Models.SubscribedSku>() : null);
            await loader.DeltaValueProvider.SetDeltaToken("synthetic-existing-delta");
            loader.DeltaUsersOverride = new List<GraphUser> { owner };
            var store = new UserIdentityQuarantineStore(new InMemoryKeyValueStore(), Guid.Empty);
            UserMetadataUpdater Updater() => new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader) { IdentityQuarantineStore = store };

            Assert.IsTrue(await Updater().InsertAndUpdateDatabaseFromExternalUsers());
            Assert.AreEqual(1, (await store.LoadAsync()).Count);
            loader.DeltaUsersOverride = new List<GraphUser>();
            var calls = 0;
            loader.ReusedUpnVerification = (fresh, previous) =>
            {
                calls++;
                Assert.AreEqual(oldId, previous);
                Assert.AreEqual(newId, fresh.Id);
                return Task.FromResult(true); // Simulates the bounded fresh directory verification, not delta absence.
            };
            Assert.IsTrue(await Updater().InsertAndUpdateDatabaseFromExternalUsers());
            Assert.AreEqual(1, calls, "Only the conflicting pair is verified.");
            Assert.AreEqual(0, (await store.LoadAsync()).Count);
            using (var db = new AnalyticsEntitiesContext())
            {
                var current = await db.users.Include(u => u.Department).SingleAsync(u => u.AzureAdId == newId);
                var old = await db.users.SingleAsync(u => u.ID == oldRow);
                if (missingSource)
                {
                    Assert.AreNotEqual(oldRow, current.ID, "A new canonical row is created, never the history row.");
                    Assert.AreEqual(owner.CreatedDateTime, await db.Database.SqlQuery<DateTime?>(
                        "SELECT created_utc FROM dbo.users WHERE id = @p0", current.ID).SingleAsync());
                }
                else
                    Assert.AreEqual(newRow, current.ID, "The existing canonical PK is preserved.");
                Assert.AreEqual(desired, current.UserPrincipalName);
                Assert.AreEqual(newId, current.AzureAdId);
                Assert.AreEqual("Contoso Sales", current.Department?.Name);
                Assert.AreEqual(oldId, old.AzureAdId);
                Assert.AreEqual(false, old.AccountEnabled);
                Assert.AreEqual(UserRenameProcessor.HistoricalName(oldRow, Guid.Parse(oldId)), old.UserPrincipalName);
                Assert.AreEqual(oldRow, (await db.users.SingleAsync(u => u.ID == historyRow)).ManagerId, "Existing FK history is untouched.");
            }
            Assert.IsTrue(await Updater().InsertAndUpdateDatabaseFromExternalUsers(), "Idempotent after release.");
            Assert.AreEqual(1, calls);
        }

        [DataTestMethod]
        [DataRow(false, false, false)]
        [DataRow(true, false, false)]
        [DataRow(false, true, false)]
        [DataRow(false, false, true)]
        public async Task ReusedName_WithoutSource_WithoutFreshEvidence_NeverOverwritesOldObject(bool disabledOld, bool incompleteRead, bool filtered)
        {
            var name = "blocked-" + Guid.NewGuid().ToString("N") + "@contoso.com";
            var oldId = Guid.NewGuid().ToString();
            var newId = Guid.NewGuid().ToString();
            int oldRow;
            using (var db = new AnalyticsEntitiesContext())
            {
                var old = new User { UserPrincipalName = name, AzureAdId = oldId, AccountEnabled = !disabledOld, PostalCode = "12345" };
                db.users.Add(old);
                // Keep one active row so a test of a delta does not trigger the empty-database full-read reset.
                db.users.Add(new User { UserPrincipalName = "control-" + Guid.NewGuid().ToString("N") + "@contoso.com", AccountEnabled = true });
                await db.SaveChangesAsync(); oldRow = old.ID;
            }
            var loader = new FakeUserMetadataLoader(new List<GraphUser>()) { SimulateIncompleteDeltaRead = incompleteRead };
            await loader.DeltaValueProvider.SetDeltaToken("synthetic-delta");
            loader.DeltaUsersOverride = new List<GraphUser> { Graph(newId, name) };
            var calls = 0;
            loader.ReusedUpnVerification = (current, previous) => { calls++; return Task.FromResult(filtered); };
            var updater = new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader);
            if (filtered)
                updater.WithUserScope(TestUserScopes.Provider(TestUserScopes.OfMembers((newId, name, name))), lastRunStore: null);
            Assert.AreEqual(!incompleteRead, await updater.InsertAndUpdateDatabaseFromExternalUsers());
            Assert.AreEqual(incompleteRead || filtered ? 0 : 1, calls);
            using (var db = new AnalyticsEntitiesContext())
            {
                var old = await db.users.SingleAsync(u => u.ID == oldRow);
                Assert.AreEqual(oldId, old.AzureAdId);
                Assert.AreEqual(name, old.UserPrincipalName);
                Assert.AreEqual("12345", old.PostalCode);
                Assert.IsFalse(await db.users.AnyAsync(u => u.AzureAdId == newId));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task NewcomerInRenameChain_IsReleasedForInsertOnlyIfPredecessorActuallyRenamed(bool changedDuringRename)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var heldName = "chain-held-" + suffix + "@contoso.com";
            var freeName = "chain-free-" + suffix + "@contoso.com";
            var old = new User { UserPrincipalName = heldName, AzureAdId = Guid.NewGuid().ToString() };
            var newcomer = Graph(Guid.NewGuid().ToString(), heldName);
            using (var db = new AnalyticsEntitiesContext())
            {
                db.users.Add(old);
                await db.SaveChangesAsync();
                var result = await UserRenameProcessor.ReconcileAsync(db.Database.Connection.ConnectionString,
                    new[] { newcomer, Graph(old.AzureAdId, freeName) }, new List<User> { old },
                    Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, async () =>
                    {
                        if (changedDuringRename)
                            await db.Database.ExecuteSqlCommandAsync("UPDATE dbo.users SET azure_ad_id = @p0 WHERE id = @p1", Guid.NewGuid().ToString(), old.ID);
                    });
                Assert.AreEqual(changedDuringRename, result.Conflicts.ContainsKey(newcomer.Id));
                Assert.AreEqual(changedDuringRename ? heldName : freeName,
                    (await db.users.AsNoTracking().SingleAsync(u => u.ID == old.ID)).UserPrincipalName);
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Release_ConcurrentMutationOfEitherRow_LeavesBothNamesUnchanged(bool mutateSource)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var desired = "atomic-" + suffix + "@contoso.com";
            var owner = Graph(Guid.NewGuid().ToString(), desired);
            var source = new User { UserPrincipalName = "atomic-old-" + suffix + "@contoso.com", AzureAdId = owner.Id, AccountEnabled = true };
            var destination = new User { UserPrincipalName = desired, AzureAdId = Guid.NewGuid().ToString(), AccountEnabled = true };
            string connectionString;
            using (var db = new AnalyticsEntitiesContext())
            {
                db.users.AddRange(new[] { source, destination });
                await db.SaveChangesAsync();
                connectionString = db.Database.Connection.ConnectionString;
            }
            var historical = UserRenameProcessor.HistoricalName(destination.ID, Guid.Parse(destination.AzureAdId));
            using (var competing = new Microsoft.Data.SqlClient.SqlConnection(connectionString))
            {
                await competing.OpenAsync();
                using (var transaction = competing.BeginTransaction())
                {
                    using (var update = competing.CreateCommand())
                    {
                        update.Transaction = transaction;
                        update.CommandText = "UPDATE dbo.users SET last_updated = GETUTCDATE() WHERE id = @id";
                        update.Parameters.AddWithValue("@id", mutateSource ? source.ID : destination.ID);
                        await update.ExecuteNonQueryAsync();
                    }
                    var release = UserRenameProcessor.ReleaseAndAssignAsync(connectionString, source, destination, owner, historical);
                    Assert.AreNotSame(release, await Task.WhenAny(release, Task.Delay(200)), "The reconciliation must wait for the other writer.");
                    transaction.Commit();
                    Assert.IsNull(await release, "The changed snapshot must be rejected after the writer commits.");
                }
            }
            using (var db = new AnalyticsEntitiesContext())
            {
                Assert.AreEqual(desired, (await db.users.SingleAsync(u => u.ID == destination.ID)).UserPrincipalName);
                Assert.AreEqual(source.UserPrincipalName, (await db.users.SingleAsync(u => u.ID == source.ID)).UserPrincipalName);
                Assert.IsFalse(await db.users.AnyAsync(u => u.UserPrincipalName == historical));
            }
        }

        [TestMethod]
        public async Task Release_AssignmentFailsAfterRetirement_TransactionRollsBackRetirement()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var owner = Graph(Guid.NewGuid().ToString(), "rollback-" + suffix + "@contoso.com");
            var old = new User { UserPrincipalName = owner.UserPrincipalName, AzureAdId = Guid.NewGuid().ToString(), AccountEnabled = true };
            using (var db = new AnalyticsEntitiesContext())
            {
                db.users.Add(old);
                await db.SaveChangesAsync();
                var historical = UserRenameProcessor.HistoricalName(old.ID, Guid.Parse(old.AzureAdId));
                var trigger = "reused_upn_test_" + suffix;
                await db.Database.ExecuteSqlCommandAsync("CREATE TRIGGER dbo.[" + trigger + @"] ON dbo.users AFTER INSERT AS
BEGIN
IF EXISTS (SELECT 1 FROM inserted WHERE azure_ad_id = '" + owner.Id + @"')
    THROW 51000, 'Synthetic assignment failure', 1;
END");
                try
                {
                    await Assert.ThrowsExceptionAsync<Microsoft.Data.SqlClient.SqlException>(() =>
                        UserRenameProcessor.ReleaseAndAssignAsync(db.Database.Connection.ConnectionString, null, old, owner, historical));
                    var actual = await db.users.AsNoTracking().SingleAsync(u => u.ID == old.ID);
                    Assert.AreEqual(owner.UserPrincipalName, actual.UserPrincipalName);
                    Assert.AreEqual(true, actual.AccountEnabled);
                    Assert.AreEqual(old.AzureAdId, actual.AzureAdId);
                    Assert.IsFalse(await db.users.AnyAsync(u => u.AzureAdId == owner.Id));
                }
                finally { await db.Database.ExecuteSqlCommandAsync("DROP TRIGGER dbo.[" + trigger + "]"); }
            }
        }

        [TestMethod]
        public async Task Release_ConcurrentPlaceholderCannotTakeNameBetweenRetirementAndAssignment()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var owner = Graph(Guid.NewGuid().ToString(), "placeholder-race-" + suffix + "@contoso.com");
            var old = new User { UserPrincipalName = owner.UserPrincipalName, AzureAdId = Guid.NewGuid().ToString(), AccountEnabled = true };
            using (var db = new AnalyticsEntitiesContext())
            {
                db.users.Add(old);
                await db.SaveChangesAsync();
                var trigger = "reused_upn_pause_" + suffix;
                var signal = "reused-upn-test-" + suffix;
                await db.Database.ExecuteSqlCommandAsync("CREATE TRIGGER dbo.[" + trigger + @"] ON dbo.users AFTER UPDATE AS
BEGIN
IF EXISTS (SELECT 1 FROM inserted WHERE id = " + old.ID + @" AND user_name LIKE 'retired:%')
BEGIN
    EXEC sp_getapplock @Resource = '" + signal + @"', @LockMode = 'Exclusive', @LockOwner = 'Transaction';
    WAITFOR DELAY '00:00:02';
END
END");
                try
                {
                    var release = UserRenameProcessor.ReleaseAndAssignAsync(db.Database.Connection.ConnectionString, null, old, owner,
                        UserRenameProcessor.HistoricalName(old.ID, Guid.Parse(old.AzureAdId)));
                    var sawRetirement = false;
                    for (var attempt = 0; attempt < 100; attempt++)
                    {
                        if (await db.Database.SqlQuery<int>("SELECT APPLOCK_TEST('public', @p0, 'Exclusive', 'Session')", signal).SingleAsync() == 0)
                        {
                            sawRetirement = true;
                            break;
                        }
                        await Task.Delay(20);
                    }
                    Assert.IsTrue(sawRetirement, "The transaction is paused after retirement, before assignment.");
                    using (var competitor = new Microsoft.Data.SqlClient.SqlConnection(db.Database.Connection.ConnectionString))
                    {
                        await competitor.OpenAsync();
                        using (var insert = competitor.CreateCommand())
                        {
                            insert.CommandText = "INSERT dbo.users (user_name) VALUES (@name)";
                            insert.Parameters.AddWithValue("@name", owner.UserPrincipalName);
                            var placeholder = insert.ExecuteNonQueryAsync();
                            Assert.AreNotSame(placeholder, await Task.WhenAny(placeholder, Task.Delay(100)),
                                "The unique-name range remains locked during the apparent release.");
                            Assert.IsNotNull(await release);
                            var error = await Assert.ThrowsExceptionAsync<Microsoft.Data.SqlClient.SqlException>(async () => await placeholder);
                            Assert.IsTrue(error.Number == 2601 || error.Number == 2627);
                        }
                    }
                    Assert.AreEqual(owner.Id, (await db.users.AsNoTracking().SingleAsync(u => u.UserPrincipalName == owner.UserPrincipalName)).AzureAdId);
                }
                finally { await db.Database.ExecuteSqlCommandAsync("DROP TRIGGER dbo.[" + trigger + "]"); }
            }
        }

        [TestMethod]
        public async Task Release_HistoricalNameAlreadyOccupied_DoesNotOverwriteEitherIdentity()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var owner = Graph(Guid.NewGuid().ToString(), "collision-" + suffix + "@contoso.com");
            var source = new User { UserPrincipalName = "prior-" + suffix + "@contoso.com", AzureAdId = owner.Id };
            var old = new User { UserPrincipalName = owner.UserPrincipalName, AzureAdId = Guid.NewGuid().ToString(), AccountEnabled = true };
            using (var db = new AnalyticsEntitiesContext())
            {
                db.users.AddRange(new[] { old, source });
                await db.SaveChangesAsync();
                var historical = UserRenameProcessor.HistoricalName(old.ID, Guid.Parse(old.AzureAdId));
                var occupied = new User { UserPrincipalName = historical, AzureAdId = Guid.NewGuid().ToString() };
                db.users.Add(occupied);
                await db.SaveChangesAsync();
                Assert.IsNull(await UserRenameProcessor.ReleaseAndAssignAsync(db.Database.Connection.ConnectionString, source, old, owner, historical));
                Assert.AreEqual(owner.UserPrincipalName, (await db.users.AsNoTracking().SingleAsync(u => u.ID == old.ID)).UserPrincipalName);
                Assert.AreEqual(occupied.AzureAdId, (await db.users.AsNoTracking().SingleAsync(u => u.ID == occupied.ID)).AzureAdId);
                Assert.AreEqual(source.UserPrincipalName, (await db.users.AsNoTracking().SingleAsync(u => u.ID == source.ID)).UserPrincipalName);
            }
        }

        [TestMethod]
        public async Task BulkMetadata_PlaceholderAcquiredAnotherObjectAfterSnapshot_DoesNotOverwriteItsIdentityOrMetadata()
        {
            var name = "bulk-guard-" + Guid.NewGuid().ToString("N") + "@contoso.com";
            var owner = Graph(Guid.NewGuid().ToString(), name);
            owner.PostalCode = "54321";
            using (var db = new AnalyticsEntitiesContext())
            {
                var row = new User { UserPrincipalName = name, PostalCode = "12345" };
                db.users.Add(row);
                await db.SaveChangesAsync();
                var table = UserBulkUpdateRules.BuildUpdateTable(new List<GraphUser> { owner }, new LookupEntityMaps(),
                    new Dictionary<string, User>(), new Dictionary<string, User> { [name] = row },
                    new Dictionary<string, GraphUser>(), DateTime.UtcNow);
                var otherId = Guid.NewGuid().ToString();
                await db.Database.ExecuteSqlCommandAsync("UPDATE dbo.users SET azure_ad_id = @p0 WHERE id = @p1", otherId, row.ID);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    new SqlUserBulkUpdateWriter(db.Database.Connection.ConnectionString).ExecuteAsync(table));
                var actual = await db.users.AsNoTracking().SingleAsync(u => u.ID == row.ID);
                Assert.AreEqual(otherId, actual.AzureAdId);
                Assert.AreEqual("12345", actual.PostalCode);
            }
        }

        [TestMethod]
        public async Task BulkMetadata_IdentityChangesAfterSnapshot_AbortsBeforeLicencesOrOrgsAndWithholdsCheckpoint()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var name = "bulk-checkpoint-" + suffix + "@contoso.com";
            var controlName = "bulk-control-" + suffix + "@contoso.com";
            var owner = Graph(Guid.NewGuid().ToString(), name);
            owner.PostalCode = "54321";
            owner.AdditionalProperties["employeeType"] = "Contoso New";
            var control = Graph(Guid.NewGuid().ToString(), controlName);
            control.PostalCode = "54321";
            var otherId = Guid.NewGuid().ToString();
            var skuId = Guid.NewGuid();
            int rowId, controlId, existingLicenceId;
            string connectionString;
            using (var db = new AnalyticsEntitiesContext())
            {
                var row = new User { UserPrincipalName = name, PostalCode = "12345", AccountEnabled = true };
                var normal = new User { UserPrincipalName = controlName, AzureAdId = control.Id, PostalCode = "12345", AccountEnabled = true };
                var licence = new LicenseType { Name = "Contoso Prior " + suffix, SKUID = Guid.NewGuid().ToString() };
                db.users.AddRange(new[] { row, normal });
                db.LicenseTypes.Add(licence);
                await db.SaveChangesAsync();
                db.UserLicenseTypeLookups.Add(new UserLicenseTypeLookup { UserId = row.ID, LicenseTypeId = licence.ID });
                await db.SaveChangesAsync();
                rowId = row.ID; controlId = normal.ID; existingLicenceId = licence.ID;
                connectionString = db.Database.Connection.ConnectionString;
            }
            var types = UserOrgStores.CreateTypeStore(connectionString);
            var assignments = UserOrgStores.CreateAssignmentStore(connectionString);
            var typeId = await types.CreateAsync(new UserOrgType
            {
                Name = "Contoso Guard " + suffix, SourceKind = UserOrgSourceKind.EntraAttribute,
                EntraAttributeName = "employeeType", IsEnabled = true,
            });
            try
            {
                await assignments.MergeAsync(new[] { new UserOrgAssignmentUpdate(rowId, typeId, "Contoso Prior") });
                var loader = new FakeUserMetadataLoader(new List<GraphUser> { owner, control },
                    new List<Microsoft.Graph.Models.SubscribedSku>
                    {
                        new Microsoft.Graph.Models.SubscribedSku { SkuId = skuId, SkuPartNumber = "CONTOSO_NEW" },
                    },
                    new Dictionary<Guid, List<Microsoft.Graph.Models.User>>
                    {
                        [skuId] = new List<Microsoft.Graph.Models.User> { new Microsoft.Graph.Models.User { Id = owner.Id, UserPrincipalName = name } },
                    });
                loader.SetOrgSelection(GraphUserOrgSelection.FromTypes(await types.GetEnabledEntraTypesAsync()));
                await loader.DeltaValueProvider.SetDeltaToken("synthetic-original-checkpoint");
                var licenceReads = 0;
                loader.OnLoadUsersBySku = _ => { licenceReads++; return Task.CompletedTask; };
                var mutations = 0;
                var factory = new BeforeSaveContextFactory(async () =>
                {
                    mutations++;
                    using (var competing = new AnalyticsEntitiesContext())
                        await competing.Database.ExecuteSqlCommandAsync("UPDATE dbo.users SET azure_ad_id = @p0 WHERE id = @p1", otherId, rowId);
                });
                var updater = new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader,
                    factory, orgTypeStore: types, orgAssignmentStore: assignments);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => updater.InsertAndUpdateDatabaseFromExternalUsers());
                Assert.AreEqual(1, mutations, "The competing identity is stamped after the snapshot, during lookup-cache save.");
                Assert.AreEqual("synthetic-original-checkpoint", await loader.DeltaValueProvider.GetDeltaToken());
                Assert.AreEqual(0, licenceReads, "The stale mapping must never reach the licence refresh.");
                Assert.AreEqual("Contoso Prior", (await assignments.GetForUserAsync(rowId)).Single(v => v.OrgTypeId == typeId).Value);
                using (var db = new AnalyticsEntitiesContext())
                {
                    var actual = await db.users.SingleAsync(u => u.ID == rowId);
                    Assert.AreEqual(otherId, actual.AzureAdId);
                    Assert.AreEqual("12345", actual.PostalCode);
                    Assert.AreEqual("12345", (await db.users.SingleAsync(u => u.ID == controlId)).PostalCode,
                        "A matching row in the same batch is rolled back, not half committed.");
                    CollectionAssert.AreEqual(new[] { existingLicenceId },
                        (await db.UserLicenseTypeLookups.Where(l => l.UserId == rowId).Select(l => l.LicenseTypeId).ToListAsync()).ToArray());
                }
            }
            finally { await types.DeleteAsync(typeId); }
        }

        /// <summary>
        /// C-1: a quarantined user is retried every cycle from the stored profile - even when Graph reports nothing new -
        /// and resolved once the other account's row moves out of the way, here by that account's own rename. Their
        /// stored changes are then written. The store is rewritten only when the set changes.
        /// </summary>
        [TestMethod]
        public async Task QuarantinedUser_IsRetriedEveryCycle_AndWrittenOnceTheConflictClears()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var aId = Guid.NewGuid().ToString();
            var bId = Guid.NewGuid().ToString();
            var aOld = "q-a-old-" + suffix + "@contoso.com";
            var taken = "q-taken-" + suffix + "@contoso.com";
            var bNew = "q-b-new-" + suffix + "@contoso.com";
            int aRow, bRow;
            using (var db = new AnalyticsEntitiesContext())
            {
                var a = new User { UserPrincipalName = aOld, AzureAdId = aId, AccountEnabled = true };
                var b = new User { UserPrincipalName = taken, AzureAdId = bId, AccountEnabled = true };
                db.users.Add(a);
                db.users.Add(b);
                await db.SaveChangesAsync();
                aRow = a.ID;
                bRow = b.ID;
            }

            var keyValues = new CountingKeyValueStore();
            var store = new UserIdentityQuarantineStore(keyValues, Guid.Empty);
            var clock = new FixedClock(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
            var loader = new FakeUserMetadataLoader(new List<GraphUser>());
            await loader.DeltaValueProvider.SetDeltaToken("an-earlier-checkpoint");
            UserMetadataUpdater NewUpdater() => new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader, clock) { IdentityQuarantineStore = store };

            // Cycle 1: A is renamed to a name B's row still holds.
            loader.DeltaUsersOverride = new List<GraphUser> { new GraphUser { Id = aId, UserPrincipalName = taken, AccountEnabled = true, Mail = taken, Department = "Πωλήσεις" } };
            loader.SimulatedNewDeltaToken = "checkpoint-1";
            Assert.IsTrue(await NewUpdater().InsertAndUpdateDatabaseFromExternalUsers(), "Cycle 1");
            Assert.AreEqual("checkpoint-1", await loader.DeltaValueProvider.GetDeltaToken());
            var stored = (await store.LoadAsync()).Single();
            Assert.AreEqual(aId, stored.ObjectId);
            Assert.AreEqual(UserIdentityConflictKind.DestinationOtherObjectId, stored.Kind);
            Assert.AreEqual(clock.UtcNow, stored.SinceUtc);
            var writes = keyValues.SetCalls;

            // Cycle 2: Graph reports nothing new. A is checked again from the store, is still in conflict, and the store is
            // not rewritten.
            clock.UtcNow = clock.UtcNow.AddHours(6);
            loader.DeltaUsersOverride = new List<GraphUser>();
            loader.SimulatedNewDeltaToken = "checkpoint-2";
            Assert.IsTrue(await NewUpdater().InsertAndUpdateDatabaseFromExternalUsers(), "Cycle 2");
            Assert.AreEqual(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), (await store.LoadAsync()).Single().SinceUtc, "Still quarantined since cycle 1.");
            Assert.AreEqual(writes, keyValues.SetCalls, "An unchanged set is not rewritten.");
            using (var db = new AnalyticsEntitiesContext())
            {
                Assert.AreEqual(aOld, (await db.users.SingleAsync(u => u.ID == aRow)).UserPrincipalName);
            }

            // Cycle 3: B moves to a new name; only B is in the read. A's stored profile now goes through.
            loader.DeltaUsersOverride = new List<GraphUser> { new GraphUser { Id = bId, UserPrincipalName = bNew, AccountEnabled = true, Mail = bNew } };
            loader.SimulatedNewDeltaToken = "checkpoint-3";
            Assert.IsTrue(await NewUpdater().InsertAndUpdateDatabaseFromExternalUsers(), "Cycle 3");
            Assert.AreEqual(0, (await store.LoadAsync()).Count, "Resolved, so no longer stored.");
            using (var db = new AnalyticsEntitiesContext())
            {
                var a = await db.users.Include(u => u.Department).SingleAsync(u => u.ID == aRow);
                Assert.AreEqual(taken, a.UserPrincipalName, "Renamed in place on the row they always had.");
                Assert.AreEqual(taken, a.Mail);
                Assert.AreEqual("Πωλήσεις", a.Department?.Name, "The change Graph reported back in cycle 1 is written.");
                Assert.AreEqual(bNew, (await db.users.SingleAsync(u => u.ID == bRow)).UserPrincipalName);
            }
        }

        /// <summary>
        /// The checkpoint only moves once the quarantined user is safely stored: otherwise Graph would stop reporting a
        /// change that was never written. Everyone else is still imported.
        /// </summary>
        [DataTestMethod]
        [DataRow(true, DisplayName = "The stored set cannot be read")]
        [DataRow(false, DisplayName = "The new set cannot be saved")]
        public async Task QuarantineStoreUnavailable_CheckpointDoesNotMove_ButEveryoneElseIsImported(bool failLoad)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var aId = Guid.NewGuid().ToString();
            var aOld = "qs-a-old-" + suffix + "@contoso.com";
            var taken = "qs-taken-" + suffix + "@contoso.com";
            var newcomer = "qs-newcomer-" + suffix + "@contoso.com";
            using (var db = new AnalyticsEntitiesContext())
            {
                db.users.Add(new User { UserPrincipalName = aOld, AzureAdId = aId, AccountEnabled = true });
                db.users.Add(new User { UserPrincipalName = taken, AzureAdId = Guid.NewGuid().ToString(), AccountEnabled = true });
                await db.SaveChangesAsync();
            }
            var loader = new FakeUserMetadataLoader(new List<GraphUser>());
            await loader.DeltaValueProvider.SetDeltaToken("an-earlier-checkpoint");
            loader.DeltaUsersOverride = new List<GraphUser>
            {
                new GraphUser { Id = aId, UserPrincipalName = taken, AccountEnabled = true },
                new GraphUser { Id = Guid.NewGuid().ToString(), UserPrincipalName = newcomer, AccountEnabled = true },
            };
            var updater = new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader)
            {
                IdentityQuarantineStore = new FailingQuarantineStore { FailLoad = failLoad, FailSave = !failLoad },
            };

            Assert.IsFalse(await updater.InsertAndUpdateDatabaseFromExternalUsers());
            Assert.AreEqual("an-earlier-checkpoint", await loader.DeltaValueProvider.GetDeltaToken());
            using (var db = new AnalyticsEntitiesContext())
            {
                Assert.IsTrue(await db.users.AnyAsync(u => u.UserPrincipalName == newcomer));
            }
        }

        /// <summary>
        /// C-2: the conflict line names every kind and the first conflicts by object id and row ids, so an operator can
        /// find the rows - and never a sign-in name. The per-cycle reconciliation and quarantine lines are written too.
        /// </summary>
        [TestMethod]
        public async Task ConflictLogging_NamesKindsAndIds_NeverSignInNames()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var aId = Guid.NewGuid().ToString();
            var aOld = "log-a-old-" + suffix + "@contoso.com";
            var taken = "log-taken-" + suffix + "@contoso.com";
            int aRow, takenRow;
            using (var db = new AnalyticsEntitiesContext())
            {
                var a = new User { UserPrincipalName = aOld, AzureAdId = aId, AccountEnabled = true };
                var holder = new User { UserPrincipalName = taken, AzureAdId = Guid.NewGuid().ToString(), AccountEnabled = true };
                db.users.Add(a);
                db.users.Add(holder);
                await db.SaveChangesAsync();
                aRow = a.ID;
                takenRow = holder.ID;
            }

            var channel = new RecordingTelemetryChannel();
            using (var configuration = new TelemetryConfiguration { TelemetryChannel = channel, ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001" })
            {
                var logger = new AnalyticsLogger(new TelemetryClient(configuration), "UserImportTest");
                var loader = new FakeUserMetadataLoader(new List<GraphUser> { new GraphUser { Id = aId, UserPrincipalName = taken, AccountEnabled = true } });
                Assert.IsTrue(await new UserMetadataUpdater(logger, new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers());
            }

            var traces = channel.Sent.OfType<TraceTelemetry>().ToList();
            var conflicts = traces.Single(t => t.Message.Contains("User import - identity conflicts:"));
            Assert.AreEqual(SeverityLevel.Warning, conflicts.SeverityLevel);
            StringAssert.Contains(conflicts.Message, "destinationOtherObjectId=1");
            StringAssert.Contains(conflicts.Message, "conflictingGraphNames=0");
            StringAssert.Contains(conflicts.Message, $"{aId} DestinationOtherObjectId source row(s) [{aRow}] destination row {takenRow}");

            var reconciliation = traces.Single(t => t.Message.Contains("User import - identity reconciliation:"));
            StringAssert.Contains(reconciliation.Message, "1 quarantined");
            StringAssert.Contains(reconciliation.Message, " ms.");
            var quarantine = traces.Single(t => t.Message.Contains("User import - identity quarantine:"));
            StringAssert.Contains(quarantine.Message, "1 user(s) quarantined, oldest since");

            foreach (var trace in traces.Where(t => t.Message.Contains("identity")))
            {
                Assert.IsFalse(trace.Message.Contains(taken) || trace.Message.Contains(aOld), "No sign-in names: " + trace.Message);
            }
        }

        #endregion

        #region Helpers

        private sealed class BeforeSaveContextFactory : IAnalyticsDbContextFactory
        {
            private readonly Func<Task> _beforeSave;
            public BeforeSaveContextFactory(Func<Task> beforeSave) { _beforeSave = beforeSave; }
            public AnalyticsEntitiesContext Create() => new BeforeSaveContext(_beforeSave);
        }

        private sealed class BeforeSaveContext : AnalyticsEntitiesContext
        {
            private Func<Task> _beforeSave;
            public BeforeSaveContext(Func<Task> beforeSave)
            {
                Database.SetInitializer<BeforeSaveContext>(null);
                _beforeSave = beforeSave;
            }
            public override async Task<int> SaveChangesAsync()
            {
                if (_beforeSave != null)
                {
                    var callback = _beforeSave;
                    _beforeSave = null;
                    await callback();
                }
                return await base.SaveChangesAsync();
            }
        }

        private static GraphUser Graph(string id, string upn) => new GraphUser { Id = id, UserPrincipalName = upn, AccountEnabled = true };

        private static User Row(int id, string upn, string objectId) => new User { ID = id, UserPrincipalName = upn, AzureAdId = objectId };

        private static QuarantinedUserIdentity Entry(string objectId, DateTime since) => new QuarantinedUserIdentity
        {
            ObjectId = objectId,
            Kind = UserIdentityConflictKind.DestinationOtherObjectId,
            SinceUtc = since,
            Profile = new GraphUser { Id = objectId, UserPrincipalName = objectId + "@contoso.com", AccountEnabled = true },
        };

        private sealed class FixedClock : IClock
        {
            public FixedClock(DateTime utcNow) { UtcNow = utcNow; }
            public DateTime UtcNow { get; set; }
        }

        private sealed class CountingKeyValueStore : IKeyValueStore
        {
            private readonly InMemoryKeyValueStore _inner = new InMemoryKeyValueStore();
            public int SetCalls { get; private set; }
            public string Description => "counting in-memory store";
            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default) => _inner.GetStringAsync(key, cancellationToken);
            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
            {
                SetCalls++;
                return _inner.SetStringAsync(key, value, timeToLive, cancellationToken);
            }
            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);
            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => _inner.ExistsAsync(key, cancellationToken);
        }

        private sealed class FaultyKeyValueStore : IKeyValueStore
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);
            public Func<string, bool> FailSet { get; set; }
            public Func<string, bool> FailDelete { get; set; }
            public IReadOnlyCollection<string> Keys => _values.Keys.ToList();
            public string Description => "faulty in-memory store";
            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
                => Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
            {
                if (FailSet?.Invoke(key) == true) throw new InvalidOperationException("synthetic write failure");
                if (value == null) _values.Remove(key); else _values[key] = value;
                return Task.CompletedTask;
            }
            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
            {
                if (FailDelete?.Invoke(key) == true) throw new InvalidOperationException("synthetic delete failure");
                return Task.FromResult(_values.Remove(key));
            }
            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.ContainsKey(key));
        }

        private sealed class FailingQuarantineStore : IUserIdentityQuarantineStore
        {
            public bool FailLoad { get; set; }
            public bool FailSave { get; set; }
            public string Description => "failing store";
            public Task<List<QuarantinedUserIdentity>> LoadAsync()
                => FailLoad ? throw new InvalidOperationException("synthetic load failure") : Task.FromResult(new List<QuarantinedUserIdentity>());
            public Task SaveAsync(IReadOnlyCollection<QuarantinedUserIdentity> entries)
                => FailSave ? throw new InvalidOperationException("synthetic save failure") : Task.CompletedTask;
        }

        private sealed class RecordingTelemetryChannel : ITelemetryChannel
        {
            private readonly object _gate = new object();
            private readonly List<ITelemetry> _sent = new List<ITelemetry>();

            public IList<ITelemetry> Sent
            {
                get { lock (_gate) { return _sent.ToList(); } }
            }

            public bool? DeveloperMode { get; set; }
            public string EndpointAddress { get; set; }

            public void Send(ITelemetry item)
            {
                lock (_gate) { _sent.Add(item); }
            }

            public void Flush() { }
            public void Dispose() { }
        }

        #endregion
    }
}
