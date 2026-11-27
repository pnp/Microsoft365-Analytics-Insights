extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.UserScope;
using Common.Entities.State;
using Common.Entities.UserScope;
using Common.Entities.UserScope.Purge;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Migrations;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;

namespace Tests.UnitTests
{
    [TestClass]
    public class UserDeleteCheckpointTests
    {
        [DataTestMethod]
        [DataRow(true, false)]
        [DataRow(false, false)]
        [DataRow(true, true)]
        public async Task BeforeDispatchOnlyTheCurrentOwnerCanCorrectTheIntent(bool hostStops, bool replaced)
        {
            var state = new UserScopePurgeStateStore(new InMemoryKeyValueStore(), true);
            var queued = await state.CreateAsync(null, null, 1, 1);
            var job = await state.TryClaimAsync(queued.Id, () => Task.CompletedTask, CancellationToken.None);
            using (var stopping = new CancellationTokenSource())
            {
                var checkpoint = new UserScopePurgeEngine.UserDeleteCheckpoint(job, async () =>
                {
                    await state.SaveOwnedAsync(job, CancellationToken.None);
                    if (!hostStops) throw new TimeoutException("Synthetic stop check failure.");
                    stopping.Cancel();
                    return false;
                }, stopping.Token);
                var sent = false;
                Task<int> Attempt() => checkpoint.ExecuteAsync(dispatch =>
                {
                    dispatch();
                    sent = true;
                    return Task.FromResult(1);
                }, count => count);
                if (hostStops) await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => Attempt());
                else await Assert.ThrowsExceptionAsync<TimeoutException>(() => Attempt());
                Assert.IsFalse(sent);
                Assert.IsFalse(job.HasUnaccountedUserDeletes);
                Assert.IsTrue((await state.GetAsync(job.Id)).HasUnaccountedUserDeletes);
                if (replaced)
                {
                    await state.TryClaimAsync(job.Id, () => Task.CompletedTask, CancellationToken.None);
                    await Assert.ThrowsExceptionAsync<UserScopePurgeLockLostException>(() => state.SaveOwnedAsync(job, CancellationToken.None));
                }
                else await state.SaveOwnedAsync(job, CancellationToken.None);
                Assert.AreEqual(replaced, (await state.GetAsync(job.Id)).HasUnaccountedUserDeletes);
            }
        }

