extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption;
using AnalyticsWeb::Web.AnalyticsWeb.Models.UserDiagnostics;
using Common.Entities;
using Common.Entities.CopilotAdoption;
using Common.Entities.Licences;
using Common.Entities.State;
using Common.Entities.UserDiagnostics;
using Common.Entities.UserScope;
using DataUtils;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UserGroupsFilterModel = Common.Entities.Config.UserGroupsFilterModel;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// The one-person refresh: the durable request queue (claims, leases, expiry), the licence change rules, the
    /// importer worker against a real (LocalDB) database, the users-and-licences writer lock, and the licence-data
    /// generation that moves report caches on after a change.
    /// </summary>
    [TestClass]
    public class UserRefreshTests
    {
        private static readonly DateTime Start = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

        #region Request queue

        [TestMethod]
        public async Task Queue_ClaimsOnce_AndALateWorkerCannotOverwriteItsSuccessor()
        {
            var now = Start;
            var store = new UserRefreshStore(new InMemoryKeyValueStore(() => now), () => now);
            var submitted = (await store.SubmitAsync(7, "jane.doe@contoso.com", null, "admin@contoso.com")).Request;

            var first = await store.TryClaimNextAsync(TimeSpan.FromMinutes(10));
            Assert.AreEqual(submitted.Id, first.Id);
            Assert.IsNull(await store.TryClaimNextAsync(TimeSpan.FromMinutes(10)), "A running request with a live lease is nobody else's.");

            // The first worker dies; its lease passes and a second attempt takes over.
            now = now.AddMinutes(11);
            var second = await store.TryClaimNextAsync(TimeSpan.FromMinutes(10));
            Assert.AreEqual(2, second.Attempts);
            CollectionAssert.Contains(second.Notes, UserRefreshNotes.ReconciledAfterInterruption);

            first.State = UserRefreshStates.Completed;
            first.Outcome = UserRefreshOutcomes.Unchanged;
            Assert.IsFalse(await store.CompleteAsync(first), "The first claim is no longer valid.");

            second.State = UserRefreshStates.Completed;
            second.Outcome = UserRefreshOutcomes.Updated;
            Assert.IsTrue(await store.CompleteAsync(second));
            Assert.AreEqual(UserRefreshOutcomes.Updated, (await store.GetAsync(submitted.Id)).Outcome);
            Assert.IsFalse(await store.HasQueuedAsync());
        }

        [TestMethod]
        public async Task Queue_ExpiresUnclaimedRequests_FailsRepeatedInterruptions_AndIsBounded()
        {
            var now = Start;
            var store = new UserRefreshStore(new InMemoryKeyValueStore(() => now), () => now);

            var waiting = (await store.SubmitAsync(1, "a@contoso.com", null, "admin")).Request;
            now = now.Add(UserRefreshStore.QueueExpiry).AddMinutes(1);
            Assert.IsNull(await store.TryClaimNextAsync(TimeSpan.FromMinutes(10)));
            Assert.AreEqual(UserRefreshStates.Expired, (await store.GetAsync(waiting.Id)).State);

            var interrupted = (await store.SubmitAsync(2, "b@contoso.com", null, "admin")).Request;
            for (var attempt = 0; attempt < UserRefreshStore.MaxAttempts; attempt++)
            {
                Assert.IsNotNull(await store.TryClaimNextAsync(TimeSpan.FromMinutes(1)));
                now = now.AddMinutes(2);
            }
            Assert.IsNull(await store.TryClaimNextAsync(TimeSpan.FromMinutes(1)));
            var failed = await store.GetAsync(interrupted.Id);
            Assert.AreEqual(UserRefreshStates.Failed, failed.State);
            Assert.AreEqual(UserRefreshOutcomes.Interrupted, failed.Outcome);

            for (var user = 100; user < 100 + UserRefreshStore.MaxQueueLength; user++)
            {
                Assert.IsNotNull((await store.SubmitAsync(user, "u@contoso.com", null, "admin")).Request);
            }
            var overflow = await store.SubmitAsync(999, "late@contoso.com", null, "admin");
            Assert.IsNull(overflow.Request);
            Assert.AreEqual(UserRefreshRejections.QueueFull, overflow.Rejection);
        }

        #endregion

        #region Licence change rules

        [TestMethod]
        public void Plan_AnIncompleteAnswer_RemovesNothing()
        {
            var plan = TargetedUserRefreshWorker.PlanLicenceChanges(
                new[] { 1, 2 }, new Dictionary<int, string> { [1] = "SKU_A", [2] = "SKU_B" },
                new Dictionary<string, int> { ["SKU_C"] = 3 }, complete: false);

            CollectionAssert.AreEqual(new[] { 3 }, plan.ToAdd, "An addition is safe on any answer that names the licence.");
            Assert.AreEqual(0, plan.ToRemove.Count);
            CollectionAssert.AreEqual(new[] { 1, 2 }, plan.HeldBack);
            CollectionAssert.Contains(plan.Notes, UserRefreshNotes.IncompleteDirectoryAnswer);
        }

        [TestMethod]
        public void Plan_LeavesARenamedProductToTheScheduledCarryForward_AndRemovesTheRest()
        {
            // Type 1 was filed for SKU_C under an older product name; SKU_C is now filed as type 3. Adding type 3 here
            // would stop the scheduled carry-forward moving type 1's history across, so neither is touched.
            var plan = TargetedUserRefreshWorker.PlanLicenceChanges(
                new[] { 1, 2 }, new Dictionary<int, string> { [1] = "sku_c", [2] = "SKU_B" },
                new Dictionary<string, int> { ["SKU_C"] = 3 }, complete: true);

            Assert.AreEqual(0, plan.ToAdd.Count);
            CollectionAssert.AreEqual(new[] { 2 }, plan.ToRemove);
            CollectionAssert.AreEqual(new[] { 1 }, plan.HeldBack);
            CollectionAssert.Contains(plan.Notes, UserRefreshNotes.AmbiguousLicenceKept);
        }

        [TestMethod]
        public void Plan_StillAddsANameAnotherHeldSkuJustifies()
        {
            // Type 3 now stands for SKU_C (renamed from type 1) AND SKU_D, which the person also holds: SKU_D alone
            // justifies adding it.
            var plan = TargetedUserRefreshWorker.PlanLicenceChanges(
                new[] { 1 }, new Dictionary<int, string> { [1] = "SKU_C" },
                new Dictionary<string, int> { ["SKU_C"] = 3, ["SKU_D"] = 3 }, complete: true);

            CollectionAssert.AreEqual(new[] { 3 }, plan.ToAdd);
            CollectionAssert.AreEqual(new[] { 1 }, plan.HeldBack);
        }

        [TestMethod]
        public void Plan_AComplete_EmptyAnswer_RemovesEverything()
        {
            var plan = TargetedUserRefreshWorker.PlanLicenceChanges(
                new[] { 1 }, new Dictionary<int, string> { [1] = "SKU_A" }, new Dictionary<string, int>(), complete: true);

            CollectionAssert.AreEqual(new[] { 1 }, plan.ToRemove);
            Assert.AreEqual(0, plan.HeldBack.Count);
        }

        [DataTestMethod]
        [DataRow((byte)0, "2026-09-01", true, DisplayName = "Seeded when history began")]
        [DataRow((byte)0, "2026-09-20", false, DisplayName = "Opened later by the tenant refresh's repair")]
        [DataRow((byte)1, "2026-08-01", false, DisplayName = "Observed by a tenant refresh")]
        [DataRow((byte)2, "2026-08-01", false, DisplayName = "Observed by a one-person refresh")]
        public void HeldBeforeTrackingBegan_FollowsTheReportsRule(byte fromSource, string validFrom, bool expected)
        {
            var began = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(expected, LicenceHistorySources.HeldBeforeTrackingBegan(fromSource, DateTime.Parse(validFrom, System.Globalization.CultureInfo.InvariantCulture), began));
            Assert.IsFalse(LicenceHistorySources.HeldBeforeTrackingBegan(fromSource, began, null), "Without history there is no 'before'.");
        }

        #endregion

        #region Scheduled import side

        [TestMethod]
        public async Task LicenceWrites_StopAtTheFirstChunkAfterTheLockIsLost()
        {
            var store = new InMemoryLicenceStore();
            var checks = 0;
            var processor = new UserLicenseProcessor(AnalyticsLogger.ConsoleOnlyTracer(), new Tests.UnitTests.FakeLoaderClasses.FakeUserMetadataLoader(),
                new UserMetadataCache(null), new TokenNames("unused"), _ => store)
            {
                // Held for the first chunk, lost before the second.
                WriteFence = () => Task.FromResult(++checks == 1),
            };
            var desired = new HashSet<UserLicenseAssignment>(Enumerable.Range(1, UserLicenseProcessor.FencedWriteChunk + 10).Select(u => new UserLicenseAssignment(u, 1)));

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => processor.ReconcileCollectedUserLicenses(null, desired.Select(d => d.UserId).ToList(), desired));

            Assert.AreEqual(UserLicenseProcessor.FencedWriteChunk, store.Added, "Exactly one chunk landed; none started after the lock was lost.");
        }

        [TestMethod]
        public async Task LicencesChanged_FiresWhenOnlyARenameMovedAssignments()
        {
            var store = new InMemoryLicenceStore { Moved = 3 };
            var notified = 0;
            var processor = new UserLicenseProcessor(AnalyticsLogger.ConsoleOnlyTracer(), new Tests.UnitTests.FakeLoaderClasses.FakeUserMetadataLoader(),
                new UserMetadataCache(null), new TokenNames("unused"), _ => store)
            {
                LicencesChanged = () => { notified++; return Task.CompletedTask; },
            };

            await processor.ReconcileCollectedUserLicenses(null, new[] { 1 }, new HashSet<UserLicenseAssignment>());

            Assert.AreEqual(1, notified, "Renamed licence types change what reports count, so they must move on.");
        }

        [TestMethod]
        public async Task Submit_IsAccepted_WhenOnlyThePerPersonHistoryCouldNotBeSaved()
        {
            var store = new UserRefreshStore(new FailingKeysStore(new InMemoryKeyValueStore(), "User:"));

            var (request, rejection) = await store.SubmitAsync(7, "jane.doe@contoso.com", null, "admin@contoso.com");

            Assert.IsNotNull(request, "The request is queued and will run, so it must be reported as accepted.");
            Assert.IsNull(rejection);
            Assert.IsTrue(await store.HasQueuedAsync());
        }

        [TestMethod]
        public async Task Worker_WaitsWhileTheScheduledImportIsMarkedRunning()
        {
            var store = new UserRefreshStore(new InMemoryKeyValueStore());
            var request = (await store.SubmitAsync(1, "jane.doe@contoso.com", null, "admin")).Request;
            await store.MarkScheduledImportActiveAsync();

            Assert.AreEqual(0, await Worker(store, new FakeReader(null), UserImportScopeResolution.Unfiltered(Start), "unused").DrainAsync());
            Assert.AreEqual(UserRefreshStates.Queued, (await store.GetAsync(request.Id)).State, "Not claimed while a batch of the import's older reading may still land.");

            await store.ClearScheduledImportActiveAsync();
            Assert.IsFalse(await store.IsScheduledImportActiveAsync());
        }

        #endregion

        #region Report cache generation

        [TestMethod]
        public void CopilotAdoptionCacheKey_MovesOnWithTheLicenceGeneration_AndIsUnchangedWithoutOne()
        {
            var range = CopilotAdoptionDateRange.Create(28, null, null, Start);
            var legacy = CopilotAdoptionAnalysisCoordinator.CacheKey(range, new[] { 1 });

            Assert.AreEqual(legacy, CopilotAdoptionAnalysisCoordinator.CacheKey(range, new[] { 1 }, null, null));
            Assert.AreNotEqual(legacy, CopilotAdoptionAnalysisCoordinator.CacheKey(range, new[] { 1 }, null, "g1"));
            Assert.AreNotEqual(
                CopilotAdoptionAnalysisCoordinator.CacheKey(range, new[] { 1 }, null, "g1"),
                CopilotAdoptionAnalysisCoordinator.CacheKey(range, new[] { 1 }, null, "g2"));
        }

        [TestMethod]
        public async Task GenerationSource_ReadsAtMostOncePerInterval_AndKeepsItsLastValueWhenStorageFails()
        {
            var now = Start;
            var reads = 0;
            var fail = false;
            var value = "g1";
            var source = new LicenceDataGenerationSource(() =>
            {
                reads++;
                if (fail) throw new InvalidOperationException("storage down");
                return Task.FromResult(value);
            }, () => now);

            Assert.AreEqual("g1", await source.GetAsync());
            value = "g2";
            Assert.AreEqual("g1", await source.GetAsync(), "Within the interval the last value is reused.");
            Assert.AreEqual(1, reads);

            now = now.Add(LicenceDataGenerationSource.RefreshInterval);
            Assert.AreEqual("g2", await source.GetAsync());

            now = now.Add(LicenceDataGenerationSource.RefreshInterval);
            fail = true;
            Assert.AreEqual("g2", await source.GetAsync(), "A failed read must not fail the report.");
            Assert.AreEqual(3, reads);
        }

        #endregion

        #region Writer lock (LocalDB)

        [TestMethod]
        public async Task WriterLock_IsExclusive_AndReleasedOnDispose()
        {
            var connectionString = TestConnectionString();
            using (var held = await DirectoryWriterLock.TryAcquireAsync(connectionString, TimeSpan.Zero))
            {
                Assert.IsNotNull(held);
                Assert.IsTrue(await held.IsStillHeldAsync());
                Assert.IsNull(await DirectoryWriterLock.TryAcquireAsync(connectionString, TimeSpan.Zero), "A second writer must wait its turn.");

                held.Dispose();
                Assert.IsFalse(await held.IsStillHeldAsync());
            }

            using (var next = await DirectoryWriterLock.TryAcquireAsync(connectionString, TimeSpan.Zero))
            {
                Assert.IsNotNull(next, "Disposing must release the lock at once, not when a pooled session is reset.");
            }
        }

        [TestMethod]
        public async Task WriterLock_ADroppedSession_IsLostForGood_AndReleasesTheLock()
        {
            var connectionString = TestConnectionString();
            using (var held = await DirectoryWriterLock.TryAcquireAsync(connectionString, TimeSpan.Zero))
            {
                await KillSessionAsync(connectionString, await SpidOfAsync(held.Connection));

                Assert.IsFalse(await held.IsStillHeldAsync(), "The session holding the lock is gone.");
                Assert.IsTrue(held.IsLost);
                Assert.IsFalse(await held.IsStillHeldAsync(), "Lost stays lost.");

                using (var other = await DirectoryWriterLock.TryAcquireAsync(connectionString, TimeSpan.Zero))
                {
                    Assert.IsNotNull(other, "SQL Server released the lock with the session.");
                }
            }
        }

        [TestMethod]
        public async Task WriterLock_KeepAlive_BeatsWhileHeld_AndNoticesALostSessionWithoutBeingAsked()
        {
            var connectionString = TestConnectionString();
            using (var held = await DirectoryWriterLock.TryAcquireAsync(connectionString, TimeSpan.Zero))
            {
                var spid = await SpidOfAsync(held.Connection); // before the keep-alive starts using the connection
                var beats = 0;
                held.StartKeepAlive(TimeSpan.FromMilliseconds(100), () => { Interlocked.Increment(ref beats); return Task.CompletedTask; });
                await WaitUntilAsync(() => Volatile.Read(ref beats) >= 2);
                Assert.IsFalse(held.IsLost);

                await KillSessionAsync(connectionString, spid);
                await WaitUntilAsync(() => held.IsLost);
                var beatsWhenLost = Volatile.Read(ref beats);
                await Task.Delay(400);
                Assert.AreEqual(beatsWhenLost, Volatile.Read(ref beats), "No heartbeat may claim the import is running once its lock is gone.");
            }
        }

        private static async Task<short> SpidOfAsync(SqlConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT @@SPID;";
                return (short)await command.ExecuteScalarAsync();
            }
        }

        private static async Task KillSessionAsync(string connectionString, short spid)
        {
            using (var admin = new SqlConnection(connectionString))
            {
                await admin.OpenAsync();
                using (var kill = admin.CreateCommand())
                {
                    kill.CommandText = "KILL " + spid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";";
                    await kill.ExecuteNonQueryAsync();
                }
            }
        }

        [TestMethod]
        public async Task WriterLock_StopKeepAlive_EndsTheHeartbeat_ButKeepsTheLock()
        {
            using (var held = await DirectoryWriterLock.TryAcquireAsync(TestConnectionString(), TimeSpan.Zero))
            {
                var beats = 0;
                held.StartKeepAlive(TimeSpan.FromMilliseconds(50), () => { Interlocked.Increment(ref beats); return Task.CompletedTask; });
                await WaitUntilAsync(() => Volatile.Read(ref beats) >= 1);

                held.StopKeepAlive();
                var beatsWhenStopped = Volatile.Read(ref beats);
                await Task.Delay(300);

                Assert.AreEqual(beatsWhenStopped, Volatile.Read(ref beats), "A beat after the marker is cleared would set it again.");
                Assert.IsTrue(await held.IsStillHeldAsync());
            }
        }

        [TestMethod]
        public async Task WriterLock_StopKeepAlive_SaysWhenABeatOutlivedTheWait()
        {
            using (var held = await DirectoryWriterLock.TryAcquireAsync(TestConnectionString(), TimeSpan.Zero))
            {
                var inBeat = new TaskCompletionSource<bool>();
                var release = new TaskCompletionSource<bool>();
                held.StartKeepAlive(TimeSpan.FromMilliseconds(20), async () => { inBeat.TrySetResult(true); await release.Task; });
                await inBeat.Task;

                Assert.IsFalse(held.StopKeepAlive(TimeSpan.FromMilliseconds(100)), "A slow heartbeat write may still land: the caller must know.");

                release.SetResult(true);
                Assert.IsTrue(held.StopKeepAlive(), "Stopping again is a no-op.");
                Assert.IsTrue(await held.IsStillHeldAsync());
            }
        }

        [TestMethod]
        public async Task ScheduledImport_FallbackLicencePath_ChecksTheWriterLock()
        {
            // No tenant SKUs (no Organization.Read.All): licences come from the per-user fallback, which must check the
            // same lock as the tenant path before each chunk it writes.
            var token = "ufb" + Guid.NewGuid().ToString("N").Substring(0, 10);
            var graphUser = new GraphUser { Id = Guid.NewGuid().ToString(), UserPrincipalName = token + "@contoso.local", AccountEnabled = true };
            var connectionString = TestConnectionString();
            var updater = new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new Common.Entities.Config.AppConfig(),
                new Tests.UnitTests.FakeLoaderClasses.FakeUserMetadataLoader(new List<GraphUser> { graphUser }))
            {
                WriterLockFactory = () => DirectoryWriterLock.TryAcquireAsync(connectionString, TimeSpan.FromSeconds(30)),
            };
            try
            {
                await updater.InsertAndUpdateDatabaseFromExternalUsers();

                // The run is over and its lock released: a check of that lock must now fail. Without the wiring the
                // processor keeps its default check, which always passes.
                Assert.IsNotNull(updater.LicenceWriteFence);
                Assert.IsFalse(await updater.LicenceWriteFence());
            }
            finally
            {
                await CleanupAsync(token);
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for the condition.");
                await Task.Delay(50);
            }
        }

        #endregion

        #region Worker (LocalDB)

        [TestMethod]
        public async Task Worker_WritesOnlyThisPersonsLicenceDifference_WithoutATenantRefreshRun()
        {
            var token = "uref" + Guid.NewGuid().ToString("N").Substring(0, 10);
            var seed = await SeedAsync(token);
            try
            {
                var objectId = seed.ObjectId;
                var guidB = Guid.NewGuid();
                var guidC = Guid.NewGuid();
                var observed = Start.AddSeconds(30);
                var userJson = JObject.FromObject(new { id = objectId, userPrincipalName = seed.Upn, department = "Πωλήσεις " + token });
                var reader = new FakeReader(LiveDirectoryReadResult.Ok(new LiveDirectoryObservation
                {
                    ObjectId = objectId,
                    UserPrincipalName = seed.Upn,
                    AssignedLicencesPresent = true,
                    AssignedSkuIds = { guidB, guidC },
                    LicenceDetails = new List<LiveLicenceDetail>
                    {
                        new LiveLicenceDetail { SkuId = guidB, SkuPartNumber = token + "_B" },
                        new LiveLicenceDetail { SkuId = guidC, SkuPartNumber = token + "_C" },
                    },
                    ObservedAtUtc = observed,
                }, userJson));

                var runsBefore = await CountAsync("SELECT COUNT(*) FROM dbo.license_refresh_runs");
                var store = new UserRefreshStore(new InMemoryKeyValueStore());
                var request = (await store.SubmitAsync(seed.UserId, seed.Upn, objectId, "admin@contoso.com")).Request;

                var finished = await Worker(store, reader, UserImportScopeResolution.Unfiltered(Start), token).DrainAsync();

                Assert.AreEqual(1, finished);
                var result = await store.GetAsync(request.Id);
                Assert.AreEqual(UserRefreshStates.Completed, result.State);
                Assert.AreEqual(UserRefreshOutcomes.Partial, result.Outcome, "A record was kept for lack of evidence.");
                CollectionAssert.AreEqual(new[] { token + " B" }, result.Added, "The renamed product's new name is left to the scheduled carry-forward.");
                CollectionAssert.AreEqual(new[] { token + " A" }, result.Removed);
                CollectionAssert.AreEqual(new[] { token + " C Old" }, result.HeldBack, "The person still holds the SKU the old record was filed for.");
                CollectionAssert.AreEqual(new[] { UserRefreshProfileFields.Department }, result.ProfileChanges);
                Assert.AreEqual(observed, result.ObservedUtc);
                Assert.IsNull(await store.GetVerificationAsync(seed.UserId), "A partly applied answer is not a verification.");
                Assert.IsNotNull(await store.GetLicenceDataGenerationAsync(), "Reports must be told licences changed.");

                using (var db = new AnalyticsEntitiesContext())
                {
                    var held = await db.Database.SqlQuery<string>(
                        "SELECT lt.name FROM dbo.user_license_type_lookups l JOIN dbo.license_types lt ON lt.id = l.license_type_id WHERE l.user_id = @u",
                        new SqlParameter("@u", seed.UserId)).ToListAsync();
                    CollectionAssert.AreEquivalent(new[] { token + " B", token + " C Old" }, held);

                    var opened = await db.Database.SqlQuery<HistoryRow>(
                        "SELECT h.from_source AS FromSource, h.valid_from_previous_refresh_utc AS PreviousBound, h.valid_to_utc AS ValidTo, h.valid_to_previous_refresh_utc AS PreviousToBound, lt.name AS Name " +
                        "FROM dbo.user_license_history h JOIN dbo.license_types lt ON lt.id = h.license_type_id WHERE h.user_id = @u",
                        new SqlParameter("@u", seed.UserId)).ToListAsync();
                    var b = opened.Single(r => r.Name == token + " B");
                    Assert.AreEqual(LicenceHistorySources.UserRefresh, b.FromSource);
                    Assert.IsNull(b.PreviousBound, "A one-person refresh cannot say when the change happened before it saw it.");
                    var a = opened.Single(r => r.Name == token + " A");
                    Assert.IsNotNull(a.ValidTo);
                    Assert.IsNull(a.PreviousToBound);

                    var department = await db.Database.SqlQuery<string>(
                        "SELECT d.name FROM dbo.users u JOIN dbo.user_departments d ON d.id = u.department_id WHERE u.id = @u",
                        new SqlParameter("@u", seed.UserId)).SingleAsync();
                    Assert.AreEqual("Πωλήσεις " + token, department, "Directory text must survive the round trip unchanged.");
                }

                Assert.AreEqual(runsBefore, await CountAsync("SELECT COUNT(*) FROM dbo.license_refresh_runs"),
                    "A one-person refresh must never tell history readers the whole tenant was checked.");
            }
            finally
            {
                await CleanupAsync(token);
            }
        }

        [TestMethod]
        public async Task Worker_RefusesOutOfScopePeopleWithoutReadingTheDirectory_AndKeepsLicencesOnAnIncompleteAnswer()
        {
            var token = "uref" + Guid.NewGuid().ToString("N").Substring(0, 10);
            var seed = await SeedAsync(token);
            try
            {
                var reader = new FakeReader(LiveDirectoryReadResult.Ok(new LiveDirectoryObservation
                {
                    ObjectId = seed.ObjectId,
                    UserPrincipalName = seed.Upn,
                    AssignedLicencesPresent = false, // the property never arrived: not an empty answer
                    ObservedAtUtc = Start,
                }));
                var store = new UserRefreshStore(new InMemoryKeyValueStore());

                var members = new UserScopeMembers();
                members.Add("00000000-0000-0000-0000-000000000002", "someone.else@contoso.com", null);
                var excluded = UserImportScopeResolution.Resolved(members,
                    new List<ResolvedGroup> { new ResolvedGroup("g", "Pilot", new[] { "Pilot" }, 1) }, null, Start);
                var refused = (await store.SubmitAsync(seed.UserId, seed.Upn, seed.ObjectId, "admin")).Request;
                await Worker(store, reader, excluded, token).DrainAsync();
                Assert.AreEqual(UserRefreshOutcomes.ScopeExcluded, (await store.GetAsync(refused.Id)).Outcome);
                Assert.AreEqual(0, reader.Calls, "Nobody outside the import scope is read from the directory.");

                var request = (await store.SubmitAsync(seed.UserId, seed.Upn, seed.ObjectId, "admin")).Request;
                await Worker(store, reader, UserImportScopeResolution.Unfiltered(Start), token).DrainAsync();
                var result = await store.GetAsync(request.Id);
                Assert.AreEqual(UserRefreshOutcomes.Partial, result.Outcome);
                Assert.AreEqual(0, result.Removed.Count);
                CollectionAssert.AreEquivalent(new[] { token + " A", token + " C Old" }, result.HeldBack);
                CollectionAssert.Contains(result.Notes, UserRefreshNotes.IncompleteDirectoryAnswer);
                Assert.AreEqual(2, await CountAsync("SELECT COUNT(*) FROM dbo.user_license_type_lookups WHERE user_id = " + seed.UserId));
            }
            finally
            {
                await CleanupAsync(token);
            }
        }

        [TestMethod]
        public async Task Worker_LeavesRequestsQueued_WhileAnotherWriterHoldsTheLock()
        {
            var store = new UserRefreshStore(new InMemoryKeyValueStore());
            var request = (await store.SubmitAsync(1, "jane.doe@contoso.com", null, "admin")).Request;

            using (await DirectoryWriterLock.TryAcquireAsync(TestConnectionString(), TimeSpan.Zero))
            {
                var finished = await Worker(store, new FakeReader(null), UserImportScopeResolution.Unfiltered(Start), "unused").DrainAsync();
                Assert.AreEqual(0, finished);
            }

            Assert.AreEqual(UserRefreshStates.Queued, (await store.GetAsync(request.Id)).State, "Not claimed, so no attempt is used up.");
        }

        #endregion

        #region Helpers

        private static TargetedUserRefreshWorker Worker(UserRefreshStore store, ILiveDirectoryReader reader, UserImportScopeResolution scope, string token)
        {
            var connectionString = TestConnectionString();
            return new TargetedUserRefreshWorker(AnalyticsLogger.ConsoleOnlyTracer(), store, reader, new FixedScope(scope), () => true,
                () => DirectoryWriterLock.TryAcquireAsync(connectionString, TimeSpan.Zero), new TokenNames(token));
        }

        private static string TestConnectionString()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                return db.Database.Connection.ConnectionString;
            }
        }

        private sealed class SeedResult
        {
            public int UserId;
            public string Upn;
            public string ObjectId;
        }

        /// <summary>A person holding licence A and an old record of SKU C; licence type B exists but is not held.</summary>
        private static async Task<SeedResult> SeedAsync(string token)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.Initialize(false);
                var objectId = Guid.NewGuid().ToString();
                var user = new User { UserPrincipalName = token + "@contoso.local", AzureAdId = objectId };
                var a = new LicenseType { Name = token + " A", SKUID = token + "_A" };
                var b = new LicenseType { Name = token + " B", SKUID = token + "_B" };
                var oldC = new LicenseType { Name = token + " C Old", SKUID = token + "_C" };
                db.users.Add(user);
                db.LicenseTypes.AddRange(new[] { a, b, oldC });
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlCommandAsync(@"
INSERT INTO dbo.user_license_type_lookups (user_id, license_type_id) VALUES (@u, @a), (@u, @c);
IF OBJECT_ID(N'dbo.user_license_history', N'U') IS NOT NULL
INSERT INTO dbo.user_license_history (user_id, license_type_id, valid_from_utc, valid_to_utc, from_source) VALUES
(@u, @a, '2026-09-01', NULL, 1), (@u, @c, '2026-09-01', NULL, 1);",
                    new SqlParameter("@u", user.ID), new SqlParameter("@a", a.ID), new SqlParameter("@c", oldC.ID));
                return new SeedResult { UserId = user.ID, Upn = user.UserPrincipalName, ObjectId = objectId };
            }
        }

        private static async Task CleanupAsync(string token)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await db.Database.ExecuteSqlCommandAsync(@"
DECLARE @users TABLE (id int PRIMARY KEY);
INSERT INTO @users SELECT id FROM dbo.users WHERE user_name LIKE @prefix;
DELETE FROM dbo.user_license_history WHERE user_id IN (SELECT id FROM @users);
DELETE FROM dbo.user_license_type_lookups WHERE user_id IN (SELECT id FROM @users);
DELETE FROM dbo.users WHERE id IN (SELECT id FROM @users);
DELETE FROM dbo.user_departments WHERE name LIKE @department;
DELETE FROM dbo.license_types WHERE sku_id LIKE @prefix;",
                    new SqlParameter("@prefix", token + "%"),
                    new SqlParameter("@department", "%" + token));
            }
        }

        private static async Task<int> CountAsync(string sql)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                return await db.Database.SqlQuery<int>(sql).SingleAsync();
            }
        }

        private sealed class HistoryRow
        {
            public byte FromSource { get; set; }
            public DateTime? PreviousBound { get; set; }
            public DateTime? ValidTo { get; set; }
            public DateTime? PreviousToBound { get; set; }
            public string Name { get; set; }
        }

        private sealed class FakeReader : ILiveDirectoryReader
        {
            private readonly LiveDirectoryReadResult _result;
            public FakeReader(LiveDirectoryReadResult result) => _result = result;
            public int Calls { get; private set; }

            public Task<LiveDirectoryReadResult> ReadAsync(string objectIdOrUserPrincipalName, CancellationToken cancellationToken, string profileSelect = null)
            {
                Calls++;
                return Task.FromResult(_result);
            }
        }

        private sealed class FixedScope : IUserImportScopeProvider
        {
            private readonly UserImportScopeResolution _resolution;
            public FixedScope(UserImportScopeResolution resolution) => _resolution = resolution;
            public UserGroupsFilterModel Filter => new UserGroupsFilterModel();
            public Task<UserImportScope> GetScopeAsync() => Task.FromResult(UserImportScope.Unfiltered);
            public Task<UserImportScopeResolution> GetResolutionAsync() => Task.FromResult(_resolution);
            public Task<UserImportScopeResolution> RefreshAsync() => Task.FromResult(_resolution);
        }

        /// <summary>SKU B keeps its product name; SKU C has been renamed since its old record was filed.</summary>
        private sealed class TokenNames : IOfficeLicenseNameResolver
        {
            private readonly string _token;
            public TokenNames(string token) => _token = token;

            public string GetDisplayNameFor(string id)
            {
                if (id == _token + "_B") return _token + " B";
                if (id == _token + "_C") return _token + " C New";
                return null;
            }
        }

        /// <summary>A licence store with no database: counts what was written.</summary>
        private sealed class InMemoryLicenceStore : IUserLicenseStore
        {
            public int Moved { get; set; }
            public int Added { get; private set; }

            public Task<HashSet<UserLicenseAssignment>> LoadAssignmentsFor(ICollection<int> userIds) => Task.FromResult(new HashSet<UserLicenseAssignment>());
            public Task<LicenseRefreshRunInfo> StartRefresh(DateTime completedUtc) => Task.FromResult(new LicenseRefreshRunInfo(completedUtc, null, false));
            public Task<int> CarryAssignmentsAcrossRenamedLicenceTypes(IReadOnlyList<int> currentLicenseTypeIds, LicenseRefreshRunInfo refresh) => Task.FromResult(Moved);
            public Task<int> AddAssignments(IReadOnlyList<UserLicenseAssignment> assignments, LicenseRefreshRunInfo refresh) { Added += assignments.Count; return Task.FromResult(assignments.Count); }
            public Task<int> RemoveAssignments(IReadOnlyList<UserLicenseAssignment> assignments, LicenseRefreshRunInfo refresh) => Task.FromResult(assignments.Count);
            public Task<LicenseHistoryReconcileResult> ReconcileHistoryWithCurrentLookups(LicenseRefreshRunInfo refresh) => Task.FromResult(new LicenseHistoryReconcileResult());
            public Task<int?> CompleteRefresh(LicenseRefreshRunInfo refresh, IReadOnlyList<LicenseSeatCountSnapshot> seatCounts) => Task.FromResult<int?>(null);
        }

        /// <summary>Table storage that fails writes to keys with one prefix, as a partial outage would.</summary>
        private sealed class FailingKeysStore : IConditionalKeyValueStore
        {
            private readonly IConditionalKeyValueStore _inner;
            private readonly string _failingPrefix;

            public FailingKeysStore(IConditionalKeyValueStore inner, string failingPrefix)
            {
                _inner = inner;
                _failingPrefix = failingPrefix;
            }

            public string Description => _inner.Description;
            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default) => _inner.GetStringAsync(key, cancellationToken);
            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
                => key.StartsWith(_failingPrefix, StringComparison.Ordinal) ? throw new InvalidOperationException("storage write failed") : _inner.SetStringAsync(key, value, timeToLive, cancellationToken);
            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);
            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => _inner.ExistsAsync(key, cancellationToken);
            public Task<VersionedValue> GetVersionedAsync(string key, CancellationToken cancellationToken = default) => _inner.GetVersionedAsync(key, cancellationToken);
            public Task<bool> TrySetStringAsync(string key, string value, string expectedVersionToken, CancellationToken cancellationToken = default)
                => _inner.TrySetStringAsync(key, value, expectedVersionToken, cancellationToken);
        }

        #endregion
    }
}
