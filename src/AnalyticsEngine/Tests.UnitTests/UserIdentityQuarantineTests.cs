using Common.Entities;
using Common.Entities.Config;
using Common.Entities.State;
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
