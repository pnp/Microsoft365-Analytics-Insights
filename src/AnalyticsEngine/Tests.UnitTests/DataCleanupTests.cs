using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Entities.AuditLog;
using Common.Entities.Entities.Teams;
using Common.Entities.Entities.UsageReports;
using Common.Entities.Entities.WebTraffic;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class DataCleanupTests
    {
        [TestMethod]
        public async Task CleanupHistoricalDataTests()
        {
            // Read the cleanup script directly from src\Clean Old Data Data.sql so it's obvious which script is under test.
            var cleanupScript = File.ReadAllText(GetCleanOldDataSqlPath());

            using (var db = new AnalyticsEntitiesContext())
            {
                await InsertTestDataAll(db);

                await db.Database.ExecuteSqlCommandAsync(cleanupScript);
            }
        }

        /// <summary>
        /// Issue #286 asked for the Copilot usage-report tables to gain a retention bound AND for the
        /// delete to be batched. The per-user detail report is requested for a single period
        /// (<c>CopilotReportRequest.DefaultRefreshPeriod</c>, "D28"), so the table gains about one row
        /// per licensed user per day - roughly 200,000 a day at the 200,000-user baseline, or ~6 million
        /// over the one-month retention window. The first purge after enabling the import has to clear
        /// everything accumulated since then, which can be far more.
        /// <para>
        /// This asserts the outcome rather than merely running the script: rows older than the cutoff
        /// go, rows inside it stay. Before the batching change the script deleted these in one
        /// statement; the assertion holds either way, which is the point - it pins the retention
        /// behaviour so a future rewrite of the loop cannot silently stop purging.
        /// </para>
        /// </summary>
        [TestMethod]
        public async Task Cleanup_PurgesOldCopilotUsageReportRows_AndKeepsRecentOnes()
        {
            var cleanupScript = File.ReadAllText(GetCleanOldDataSqlPath());

            // The script's cutoff is one month before now, so straddle it.
            var wellOutsideRetention = DateTime.Now.AddMonths(-6).Date;
            var wellInsideRetention = DateTime.Now.AddDays(-2).Date;

            int userId;
            using (var db = new AnalyticsEntitiesContext())
            {
                var user = new User
                {
                    UserPrincipalName = "purge" + DateTime.Now.Ticks + "@example.com",
                    Mail = "purge" + DateTime.Now.Ticks + "@example.com",
                };
                db.users.Add(user);
                await db.SaveChangesAsync();
                userId = user.ID;

                db.CopilotUsageUserActivityLogs.Add(new CopilotUsageUserActivityLog
                {
                    User = user,
                    Date = wellOutsideRetention,
                    ReportPeriodDays = 28,
                });
                db.CopilotUsageUserActivityLogs.Add(new CopilotUsageUserActivityLog
                {
                    User = user,
                    Date = wellInsideRetention,
                    ReportPeriodDays = 28,
                });
                await db.SaveChangesAsync();
            }

            using (var db = new AnalyticsEntitiesContext())
            {
                await db.Database.ExecuteSqlCommandAsync(cleanupScript);
            }

            using (var db = new AnalyticsEntitiesContext())
            {
                var remaining = db.CopilotUsageUserActivityLogs
                    .Where(r => r.User.ID == userId)
                    .Select(r => r.Date)
                    .ToList();

                Assert.IsFalse(remaining.Contains(wellOutsideRetention),
                    "A Copilot usage-report row older than the retention cutoff survived the purge. " +
                    "copilot_usage_user_activity_log is the fastest-growing table this feature adds " +
                    "(issue #286); without this delete it grows forever.");
                Assert.IsTrue(remaining.Contains(wellInsideRetention),
                    "The purge deleted a Copilot usage-report row INSIDE the retention window. " +
                    "A batching bug that ignores the date predicate would look exactly like this.");
            }
        }

        /// <summary>
        /// Inventory guard, also from issue #286: fail when a Copilot usage-report table exists in the
        /// entity model but is not aged by the cleanup script. Adding a table to a growing feature and
        /// forgetting to age it is precisely how the Teams add-on tables became expensive enough to
        /// need deprecating, and nothing else in the build notices.
        /// <para>
        /// The table list is derived from the EF model by reflection, NOT hard-coded, because a
        /// hard-coded list cannot fail for the case the issue actually cares about - somebody adding a
        /// FOURTH usage-report entity later. A hard-coded array would stay green precisely when it
        /// matters.
        /// </para>
        /// <para>
        /// Two subtleties, both of which an earlier version of this test got wrong:
        /// </para>
        /// <para>
        /// It looks for the actual batched <c>DELETE</c>, not merely the table name. A plain substring
        /// search over the whole file is satisfied by any passing mention in a comment, so removing the
        /// delete while leaving the surrounding prose - which names all three tables - would have kept
        /// this green.
        /// </para>
        /// <para>
        /// It matches on word boundaries. <c>copilot_user</c> is a substring of
        /// <c>copilot_user_count_log</c>, so a future table with a prefix name would have been "found"
        /// inside a longer one and slipped through the very guard meant to catch it. Underscore is a
        /// regex word character, so <c>\bcopilot_user\b</c> correctly does not match inside
        /// <c>copilot_user_count_log</c>.
        /// </para>
        /// <para>
        /// A table that genuinely must not be aged can opt out with a <c>RETENTION-EXEMPT:</c> line
        /// naming it, which keeps the decision explicit and reviewable rather than silent.
        /// </para>
        /// </summary>
        [TestMethod]
        public void CleanupScript_AccountsForEveryCopilotUsageReportTable()
        {
            var cleanupScript = File.ReadAllText(GetCleanOldDataSqlPath());
            var copilotTables = DiscoverCopilotUsageReportTables();

            // A reflection-driven test that discovers nothing passes vacuously, which would be worse
            // than no test at all. Pin the floor at the three tables that exist today.
            Assert.IsTrue(copilotTables.Count >= 3,
                "Expected to discover at least the three Copilot usage-report tables by reflection, but " +
                $"found {copilotTables.Count} ({string.Join(", ", copilotTables)}). The namespace or the " +
                "[Table] attributes have moved, and this guard is no longer guarding anything.");

            foreach (var table in copilotTables)
            {
                // Deliberately matched against the RAW script, not the comment-stripped text that
                // IsPurgedInBatches uses: an exemption is meant to BE a comment. The two checks look at
                // opposite halves of the file on purpose.
                var exempt = Regex.IsMatch(
                    cleanupScript,
                    @"RETENTION-EXEMPT:\s*" + Regex.Escape(table) + @"\b",
                    RegexOptions.IgnoreCase);

                Assert.IsTrue(IsPurgedInBatches(cleanupScript, table) || exempt,
                    $"'{table}' is a Copilot usage-report table in the entity model, but Clean Old Data " +
                    "Data.sql neither purges it nor declares it exempt, so it grows forever (issue #286). " +
                    $"Either add 'delete top (@copilotBatch) from {table} where ...' inside a batch loop, " +
                    $"or - if it genuinely must not be aged - add a comment line 'RETENTION-EXEMPT: {table}' " +
                    "explaining why, as copilot_interaction_user_watermarks is excluded on purpose.");
            }
        }

        /// <summary>
        /// Every <c>[Table]</c>-mapped Copilot entity declared alongside the usage-report classes.
        /// <para>
        /// <see cref="Assembly.GetTypes"/> throws <see cref="ReflectionTypeLoadException"/> if any type
        /// in the assembly fails to load, which would error this test rather than fail it cleanly. The
        /// partial results it carries are enough for an inventory check, so use them.
        /// </para>
        /// </summary>
        private static List<string> DiscoverCopilotUsageReportTables()
        {
            var usageReportNamespace = typeof(CopilotUsageUserActivityLog).Namespace;

            Type[] types;
            try
            {
                types = typeof(CopilotUsageUserActivityLog).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }

            return types
                .Where(t => t.Namespace == usageReportNamespace)
                .Select(t => t.GetCustomAttribute<TableAttribute>())
                .Where(a => a != null && (a.Name.StartsWith("copilot_", StringComparison.OrdinalIgnoreCase)
                    || a.Name.StartsWith("cowork_", StringComparison.OrdinalIgnoreCase)))
                .Select(a => a.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The three usage-report tables that exist today are the fast-growing ones. This pins them by
        /// name, which the reflection-driven guard above deliberately does not: if the namespace or the
        /// <c>[Table]</c> attributes move, discovery could quietly stop finding them and only the
        /// <c>&gt;= 3</c> floor would notice. Both tests use the same <see cref="IsPurgedInBatches"/>
        /// matcher, so there is one definition of what counts as batched.
        /// </summary>
        [TestMethod]
        public void CleanupScript_BatchesTheCopilotUsageReportDeletes()
        {
            var cleanupScript = File.ReadAllText(GetCleanOldDataSqlPath());
            var discovered = DiscoverCopilotUsageReportTables();

            var mustBeBatched = new[]
            {
                "copilot_usage_user_activity_log",
                "cowork_usage_user_activity_log",
                "copilot_user_count_log",
                "copilot_usage_report_import_log",
            };

            foreach (var table in mustBeBatched)
            {
                Assert.IsTrue(
                    discovered.Contains(table, StringComparer.OrdinalIgnoreCase),
                    $"'{table}' was not discovered by reflection over the usage-report namespace, so the " +
                    "inventory guard is no longer covering it. Check that its [Table] attribute and " +
                    "namespace still match what DiscoverCopilotUsageReportTables looks for.");

                Assert.IsTrue(
                    IsPurgedInBatches(cleanupScript, table),
                    $"'{table}' has no batched retention delete in Clean Old Data Data.sql (issue #286). " +
                    "The batch size must come from @copilotBatch rather than a literal, because the loop " +
                    "exits by comparing @@ROWCOUNT against that same variable - a literal that drifts " +
                    "from it would stop the purge after a single pass.");
            }
        }

        /// <summary>
        /// Whether <paramref name="cleanupScript"/> deletes <paramref name="table"/> in batches driven by
        /// <c>@copilotBatch</c>.
        /// <para>
        /// Matches against the EXECUTABLE SQL only. Comments and string literals are stripped first,
        /// because a matcher run over raw text cannot tell a live statement from a commented-out one -
        /// and commenting out a <c>DELETE</c> is by far the most likely way a purge actually gets
        /// disabled ("just while I debug this"). Without the strip, doing so leaves both retention
        /// guards green, which is the same false-pass category this guard exists to close.
        /// </para>
        /// <para>
        /// Tolerates the SQL spellings a future edit might reasonably use - an optional <c>dbo.</c>
        /// qualifier and optional square brackets around either part - because bracket-quoting is the
        /// house style elsewhere in this very script (<c>[sessions]</c>, <c>[date]</c>,
        /// <c>[event_meta_general]</c>) and <c>dbo.</c> appears in every <c>OBJECT_ID</c> guard. A guard
        /// that reports correct, batched code as "grows forever" gets weakened rather than trusted.
        /// </para>
        /// <para>
        /// The <c>\b</c> sits immediately after the table name and before the optional closing bracket,
        /// so <c>copilot_user</c> still does not match inside <c>copilot_user_count_log</c> - underscore
        /// is a regex word character, which is what makes that work.
        /// </para>
        /// </summary>
        private static bool IsPurgedInBatches(string cleanupScript, string table) =>
            Regex.IsMatch(
                StripSqlCommentsAndLiterals(cleanupScript),
                @"delete\s+top\s*\(\s*@copilotBatch\s*\)\s+from\s+(?:\[?dbo\]?\s*\.\s*)?\[?"
                    + Regex.Escape(table) + @"\b\]?",
                RegexOptions.IgnoreCase);

        /// <summary>
        /// Replaces every SQL line comment, block comment and single-quoted string literal with
        /// whitespace, leaving only executable text (and preserving newlines so offsets stay readable).
        /// <para>
        /// Written as a single left-to-right scan rather than a set of regexes on purpose: applied
        /// separately, a <c>--</c> inside a string literal would be mistaken for a comment, and a lone
        /// apostrophe inside a comment would be mistaken for the start of a literal - either of which
        /// would swallow the real statement that follows. T-SQL block comments nest, so depth is tracked.
        /// </para>
        /// </summary>
        internal static string StripSqlCommentsAndLiterals(string sql)
        {
            var outp = new StringBuilder(sql.Length);
            var i = 0;

            while (i < sql.Length)
            {
                // Line comment: -- to end of line.
                if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                {
                    while (i < sql.Length && sql[i] != '\n') { outp.Append(' '); i++; }
                    continue;
                }

                // Block comment, which nests in T-SQL.
                if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                {
                    var depth = 0;
                    while (i < sql.Length)
                    {
                        if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                        {
                            depth++; outp.Append("  "); i += 2; continue;
                        }
                        if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
                        {
                            depth--; outp.Append("  "); i += 2;
                            if (depth == 0) break;
                            continue;
                        }
                        outp.Append(sql[i] == '\n' ? '\n' : ' ');
                        i++;
                    }
                    continue;
                }

                // Single-quoted string literal; '' is an escaped quote, not a terminator.
                if (sql[i] == '\'')
                {
                    outp.Append(' '); i++;
                    while (i < sql.Length)
                    {
                        if (sql[i] == '\'')
                        {
                            if (i + 1 < sql.Length && sql[i + 1] == '\'') { outp.Append("  "); i += 2; continue; }
                            outp.Append(' '); i++; break;
                        }
                        outp.Append(sql[i] == '\n' ? '\n' : ' ');
                        i++;
                    }
                    continue;
                }

                outp.Append(sql[i]);
                i++;
            }

            return outp.ToString();
        }

        /// <summary>
        /// The retention guards match executable SQL only. Without this, commenting out a purge - the
        /// most likely way one ever actually gets disabled - would leave both guards green, which is the
        /// same false-pass category they exist to close.
        /// <para>
        /// Tests the matcher directly against synthetic SQL rather than the real script, so it pins the
        /// behaviour regardless of what the real script happens to contain today.
        /// </para>
        /// </summary>
        [TestMethod]
        public void RetentionGuard_IgnoresPurgesThatAreCommentedOutOrQuoted()
        {
            const string table = "copilot_usage_user_activity_log";
            const string stmt = "delete top (@copilotBatch) from copilot_usage_user_activity_log where [date] < @d";

            Assert.IsTrue(IsPurgedInBatches(stmt, table),
                "The bare executable statement must be recognised, or the guard is useless.");

            Assert.IsFalse(IsPurgedInBatches("-- " + stmt, table),
                "A line-commented purge must NOT count as purging.");
            Assert.IsFalse(IsPurgedInBatches("/* " + stmt + " */", table),
                "A block-commented purge must NOT count as purging.");
            Assert.IsFalse(IsPurgedInBatches("/* outer /* nested */ " + stmt + " */", table),
                "T-SQL block comments nest; a purge inside a nested comment must NOT count.");
            Assert.IsFalse(IsPurgedInBatches("PRINT '" + stmt + "'", table),
                "A purge inside a string literal must NOT count as purging.");

            // The scanner must not over-strip: a '--' or a quote inside one construct must not be
            // mistaken for the start of another, swallowing the real statement that follows.
            Assert.IsTrue(IsPurgedInBatches("PRINT 'note -- not a comment';\n" + stmt, table),
                "A '--' inside a string literal must not start a comment and swallow the real purge.");
            Assert.IsTrue(IsPurgedInBatches("-- don't be fooled by this apostrophe\n" + stmt, table),
                "An apostrophe inside a line comment must not start a string literal and swallow the purge.");
            Assert.IsTrue(IsPurgedInBatches("/* don't be fooled */\n" + stmt, table),
                "An apostrophe inside a block comment must not start a string literal.");
        }

        // Resolves to <repoRoot>\src\Clean Old Data Data.sql relative to this source file (Tests.UnitTests\DataCleanupTests.cs).
        private static string GetCleanOldDataSqlPath([CallerFilePath] string thisFilePath = "")
        {
            // thisFilePath = ...\src\AnalyticsEngine\Tests.UnitTests\DataCleanupTests.cs
            // target       = ...\src\Clean Old Data Data.sql
            var testsUnitTestsDir = Path.GetDirectoryName(thisFilePath);
            var analyticsEngineDir = Path.GetDirectoryName(testsUnitTestsDir);
            var srcDir = Path.GetDirectoryName(analyticsEngineDir);
            var sqlPath = Path.Combine(srcDir, "Clean Old Data Data.sql");

            if (!File.Exists(sqlPath))
                throw new FileNotFoundException("Could not find cleanup SQL script.", sqlPath);

            return sqlPath;
        }

        /// <summary>
        /// <c>CleanDataByUser</c> removes one person the way the portal's User scope purge does. The person here has what
        /// nearly every user of a tracked tenant has - a page view in a web session - plus Microsoft Entra ID, Power Platform
        /// and Copilot Studio audit metadata, shares and a call with someone else, Copilot Studio credits and credit history
        /// readings: every one of them a foreign key that does not cascade, or a reference with no foreign key at all. It runs
        /// as a DBA runs it, outside any transaction of the caller's.
        /// </summary>
        [TestMethod]
        public async Task CleanupDataForUserDataTests()
        {
            int userId;
            using (var db = new AnalyticsEntitiesContext())
            {
                var user = await InsertTestDataAll(db);
                userId = user.ID;
            }
            var other = await SeedWhatOnlyThePurgeHandlesAsync(userId);
            var profiles = await WeeklyProfileTables.EnsureAsync(ConnectionString());
            try
            {
                await ExecuteAsync(WeeklyProfileTables.InsertSql(userId) + WeeklyProfileTables.InsertSql(other.OtherUser));
                await CreateCleanDataByUserAsync();
                await ExecuteCleanDataByUserAsync(userId);

                using (var db = new AnalyticsEntitiesContext())
                {
                    Assert.IsNull(await db.users.FindAsync(userId), "The user is gone.");
                }

                async Task Count(int expected, string what, string sql)
                    => Assert.AreEqual(expected, Convert.ToInt32(await ScalarAsync(sql.Replace("@user", userId.ToString()).Replace("@other", other.OtherUser.ToString()))), what);

                await Count(0, "Their audit events are gone.", "SELECT COUNT(*) FROM dbo.audit_events WHERE user_id = @user");
                await Count(0, "Their sessions are gone, with the page views in them.", "SELECT COUNT(*) FROM dbo.sessions WHERE user_id = @user");
                await Count(0, "Their Copilot Studio credits are gone.", "SELECT COUNT(*) FROM dbo.copilot_studio_credit_user_daily WHERE entra_object_id = '" + other.UserObject + "'");
                await Count(0, "Their Copilot credit history is gone.", "SELECT COUNT(*) FROM dbo.copilot_cost_observations WHERE object_id = '" + other.UserObject + "'");
                await Count(0, "...including the service readings under it.", "SELECT COUNT(*) FROM dbo.copilot_cost_services WHERE object_id = '" + other.UserObject + "'");
                await Count(0, "...and their history bundles.", "SELECT COUNT(*) FROM dbo.copilot_cost_history WHERE object_id = '" + other.UserObject + "'");
                await Count(0, "Search text only they used is gone...", $"SELECT COUNT(*) FROM dbo.search_terms WHERE id = {other.OwnSearchTerm}");
                await Count(1, "...while search text someone else used too stays.", $"SELECT COUNT(*) FROM dbo.search_terms WHERE id = {other.SharedSearchTerm}");
                await Count(0, "A prompt phrase only they used is gone...", $"SELECT COUNT(*) FROM dbo.keywords WHERE id = {other.OwnPhrase}");
                await Count(1, "...while one someone else used too stays.", $"SELECT COUNT(*) FROM dbo.keywords WHERE id = {other.SharedPhrase}");
                await Count(0, "Their own email address is gone once no email refers to it.", $"SELECT COUNT(*) FROM dbo.email_addresses WHERE id = {other.OwnAddress}");

                await Count(1, "The other person's reply to their comment stays...", $"SELECT COUNT(*) FROM dbo.page_comments WHERE id = {other.Reply} AND parent_id IS NULL");
                await Count(2, "...as do the shares the other person made to them, without a recipient...",
                    $"SELECT (SELECT COUNT(*) FROM dbo.event_meta_power_app_share WHERE id = {other.AppShare} AND shared_with_user_id IS NULL) + " +
                    $"(SELECT COUNT(*) FROM dbo.event_meta_power_automate_flow_share WHERE id = {other.FlowShare} AND shared_with_user_id IS NULL)");
                await Count(1, "...and the other person's call, with them as the anonymous Unknown User.",
                    $"SELECT COUNT(*) FROM dbo.call_sessions s JOIN dbo.users u ON u.id = s.attendee_user_id WHERE s.call_record_id = {other.Call} AND u.user_name = 'Unknown User'");
                await Count(1, "The other person is untouched.", "SELECT COUNT(*) FROM dbo.users WHERE id = @other");
                foreach (var table in WeeklyProfileTables.Names)
                {
                    await Count(0, $"Their weekly profile ({table}) is gone...", $"SELECT COUNT(*) FROM profiling.[{table}] WHERE user_id = @user");
                    await Count(2, $"...while the other person's ({table}) stays.", $"SELECT COUNT(*) FROM profiling.[{table}] WHERE user_id = @other");
                }
            }
            finally
            {
                await ExecuteAsync(WeeklyProfileTables.DeleteSql(userId, other.OtherUser));
                await WeeklyProfileTables.DropAsync(ConnectionString(), profiles);
                await RemoveWhatOnlyThePurgeHandlesAsync(other);
            }
        }

        /// <summary>
        /// The procedure is all or nothing. When one of its deletes fails - here because something it doesn't know about
        /// still refers to the user - nothing it deleted before stays deleted, so a failed run never leaves someone
        /// half-erased.
        /// </summary>
        [TestMethod]
        public async Task CleanDataByUser_RollsEverythingBack_WhenADeleteFails()
        {
            int userId;
            using (var db = new AnalyticsEntitiesContext())
            {
                var user = await InsertTestDataAll(db);
                userId = user.ID;
            }
            var blocker = "test_clean_by_user_blocker_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                await CreateCleanDataByUserAsync();
                await ExecuteAsync($"CREATE TABLE dbo.{blocker} (user_id int NOT NULL REFERENCES dbo.users (id)); INSERT INTO dbo.{blocker} (user_id) VALUES ({userId});");

                var failure = await CaptureAsync(() => ExecuteCleanDataByUserAsync(userId));
                Assert.AreEqual(547, SqlErrorNumber(failure), failure?.ToString());

                Assert.AreEqual(1, Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM dbo.users WHERE id = {userId}")));
                Assert.IsTrue(Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM dbo.page_likes WHERE user_id = {userId}")) > 0,
                    "Their page likes, deleted early on, are back.");
                Assert.IsTrue(Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM dbo.audit_events WHERE user_id = {userId}")) > 0,
                    "So are their audit events.");
                Assert.IsTrue(Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM dbo.sessions WHERE user_id = {userId}")) > 0,
                    "And their web sessions.");

                await ExecuteAsync($"DROP TABLE dbo.{blocker};");
                await ExecuteCleanDataByUserAsync(userId);
                Assert.AreEqual(0, Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM dbo.users WHERE id = {userId}")), "Run again, it finishes.");
            }
            finally
            {
                await ExecuteAsync($"IF OBJECT_ID(N'dbo.{blocker}', N'U') IS NOT NULL DROP TABLE dbo.{blocker};");
            }
        }

        private sealed class OtherPeoplesData
        {
            public int OtherUser, Reply, AppShare, FlowShare, Call, CallType;
            public int OwnSearchTerm, SharedSearchTerm, OwnPhrase, SharedPhrase, OwnAddress, OtherSession, OtherInteractionSession;
            public Guid UserObject, CostRun;
        }

        /// <summary>
        /// What <see cref="InsertTestDataAll(AnalyticsEntitiesContext)"/> doesn't create: the person's audit metadata that
        /// does not cascade, their credits and credit history, and someone else's records that name them.
        /// </summary>
        private static async Task<OtherPeoplesData> SeedWhatOnlyThePurgeHandlesAsync(int userId)
        {
            var token = "cdu" + Guid.NewGuid().ToString("N").Substring(0, 12);
            const string sql = @"
SET NOCOUNT ON;
DECLARE @now datetime = SYSUTCDATETIME(), @other int, @userObject uniqueidentifier = NEWID();
UPDATE dbo.users SET azure_ad_id = CONVERT(nvarchar(36), @userObject), user_name = @token + '-user@contoso.local', mail = @token + '-user@contoso.local' WHERE id = @user;
INSERT INTO dbo.users (user_name, mail) VALUES (@token + '-other@contoso.local', @token + '-other@contoso.local'); SET @other = SCOPE_IDENTITY();

-- Audit metadata whose foreign keys to the event don't cascade.
DECLARE @entra uniqueidentifier = NEWID(), @app uniqueidentifier = NEWID(), @flow uniqueidentifier = NEWID(), @bi uniqueidentifier = NEWID(),
        @studio uniqueidentifier = NEWID(), @otherShare uniqueidentifier = NEWID();
INSERT INTO dbo.audit_events (id, time_stamp, user_id) VALUES (@entra, @now, @user), (@app, @now, @user), (@flow, @now, @user), (@bi, @now, @user),
    (@studio, @now, @user), (@otherShare, @now, @other);
INSERT INTO dbo.event_meta_azure_ad (event_id) VALUES (@entra);
INSERT INTO dbo.event_meta_power_app (event_id) VALUES (@app);
INSERT INTO dbo.event_meta_power_automate_flow (event_id) VALUES (@flow);
INSERT INTO dbo.event_meta_power_bi (event_id) VALUES (@bi);
INSERT INTO dbo.event_meta_copilot_studio (event_id) VALUES (@studio);

-- Someone else shared an app and a flow with them, and replied to their page comment.
DECLARE @appShare int, @flowShare int, @reply int;
INSERT INTO dbo.event_meta_power_app_share (event_id, shared_with_user_id) VALUES (@otherShare, @user); SET @appShare = SCOPE_IDENTITY();
INSERT INTO dbo.event_meta_power_automate_flow_share (event_id, shared_with_user_id) VALUES (@otherShare, @user); SET @flowShare = SCOPE_IDENTITY();
INSERT INTO dbo.page_comments (user_id, url_id, created, sp_id, comment, parent_id)
SELECT TOP (1) @other, c.url_id, @now, 1, N'reply', c.id FROM dbo.page_comments c WHERE c.user_id = @user;
SET @reply = SCOPE_IDENTITY();

-- The other person called them.
DECLARE @callType int, @call int;
INSERT INTO dbo.call_types (name) VALUES (@token); SET @callType = SCOPE_IDENTITY();
INSERT INTO dbo.call_records (organizer_id, call_type_id, start, [end], graph_id) VALUES (@other, @callType, @now, @now, @token); SET @call = SCOPE_IDENTITY();
INSERT INTO dbo.call_sessions (attendee_user_id, start, [end], call_record_id) VALUES (@user, @now, @now, @call);

-- Search text, prompt phrases and an address: some only theirs, some shared with the other person.
DECLARE @ownTerm int, @sharedTerm int, @otherSession int, @ownPhrase int, @sharedPhrase int, @ownAddress int;
DECLARE @userSession int = (SELECT TOP (1) id FROM dbo.sessions WHERE user_id = @user);
INSERT INTO dbo.search_terms (search_term) VALUES (N'own ' + @token); SET @ownTerm = SCOPE_IDENTITY();
INSERT INTO dbo.search_terms (search_term) VALUES (N'shared ' + @token); SET @sharedTerm = SCOPE_IDENTITY();
INSERT INTO dbo.sessions (user_id) VALUES (@other); SET @otherSession = SCOPE_IDENTITY();
INSERT INTO dbo.searches (session_id, search_term_id) VALUES (@userSession, @ownTerm), (@userSession, @sharedTerm), (@otherSession, @sharedTerm);

DECLARE @userChats int, @otherChats int, @userPrompt int, @otherPrompt int;
INSERT INTO dbo.copilot_interaction_sessions (session_ref, user_id) VALUES (@token + '-user', @user); SET @userChats = SCOPE_IDENTITY();
INSERT INTO dbo.copilot_interaction_sessions (session_ref, user_id) VALUES (@token + '-other', @other); SET @otherChats = SCOPE_IDENTITY();
INSERT INTO dbo.copilot_interactions (graph_interaction_id, session_id, user_id, created_utc, body_char_count, body_word_count, attachment_count, link_count, mention_count, context_count)
VALUES (@token + '-user', @userChats, @user, @now, 1, 1, 0, 0, 0, 0); SET @userPrompt = SCOPE_IDENTITY();
INSERT INTO dbo.copilot_interactions (graph_interaction_id, session_id, user_id, created_utc, body_char_count, body_word_count, attachment_count, link_count, mention_count, context_count)
VALUES (@token + '-other', @otherChats, @other, @now, 1, 1, 0, 0, 0, 0); SET @otherPrompt = SCOPE_IDENTITY();
INSERT INTO dbo.keywords (name) VALUES (N'own ' + @token); SET @ownPhrase = SCOPE_IDENTITY();
INSERT INTO dbo.keywords (name) VALUES (N'shared ' + @token); SET @sharedPhrase = SCOPE_IDENTITY();
INSERT INTO dbo.copilot_interaction_keywords (interaction_id, keyword_id) VALUES (@userPrompt, @ownPhrase), (@userPrompt, @sharedPhrase), (@otherPrompt, @sharedPhrase);

INSERT INTO dbo.email_addresses (address) VALUES (@token + '-user@contoso.local'); SET @ownAddress = SCOPE_IDENTITY();
INSERT INTO dbo.sent_emails (sent_date, graph_message_id, from_address_id, user_id) VALUES (@now, @token, @ownAddress, @user);

-- Copilot Studio credits, and Copilot credit history keyed by their object id (not yet linked to their user row).
INSERT INTO dbo.copilot_studio_credit_user_daily (usage_date, billed_credits, dimension_hash, imported_utc, user_id, entra_object_id)
VALUES (@now, 1, @token + '-linked', @now, @user, CONVERT(nvarchar(36), @userObject)),
       (@now, 1, @token + '-unlinked', @now, NULL, CONVERT(nvarchar(36), @userObject));
DECLARE @costRun uniqueidentifier = NEWID();
INSERT INTO dbo.copilot_cost_runs (run_id, generation, inventory_date, last_served) VALUES (@costRun, @token, '2026-09-01', SYSDATETIMEOFFSET());
INSERT INTO dbo.copilot_cost_work (run_id, object_id, user_id, next_attempt) VALUES (@costRun, @userObject, @user, SYSDATETIMEOFFSET());
INSERT INTO dbo.copilot_cost_observations (observation_date, object_id, user_id, observed_at, quality, selection_status)
VALUES ('2026-09-01', @userObject, NULL, SYSDATETIMEOFFSET(), 2, N'{}');
INSERT INTO dbo.copilot_cost_services (observation_date, object_id, category_hash, category, policy_id)
VALUES ('2026-09-01', @userObject, HASHBYTES('SHA2_256', N'chat'), N'chat', N'policy');
INSERT INTO dbo.copilot_cost_history (run_id, publication_id, observation_date, object_id, observed_at, quality, bundle)
VALUES (@costRun, NEWID(), '2026-09-01', @userObject, SYSDATETIMEOFFSET(), 2, N'{}');

SELECT @other, @reply, @appShare, @flowShare, @call, @callType, @ownTerm, @sharedTerm, @ownPhrase, @sharedPhrase, @ownAddress,
       @otherSession, @otherChats, @userObject, @costRun;";

            using (var connection = new System.Data.SqlClient.SqlConnection(ConnectionString()))
            using (var command = new System.Data.SqlClient.SqlCommand(sql, connection))
            {
                await connection.OpenAsync();
                command.Parameters.AddWithValue("@token", token);
                command.Parameters.AddWithValue("@user", userId);
                using (var reader = await command.ExecuteReaderAsync())
                {
                    Assert.IsTrue(await reader.ReadAsync());
                    return new OtherPeoplesData
                    {
                        OtherUser = reader.GetInt32(0), Reply = reader.GetInt32(1), AppShare = reader.GetInt32(2), FlowShare = reader.GetInt32(3),
                        Call = reader.GetInt32(4), CallType = reader.GetInt32(5), OwnSearchTerm = reader.GetInt32(6), SharedSearchTerm = reader.GetInt32(7),
                        OwnPhrase = reader.GetInt32(8), SharedPhrase = reader.GetInt32(9), OwnAddress = reader.GetInt32(10),
                        OtherSession = reader.GetInt32(11), OtherInteractionSession = reader.GetInt32(12),
                        UserObject = reader.GetGuid(13), CostRun = reader.GetGuid(14),
                    };
                }
            }
        }

        private static Task RemoveWhatOnlyThePurgeHandlesAsync(OtherPeoplesData other) => ExecuteAsync($@"
DELETE FROM dbo.copilot_cost_services WHERE object_id = '{other.UserObject}';
DELETE FROM dbo.copilot_cost_observations WHERE object_id = '{other.UserObject}';
DELETE FROM dbo.copilot_cost_history WHERE run_id = '{other.CostRun}';
DELETE FROM dbo.copilot_cost_work WHERE run_id = '{other.CostRun}';
DELETE FROM dbo.copilot_cost_runs WHERE run_id = '{other.CostRun}';
DELETE FROM dbo.copilot_studio_credit_user_daily WHERE entra_object_id = '{other.UserObject}';
DELETE FROM dbo.copilot_interaction_keywords WHERE keyword_id IN ({other.OwnPhrase}, {other.SharedPhrase});
DELETE FROM dbo.copilot_interactions WHERE session_id = {other.OtherInteractionSession};
DELETE FROM dbo.copilot_interaction_sessions WHERE id = {other.OtherInteractionSession};
DELETE FROM dbo.keywords WHERE id IN ({other.OwnPhrase}, {other.SharedPhrase});
DELETE FROM dbo.searches WHERE search_term_id IN ({other.OwnSearchTerm}, {other.SharedSearchTerm});
DELETE FROM dbo.sessions WHERE id = {other.OtherSession};
DELETE FROM dbo.search_terms WHERE id IN ({other.OwnSearchTerm}, {other.SharedSearchTerm});
DELETE FROM dbo.sent_emails WHERE from_address_id = {other.OwnAddress};
DELETE FROM dbo.email_addresses WHERE id = {other.OwnAddress};
DELETE FROM dbo.page_comments WHERE id = {other.Reply};
DELETE FROM dbo.event_meta_power_app_share WHERE id = {other.AppShare};
DELETE FROM dbo.event_meta_power_automate_flow_share WHERE id = {other.FlowShare};
DELETE FROM dbo.audit_events WHERE user_id = {other.OtherUser};
DELETE s FROM dbo.call_sessions s JOIN dbo.call_records r ON r.id = s.call_record_id WHERE r.call_type_id = {other.CallType};
DELETE FROM dbo.call_records WHERE call_type_id = {other.CallType};
DELETE FROM dbo.call_types WHERE id = {other.CallType};
DELETE FROM dbo.users WHERE id = {other.OtherUser};");

        private static async Task CreateCleanDataByUserAsync()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                foreach (var statement in StringUtils.SplitSqlStatements(ResourceProxy.CreateOrUpdateClean_Data_By_User_StoredProc))
                {
                    await db.Database.ExecuteSqlCommandAsync(statement);
                }
            }
        }

        /// <summary>As a DBA runs it: <c>EXEC dbo.CleanDataByUser</c>, with no transaction of the caller's around it.</summary>
        private static async Task ExecuteCleanDataByUserAsync(int userId)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.CommandTimeout = 300;
                await db.Database.ExecuteSqlCommandAsync(System.Data.Entity.TransactionalBehavior.DoNotEnsureTransaction, "EXEC dbo.CleanDataByUser @p0", userId);
            }
        }

        private static async Task ExecuteAsync(string sql)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.CommandTimeout = 300;
                await db.Database.ExecuteSqlCommandAsync(System.Data.Entity.TransactionalBehavior.DoNotEnsureTransaction, sql);
            }
        }

        private static string ConnectionString()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                return db.Database.Connection.ConnectionString;
            }
        }

        private static async Task<object> ScalarAsync(string sql)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                return await db.Database.SqlQuery<int>(sql).FirstAsync();
            }
        }

        private static async Task<Exception> CaptureAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                return ex;
            }
            Assert.Fail("Expected the procedure to fail.");
            return null;
        }

        /// <summary>The SQL Server error number of the first SqlException in <paramref name="error"/>'s chain, whichever client raised it.</summary>
        private static int? SqlErrorNumber(Exception error)
        {
            for (var e = error; e != null; e = e.InnerException)
            {
                if (e.GetType().Name == "SqlException")
                {
                    return (int)e.GetType().GetProperty("Number").GetValue(e);
                }
            }
            return null;
        }


        private async Task<User> InsertTestDataAll(AnalyticsEntitiesContext db)
        {
            var user = new User { UserPrincipalName = "user" + DateTime.Now.Ticks + "@example.com", Mail = "user" + DateTime.Now.Ticks + "@example.com" };
            db.users.Add(user);
            await InsertTestDataAll(user, db);
            return user;
        }
        private async Task InsertTestDataAll(User user, AnalyticsEntitiesContext db)
        {

            var oneYearAgo = DateTime.Now.AddYears(-1);
            long ticks = DateTime.Now.Ticks;

            // Base lookups first (reused later)
            var site = new Site { UrlBase = "http://site-" + ticks, SiteId = "SiteId-" + ticks };
            db.sites.Add(site);

            var web = new Web { url_base = site.UrlBase + "/web-" + ticks, title = "Web Title " + ticks, site = site };
            db.webs.Add(web);

            var url = new Url { FullUrl = web.url_base + "/page-" + ticks };
            db.urls.Add(url);

            var op = new EventOperation { Name = "Operation-" + ticks };
            db.event_operations.Add(op);


            var browser = new Browser { browser_name = "Browser-" + ticks };
            var city = new City { city_name = "City-" + ticks };
            var country = new Country { country_name = "Country-" + ticks };
            var device = new Device { device_name = "Device-" + ticks };
            var os = new Common.Entities.OperatingSystem { os_name = "OS-" + ticks };
            db.browsers.Add(browser);
            db.cities.Add(city);
            db.countries.Add(country);
            db.devices.Add(device);
            db.operating_systems.Add(os);

            var pageTitle = new PageTitle { title = "Title-" + ticks };
            db.page_titles.Add(pageTitle);

            var lang = new Language { Name = "Lang-" + (ticks % 900000) };
            db.Languages.Add(lang);

            var keyword = new KeyWord { Name = "Keyword-" + ticks };
            db.KeyWords.Add(keyword);

            var fileField = new FileMetadataFieldName { Name = "Field-" + ticks };
            db.FileMetadataFields.Add(fileField);

            var fileProp = new FileMetadataPropertyValue
            {
                Url = url,
                Field = fileField,
                FieldValue = "Value-" + ticks,
                Updated = oneYearAgo
            };
            db.FileMetadataPropertyValues.Add(fileProp);

            // URL user records
            var pageLike = new PageLike { Url = url, User = user, Created = oneYearAgo, SpID = (int)(ticks % int.MaxValue) };
            var pageComment = new PageComment { Url = url, User = user, Created = oneYearAgo, Comment = "Comment-" + ticks, SpID = (int)(ticks % int.MaxValue) };
            db.UrlLikes.Add(pageLike);
            db.UrlComments.Add(pageComment);

            // Audit base event
            var auditEvent = new CommonAuditEvent
            {
                Id = Guid.NewGuid(),
                TimeStamp = oneYearAgo,
                Operation = op,
                User = user,
                EventData = "{}"
            };
            db.AuditEventsCommon.Add(auditEvent);

            // SP Event type + extension + filename
            var spExt = new SPEventFileExtension { extension_name = "Ext-" + ticks };
            var spFileName = new SPEventFileName { Name = "File-" + ticks };
            var spType = new SPEventType { type_name = "Type-" + ticks };
            db.event_file_ext.Add(spExt);
            db.event_file_names.Add(spFileName);
            db.event_types.Add(spType);

            var spEvent = new SharePointEventMetadata
            {
                url = url,
                AuditEvent = new CommonAuditEvent
                {
                    Id = Guid.NewGuid(),
                    Operation = op,
                    User = user,
                    TimeStamp = oneYearAgo,
                    EventData = "{}"
                }
            };
            db.sharepoint_events.Add(spEvent);

            // A web session, and a page view in it: what anyone who visits a tracked page has.
            var session = new UserSession { ai_session_id = "sess-" + ticks, user = user };
            db.sessions.Add(session);

            var hit = new Hit
            {
                url = url,
                web = web,
                agent = browser,
                city = city,
                country = country,
                device = device,
                os = os,
                hit_timestamp = oneYearAgo,
                page_title = pageTitle,
                page_request_id = Guid.NewGuid(),
                session = session
            };
            db.hits.Add(hit);

            // Search term + search
            var term = new SearchTerm { search_term = "term-" + ticks };
            db.search_terms.Add(term);

            var search = new Search
            {
                search_term = term,
                session = session,
                DateTime = oneYearAgo
            };
            db.searches.Add(search);

            // Org + OrgUrl
            var org = new Org { org_name = "Org-" + ticks };
            db.orgs.Add(org);
            var orgUrl = new OrgUrl { UrlBase = "https://org-" + ticks + ".example.com" };
            db.org_urls.Add(orgUrl);

            // Import log
            var import = new ImportLog { time_stamp = oneYearAgo, import_message = "Details-" + ticks, contents = "Test", machine_name = "UnitTestVM" };
            db.import_log.Add(import);

            // Ignored event
            var ignored = new IgnoredEvent { processed_timestamp = oneYearAgo, event_id = Guid.NewGuid() };
            db.ignored_audit_events.Add(ignored);

            // Geography
            var province = new Province { province_name = "Prov-" + ticks };
            db.provinces.Add(province);

            // Simple taxonomy style lookups for user
            var dept = new UserDepartment { Name = "Dept-" + ticks };
            var job = new UserJobTitle { Name = "Job-" + ticks };
            var officeLoc = new UserOfficeLocation { Name = "Office-" + ticks };
            var usageLoc = new UserUsageLocation { Name = "UL-" + ticks };
            var licType = new LicenseType { Name = "Lic-" + ticks };
            var stateOrProv = new StateOrProvince { Name = "StateProv-" + ticks };
            var countryOrRegion = new CountryOrRegion { Name = "CountryRegion-" + ticks };
            var company = new CompanyName { Name = "Company-" + ticks };

            db.UserDepartments.Add(dept);
            db.UserJobTitles.Add(job);
            db.UserOfficeLocations.Add(officeLoc);
            db.UserUsageLocations.Add(usageLoc);
            db.LicenseTypes.Add(licType);
            db.StateOrProvinces.Add(stateOrProv);
            db.CountryOrRegions.Add(countryOrRegion);
            db.CompanyNames.Add(company);

            var userLicense = new UserLicenseTypeLookup { License = licType, User = user };
            db.UserLicenseTypeLookups.Add(userLicense);

            // Teams definitions
            var team = new TeamDefinition
            {
                Name = "Team-" + ticks,
                GraphID = "TeamGraph-" + ticks,
                FirstDiscovered = oneYearAgo,
                HasRefreshToken = false
            };
            db.Teams.Add(team);

            var teamChannel = new Common.Entities.Teams.TeamChannel
            {
                GraphID = "ChanGraph-" + ticks,
                Name = "Channel-" + ticks,
                Team = team
            };
            db.TeamChannels.Add(teamChannel);

            var owner = new TeamOwners
            {
                Team = team,
                Owner = user,
                Discovered = oneYearAgo
            };
            db.TeamOwners.Add(owner);

            var membership = new TeamMembershipLog
            {
                Team = team,
                User = user,
                Date = oneYearAgo
            };
            db.TeamMembershipLogs.Add(membership);

            var tabDef = new TeamTabDefinition
            {
                GraphID = "TabGraph-" + ticks,
                Name = "Tab-" + ticks,
                WebUrl = "https://tab/" + ticks
            };
            db.TeamTabDefinitions.Add(tabDef);

            var channelTabLog = new ChannelTabLog
            {
                Channel = teamChannel,
                Date = oneYearAgo,
                TabDefinition = tabDef
            };
            db.ChannelTabLogs.Add(channelTabLog);

            // Channel stats & associations
            var chanStats = new ChannelStatsLog
            {
                Channel = teamChannel,
                Date = oneYearAgo,
                ChatsCount = 1,
                SentimentScore = 0.5
            };
            db.TeamChannelStats.Add(chanStats);

            var chanKeyword = new ChannelLogKeyword
            {
                ChannelStatsLog = chanStats,
                KeyWord = keyword,
                KeyWordCount = 2
            };
            db.TeamChannelStatKeywords.Add(chanKeyword);

            var chanLang = new ChannelLogLanguage
            {
                ChannelStatsLog = chanStats,
                Language = lang
            };
            db.TeamChannelStatLanguages.Add(chanLang);

            // Reactions
            var reactType = new TeamsReactionType { Name = "Reaction-" + ticks };
            db.TeamsReactionTypes.Add(reactType);

            var reaction = new TeamsUserReaction
            {
                Reaction = reactType,
                User = user,
                Channel = teamChannel,
                Date = oneYearAgo
            };
            db.TeamsUserReactions.Add(reaction);

            // Usage activity logs
            db.TeamUserActivityLogs.Add(new GlobalTeamsUserUsageLog
            {
                User = user,
                Date = oneYearAgo,
                PrivateChatMessageCount = 1,
                TeamChatMessageCount = 1,
                CallCount = 0,
                MeetingCount = 0
            });

            db.TeamsUserDeviceUsageLog.Add(new GlobalTeamsUserDeviceUsageLog
            {
                User = user,
                Date = oneYearAgo,
                UsedWindows = true
            });

            db.AppPlatformUserUsageLog.Add(new AppPlatformUserActivityLog
            {
                User = user,
                Date = oneYearAgo,
                Web = true
            });

            db.OutlookUsageActivityLogs.Add(new OutlookUsageActivityLog { User = user, Date = oneYearAgo });
            db.OneDriveUserActivityLogs.Add(new OneDriveUserActivityLog { User = user, Date = oneYearAgo });
            db.OneDriveUsageLogs.Add(new OneDriveUsageLog { Date = oneYearAgo, StorageUsedInBytes = 1 });
            db.SharePointUserActivityLogs.Add(new SharePointUserActivityLog { User = user, Date = oneYearAgo });
            db.YammerUserActivityLogs.Add(new YammerUserActivityLog { User = user, Date = oneYearAgo });
            db.YammerGroupActivityLogs.Add(new YammerGroupActivityLog { Date = oneYearAgo, PostedCount = 1 });
            db.YammerDeviceActivityLogs.Add(new YammerDeviceActivityLog { Date = oneYearAgo, UsedWeb = true });
            db.SharePointSiteStats.Add(new SharePointSitesFileWeeklyStats { Site = site, ForWeekEnding = oneYearAgo });

            // Call related
            var callType = new CallType { Name = "CallType-" + ticks };
            db.CallTypes.Add(callType);

            var callModality = new CallModality { Name = "Modality-" + ticks };
            db.CallModalities.Add(callModality);

            var callRecord = new CallRecord
            {
                Organizer = user,
                CallType = callType,
                GraphID = "CallGraph-" + ticks,
                StartDateTime = oneYearAgo,
                EndDateTime = oneYearAgo.AddMinutes(5)
            };
            db.CallRecords.Add(callRecord);

            var callSession = new CallSession
            {
                Attendee = user,
                Start = oneYearAgo,
                End = oneYearAgo.AddMinutes(5),
                ParentRecord = callRecord
            };
            db.CallSessions.Add(callSession);

            var callSessionModality = new CallSessionModalityLookup
            {
                CallSession = callSession,
                CallModality = callModality
            };
            db.CallModalityLookups.Add(callSessionModality);

            var callFeedback = new CallFeedback
            {
                Call = callRecord,
                Rating = "5"
            };
            db.CallFeedback.Add(callFeedback);

            var callFailureReason = new CallFailureReasonLookup
            {
                Call = callRecord,
                Reason = "None-" + ticks
            };
            db.CallFailures.Add(callFailureReason);

            // Yammer
            var yammerGroup = new YammerGroup { Name = "Group-" + ticks };
            db.YammerGroups.Add(yammerGroup);

            // O365 client app
            var clientApp = new O365ClientApplication { ClientApplicationId = Guid.NewGuid(), Name = "ClientAppName-" + ticks };
            db.O365ClientApplications.Add(clientApp);

            // Click tracking
            var clickedTitle = new ClickedElementTitle { Name = "CTitle-" + ticks };
            var clickedClasses = new ClickedElementsClassNames { AllClassNames = "cls-" + ticks };
            db.ClickedElementTitles.Add(clickedTitle);
            db.ClickedElementsClassNames.Add(clickedClasses);

            var click = new Clicks
            {
                Url = url,
                TimeStamp = oneYearAgo,
                Title = clickedTitle,
                ClassNames = clickedClasses,
                PageView = hit
            };
            db.Clicks.Add(click);

            var copilotFileEvent = new CopilotEventMetadataFile
            {
                // Reuse existing SP file extension & file name & site
                FileExtension = spExt,
                FileName = spFileName,
                Site = site,
                RelatedChat = new CopilotChat
                {
                    AuditEvent = new CommonAuditEvent
                    {
                        Id = Guid.NewGuid(),
                        TimeStamp = oneYearAgo,
                        Operation = op,
                        User = user,
                        EventData = "{}"
                    },
                    AppHost = "AppHost-" + ticks
                }
            };

            // Use generic set to avoid needing explicit DbSet property
            db.CopilotEventMetadataFiles.Add(copilotFileEvent);

            await db.SaveChangesAsync();
        }
    }
}
