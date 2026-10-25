using Common.Entities;
using Common.Entities.UserDiagnostics;
using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Maintenance;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Rules;

namespace Tests.UnitTests
{
    /// <summary>
    /// The importer-side cleanup of the placeholder users older builds created from concealed (hashed) usage-report
    /// identities. Every test runs against SQL Server (the LocalDB test catalog) with synthetic users only, and scopes the
    /// cleanup to the users it created (<c>MinUserIdExclusive</c>), so nothing else in the shared catalog is touched.
    /// </summary>
    [TestClass]
    public class ConcealedIdentityUserCleanupTests
    {
        /// <summary>Microsoft's documented example of a concealed UPN (the getMicrosoft365CopilotUsageUserDetail response).</summary>
        private const string DocumentedConcealedUpn = "DC8C64D6EC3A3AA17481D7E5EB5B68A6";

        private static string _connectionString;
        private readonly List<int> _createdUserIds = new List<int>();
        private readonly Dictionary<string, string> _requiredColumnsByTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private int _baselineUserId;
        private int? _licenceTypeId;
        private readonly List<Guid> _eventIds = new List<Guid>();
        private int? _orgTypeId;
        private int? _orgJobId;

        private static IReadOnlyList<string> UsageTables => ConcealedIdentityUserCleanupSql.UsageReportActivityTables;

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.Initialize(false);
                _connectionString = db.Database.Connection.ConnectionString;
            }
        }

        [TestInitialize]
        public void Baseline() => _baselineUserId = Scalar<int>("SELECT ISNULL(MAX(id), 0) FROM dbo.users;");

        [TestCleanup]
        public void RemoveTestRows()
        {
            var sql = new System.Text.StringBuilder();
            if (_eventIds.Count > 0)
            {
                var events = string.Join(",", _eventIds.Select(e => $"'{e}'"));
                sql.AppendLine($@"DELETE FROM dbo.copilot_chats WHERE event_id IN ({events});
DELETE FROM dbo.event_meta_power_app_share WHERE event_id IN ({events});
DELETE FROM dbo.event_meta_power_automate_flow_share WHERE event_id IN ({events});
DELETE FROM dbo.audit_events WHERE id IN ({events});");
            }
            if (_orgJobId.HasValue) sql.AppendLine($"DELETE FROM dbo.user_org_import_changes WHERE job_id = {_orgJobId}; DELETE FROM dbo.user_org_import_jobs WHERE id = {_orgJobId};");
            if (_orgTypeId.HasValue) sql.AppendLine($"DELETE FROM dbo.user_org_types WHERE id = {_orgTypeId};");
            if (_createdUserIds.Count > 0)
            {
                var ids = string.Join(",", _createdUserIds);
                sql.AppendLine(string.Join(Environment.NewLine, UsageTables.Select(t => $"DELETE FROM dbo.[{t}] WHERE user_id IN ({ids});")));
                sql.AppendLine($@"DELETE FROM dbo.copilot_usage_user_activity_log WHERE user_id IN ({ids});
DELETE FROM dbo.cowork_usage_user_activity_log WHERE user_id IN ({ids});
DELETE FROM dbo.user_license_type_lookups WHERE user_id IN ({ids});
DELETE FROM dbo.audit_events WHERE user_id IN ({ids});
UPDATE dbo.users SET manager_id = NULL WHERE manager_id IN ({ids});
DELETE FROM dbo.users WHERE id IN ({ids});");
            }
            if (_licenceTypeId.HasValue) sql.AppendLine($"DELETE FROM dbo.license_types WHERE id = {_licenceTypeId.Value};");
            if (sql.Length > 0) Execute(sql.ToString());
        }

        /// <summary>An audit event that names no user: the parent a share or a chat needs, without itself keeping anyone.</summary>
        private Guid InsertAuditEvent()
        {
            var id = Guid.NewGuid();
            Execute($"INSERT INTO dbo.audit_events (id, time_stamp, user_id) VALUES ('{id}', '2026-09-30', NULL);");
            _eventIds.Add(id);
            return id;
        }

        [TestMethod]
        public async Task PlaceholdersReferencedFromTheTablesWithoutAUserIndex_AreKept()
        {
            var control = InsertUser(NewHash());
            var byCowork = InsertUser(NewHash());
            var byAppShare = InsertUser(NewHash());
            var byFlowShare = InsertUser(NewHash());
            var byCopilotChat = InsertUser(NewHash());
            var byOrgImportChange = InsertUser(NewHash());
            var kept = new[] { byCowork, byAppShare, byFlowShare, byCopilotChat, byOrgImportChange };
            foreach (var id in kept.Concat(new[] { control })) InsertActivity("teams_user_activity_log", id, days: 2);

            Execute($"INSERT INTO dbo.cowork_usage_user_activity_log (user_id, [date], report_period_days) VALUES ({byCowork}, '2026-09-30', 7);");
            Execute($"INSERT INTO dbo.event_meta_power_app_share (event_id, shared_with_user_id) VALUES ('{InsertAuditEvent()}', {byAppShare});");
            Execute($"INSERT INTO dbo.event_meta_power_automate_flow_share (event_id, shared_with_user_id) VALUES ('{InsertAuditEvent()}', {byFlowShare});");
            // A chat whose audit event names nobody: only copilot_chats.user_id refers to the placeholder.
            Execute($"INSERT INTO dbo.copilot_chats (event_id, user_id, time_stamp) VALUES ('{InsertAuditEvent()}', {byCopilotChat}, '2026-09-30');");
            _orgTypeId = Scalar<int>("INSERT INTO dbo.user_org_types (name, source_kind) OUTPUT INSERTED.id VALUES (N'Contoso test organisation', 1);");
            _orgJobId = Scalar<int>($"INSERT INTO dbo.user_org_import_jobs (org_type_id, mode, status, started_by) OUTPUT INSERTED.id VALUES ({_orgTypeId}, 1, 1, N'admin@contoso.com');");
            Execute($"INSERT INTO dbo.user_org_import_changes (job_id, user_id, upn) VALUES ({_orgJobId}, {byOrgImportChange}, N'placeholder');");

            var result = await NewCleanup(new RecordingLogger()).RunIfDueAsync();

            Assert.IsNull(result.Error, result.Error?.ToString());
            Assert.IsFalse(UserExists(control), "The control placeholder is deleted, so the run did run.");
            foreach (var id in kept)
            {
                Assert.IsTrue(UserExists(id), $"User {id} must be kept.");
                Assert.AreEqual(2, CountActivity("teams_user_activity_log", id), "A kept user keeps its usage-report rows.");
            }
            Assert.AreEqual(1, Scalar<int>($"SELECT COUNT(*) FROM dbo.cowork_usage_user_activity_log WHERE user_id = {byCowork};"), "Not cascade-deleted.");
            Assert.AreEqual(1, Scalar<int>($"SELECT COUNT(*) FROM dbo.event_meta_power_app_share WHERE shared_with_user_id = {byAppShare};"));
            Assert.AreEqual(1, Scalar<int>($"SELECT COUNT(*) FROM dbo.event_meta_power_automate_flow_share WHERE shared_with_user_id = {byFlowShare};"));
            Assert.AreEqual(1, Scalar<int>($"SELECT COUNT(*) FROM dbo.copilot_chats WHERE user_id = {byCopilotChat};"));
            Assert.AreEqual(1, Scalar<int>($"SELECT COUNT(*) FROM dbo.user_org_import_changes WHERE user_id = {byOrgImportChange};"));

            // Four by the run's single read of those tables; the organisation-import change by its locked check in the batch.
            Assert.AreEqual(4, result.KeptByRunSnapshot);
            Assert.AreEqual(5, result.UsersKept);
            Assert.AreEqual(1, result.UsersDeleted);
        }

        [TestMethod]
        public async Task ALateNoActionReference_RollsTheBatchBack_AndTheNextRunKeepsTheUser()
        {
            var placeholder = InsertUser(NewHash());
            InsertActivity("teams_user_activity_log", placeholder, days: 2);
            InsertActivity("platform_user_activity_log", placeholder, days: 2);
            var eventId = InsertAuditEvent();

            // After the once-per-run read has found nothing, a Power Platform share names the placeholder. Its foreign key is
            // NO ACTION, so the batch's DELETE FROM dbo.users must fail and roll back, not orphan or cascade it.
            var first = NewCleanup(new RecordingLogger());
            first.AfterReferenceRead = () =>
            {
                Execute($"INSERT INTO dbo.event_meta_power_app_share (event_id, shared_with_user_id) VALUES ('{eventId}', {placeholder});");
                return Task.CompletedTask;
            };
            var failed = await first.RunIfDueAsync();

            Assert.IsInstanceOfType(failed.Error, typeof(SqlException));
            Assert.AreEqual(547, ((SqlException)failed.Error).Number, "The REFERENCE constraint stops the delete.");
            Assert.AreEqual(0, failed.UsersDeleted);
            Assert.AreEqual(ConcealedIdentityUserCleanup.MaxBatchSize, first.BatchSizeLimit, "A reference that appeared mid-batch is not about size.");
            Assert.IsTrue(UserExists(placeholder));
            Assert.AreEqual(2, CountActivity("teams_user_activity_log", placeholder), "The batch's earlier deletes are rolled back with it.");
            Assert.AreEqual(2, CountActivity("platform_user_activity_log", placeholder));
            Assert.AreEqual(1, Scalar<int>($"SELECT COUNT(*) FROM dbo.event_meta_power_app_share WHERE shared_with_user_id = {placeholder};"));

            var next = await NewCleanup(new RecordingLogger()).RunIfDueAsync();
            Assert.IsNull(next.Error, next.Error?.ToString());
            Assert.AreEqual(1, next.KeptByRunSnapshot, "The next run's read sees the share and keeps the user before any batch.");
            Assert.AreEqual(0, next.UsersDeleted);
            Assert.IsTrue(UserExists(placeholder));
        }

        private static string NewHash() => Guid.NewGuid().ToString("N");

        private ConcealedIdentityUserCleanup NewCleanup(RecordingLogger logger, bool enabled = true) =>
            new ConcealedIdentityUserCleanup(_connectionString, enabled, logger) { MinUserIdExclusive = _baselineUserId };

        [TestMethod]
        public async Task ConcealedPlaceholders_AreDeletedWithTheirUsageReportRows_AndEveryOtherUserIsKept()
        {
            var placeholders = new[] { InsertUser(NewHash()), InsertUser(NewHash()) };
            foreach (var id in placeholders)
            {
                foreach (var table in UsageTables) InsertActivity(table, id, days: 3);
            }

            var realUser = InsertUser($"ada.{NewHash().Substring(0, 8)}@contoso.com");
            var hashWithDirectoryId = InsertUser(NewHash(), azureAdId: "00000000-0000-0000-0000-0000000000a1");
            var hashWithMail = InsertUser(NewHash(), mail: "someone@contoso.com");
            var referencedByAudit = InsertUser(NewHash());
            var referencedByCopilot = InsertUser(NewHash());
            var referencedByLicence = InsertUser(NewHash());
            var managerOfSomeone = InsertUser(NewHash());
            InsertUser($"report.{NewHash().Substring(0, 8)}@contoso.com", managerId: managerOfSomeone);
            var kept = new[] { realUser, hashWithDirectoryId, hashWithMail, referencedByAudit, referencedByCopilot, referencedByLicence, managerOfSomeone };
            foreach (var id in kept)
            {
                InsertActivity("teams_user_activity_log", id, days: 3);
                InsertActivity("outlook_user_activity_log", id, days: 2);
            }
            Execute($"INSERT INTO dbo.audit_events (id, time_stamp, user_id) VALUES (NEWID(), '2026-09-30', {referencedByAudit});");
            // ON DELETE CASCADE, and no index leading with user_id in the shipped schema - so both are normally read once per
            // run, not in every batch: deleting the user would silently delete these rows too.
            Execute($"INSERT INTO dbo.copilot_usage_user_activity_log (user_id, [date], report_period_days, is_upn_obfuscated) VALUES ({referencedByCopilot}, '2026-09-30', 7, 0);");
            _licenceTypeId = Scalar<int>($"INSERT INTO dbo.license_types (sku_id, name) OUTPUT INSERTED.id VALUES (N'00000000-0000-0000-0000-0000000000b1', N'Contoso test SKU');");
            Execute($"INSERT INTO dbo.user_license_type_lookups (user_id, license_type_id) VALUES ({referencedByLicence}, {_licenceTypeId});");

            var logger = new RecordingLogger();
            var result = await NewCleanup(logger).RunIfDueAsync();

            Assert.AreEqual(ConcealedIdentityUserCleanupOutcome.Ran, result.Outcome);
            Assert.IsNull(result.Error, result.Error?.ToString());
            foreach (var id in placeholders)
            {
                Assert.IsFalse(UserExists(id), "A placeholder nothing else refers to is deleted.");
                foreach (var table in UsageTables)
                {
                    Assert.AreEqual(0, CountActivity(table, id), $"Its {table} rows go with it.");
                }
            }
            Assert.AreEqual(2, result.UsersDeleted);
            Assert.AreEqual(2 * UsageTables.Count * 3, result.ActivityRowsDeleted);

            foreach (var id in kept)
            {
                Assert.IsTrue(UserExists(id), $"User {id} must be kept.");
                Assert.AreEqual(3, CountActivity("teams_user_activity_log", id), $"A kept user keeps its usage-report rows (user {id}).");
                Assert.AreEqual(2, CountActivity("outlook_user_activity_log", id));
            }
            Assert.AreEqual(1, Scalar<int>($"SELECT COUNT(*) FROM dbo.audit_events WHERE user_id = {referencedByAudit};"));
            Assert.AreEqual(1, Scalar<int>($"SELECT COUNT(*) FROM dbo.copilot_usage_user_activity_log WHERE user_id = {referencedByCopilot};"),
                "The Copilot row must not have been cascade-deleted.");
            Assert.AreEqual(1, Scalar<int>($"SELECT COUNT(*) FROM dbo.user_license_type_lookups WHERE user_id = {referencedByLicence};"),
                "The licence must not have been cascade-deleted.");

            // The two with a directory id or mail never qualify; the four referenced elsewhere are kept. The Copilot and licence
            // ones are kept by the run's single read of their tables, before any batch - unless the table has an index leading
            // with user_id (the optional profiling extension adds IX_user_id to user_license_type_lookups): that check is then a
            // seek made under lock in every batch, and the user is kept by the batch instead. Either way all four are kept.
            var readOncePerRun = new[] { "copilot_usage_user_activity_log", "user_license_type_lookups" }.Count(t => !HasIndexLeadingWith(t, "user_id"));
            Assert.AreEqual(6, result.Candidates);
            Assert.AreEqual(readOncePerRun, result.KeptByRunSnapshot,
                "Kept by the run's single read: one for each of the two tables with no index leading with user_id.");
            Assert.AreEqual(4, result.UsersKept);

            Assert.AreEqual(1, logger.Entries.Count, "One summary line per run: " + string.Join(" | ", logger.Entries.Select(e => e.Message)));
            Assert.AreEqual(LogLevel.Information, logger.Entries[0].Level);
            StringAssert.Contains(logger.Entries[0].Message, "deleted 2 placeholder user(s)");
            StringAssert.Contains(logger.Entries[0].Message, "4 user(s) that look like concealed identities were kept");
            StringAssert.Contains(logger.Entries[0].Message, ConcealedIdentityUserCleanup.AppSettingName);

            // The numbers a log export needs to tell a slow cleanup from a stuck one: where the time went before the first batch,
            // what the batches looked like, and what the sizer will do next.
            StringAssert.Contains(logger.Entries[0].Message, "Preflight ");
            StringAssert.Contains(logger.Entries[0].Message, "reference reads ");
            StringAssert.Contains(logger.Entries[0].Message, $"Batches 1: users min/median/max {result.Candidates - result.KeptByRunSnapshot}/");
            StringAssert.Contains(logger.Entries[0].Message, "Next batch ");
            StringAssert.Contains(logger.Entries[0].Message, ", size limit 1,000");
            Assert.IsTrue(result.PreflightElapsed >= result.ReferenceReadElapsed);
            Assert.IsTrue(result.ReferenceReads.Count >= readOncePerRun, "One timed read per table with no index leading with its user column.");
        }

        [TestMethod]
        public async Task AnUnreadableKillSwitchValue_TurnsTheCleanupOff_AndSaysSoOnceAsAnError()
        {
            var placeholder = InsertUser(NewHash());
            InsertActivity("teams_user_activity_log", placeholder, days: 1);
            var logger = new RecordingLogger();
            var cleanup = new ConcealedIdentityUserCleanup(_connectionString, enabled: false, logger, unparseableSettingValue: "off") { MinUserIdExclusive = _baselineUserId };

            var first = await cleanup.RunIfDueAsync();
            var second = await cleanup.RunIfDueAsync();

            Assert.AreEqual(ConcealedIdentityUserCleanupOutcome.Disabled, first.Outcome);
            Assert.AreEqual(ConcealedIdentityUserCleanupOutcome.Disabled, second.Outcome);
            Assert.IsTrue(UserExists(placeholder), "An unreadable value must never leave the irreversible delete on.");
            Assert.AreEqual(1, logger.Entries.Count, "Once per process: " + string.Join(" | ", logger.Entries.Select(e => e.Message)));
            Assert.AreEqual(LogLevel.Error, logger.Entries[0].Level);
            StringAssert.Contains(logger.Entries[0].Message, ConcealedIdentityUserCleanup.AppSettingName);
            StringAssert.Contains(logger.Entries[0].Message, "'off'");
            StringAssert.Contains(logger.Entries[0].Message, "OFF");
        }

        [TestMethod]
        public async Task APreflightThatRunsOutOfItsBudget_ChangesNothing_AndWaitsBeforeReadingAgain()
        {
            // The reads of the tables with no index on their user column run while the users-and-licences lock is held, and
            // used to be allowed 30 minutes each, before the run's own two-minute budget was ever looked at.
            var readOncePerRun = new[] { "copilot_usage_user_activity_log", "user_license_type_lookups" }.Count(t => !HasIndexLeadingWith(t, "user_id"));
            if (readOncePerRun == 0) Assert.Inconclusive("Every reference has an index leading with its user column here, so there is no preflight read to bound.");

            var placeholder = InsertUser(NewHash());
            InsertActivity("teams_user_activity_log", placeholder, days: 2);
            var logger = new RecordingLogger();
            var cleanup = NewCleanup(logger);
            cleanup.PreflightBudget = TimeSpan.FromMilliseconds(50);
            cleanup.BeforeEachReferenceRead = () => Task.Delay(300);

            var result = await cleanup.RunIfDueAsync();

            Assert.AreEqual(ConcealedIdentityUserCleanupOutcome.Ran, result.Outcome);
            Assert.IsTrue(result.PreflightExceeded);
            Assert.IsNull(result.Error);
            Assert.AreEqual(0, result.Batches);
            Assert.AreEqual(0, result.UsersDeleted);
            Assert.AreEqual(1, result.ReferenceReads.Count, "It stops at the first table it has no time for.");
            Assert.IsTrue(UserExists(placeholder));
            Assert.AreEqual(2, CountActivity("teams_user_activity_log", placeholder));
            Assert.AreEqual(1, logger.Entries.Count);
            Assert.AreEqual(LogLevel.Warning, logger.Entries[0].Level);
            StringAssert.Contains(logger.Entries[0].Message, "ran out of its time budget");
            StringAssert.Contains(logger.Entries[0].Message, "Preflight ");

            var again = await cleanup.RunIfDueAsync();
            Assert.AreEqual(ConcealedIdentityUserCleanupOutcome.NotDue, again.Outcome, "Not read again every cycle: it waits as after a failure.");
        }

        [TestMethod]
        public async Task NamesThatOnlyResembleAConcealedIdentity_AreNeverDeleted()
        {
            var exact = InsertUser(NewHash());
            var nearMisses = new[]
            {
                NewHash().ToUpperInvariant(),                         // Graph's own spelling; the importer always stored lower case
                "A" + NewHash().Substring(1),                         // one upper-case letter
                NewHash().Substring(1),                               // 31 characters
                NewHash() + "a",                                      // 33
                NewHash() + NewHash(),                                // 64
                NewHash().Substring(1) + "g",                         // not hexadecimal
                NewHash() + " ",                                      // trailing space - invisible to LEN and LIKE
                " " + NewHash().Substring(1),                         // leading space
                NewHash() + "@" + NewHash().Substring(0, 8),          // hash@hash
                NewHash() + "@contoso.com",                           // a real address with a hexadecimal local part
            }.Select(name => InsertUser(name)).ToList();
            foreach (var id in nearMisses.Concat(new[] { exact }))
            {
                InsertActivity("teams_user_activity_log", id, days: 2);
            }

            var result = await NewCleanup(new RecordingLogger()).RunIfDueAsync();

            Assert.IsFalse(UserExists(exact), "The control - an exact placeholder - is deleted, so the run did run.");
            Assert.AreEqual(1, result.UsersDeleted);
            foreach (var id in nearMisses)
            {
                Assert.IsTrue(UserExists(id), $"'{Scalar<string>($"SELECT user_name FROM dbo.users WHERE id = {id};")}' must be kept.");
                Assert.AreEqual(2, CountActivity("teams_user_activity_log", id));
            }
        }

        [TestMethod]
        public void SqlRule_AgreesWithTheCSharpRule_AndWithTheImportPolicy()
        {
            // What the old loader stored for Microsoft's example: the policy calls it concealed, and its lower-cased form is a placeholder.
            Assert.IsTrue(UsageReportIdentityPolicy.IsConcealedUserIdentity(DocumentedConcealedUpn));
            Assert.IsTrue(ConcealedIdentityPlaceholderRule.IsPlaceholderUserName(DocumentedConcealedUpn.ToLowerInvariant()));

            var samples = new[]
            {
                DocumentedConcealedUpn.ToLowerInvariant(), DocumentedConcealedUpn, "0123456789abcdef0123456789abcdef",
                "0123456789abcdef0123456789abcdeF", "0123456789abcdef0123456789abcde", "0123456789abcdef0123456789abcdef0",
                "0123456789abcdef0123456789abcdeg", "0123456789abcdef0123456789abcdef ", " 123456789abcdef0123456789abcdef",
                "0123456789abcdef@123456789abcdef", "0123456789abcdef0123456789abcdef@contoso.com", "ada@contoso.com", "", "Unknown User",
            };
            foreach (var name in samples)
            {
                var sqlSays = Scalar<int>($"SELECT CASE WHEN {ConcealedIdentityPlaceholderRule.SqlUserNameMatches} THEN 1 ELSE 0 END " +
                    "FROM (SELECT CAST(@name AS varchar(250)) AS user_name) AS u;", new SqlParameter("@name", name)) == 1;
                Assert.AreEqual(ConcealedIdentityPlaceholderRule.IsPlaceholderUserName(name), sqlSays, $"The two rules disagree on '{name}'.");
            }
        }

        [TestMethod]
        public async Task AnInterruptedRun_ResumesOnTheNextRun_AndARerunChangesNothing()
        {
            var ids = Enumerable.Range(0, 5).Select(_ => InsertUser(NewHash())).ToList();
            foreach (var id in ids)
            {
                InsertActivity("teams_user_activity_log", id, days: 2);
                InsertActivity("sharepoint_user_activity_log", id, days: 2);
            }

            var logger = new RecordingLogger();
            var cleanup = NewCleanup(logger);
            cleanup.BatchSize = 2;
            cleanup.MaxBatchesPerRun = 1;

            var first = await cleanup.RunIfDueAsync();
            Assert.AreEqual(2, first.UsersDeleted, "One batch of two.");
            Assert.AreEqual(3, first.Remaining);
            CollectionAssert.AreEqual(new[] { false, false, true, true, true }, ids.Select(UserExists).ToArray(), "Batches go in id order.");
            Assert.AreEqual(1, logger.Entries.Count);
            StringAssert.Contains(logger.Entries[0].Message, "3 still to check");

            cleanup.MaxBatchesPerRun = null;
            var second = await cleanup.RunIfDueAsync();
            Assert.AreEqual(3, second.UsersDeleted, "The next run picks up where the first stopped.");
            Assert.AreEqual(0, second.Remaining);
            Assert.IsTrue(ids.All(id => !UserExists(id)));
            Assert.AreEqual(0, ids.Sum(id => CountActivity("teams_user_activity_log", id) + CountActivity("sharepoint_user_activity_log", id)));

            var third = await cleanup.RunIfDueAsync();
            Assert.AreEqual(ConcealedIdentityUserCleanupOutcome.NothingToDo, third.Outcome, "Idempotent: nothing left to do.");
            var fourth = await cleanup.RunIfDueAsync();
            Assert.AreEqual(ConcealedIdentityUserCleanupOutcome.NotDue, fourth.Outcome, "Once nothing is left it is not looked at again until the recheck interval.");
            Assert.AreEqual(3, logger.Entries.Count, "One line per run that did something, and one to say there was nothing: " +
                string.Join(" | ", logger.Entries.Select(e => e.Message)));
        }

        [TestMethod]
        public async Task ABatchThatCannotFinish_IsRolledBackWhole_AndTheNextRunCompletesIt()
        {
            var ids = Enumerable.Range(0, 3).Select(_ => InsertUser(NewHash())).ToList();
            foreach (var id in ids)
            {
                InsertActivity("teams_user_activity_log", id, days: 2);       // deleted first in a batch
                InsertActivity("platform_user_activity_log", id, days: 2);    // deleted last
            }

            var logger = new RecordingLogger();
            var cleanup = NewCleanup(logger);
            ConcealedIdentityUserCleanupResult failed;
            using (var blocker = AzureSqlTokenAuth.CreateConnection(_connectionString))
            {
                await blocker.OpenAsync();
                using (var transaction = blocker.BeginTransaction())
                {
                    using (var hold = new SqlCommand("SELECT COUNT(*) FROM dbo.platform_user_activity_log WITH (TABLOCKX, HOLDLOCK);", blocker, transaction))
                    {
                        await hold.ExecuteScalarAsync();
                    }

                    cleanup.LockTimeoutMilliseconds = 500;
                    failed = await cleanup.RunIfDueAsync();
                    transaction.Rollback();
                }
            }

            Assert.IsInstanceOfType(failed.Error, typeof(SqlException));
            Assert.AreEqual(1222, ((SqlException)failed.Error).Number, "Lock request time out.");
            Assert.AreEqual(0, failed.UsersDeleted);
            foreach (var id in ids)
            {
                Assert.IsTrue(UserExists(id));
                Assert.AreEqual(2, CountActivity("teams_user_activity_log", id), "Rows deleted earlier in the failed batch are rolled back with it.");
                Assert.AreEqual(2, CountActivity("platform_user_activity_log", id));
            }
            Assert.AreEqual(1, logger.Entries.Count);
            // A unit of work failed: an Error, naming the step, the exception and the SQL error number.
            Assert.AreEqual(LogLevel.Error, logger.Entries[0].Level);
            StringAssert.Contains(logger.Entries[0].Message, $"tries again in {ConcealedIdentityUserCleanup.FailureRetryInterval.TotalMinutes:N0} minutes");
            StringAssert.Contains(logger.Entries[0].Message, "step 'batch' (SqlException, SQL error 1222)");
            StringAssert.Contains(logger.Entries[0].Message, "Next batch 1 user(s), size limit 1,000");
            Assert.AreEqual(1, cleanup.BatchSize, "Any failure halves the next batch.");
            Assert.AreEqual(ConcealedIdentityUserCleanup.MaxBatchSize, cleanup.BatchSizeLimit, "A lock timeout is not about size: it sets no lasting limit.");

            var retried = await NewCleanup(new RecordingLogger()).RunIfDueAsync();
            Assert.AreEqual(3, retried.UsersDeleted);
            Assert.IsTrue(ids.All(id => !UserExists(id) && CountActivity("teams_user_activity_log", id) == 0 && CountActivity("platform_user_activity_log", id) == 0));
        }

        [TestMethod]
        public async Task ABatchThatTimesOut_IsRolledBackWhole_AndNoLaterBatchIsThatBig()
        {
            var ids = Enumerable.Range(0, 4).Select(_ => InsertUser(NewHash())).ToList();
            foreach (var id in ids)
            {
                InsertActivity("teams_user_activity_log", id, days: 2);
                InsertActivity("platform_user_activity_log", id, days: 2);
            }

            var clock = new global::UnitTests.FakeLoaderClasses.FixedClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var cleanup = new ConcealedIdentityUserCleanup(_connectionString, true, new RecordingLogger(), clock) { MinUserIdExclusive = _baselineUserId };
            cleanup.BatchSize = 4;
            ConcealedIdentityUserCleanupResult failed;
            using (var blocker = AzureSqlTokenAuth.CreateConnection(_connectionString))
            {
                await blocker.OpenAsync();
                using (var transaction = blocker.BeginTransaction())
                {
                    using (var hold = new SqlCommand("SELECT COUNT(*) FROM dbo.platform_user_activity_log WITH (TABLOCKX, HOLDLOCK);", blocker, transaction))
                    {
                        await hold.ExecuteScalarAsync();
                    }

                    // The batch would wait for the lock for longer than its command may run, so the command timeout stops it.
                    cleanup.LockTimeoutMilliseconds = 60000;
                    cleanup.CommandTimeoutSeconds = 1;
                    failed = await cleanup.RunIfDueAsync();
                    transaction.Rollback();
                }
            }

            Assert.IsInstanceOfType(failed.Error, typeof(SqlException));
            Assert.AreEqual(-2, ((SqlException)failed.Error).Number, "The command timeout.");
            Assert.AreEqual(0, failed.UsersDeleted);
            foreach (var id in ids)
            {
                Assert.IsTrue(UserExists(id));
                Assert.AreEqual(2, CountActivity("teams_user_activity_log", id), "Rows deleted earlier in the timed-out batch are rolled back with it.");
                Assert.AreEqual(2, CountActivity("platform_user_activity_log", id));
            }
            Assert.AreEqual(2, cleanup.BatchSize, "The next batch is half the one that timed out.");
            Assert.AreEqual(2, cleanup.BatchSizeLimit, "A timeout is about size: no later batch is that big again.");

            clock.Advance(ConcealedIdentityUserCleanup.FailureRetryInterval);
            cleanup.CommandTimeoutSeconds = ConcealedIdentityUserCleanup.BatchCommandTimeoutSeconds;
            var retried = await cleanup.RunIfDueAsync();
            Assert.IsNull(retried.Error, retried.Error?.ToString());
            Assert.AreEqual(4, retried.UsersDeleted);
            Assert.AreEqual(2, retried.Batches, "Fast batches do not lift the limit that the timeout set.");
            Assert.IsTrue(ids.All(id => !UserExists(id) && CountActivity("teams_user_activity_log", id) == 0 && CountActivity("platform_user_activity_log", id) == 0));
        }

        [TestMethod]
        public async Task WhileAnotherWriterHoldsTheUsersAndLicencesLock_NothingIsDeleted()
        {
            var id = InsertUser(NewHash());
            InsertActivity("teams_user_activity_log", id, days: 1);

            var logger = new RecordingLogger();
            var cleanup = NewCleanup(logger);
            using (var userImport = await DirectoryWriterLock.TryAcquireAsync(_connectionString, TimeSpan.Zero))
            {
                Assert.IsNotNull(userImport);
                var skipped = await cleanup.RunIfDueAsync();
                Assert.AreEqual(ConcealedIdentityUserCleanupOutcome.LockBusy, skipped.Outcome);
                Assert.IsTrue(UserExists(id));
            }
            StringAssert.Contains(logger.Entries.Single().Message, "users-and-licences lock");

            var ran = await cleanup.RunIfDueAsync();
            Assert.AreEqual(1, ran.UsersDeleted, "The next cycle carries on once the lock is free.");
        }

        [TestMethod]
        public async Task WhileTheUsageReportImportRuns_TheCleanupWaits()
        {
            var id = InsertUser(NewHash());
            InsertActivity("teams_user_activity_log", id, days: 1);

            var cleanup = NewCleanup(new RecordingLogger());
            var waited = await cleanup.RunIfDueAsync(usageReportImportRunning: true);
            Assert.AreEqual(ConcealedIdentityUserCleanupOutcome.WaitingForUsageReports, waited.Outcome);
            Assert.IsTrue(UserExists(id));
            Assert.AreEqual(1, CountActivity("teams_user_activity_log", id));

            var ran = await cleanup.RunIfDueAsync(usageReportImportRunning: false);
            Assert.AreEqual(1, ran.UsersDeleted);
        }

        [TestMethod]
        public async Task TheKillSwitch_LeavesEverythingInPlace_AndSaysSoOnce()
        {
            var id = InsertUser(NewHash());
            InsertActivity("teams_user_activity_log", id, days: 1);

            var logger = new RecordingLogger();
            var cleanup = NewCleanup(logger, enabled: false);
            Assert.AreEqual(ConcealedIdentityUserCleanupOutcome.Disabled, (await cleanup.RunIfDueAsync()).Outcome);
            Assert.AreEqual(ConcealedIdentityUserCleanupOutcome.Disabled, (await cleanup.RunIfDueAsync()).Outcome);

            Assert.IsTrue(UserExists(id));
            Assert.AreEqual(1, CountActivity("teams_user_activity_log", id));
            StringAssert.Contains(logger.Entries.Single().Message, ConcealedIdentityUserCleanup.AppSettingName + "=false");
        }

        [TestMethod]
        public void UsageReportActivityTables_AreExactlyTheTablesTheUserKeyedDailyLoadersWrite()
        {
            var loaderTables = typeof(AbstractUserDailyActivityLoader<,>).Assembly.GetTypes()
                .Where(t => !t.IsAbstract)
                .Select(UserKeyedReportEntity)
                .Where(entity => entity != null)
                .Select(entity => entity.GetCustomAttribute<TableAttribute>().Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            CollectionAssert.AreEqual(loaderTables, UsageTables.OrderBy(name => name, StringComparer.Ordinal).ToList(),
                "A user-keyed daily loader was added or removed: update ConcealedIdentityUserCleanupSql.UsageReportActivityTables.");
        }

        private static Type UserKeyedReportEntity(Type loader)
        {
            for (var type = loader.BaseType; type != null; type = type.BaseType)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AbstractUserDailyActivityLoader<,>))
                {
                    return type.GetGenericArguments()[0];
                }
            }
            return null;
        }

        // ---- Batch sizing, against a synthetic cost model with noise: time = (floor + perUser x users) x (1 +/- noise) -----

        // The cleanup's cadence, for the sizer's clock: each import cycle starts batches for DefaultTimeBudget, and a run that
        // fails is not due again for FailureRetryInterval. The cycle period is an assumption.
        private static readonly TimeSpan CyclePeriod = TimeSpan.FromMinutes(10);
        private static readonly DateTime SimulationStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private sealed class SimulatedRun
        {
            public List<int> Sizes { get; } = new List<int>();
            public List<double> Seconds { get; } = new List<double>();
            public List<bool> Committed { get; } = new List<bool>();

            // When each batch started, in hours from the start, and the lasting size limit after it.
            public List<double> Hours { get; } = new List<double>();
            public List<int> Limits { get; } = new List<int>();
            public int Failures { get; set; }
            public int RepeatedFailures { get; set; }
            public int TransientFailures { get; set; }
            public int OverCap { get; set; }
            public int LastingHalvings { get; set; }
            public double Days { get; set; }
            public override string ToString() => string.Join(",", Sizes.Select((s, i) => $"{s}:{Seconds[i]:F1}s"));
        }

        /// <summary>
        /// Drives a sizer through <paramref name="batches"/> batches of a seeded, noisy cost model, on a clock that follows the
        /// cleanup's cadence. A batch of more than <paramref name="failAboveUsers"/> users, or one that would run as long as the
        /// command timeout, fails after it instead; one of more than <paramref name="cliffAboveUsers"/> users takes
        /// <paramref name="cliffSeconds"/>; one at a batch index <paramref name="transientFailureAt"/> picks is a deadlock
        /// victim; and the database runs <paramref name="slowFactor"/> times slower for a random
        /// <paramref name="slowFraction"/> of the batches, and for those <paramref name="slowAt"/> picks.
        /// </summary>
        private static SimulatedRun Simulate(double floorSeconds, double perUserSeconds, double noise, int seed, int batches = 40,
            int failAboveUsers = int.MaxValue, int cliffAboveUsers = int.MaxValue, double cliffSeconds = 60,
            Func<int, bool> transientFailureAt = null, double slowFraction = 0, double slowFactor = 1, Func<int, bool> slowAt = null)
        {
            var random = new Random(seed);
            var slowRandom = new Random(seed + 1000003); // its own stream, so the noise matches a run without slow batches
            var clock = new global::UnitTests.FakeLoaderClasses.FixedClock(SimulationStart);
            var sizer = new ConcealedIdentityCleanupBatchSizer(ConcealedIdentityUserCleanup.DefaultBatchSize, ConcealedIdentityUserCleanup.MinBatchSize,
                ConcealedIdentityUserCleanup.MaxBatchSize, ConcealedIdentityUserCleanup.TargetBatchDuration, ConcealedIdentityUserCleanup.MaxBatchDuration, clock);
            var timeout = ConcealedIdentityUserCleanup.BatchCommandTimeoutSeconds;
            var run = new SimulatedRun();
            var runStarted = clock.UtcNow;
            var limit = sizer.SizeLimit;
            int? lastFailedSize = null;
            for (var i = 0; i < batches; i++)
            {
                if (clock.UtcNow - runStarted >= ConcealedIdentityUserCleanup.DefaultTimeBudget)
                {
                    runStarted += CyclePeriod;
                    clock.Advance(runStarted - clock.UtcNow);
                }

                var users = sizer.Next;
                var slow = slowRandom.NextDouble() < slowFraction || (slowAt != null && slowAt(i));
                var seconds = (floorSeconds + perUserSeconds * users) * (1 + noise * (2 * random.NextDouble() - 1)) * (slow ? slowFactor : 1);
                if (users > cliffAboveUsers) seconds = cliffSeconds;
                run.Sizes.Add(users);
                run.Hours.Add((clock.UtcNow - SimulationStart).TotalHours);
                var failed = true;
                if (users > failAboveUsers || seconds >= timeout)
                {
                    // Size-dependent: the command timeout.
                    run.Failures++;
                    if (lastFailedSize == users) run.RepeatedFailures++;
                    lastFailedSize = users;
                    run.Seconds.Add(timeout);
                    run.Committed.Add(false);
                    clock.Advance(TimeSpan.FromSeconds(timeout));
                    sizer.ObserveFailure(users, TimeSpan.FromSeconds(timeout), sizeDependent: true);
                }
                else if (transientFailureAt != null && transientFailureAt(i))
                {
                    // A deadlock victim: rolled back within a fraction of a second, whatever the size.
                    run.TransientFailures++;
                    lastFailedSize = null;
                    run.Seconds.Add(0.2);
                    run.Committed.Add(false);
                    clock.Advance(TimeSpan.FromSeconds(0.2));
                    sizer.ObserveFailure(users, TimeSpan.FromSeconds(0.2), sizeDependent: false);
                }
                else
                {
                    if (seconds > Cap) run.OverCap++;
                    lastFailedSize = null;
                    run.Seconds.Add(seconds);
                    run.Committed.Add(true);
                    clock.Advance(TimeSpan.FromSeconds(seconds));
                    sizer.Observe(users, TimeSpan.FromSeconds(seconds));
                    failed = false;
                }

                if (sizer.SizeLimit < limit) run.LastingHalvings++;
                limit = sizer.SizeLimit;
                run.Limits.Add(limit);
                if (failed)
                {
                    // A failed batch ends the run, and the next is not due for an hour.
                    runStarted += ConcealedIdentityUserCleanup.FailureRetryInterval;
                    clock.Advance(runStarted - clock.UtcNow);
                }
            }
            run.Days = (clock.UtcNow - SimulationStart).TotalDays;
            return run;
        }

        /// <summary>Users deleted per second of batch time over the last <paramref name="batches"/> batches.</summary>
        private static double Throughput(SimulatedRun run, int batches)
        {
            var from = run.Sizes.Count - batches;
            double users = 0, seconds = 0;
            for (var i = from; i < run.Sizes.Count; i++)
            {
                if (run.Committed[i]) users += run.Sizes[i];
                seconds += run.Seconds[i];
            }
            return users / seconds;
        }

        private static readonly double[] Noises = { 0.05, 0.20 };
        private static double Cap => ConcealedIdentityUserCleanup.MaxBatchDuration.TotalSeconds;

        [TestMethod]
        public void BatchSizer_WhenTheFloorDominates_TakesLargeBatches_ButNeverAboveTheCap()
        {
            foreach (var noise in Noises)
            {
                for (var seed = 1; seed <= 25; seed++)
                {
                    // 6 s of fixed cost per batch, 2 ms a user: 1,000 users take 8 s.
                    var run = Simulate(6, 0.002, noise, seed);
                    Assert.IsTrue(run.Seconds.All(s => s <= Cap), $"noise {noise}, seed {seed}: {run}");
                    Assert.IsTrue(run.Sizes.Skip(30).All(s => s >= 800), $"noise {noise}, seed {seed}: settles on large batches: {run}");

                    // 15 s of fixed cost: large batches still pay, but no batch may run past the cap - about 250 users take
                    // the 18 s the sizer plans to, and it approaches that as the batches prove each step.
                    var heavy = Simulate(15, 0.012, noise, seed);
                    Assert.IsTrue(heavy.Seconds.All(s => s <= Cap), $"noise {noise}, seed {seed}: {heavy}");
                    Assert.IsTrue(heavy.Sizes.Last() >= 150, $"noise {noise}, seed {seed}: {heavy}");
                }
            }
        }

        [TestMethod]
        public void BatchSizer_WhenThePerUserCostDominates_TakesSmallerBatches_NeverAboveTheCap()
        {
            foreach (var noise in Noises)
            {
                for (var seed = 1; seed <= 25; seed++)
                {
                    // 0.3 s floor, 150 ms a user: 1,000 users would take 150 s. About 65 users keep a batch to 10 s.
                    var run = Simulate(0.3, 0.15, noise, seed);
                    Assert.IsTrue(run.Seconds.All(s => s <= Cap), $"noise {noise}, seed {seed}: {run}");
                    Assert.IsTrue(run.Sizes.Skip(20).All(s => s <= 200), $"noise {noise}, seed {seed}: {run}");
                }
            }
        }

        [TestMethod]
        public void BatchSizer_OnASlowDatabase_NeverExceedsTheCap()
        {
            foreach (var perUser in new[] { 0.6, 0.8 })
            {
                foreach (var noise in new[] { 0.05, 0.10, 0.20 })
                {
                    for (var seed = 1; seed <= 25; seed++)
                    {
                        var run = Simulate(0.5, perUser, noise, seed);
                        Assert.IsTrue(run.Seconds.All(s => s <= Cap), $"{perUser} s/user, noise {noise}, seed {seed}: {run}");
                    }
                }
            }
        }

        [TestMethod]
        public void BatchSizer_AfterAFailedBatch_HalvesAndNeverRepeatsTheSameSize()
        {
            var sizer = new ConcealedIdentityCleanupBatchSizer(400, ConcealedIdentityUserCleanup.MinBatchSize, ConcealedIdentityUserCleanup.MaxBatchSize,
                ConcealedIdentityUserCleanup.TargetBatchDuration, ConcealedIdentityUserCleanup.MaxBatchDuration);
            sizer.ObserveFailure(400, TimeSpan.FromSeconds(ConcealedIdentityUserCleanup.BatchCommandTimeoutSeconds), sizeDependent: true);
            Assert.AreEqual(200, sizer.Next, "A timed-out batch is never retried at the same size: the next one is half.");
            sizer.ObserveFailure(200, TimeSpan.FromSeconds(0.2), sizeDependent: false);
            Assert.AreEqual(100, sizer.Next, "Any failure halves the next batch - a deadlock or lock timeout too.");

            // A database on which any batch of more than 600 users times out, although the cost model would favour 1,000:
            // the sizer must learn that from the failures, not keep paying a timeout.
            foreach (var noise in Noises)
            {
                for (var seed = 1; seed <= 25; seed++)
                {
                    var run = Simulate(4, 0.004, noise, seed, failAboveUsers: 600);
                    Assert.AreEqual(0, run.RepeatedFailures, $"noise {noise}, seed {seed}: {run}");
                    Assert.IsTrue(run.Failures <= 3, $"noise {noise}, seed {seed}: {run.Failures} failures: {run}");
                }
            }
        }

        [TestMethod]
        public void BatchSizer_ASizeDependentFailure_IsNeverRetried_OverALongBacklog()
        {
            foreach (var noise in Noises)
            {
                for (var seed = 1; seed <= 10; seed++)
                {
                    // 400 batches is a few hours; 4,000 is more than a day, so the lasting limit doubles back once on the way.
                    foreach (var batches in new[] { 400, 4000 })
                    {
                        // More than 600 users times out (a plan change, a memory-grant wait); the cost model favours 1,000.
                        var timeouts = Simulate(4, 0.004, noise, seed, batches: batches, failAboveUsers: 600);
                        Assert.IsTrue(timeouts.Failures <= 1 + (int)timeouts.Days,
                            $"noise {noise}, seed {seed}: {timeouts.Failures} timeouts in {batches} batches over {timeouts.Days:F1} days");

                        // More than 600 users takes 40 or 60 s - past the cap - or 120 s, which the command timeout stops at 90.
                        foreach (var cliffSeconds in new[] { 40.0, 60.0, 120.0 })
                        {
                            var cliff = Simulate(4, 0.004, noise, seed, batches: batches, cliffAboveUsers: 600, cliffSeconds: cliffSeconds);
                            Assert.IsTrue(cliff.OverCap + cliff.Failures <= 1 + (int)cliff.Days,
                                $"noise {noise}, seed {seed}, {cliffSeconds} s cliff: {cliff.OverCap} past the cap and {cliff.Failures} timeouts " +
                                $"in {batches} batches over {cliff.Days:F1} days");
                        }
                    }
                }
            }
        }

        [TestMethod]
        public void BatchSizer_OccasionalSlowBatches_DoNotLowerTheLastingLimit()
        {
            for (var seed = 1; seed <= 10; seed++)
            {
                // 15 s of fixed cost, 12 ms a user, +/-20% noise. Running twice as slow, no batch size fits the cap - 2 x 15 s
                // is the cap - so a batch past it is the database, not the size.
                var clean = Simulate(15, 0.012, 0.20, seed, batches: 1500);
                var twice = Simulate(15, 0.012, 0.20, seed, batches: 1500, slowFraction: 0.01, slowFactor: 2);
                Assert.IsTrue(twice.OverCap > 0, $"seed {seed}: the slow batches must run past the cap for this to test anything.");
                Assert.IsTrue(twice.LastingHalvings <= 1, $"seed {seed}: {twice.LastingHalvings} lasting halvings, ending at {twice.Limits.Last()}");
                Assert.IsTrue(Throughput(twice, 300) >= 0.8 * Throughput(clean, 300),
                    $"seed {seed}: {Throughput(twice, 300):F2} users/s against {Throughput(clean, 300):F2} without the slow batches");

                var mild = Simulate(15, 0.012, 0.20, seed, batches: 1500, slowFraction: 0.01, slowFactor: 1.3);
                Assert.AreEqual(0, mild.LastingHalvings, $"seed {seed}: 1% of batches 1.3 times as slow, ending at {mild.Limits.Last()}");
            }
        }

        [TestMethod]
        public void BatchSizer_AfterASustainedSlowdown_GetsBackToItsSize()
        {
            foreach (var (floor, perUser) in new[] { (15.0, 0.012), (3.0, 0.02) })
            {
                foreach (var noise in Noises)
                {
                    for (var seed = 1; seed <= 10; seed++)
                    {
                        // The database runs at half speed for 50 batches in a row, from batch 300.
                        var run = Simulate(floor, perUser, noise, seed, batches: 3000, slowFactor: 2, slowAt: i => i >= 300 && i < 350);
                        var before = Median(run.Sizes.Skip(250).Take(50));
                        var back = Enumerable.Range(350, run.Sizes.Count - 360).Cast<int?>()
                            .FirstOrDefault(i => Median(run.Sizes.Skip(i.Value).Take(10)) >= 0.9 * before);
                        var limited = run.Limits.Skip(300).Take(50).Min() < run.Limits[299];
                        var what = $"{floor} s + {perUser * 1000} ms, noise {noise}, seed {seed}: {before} users before, " +
                            (limited ? "the lasting limit lowered, " : "") +
                            (back.HasValue ? $"back after {back - 350} batches, {run.Hours[back.Value] - run.Hours[350]:F1} h" : "never back");
                        Assert.IsTrue(back.HasValue, what);

                        // Within 30 batches; or, where the slowdown lowered the lasting limit, within a day and an hour: the limit
                        // doubles back once per day, and this slowdown halves it at most once - a longer one can take several days.
                        Assert.IsTrue(limited ? run.Hours[back.Value] - run.Hours[350] <= 25 : back - 350 <= 30, what);
                    }
                }
            }
        }

        [TestMethod]
        public void BatchSizer_ALastingLimit_DoublesBackEachDayWithoutAnotherSizeFailure_UpToTheMaximum()
        {
            var clock = new global::UnitTests.FakeLoaderClasses.FixedClock(SimulationStart);
            var sizer = new ConcealedIdentityCleanupBatchSizer(400, ConcealedIdentityUserCleanup.MinBatchSize, ConcealedIdentityUserCleanup.MaxBatchSize,
                ConcealedIdentityUserCleanup.TargetBatchDuration, ConcealedIdentityUserCleanup.MaxBatchDuration, clock);
            var timeout = TimeSpan.FromSeconds(ConcealedIdentityUserCleanup.BatchCommandTimeoutSeconds);
            sizer.ObserveFailure(400, timeout, sizeDependent: true);
            Assert.AreEqual(200, sizer.SizeLimit);

            clock.Advance(TimeSpan.FromHours(23));
            sizer.Observe(200, TimeSpan.FromSeconds(2));
            Assert.AreEqual(200, sizer.SizeLimit, "Not before a day.");
            clock.Advance(TimeSpan.FromHours(1));
            sizer.Observe(200, TimeSpan.FromSeconds(2));
            Assert.AreEqual(400, sizer.SizeLimit, "A day without another size-dependent failure doubles it.");

            // Another one starts the day again.
            clock.Advance(TimeSpan.FromHours(12));
            sizer.ObserveFailure(400, timeout, sizeDependent: true);
            Assert.AreEqual(200, sizer.SizeLimit);
            clock.Advance(TimeSpan.FromHours(23));
            sizer.Observe(200, TimeSpan.FromSeconds(2));
            Assert.AreEqual(200, sizer.SizeLimit, "A day from the latest failure, not the first.");

            // Each day doubles it once more, and never past the most a batch may take.
            clock.Advance(TimeSpan.FromDays(10));
            sizer.Observe(200, TimeSpan.FromSeconds(2));
            Assert.AreEqual(ConcealedIdentityUserCleanup.MaxBatchSize, sizer.SizeLimit);
            Assert.IsTrue(sizer.Next <= ConcealedIdentityUserCleanup.MaxBatchSize);
        }

        [TestMethod]
        public void BatchSizer_TransientFailures_DoNotRatchetTheSizeDown()
        {
            foreach (var noise in Noises)
            {
                for (var seed = 1; seed <= 10; seed++)
                {
                    // 15 s of fixed cost, 12 ms a user, and a deadlock every 60 batches.
                    var clean = Simulate(15, 0.012, noise, seed, batches: 400);
                    var withDeadlocks = Simulate(15, 0.012, noise, seed, batches: 400, transientFailureAt: i => i % 60 == 59);
                    Assert.IsTrue(withDeadlocks.TransientFailures >= 6);
                    Assert.IsTrue(Throughput(withDeadlocks, 100) >= 0.7 * Throughput(clean, 100),
                        $"noise {noise}, seed {seed}: {Throughput(withDeadlocks, 100):F2} users/s against {Throughput(clean, 100):F2} without the deadlocks");
                    Assert.IsTrue(Median(withDeadlocks.Sizes.Skip(380)) >= 0.7 * Median(clean.Sizes.Skip(380)),
                        $"noise {noise}, seed {seed}: settles on {Median(withDeadlocks.Sizes.Skip(380))} against {Median(clean.Sizes.Skip(380))}");
                }
            }
        }

        [TestMethod]
        public void BatchSizer_AfterOneDeadlock_RecoversThePreFailureSize()
        {
            foreach (var noise in Noises)
            {
                for (var seed = 1; seed <= 10; seed++)
                {
                    // 3 s of fixed cost, 20 ms a user: one deadlock at batch 60 must not halve the batch for good.
                    var clean = Simulate(3, 0.02, noise, seed, batches: 120);
                    var once = Simulate(3, 0.02, noise, seed, batches: 120, transientFailureAt: i => i == 60);
                    Assert.IsTrue(Median(once.Sizes.Skip(100)) >= 0.9 * Median(clean.Sizes.Skip(100)),
                        $"noise {noise}, seed {seed}: settles on {Median(once.Sizes.Skip(100))} against {Median(clean.Sizes.Skip(100))}");
                }
            }
        }

        [TestMethod]
        public void BatchSizer_NeverHalvesABatchThatCommittedUnderTheCap()
        {
            ConcealedIdentityCleanupBatchSizer NewSizer() => new ConcealedIdentityCleanupBatchSizer(25, ConcealedIdentityUserCleanup.MinBatchSize,
                ConcealedIdentityUserCleanup.MaxBatchSize, ConcealedIdentityUserCleanup.TargetBatchDuration, ConcealedIdentityUserCleanup.MaxBatchDuration);

            // Inside the 18 s ceiling but with too little room to double: it used to be halved, though nothing went wrong.
            var inside = NewSizer();
            inside.Observe(25, TimeSpan.FromSeconds(12));
            Assert.IsTrue(inside.Next >= 25, $"A batch that fitted is not shrunk: {inside.Next}");

            // Under the cap but over the ceiling: one batch cannot say whether its users or a fixed cost made it slow, so a
            // modest step, never a halving.
            var slow = NewSizer();
            slow.Observe(25, TimeSpan.FromSeconds(19));
            Assert.IsTrue(slow.Next > 25 / 2 && slow.Next < 25, $"A step down towards the ceiling, not a halving: {slow.Next}");
        }

        [TestMethod]
        public void BatchSizer_WhenTheFixedCostPerBatchIsAboveTheCeiling_TakesLargeBatches_AndNeverRatchetsToOne()
        {
            // Every batch pays the foreign-key checks on the children of dbo.users that have no index leading with user_id, whatever
            // its size: a constant per batch (F) plus a little per user. With F above the 18 s ceiling no size gets a batch inside
            // it. The sizer used to halve what committed under the cap (F 19-25: settled on 12), blame the size for what ran past
            // it (F 31-89: fell to 1, a year or two for 200,000 placeholders) and time out at every size (F 91: nothing to size).
            foreach (var floor in new[] { 5.0, 19.0, 25.0, 31.0, 60.0 })
            {
                foreach (var noise in new[] { 0.0, 0.05 })
                {
                    for (var seed = 1; seed <= 10; seed++)
                    {
                        var run = Simulate(floor, 0.002, noise, seed, batches: 60);
                        var what = $"F {floor} s, noise {noise}, seed {seed}: {run}";
                        Assert.AreEqual(0, run.Failures, what);
                        Assert.IsTrue(run.Sizes.Min() >= 6, "Never ratchets down to a handful of users: " + what);
                        Assert.IsTrue(run.Sizes.Skip(30).All(s => s >= 250), "Settles on large batches, which is where a fixed cost pays: " + what);
                        Assert.AreEqual(ConcealedIdentityUserCleanup.MaxBatchSize, run.Limits.Last(),
                            "Size was never to blame, so no lasting limit stays: " + what);
                        if (floor <= 25) Assert.IsTrue(run.Seconds.Skip(30).All(s => s <= Cap), "Still inside the cap where the cap can be met: " + what);
                    }
                }
            }
        }

        [TestMethod]
        public void BatchSizer_WhenEverySizeTimesOut_HoldsTheSizeInsteadOfRatchetingToOne()
        {
            // F of 91 s is past the 90 s command timeout: every batch times out at every size, so size is not what is wrong.
            for (var seed = 1; seed <= 5; seed++)
            {
                var run = Simulate(91, 0.002, 0.0, seed, batches: 40);
                Assert.AreEqual(40, run.Failures, run.ToString());
                Assert.IsTrue(run.Sizes.Min() >= ConcealedIdentityUserCleanup.DefaultBatchSize >> ConcealedIdentityCleanupBatchSizer.MaxHalvingsWithoutACommit,
                    $"seed {seed}: halves {ConcealedIdentityCleanupBatchSizer.MaxHalvingsWithoutACommit} times and then holds: {run}");
                Assert.AreEqual(run.Sizes.Last(), run.Sizes.Skip(10).First(), $"seed {seed}: held: {run}");
            }

            // A batch that commits again lets the size be blamed for later timeouts afresh.
            var sizer = new ConcealedIdentityCleanupBatchSizer(400, ConcealedIdentityUserCleanup.MinBatchSize, ConcealedIdentityUserCleanup.MaxBatchSize,
                ConcealedIdentityUserCleanup.TargetBatchDuration, ConcealedIdentityUserCleanup.MaxBatchDuration);
            var timeout = TimeSpan.FromSeconds(ConcealedIdentityUserCleanup.BatchCommandTimeoutSeconds);
            sizer.ObserveFailure(400, timeout, sizeDependent: true);
            sizer.Observe(sizer.Next, TimeSpan.FromSeconds(2));
            for (var halvings = 1; halvings <= ConcealedIdentityCleanupBatchSizer.MaxHalvingsWithoutACommit; halvings++)
            {
                var before = sizer.Next;
                sizer.ObserveFailure(before, timeout, sizeDependent: true);
                Assert.IsTrue(sizer.Next <= Math.Max(1, before / 2), $"Timeout {halvings} after a commit still halves.");
            }
        }

        [TestMethod]
        public void BatchSizer_AnOverrunThatIsTheFixedCost_LiftsTheLimitItSetOnTheWay()
        {
            // 31 s per batch, however many users: the first batch past the cap looks like a size problem and sets a limit; the
            // batches after it, at half and a quarter the size, take just as long, which shows it was not.
            var sizer = new ConcealedIdentityCleanupBatchSizer(25, ConcealedIdentityUserCleanup.MinBatchSize, ConcealedIdentityUserCleanup.MaxBatchSize,
                ConcealedIdentityUserCleanup.TargetBatchDuration, ConcealedIdentityUserCleanup.MaxBatchDuration);
            sizer.Observe(25, TimeSpan.FromSeconds(31.05));
            Assert.IsTrue(sizer.SizeLimit < ConcealedIdentityUserCleanup.MaxBatchSize, "Tentatively blamed, as any batch past the cap on new ground is.");
            while (sizer.Next > 6) sizer.Observe(sizer.Next, TimeSpan.FromSeconds(31.02));
            sizer.Observe(sizer.Next, TimeSpan.FromSeconds(31.01));

            Assert.AreEqual(ConcealedIdentityUserCleanup.MaxBatchSize, sizer.SizeLimit, "Halving did not make a batch quicker, so no limit stays.");
            Assert.IsTrue(sizer.Next > 25, $"Bigger batches, not smaller: {sizer.Next}");
            Assert.IsNotNull(sizer.FixedCostSeconds);
            Assert.IsTrue(sizer.FixedCostSeconds.Value >= 30 && sizer.FixedCostSeconds.Value <= 32, $"The fixed cost is found: {sizer.FixedCostSeconds}");
        }

        [TestMethod]
        public void CommandTimeouts_AreSizeDependent_OtherFailuresAreTransient()
        {
            var timeout = TimeSpan.FromSeconds(ConcealedIdentityUserCleanup.BatchCommandTimeoutSeconds);
            Assert.IsTrue(ConcealedIdentityUserCleanup.IsSizeDependentFailure(new InvalidOperationException(), timeout),
                "Anything that ran for the whole command timeout is treated as one.");
            Assert.IsFalse(ConcealedIdentityUserCleanup.IsSizeDependentFailure(new InvalidOperationException(), TimeSpan.FromSeconds(2)));
            Assert.IsTrue(ConcealedIdentityUserCleanup.BatchCommandTimeoutSeconds >= 2 * Cap,
                "The command timeout must leave a batch at the cap well clear of it.");
        }

        private static double Median(IEnumerable<int> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
        }

        // ---- Synthetic data -----------------------------------------------------------------------------------------

        private int InsertUser(string userName, string azureAdId = "", string mail = "", int? managerId = null)
        {
            var id = Scalar<int>("INSERT INTO dbo.users (user_name, azure_ad_id, mail, manager_id) OUTPUT INSERTED.id VALUES (@name, @aad, @mail, @manager);",
                new SqlParameter("@name", System.Data.SqlDbType.VarChar, 250) { Value = userName },
                new SqlParameter("@aad", azureAdId),
                new SqlParameter("@mail", mail),
                new SqlParameter("@manager", (object)managerId ?? DBNull.Value));
            _createdUserIds.Add(id);
            return id;
        }

        /// <summary>One row a day for <paramref name="days"/> days, every other required column zero.</summary>
        private void InsertActivity(string table, int userId, int days)
        {
            if (!_requiredColumnsByTable.TryGetValue(table, out var required))
            {
                required = Scalar<string>(@"SELECT STRING_AGG(QUOTENAME(c.name), ',') FROM sys.columns AS c
WHERE c.object_id = OBJECT_ID(@table) AND c.is_nullable = 0 AND c.is_identity = 0 AND c.is_computed = 0 AND c.default_object_id = 0
  AND c.name NOT IN ('user_id', 'date');", new SqlParameter("@table", "dbo." + table));
                _requiredColumnsByTable[table] = required;
            }

            var columns = string.IsNullOrEmpty(required) ? "" : "," + required;
            var zeros = string.IsNullOrEmpty(required) ? "" : "," + string.Join(",", required.Split(',').Select(_ => "0"));
            var dayList = string.Join(",", Enumerable.Range(0, days).Select(d => "(" + d.ToString(CultureInfo.InvariantCulture) + ")"));
            Execute($"INSERT INTO dbo.[{table}] (user_id, [date]{columns}) SELECT {userId}, DATEADD(day, -d.n, '2026-09-30'){zeros} FROM (VALUES {dayList}) AS d(n);");
        }

        private static bool UserExists(int id) => Scalar<int>($"SELECT COUNT(*) FROM dbo.users WHERE id = {id};") == 1;

        private static int CountActivity(string table, int userId) => Scalar<int>($"SELECT COUNT(*) FROM dbo.[{table}] WHERE user_id = {userId};");

        // The schema test that decides whether the cleanup reads a reference once per run: an enabled, unfiltered B-tree index
        // whose first key column is the reference column makes the check a seek, done in every batch instead.
        private static bool HasIndexLeadingWith(string table, string column) => Scalar<int>(@"SELECT COUNT(*) FROM sys.indexes AS i
INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID(@table) AND c.name = @column
  AND i.type IN (1, 2) AND i.is_disabled = 0 AND i.is_hypothetical = 0 AND i.has_filter = 0;",
            new SqlParameter("@table", "dbo." + table), new SqlParameter("@column", column)) > 0;

        private static T Scalar<T>(string sql, params SqlParameter[] parameters)
        {
            using (var connection = AzureSqlTokenAuth.CreateConnection(_connectionString))
            using (var command = new SqlCommand(sql, connection))
            {
                command.Parameters.AddRange(parameters);
                connection.Open();
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? default(T) : (T)value;
            }
        }

        private static void Execute(string sql)
        {
            using (var connection = AzureSqlTokenAuth.CreateConnection(_connectionString))
            using (var command = new SqlCommand(sql, connection))
            {
                connection.Open();
                command.ExecuteNonQuery();
            }
        }
    }
}