        [TestMethod]
        public async Task LostSingleDeleteReplyRetainsOnlyEarlierAcknowledgementsAcrossTakeover()
        {
            var state = new UserScopePurgeStateStore(new InMemoryKeyValueStore(), true);
            var queued = await state.CreateAsync(null, null, 1, 3);
            var job = await state.TryClaimAsync(queued.Id, () => Task.CompletedTask, CancellationToken.None);
            var checkpoint = new UserScopePurgeEngine.UserDeleteCheckpoint(job, async () =>
            {
                await state.SaveOwnedAsync(job, CancellationToken.None);
                return false;
            }, CancellationToken.None);
            foreach (var rows in new[] { 1, 0, 1 })
                await checkpoint.ExecuteAsync(dispatch => { dispatch(); return Task.FromResult(rows); }, count => count);
            await Assert.ThrowsExceptionAsync<TimeoutException>(() => checkpoint.ExecuteAsync<int>(dispatch =>
            {
                dispatch();
                throw new TimeoutException("Synthetic lost SQL acknowledgement.");
            }, count => count));
            Assert.IsTrue(job.HasUnaccountedUserDeletes);
            var replacement = await state.TryClaimAsync(job.Id, () => Task.CompletedTask, CancellationToken.None);
            Assert.IsTrue(replacement.HasUnaccountedUserDeletes);
            Assert.AreEqual(2, replacement.UsersDeleted);
            Assert.AreEqual(2L, replacement.RowsAffected["users"]);
        }
    }

    [TestClass]
    [TestCategory("SqlIntegration")]
    [DoNotParallelize]
    public class UserScopePurgeSafetyTests
    {
        internal static ScratchDatabase CreateMigratedDatabase()
        {
            var db = ScratchDatabase.Create("ScopePurgeSafety");
            try
            {
                new DbMigrator(new Common.Entities.Migrations.Configuration
                {
                    TargetDatabase = new DbConnectionInfo(db.ConnectionString, "Microsoft.Data.SqlClient"),
                }).Update();
                return db;
            }
            catch
            {
                db.Dispose();
                throw;
            }
        }

        private static ScratchDatabase _db;
        private UserScopePurgeDatabase _database;
        private UserScopePurgeStateStore _state;
        private InMemoryKeyValueStore _ledger;
        private int _outside;
        private const string InsideUpn = "inside@contoso.example";
        private const string OutsideUpn = "outside@contoso.example";
        private const string LaterUpn = "later@contoso.example";
        private static readonly DateTime FirstDay = new DateTime(2020, 1, 1);

        [ClassInitialize]
        public static void CreateFixture(TestContext _) => _db = CreateMigratedDatabase();

        [ClassCleanup]
        public static void DropFixture() => _db?.Dispose();

        [TestInitialize]
        public void Seed()
        {
            _database = new UserScopePurgeDatabase(_db.ConnectionString);
            _state = new UserScopePurgeStateStore(new InMemoryKeyValueStore(), true);
            _ledger = new InMemoryKeyValueStore();
            // Only this class's throwaway migrated database. Use real scope resolution rather than a candidate override.
            _db.Execute($@"
DROP TABLE IF EXISTS dbo.contoso_keep_user;
DROP TRIGGER IF EXISTS dbo.contoso_delete_ack;
DELETE FROM dbo.copilot_studio_credit_user_daily;
DELETE FROM dbo.audit_events;
DELETE FROM dbo.teams_user_activity_log;
DELETE FROM dbo.outlook_user_activity_log;
DELETE FROM dbo.users WHERE user_name IN ('{InsideUpn}', '{OutsideUpn}', '{LaterUpn}');
INSERT dbo.users(user_name) VALUES ('{InsideUpn}'), ('{OutsideUpn}');
INSERT dbo.audit_events(id, time_stamp, user_id) SELECT NEWID(), '20200101', id FROM dbo.users WHERE user_name IN ('{InsideUpn}', '{OutsideUpn}');
INSERT dbo.copilot_studio_credit_user_daily(usage_date, entra_object_id, billed_credits, dimension_hash, imported_utc)
VALUES ('20200101', '00000000-0000-0000-0000-000000000001', 1, N'contoso', '20200102');");
            _outside = (int)_db.Scalar($"SELECT id FROM dbo.users WHERE user_name = '{OutsideUpn}'");
        }

        internal static void SeedTeamsDays(ScratchDatabase db, int days, string userPredicate)
        {
            db.Execute($@"
;WITH days AS (SELECT 0 AS n UNION ALL SELECT n + 1 FROM days WHERE n + 1 < {days})
INSERT dbo.teams_user_activity_log(private_chat_count, team_chat_count, calls_count, meetings_count, user_id, [date])
SELECT 1, 0, 0, 0, u.id, DATEADD(day, d.n, '20200101') FROM days d CROSS JOIN dbo.users u WHERE {userPredicate}
OPTION (MAXRECURSION 0);");
        }

        private static UserImportScopeResolution Members(string upn)
        {
            var members = new UserScopeMembers();
            members.Add(null, upn, upn);
            return UserImportScopeResolution.Resolved(members, null, null, DateTime.UtcNow);
        }

        private static FixedUserImportScopeProvider Provider(UserImportScopeResolution resolution) =>
            new FixedUserImportScopeProvider(UserImportScope.ForMembers(resolution.Members, "test"), resolution);

        private Task<UserScopePurgeJob> QueueAsync(UserImportScopeResolution resolution) =>
            _state.CreateAsync("admin@contoso.example", null, 1, 1, resolution.Members.MembershipFingerprint);

        private UserScopePurgeEngine Engine(bool durable) =>
            new UserScopePurgeEngine(_database, _state, null, new UsageReportCoverageStore(_ledger, durable))
            {
                RetryDelayOverride = TimeSpan.Zero,
                StateCheckInterval = TimeSpan.Zero,
                BatchSizeOverride = 1,
            };

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task UnprovenLegacyFenceIsUntouchedWhenAnExplicitGenerationAllowsRecovery(bool cancel)
        {
            var legacyToken = Guid.NewGuid().ToString("N");
            var legacy = new UsageReportCoverageStore(_ledger, true);
            await legacy.StartScopePurgeAsync(legacyToken, () => Task.CompletedTask);
            var members = Members(InsideUpn);
            var queued = await QueueAsync(members);

            Assert.AreEqual(UserScopePurgeRunOutcome.LockLost, await Engine(true).RunAsync(queued.Id, Provider(members)));
            Assert.IsTrue(await legacy.IsActivePurgeAsync(legacyToken, CancellationToken.None));
            Assert.AreEqual(1, _db.Scalar($"SELECT COUNT(*) FROM dbo.users WHERE id = {_outside}"));
            if (cancel) Assert.IsTrue(await _state.RequestCancelAsync(queued.Id));

            var recovered = new UsageReportCoverageStore(_ledger, true, null, Guid.NewGuid().ToString("N"));
            var engine = new UserScopePurgeEngine(_database, _state, null, recovered);
            Assert.AreEqual(cancel ? UserScopePurgeRunOutcome.Cancelled : UserScopePurgeRunOutcome.Completed,
                await engine.RunAsync(queued.Id, Provider(members)));
            Assert.AreEqual(cancel ? 1 : 0, _db.Scalar($"SELECT COUNT(*) FROM dbo.users WHERE id = {_outside}"));
            Assert.IsTrue(await legacy.IsActivePurgeAsync(legacyToken, CancellationToken.None), "Recovery must not update or clear an unproven fence.");
        }

        [TestMethod]
        public async Task GracefulStopAfterSavingIntentButBeforeUserSqlPreservesConsent()
        {
            var members = Members(InsideUpn);
            var queued = await QueueAsync(members);
            var first = Engine(true);
            using (var stopping = new CancellationTokenSource())
            {
                first.BeforeUserDeleteDispatch = stopping.Cancel;
                Assert.AreEqual(UserScopePurgeRunOutcome.Paused, await first.RunAsync(queued.Id, Provider(members), stopToken: stopping.Token));
            }
            var paused = await _state.GetAsync(queued.Id);
            Assert.IsFalse(paused.HasUnaccountedUserDeletes);
            Assert.AreEqual(0, paused.UsersDeleted);
            Assert.AreEqual(1, _db.Scalar($"SELECT COUNT(*) FROM dbo.users WHERE id = {_outside}"));

            Assert.AreEqual(UserScopePurgeRunOutcome.Completed, await Engine(true).RunAsync(queued.Id, Provider(members)));
            Assert.AreEqual(1, (await _state.GetAsync(queued.Id)).UsersDeleted);
            Assert.AreEqual(0, _db.Scalar($"SELECT COUNT(*) FROM dbo.users WHERE id = {_outside}"));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AcknowledgedFallbackDeleteSurvivesLaterFailureOrHandoff(bool handoff)
        {
            _db.Execute($@"INSERT dbo.users(user_name) VALUES ('{LaterUpn}');
CREATE TABLE dbo.contoso_keep_user (id int NOT NULL REFERENCES dbo.users(id));
INSERT dbo.contoso_keep_user SELECT id FROM dbo.users WHERE user_name = '{LaterUpn}';");
            var members = Members(InsideUpn);
            var queued = await _state.CreateAsync(null, null, 1, 2, members.Members.MembershipFingerprint);
            var first = Engine(true);
            first.BatchSizeOverride = 20; // Both candidates share a DELETE, so the FK forces the fallback.
            using (var session = await _database.TryOpenPurgeSessionAsync(TimeSpan.Zero))
            {
                first.BeforeOneByOneDelete = async id =>
                {
                    if (id == _outside) return;
                    var checkpoint = await _state.GetAsync(queued.Id);
                    Assert.AreEqual(1, checkpoint.UsersDeleted);
                    Assert.AreEqual(1L, checkpoint.RowsAffected["users"]);
                    Assert.IsFalse(checkpoint.HasUnaccountedUserDeletes);
                    if (handoff)
                    {
                        session.Connection.Close();
                        Assert.IsTrue(await _state.RequestCancelAsync(queued.Id));
                        Assert.AreEqual(UserScopePurgeRunOutcome.Cancelled, await Engine(true).RunAsync(queued.Id, Provider(members)));
                    }
                    throw new InvalidOperationException("Synthetic failure before the next fallback delete.");
                };
                Assert.AreEqual(handoff ? UserScopePurgeRunOutcome.LockLost : UserScopePurgeRunOutcome.Failed,
                    await first.RunAsync(queued.Id, Provider(members), lockedSession: session));
            }
            var final = await _state.GetAsync(queued.Id);
            Assert.AreEqual(handoff ? UserScopePurgeStates.Cancelled : UserScopePurgeStates.Failed, final.State);
            Assert.AreEqual(handoff ? null : UserScopePurgeErrorCodes.Unexpected, final.ErrorCode);
            Assert.AreEqual(1, final.UsersDeleted);
            Assert.AreEqual(1L, final.RowsAffected["users"]);
            Assert.AreEqual(0, _db.Scalar($"SELECT COUNT(*) FROM dbo.users WHERE id = {_outside}"));
            Assert.AreEqual(1, _db.Scalar($"SELECT COUNT(*) FROM dbo.users WHERE user_name = '{LaterUpn}'"));
        }

        [TestMethod]
        public async Task MissingCoverageStorageRefusesBeforeDeletingAuditCreditsOrMixedUsage()
        {
            _state = new UserScopePurgeStateStore(new InMemoryKeyValueStore(), false);
            SeedTeamsDays(_db, 1, $"u.user_name IN ('{InsideUpn}', '{OutsideUpn}')");
            var previous = new UsageReportCoverageStore(_ledger, true);
            await previous.CompleteDayAsync("teams", FirstDay, true, true, false, FirstDay.AddDays(4), 3);
            var members = Members(InsideUpn);
            var job = await QueueAsync(members);

            Assert.AreEqual(UserScopePurgeRunOutcome.Failed, await Engine(false).RunAsync(job.Id, Provider(members)));
            var stopped = await _state.GetAsync(job.Id);
            Assert.AreEqual("coverageStorageRequired", stopped.ErrorCode);
            Assert.AreEqual(0, stopped.RowsAffected.Count);
            Assert.AreEqual(2, _db.Scalar("SELECT COUNT(*) FROM dbo.audit_events"));
            Assert.AreEqual(1, _db.Scalar("SELECT COUNT(*) FROM dbo.copilot_studio_credit_user_daily"));
            Assert.AreEqual(2, _db.Scalar("SELECT COUNT(*) FROM dbo.teams_user_activity_log"));
            Assert.IsTrue(await previous.IsDayVerifiedAsync("teams", FirstDay), "The former store is still correct when reattached.");

            _state = new UserScopePurgeStateStore(new InMemoryKeyValueStore(), true);
            var retry = await QueueAsync(members);
            Assert.AreEqual(UserScopePurgeRunOutcome.Completed, await Engine(true).RunAsync(retry.Id, Provider(members)));
            Assert.AreEqual(1, _db.Scalar("SELECT COUNT(*) FROM dbo.teams_user_activity_log"));
            Assert.IsFalse(await previous.IsDayVerifiedAsync("teams", FirstDay));
        }

        [TestMethod]
        public async Task UnreachableConfiguredCoverageFailsBeforeUnlinkedCreditsAndAuditDeletes()
        {
            SeedTeamsDays(_db, 1, $"u.user_name = '{OutsideUpn}'");
            var members = Members(InsideUpn);
            var job = await QueueAsync(members);
            var unavailable = new UsageReportCoverageStore(new AzureTableKeyValueStore(_ =>
                Task.FromException<Azure.Data.Tables.TableClient>(new Azure.RequestFailedException(503, "Synthetic Storage outage.")),
                "Contoso", "Coverage"), true);
            var engine = new UserScopePurgeEngine(_database, _state, null, unavailable);
            Assert.AreEqual(UserScopePurgeRunOutcome.Failed, await engine.RunAsync(job.Id, Provider(members)));
            Assert.AreEqual(UserScopePurgeErrorCodes.StateUnavailable, (await _state.GetAsync(job.Id)).ErrorCode);
            Assert.AreEqual(2, _db.Scalar("SELECT COUNT(*) FROM dbo.audit_events"));
            Assert.AreEqual(1, _db.Scalar("SELECT COUNT(*) FROM dbo.copilot_studio_credit_user_daily"));
            Assert.AreEqual(1, _db.Scalar("SELECT COUNT(*) FROM dbo.teams_user_activity_log"));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task UnreadableFenceOnTakeoverStaysRecoverableUntilItsOwnerCanFinish(bool cancel)
        {
            var coverage = new UsageReportCoverageStore(_ledger, true);
            var members = Members(InsideUpn);
            var queued = await QueueAsync(members);
            SeedTeamsDays(_db, 2, $"u.user_name IN ('{InsideUpn}', '{OutsideUpn}')");
            foreach (var day in new[] { FirstDay, FirstDay.AddDays(1) })
                await coverage.CompleteDayAsync("teams", day, true, true, false, FirstDay.AddDays(5), 3);
            using (var stopping = new CancellationTokenSource())
            {
                var first = Engine(true);
                first.AfterWindow = (index, window) =>
                {
                    if (UserScopePurgePlan.Steps[index - 1].Table == "teams_user_activity_log") stopping.Cancel();
                    return Task.CompletedTask;
                };
                Assert.AreEqual(UserScopePurgeRunOutcome.Paused, await first.RunAsync(queued.Id, Provider(members), stopToken: stopping.Token));
            }
            var original = await _state.GetAsync(queued.Id);
            var originalFence = await _ledger.GetStringAsync("v1:scopePurge");
            Assert.AreEqual("active:" + original.RunToken, originalFence);
            Assert.AreEqual(1L, original.RowsAffected["teams_user_activity_log"]);
            if (cancel) Assert.IsTrue(await _state.RequestCancelAsync(queued.Id));
            Assert.AreEqual(UserScopePurgeRunOutcome.Paused, await Engine(false).RunAsync(queued.Id, Provider(members)));
            var outage = new UsageReportCoverageStore(new AzureTableKeyValueStore(_ =>
                Task.FromException<Azure.Data.Tables.TableClient>(new Azure.RequestFailedException(503, "Synthetic fence read failure.")),
                "Contoso", "Coverage"), true);
            for (var i = 0; i < 2; i++)
            {
                var retry = new UserScopePurgeEngine(_database, _state, null, outage);
                Assert.AreEqual(UserScopePurgeRunOutcome.Paused, await retry.RunAsync(queued.Id, Provider(members)));
                var pending = await _state.GetLatestAsync();
                Assert.AreEqual(UserScopePurgeStates.Running, pending.State);
                Assert.AreNotEqual(original.RunToken, pending.RunToken);
                Assert.IsNull(pending.CompletedUtc);
                Assert.AreEqual(1L, pending.RowsAffected["teams_user_activity_log"]);
                Assert.AreEqual(originalFence, await _ledger.GetStringAsync("v1:scopePurge"));
            }
            var touched = await coverage.IsDayVerifiedAsync("teams", FirstDay) ? FirstDay.AddDays(1) : FirstDay;
            var overlap = DateTime.UtcNow;
            await coverage.CompleteDayAsync("teams", touched, true, true, false, overlap, 3);
            await coverage.CompleteDayAsync("outlook", touched, true, true, true, overlap, 3);
            Assert.IsFalse(await coverage.IsDayVerifiedAsync("teams", touched));
            Assert.IsTrue(await coverage.IsDayVerifiedAsync("outlook", touched));

            Assert.AreEqual(cancel ? UserScopePurgeRunOutcome.Cancelled : UserScopePurgeRunOutcome.Completed,
                await Engine(true).RunAsync(queued.Id, Provider(members)));
            var final = await _state.GetAsync(queued.Id);
            Assert.AreEqual(cancel ? 0 : 1, final.UsersDeleted);
            Assert.AreEqual(cancel ? 1L : 2L, final.RowsAffected["teams_user_activity_log"]);
            await Assert.ThrowsExceptionAsync<UserScopePurgeLockLostException>(() => _state.SaveOwnedAsync(original, CancellationToken.None));
            await Assert.ThrowsExceptionAsync<UserScopePurgeLockLostException>(() => coverage.EndScopePurgeAsync(original.RunToken, DateTime.UtcNow));
            await coverage.CompleteDayAsync("teams", touched, true, true, false, overlap, 3);
            Assert.IsFalse(await coverage.IsDayVerifiedAsync("teams", touched));
            await coverage.CompleteDayAsync("teams", touched, true, true, false, final.CompletedUtc.Value.AddSeconds(1), 3);
            Assert.IsTrue(await coverage.IsDayVerifiedAsync("teams", touched));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task MissingStorageAllowsUnaffectedPurgeButNeverDeletesLateUsage(bool lateArrival)
        {
            _state = new UserScopePurgeStateStore(new InMemoryKeyValueStore(), false);
            SeedTeamsDays(_db, 1, $"u.user_name = '{InsideUpn}'");
            var members = Members(InsideUpn);
            var job = await QueueAsync(members);
            var engine = Engine(false);
            var inserted = false;
            engine.AfterWindow = (index, _) =>
            {
                if (lateArrival && !inserted && UserScopePurgePlan.Steps[index - 1].Table == "audit_events")
                {
                    inserted = true;
                    _db.Execute($@"INSERT dbo.outlook_user_activity_log(user_id, [date], email_send_count, email_receive_count,
                        email_read_count, meeting_created_count, meeting_interacted_count) VALUES ({_outside}, '20200101', 1, 0, 0, 0, 0)");
                }
                return Task.CompletedTask;
            };

            Assert.AreEqual(UserScopePurgeRunOutcome.Completed, await engine.RunAsync(job.Id, Provider(members)));
            Assert.AreEqual(lateArrival, inserted);
            Assert.AreEqual(lateArrival ? 1 : 0, engine.Job.UsersSkipped);
            Assert.AreEqual(lateArrival ? 0 : 1, engine.Job.UsersDeleted);
            Assert.AreEqual(lateArrival ? 1 : 0, _db.Scalar($"SELECT COUNT(*) FROM dbo.outlook_user_activity_log WHERE user_id = {_outside}"));
            Assert.AreEqual(0, _db.Scalar("SELECT COUNT(*) FROM dbo.copilot_studio_credit_user_daily"));
            Assert.AreEqual(1, _db.Scalar("SELECT COUNT(*) FROM dbo.teams_user_activity_log"));
        }

        [TestMethod]
        public async Task AReplacementCannotSpendOnePersonsConfirmationTwice()
        {
            var members = Members(InsideUpn);
            var job = await QueueAsync(members);
            var first = Engine(true);
            first.StateCheckInterval = TimeSpan.FromDays(1);
            string replacementRecord = null;
            using (var original = await _database.TryOpenPurgeSessionAsync(TimeSpan.Zero))
            {
                first.AfterWindow = async (step, _) =>
                {
                    if (first.Job.UsersDeleted != 1 || replacementRecord != null) return;
                    Assert.AreEqual(0, _db.Scalar($"SELECT COUNT(*) FROM dbo.users WHERE id = {_outside}"));
                    Assert.AreEqual(0, (await _state.GetAsync(job.Id)).UsersDeleted, "SQL committed before the progress checkpoint.");
                    Assert.AreEqual(UserScopePurgeRunOutcome.NotClaimed, await Engine(true).RunAsync(job.Id, Provider(members)));
                    original.Connection.Close();
                    _db.Execute($"INSERT dbo.users(user_name) VALUES ('{LaterUpn}')");
                    Assert.AreEqual(UserScopePurgeRunOutcome.Failed, await Engine(true).RunAsync(job.Id, Provider(members)));
                    var stopped = await _state.GetAsync(job.Id);
                    Assert.AreEqual(UserScopePurgeErrorCodes.UserDeleteOutcomeUnknown, stopped.ErrorCode);
                    Assert.AreEqual(0, stopped.UsersDeleted, "A reservation is not proof of a deletion.");
                    replacementRecord = Newtonsoft.Json.JsonConvert.SerializeObject(stopped);
                };
                Assert.AreEqual(UserScopePurgeRunOutcome.LockLost, await first.RunAsync(job.Id, Provider(members), lockedSession: original));
            }
            Assert.IsNotNull(replacementRecord);
            Assert.AreEqual(replacementRecord, Newtonsoft.Json.JsonConvert.SerializeObject(await _state.GetAsync(job.Id)));
            Assert.AreEqual(2, _db.Scalar($"SELECT COUNT(*) FROM dbo.users WHERE user_name IN ('{InsideUpn}', '{LaterUpn}')"));
            var confirmedAgain = await QueueAsync(members);
            Assert.AreEqual(UserScopePurgeRunOutcome.Completed, await Engine(true).RunAsync(confirmedAgain.Id, Provider(members)));
            Assert.AreEqual(1, (await _state.GetAsync(confirmedAgain.Id)).UsersDeleted);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AnAcknowledgedUserWindowCanReconnectOrPauseAndResume(bool pause)
        {
            var members = Members(InsideUpn);
            var job = await QueueAsync(members);
            using (var session = await _database.TryOpenPurgeSessionAsync(TimeSpan.Zero))
            using (var stopping = new CancellationTokenSource())
            {
                var engine = Engine(true);
                engine.StateCheckInterval = TimeSpan.FromDays(1);
                var interrupted = false;
                engine.AfterWindow = (_, __) =>
                {
                    if (engine.Job.UsersDeleted == 1 && !interrupted)
                    {
                        interrupted = true;
                        session.Connection.Close();
                        if (pause) stopping.Cancel();
                    }
                    return Task.CompletedTask;
                };
                Assert.AreEqual(pause ? UserScopePurgeRunOutcome.Paused : UserScopePurgeRunOutcome.Completed,
                    await engine.RunAsync(job.Id, Provider(members), stopToken: stopping.Token, lockedSession: session));
                Assert.IsTrue(interrupted);
            }
            if (pause) Assert.AreEqual(UserScopePurgeRunOutcome.Completed, await Engine(true).RunAsync(job.Id, Provider(members)));
            var final = await _state.GetAsync(job.Id);
            Assert.AreEqual(1, final.UsersDeleted);
            Assert.AreEqual(1L, final.RowsAffected["users"]);
            Assert.AreEqual(0, final.UsersSkipped);
            Assert.AreEqual(1, _db.Scalar($"SELECT COUNT(*) FROM dbo.users WHERE user_name = '{InsideUpn}'"));
        }

        [TestMethod]
        public async Task ADisconnectedWorkerCannotRestoreCandidatesFromItsOwnProcessLocalRecord()
        {
            _state = new UserScopePurgeStateStore(new InMemoryKeyValueStore(), false);
            var members = Members(InsideUpn);
            var job = await QueueAsync(members);
            using (var session = await _database.TryOpenPurgeSessionAsync(TimeSpan.Zero))
            {
                var first = Engine(false);
                string record = null;
                first.AfterWindow = async (index, window) =>
                {
                    if (record != null || UserScopePurgePlan.Steps[index - 1].Table != "audit_events") return;
                    session.Connection.Close();
                    var otherHost = new UserScopePurgeStateStore(new InMemoryKeyValueStore(), false);
                    var otherJob = await otherHost.CreateAsync("admin@contoso.example", null, 1, 1, members.Members.MembershipFingerprint);
                    var second = new UserScopePurgeEngine(_database, otherHost, null, new UsageReportCoverageStore(new InMemoryKeyValueStore(), false));
                    Assert.AreEqual(UserScopePurgeRunOutcome.Failed, await second.RunAsync(otherJob.Id, Provider(Members(OutsideUpn))));
                    record = Newtonsoft.Json.JsonConvert.SerializeObject(await _state.GetAsync(job.Id));
                };
                Assert.AreEqual(UserScopePurgeRunOutcome.LockLost, await first.RunAsync(job.Id, Provider(members), lockedSession: session));
                Assert.IsNotNull(record);
                Assert.AreEqual(record, Newtonsoft.Json.JsonConvert.SerializeObject(await _state.GetAsync(job.Id)));
                Assert.AreEqual(1, _db.Scalar($"SELECT COUNT(*) FROM dbo.users WHERE id = {_outside}"));
            }
        }

        [TestMethod]
        public async Task SameOwnerReconnectHonoursTheStopRequestedWhileItsSqlSessionWasGone()
        {
            SeedTeamsDays(_db, 2, $"u.user_name IN ('{InsideUpn}', '{OutsideUpn}')");
            var members = Members(InsideUpn);
            var job = await QueueAsync(members);
            using (var session = await _database.TryOpenPurgeSessionAsync(TimeSpan.Zero))
            {
                var engine = Engine(true);
                engine.AfterWindow = async (index, window) =>
                {
                    if (window != 1 || UserScopePurgePlan.Steps[index - 1].Table != "teams_user_activity_log") return;
                    session.Connection.Close();
                    Assert.IsTrue(await _state.RequestCancelAsync(job.Id));
                };
                Assert.AreEqual(UserScopePurgeRunOutcome.Cancelled, await engine.RunAsync(job.Id, Provider(members), lockedSession: session));
            }
            var cancelled = await _state.GetAsync(job.Id);
            Assert.AreEqual(1L, cancelled.RowsAffected["teams_user_activity_log"]);
            Assert.AreEqual(1, _db.Scalar($"SELECT COUNT(*) FROM dbo.teams_user_activity_log WHERE user_id = {_outside}"));
            Assert.AreEqual(1, _db.Scalar($"SELECT COUNT(*) FROM dbo.users WHERE id = {_outside}"));
        }

        [DataTestMethod]
        [DataRow("changed", "retry")]
        [DataRow("paused", "retry")]
        [DataRow("cancelled", "failure")]
        [DataRow("changed", "shutdown")]
        public async Task ReconnectingWorkerCannotResumeOrFinaliseAfterAReplacement(string replacement, string originalExit)
        {
            SeedTeamsDays(_db, 2, $"u.user_name IN ('{InsideUpn}', '{OutsideUpn}')");
            var members = Members(InsideUpn);
            var job = await QueueAsync(members);
            string replacementRecord = null, replacementFence = null;
            using (var original = await _database.TryOpenPurgeSessionAsync(TimeSpan.Zero))
            using (var stopping = new CancellationTokenSource())
            {
                var first = Engine(true);
                first.AfterWindow = async (step, _) =>
                {
                    if (replacementRecord != null || UserScopePurgePlan.Steps[step - 1].Table != "teams_user_activity_log") return;
                    // A clean reconnect first: ownership must be checked on the SECOND reconnect too.
                    original.Connection.Close();
                    await original.EnsureOpenAsync();
                    original.Connection.Close();
                    if (replacement == "cancelled") Assert.IsTrue(await _state.RequestCancelAsync(job.Id));
                    using (var pauseReplacement = new CancellationTokenSource())
                    {
                        var second = Engine(true);
                        second.AfterWindow = (index, window) => { pauseReplacement.Cancel(); return Task.CompletedTask; };
                        var expected = replacement == "changed" ? UserScopePurgeRunOutcome.Failed
                            : replacement == "cancelled" ? UserScopePurgeRunOutcome.Cancelled : UserScopePurgeRunOutcome.Paused;
                        Assert.AreEqual(expected, await second.RunAsync(job.Id, Provider(replacement == "changed" ? Members(OutsideUpn) : members),
                            stopToken: pauseReplacement.Token));
                    }
                    Assert.IsFalse(await _database.IsPurgeRunningAsync(), "The replacement has already released the SQL lock.");
                    replacementRecord = Newtonsoft.Json.JsonConvert.SerializeObject(await _state.GetAsync(job.Id));
                    replacementFence = await _ledger.GetStringAsync("v1:scopePurge");
                    if (originalExit == "failure") throw new InvalidOperationException("Synthetic late worker failure.");
                    if (originalExit == "shutdown") stopping.Cancel();
                };

                Assert.AreEqual(UserScopePurgeRunOutcome.LockLost, await first.RunAsync(job.Id, Provider(members), stopToken: stopping.Token, lockedSession: original));
            }
            Assert.IsNotNull(replacementRecord);
            Assert.AreEqual(replacementRecord, Newtonsoft.Json.JsonConvert.SerializeObject(await _state.GetAsync(job.Id)));
            Assert.AreEqual(replacementFence, await _ledger.GetStringAsync("v1:scopePurge"));
            Assert.AreEqual(1, _db.Scalar($"SELECT COUNT(*) FROM dbo.teams_user_activity_log WHERE user_id = {_outside}"));
            Assert.AreEqual(1, _db.Scalar($"SELECT COUNT(*) FROM dbo.users WHERE id = {_outside}"));
        }

        [DataTestMethod]
        [DataRow("rejected")]
        [DataRow("committed")]
        [DataRow("rolledBack")]
        [DataRow("handoff")]
        [DataRow("handoffCancel")]
        public async Task CoverageChangesOnlyAfterSqlAcceptsTheDeleteEvenIfTheCommitReplyIsLost(string outcome)
        {
            var loseCommitReply = outcome != "rejected";
            var cancel = outcome == "handoffCancel";
            var handoff = cancel || outcome == "handoff";
            var commitHappened = outcome != "rolledBack";
            SeedTeamsDays(_db, 2, $"u.user_name IN ('{InsideUpn}', '{OutsideUpn}')");
            var coverage = new UsageReportCoverageStore(_ledger, true);
            foreach (var day in new[] { FirstDay, FirstDay.AddDays(1) })
                await coverage.CompleteDayAsync("teams", day, true, true, false, FirstDay.AddDays(5), 3);
            await coverage.CompleteDayAsync("outlook", FirstDay, true, true, true, FirstDay.AddDays(4), 3);
            var before = await _ledger.GetVersionedAsync("v1:teams:20200101");
            var members = Members(InsideUpn);
            var job = await QueueAsync(members);
            var engine = Engine(true);
            using (var session = await _database.TryOpenPurgeSessionAsync(TimeSpan.Zero))
            {
                if (loseCommitReply)
                {
                    var attempts = 0;
                    engine.CommitUsageTransaction = tx =>
                    {
                        if (++attempts == 1) { tx.Commit(); return; }
                        if (commitHappened) tx.Commit();
                        else tx.Rollback();
                        if (handoff)
                        {
                            // Only the durable pre-COMMIT marker and previous acknowledgement
                            // survive the old worker losing SQL ownership before reporting the failure.
                            session.Connection.Close();
                            if (cancel) Assert.IsTrue(_state.RequestCancelAsync(job.Id).GetAwaiter().GetResult());
                            Assert.AreEqual(cancel ? UserScopePurgeRunOutcome.Cancelled : UserScopePurgeRunOutcome.Failed,
                                Engine(true).RunAsync(job.Id, Provider(members)).GetAwaiter().GetResult());
                        }
                        throw new TimeoutException("Synthetic lost commit reply.");
                    };
                }
                else
                {
                    _db.Execute(@"CREATE USER ContosoPurgeWriter WITHOUT LOGIN;
                        ALTER ROLE db_datareader ADD MEMBER ContosoPurgeWriter;
                        ALTER ROLE db_datawriter ADD MEMBER ContosoPurgeWriter;
                        DENY DELETE ON dbo.teams_user_activity_log TO ContosoPurgeWriter;");
                    using (var asWriter = session.Connection.CreateCommand())
                    {
                        asWriter.CommandText = "EXECUTE AS USER = 'ContosoPurgeWriter'";
                        await asWriter.ExecuteNonQueryAsync();
                    }
                }
                Assert.AreEqual(handoff ? UserScopePurgeRunOutcome.LockLost : UserScopePurgeRunOutcome.Failed,
                    await engine.RunAsync(job.Id, Provider(members), lockedSession: session));
            }
            var stopped = await _state.GetAsync(job.Id);
            Assert.AreEqual(cancel ? UserScopePurgeStates.Cancelled : UserScopePurgeStates.Failed, stopped.State);
            const string table = "teams_user_activity_log";
            if (loseCommitReply)
            {
                Assert.AreEqual(UserScopePurgeErrorCodes.UsageDeleteOutcomeUnknown, stopped.ErrorCode);
                Assert.AreEqual(table, stopped.UsageCommitPendingTable);
                Assert.AreEqual(1L, stopped.RowsAffected[table], "Only the first, acknowledged commit counts.");
                var row = UserScopeService.ToModel(stopped).RowsAffected.Find(r => r.Table == table);
                Assert.AreEqual(1L, row.Rows);
                Assert.IsTrue(row.IsLowerBound);
            }
            else
            {
                Assert.IsNull(stopped.UsageCommitPendingTable);
                Assert.IsFalse(stopped.RowsAffected.ContainsKey(table));
                Assert.AreEqual(UserScopePurgeErrorCodes.DatabaseError, stopped.ErrorCode);
            }
            Assert.AreEqual(loseCommitReply ? (commitHappened ? 2 : 3) : 4, _db.Scalar($"SELECT COUNT(*) FROM dbo.{table}"));
            Assert.AreEqual(1, _db.Scalar($"SELECT COUNT(*) FROM dbo.users WHERE id = {_outside}"));
            Assert.AreEqual(!loseCommitReply, await coverage.IsDayVerifiedAsync("teams", FirstDay));
            Assert.AreEqual(!loseCommitReply, await coverage.IsDayVerifiedAsync("teams", FirstDay.AddDays(1)));
            if (!loseCommitReply)
            {
                StringAssert.Contains(stopped.ErrorDetail, "SQL error 229");
                Assert.AreEqual(before.VersionToken, (await _ledger.GetVersionedAsync("v1:teams:20200101")).VersionToken);
            }
            Assert.IsTrue(await coverage.IsDayVerifiedAsync("outlook", FirstDay));
        }
    }
}
