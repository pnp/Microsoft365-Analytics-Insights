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
DROP TRIGGER IF EXISTS dbo.contoso_delete_ack;
DELETE FROM dbo.copilot_studio_credit_user_daily;
DELETE FROM dbo.audit_events;
DELETE FROM dbo.teams_user_activity_log;
DELETE FROM dbo.outlook_user_activity_log;
DELETE FROM dbo.users WHERE user_name IN ('{InsideUpn}', '{OutsideUpn}');
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

        [TestMethod]
        public async Task UnacknowledgedSqlDeleteKeepsItsDayUnverified()
        {
            SeedTeamsDays(_db, 1, $"u.user_name IN ('{InsideUpn}', '{OutsideUpn}')");
            var coverage = new UsageReportCoverageStore(_ledger, true);
            await coverage.CompleteDayAsync("teams", FirstDay, true, true, false, FirstDay.AddDays(4), 3);
            // RAISERROR (unlike THROW) does not honour XACT_ABORT. The statement commits but SqlClient
            // sees an error rather than a successful row count. Read back the facts to prove which happened.
            _db.Execute(@"CREATE TRIGGER dbo.contoso_delete_ack ON dbo.teams_user_activity_log AFTER DELETE AS
                IF EXISTS (SELECT 1 FROM deleted) RAISERROR ('Synthetic missing success acknowledgement', 16, 1);");
            var members = Members(InsideUpn);
            var job = await QueueAsync(members);
            Assert.AreEqual(UserScopePurgeRunOutcome.Failed, await Engine(true).RunAsync(job.Id, Provider(members)));
            Assert.AreEqual(1, _db.Scalar("SELECT COUNT(*) FROM dbo.teams_user_activity_log"));
            Assert.IsFalse(await coverage.IsDayVerifiedAsync("teams", FirstDay));
        }
    }
}
