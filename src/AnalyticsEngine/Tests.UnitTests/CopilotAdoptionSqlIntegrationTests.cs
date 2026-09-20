using Common.Entities;
using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// Runs the Copilot licence-adoption queries against a real, throwaway SQL Server database.
    ///
    /// The pure-logic tests in <see cref="CopilotAdoptionTests"/> cannot catch the failures that
    /// actually break this feature in production: a query that does not parse, a column that does not
    /// exist, a result column that does not map onto its DTO, or an aggregate that quietly counts the
    /// wrong rows. All of those only show up against a real database, and the queries here are
    /// hand-written SQL rather than LINQ, so nothing else would catch them before a customer did.
    ///
    /// Each test builds only the tables its query touches, seeds a handful of rows and drops the
    /// database again, following the same pattern as the migration tests.
    /// </summary>
    [TestClass]
    public class CopilotAdoptionSqlIntegrationTests
    {
        /// <summary>
        /// A bare EF context used purely to execute raw SQL, so the tests exercise the exact
        /// materialisation path the service uses (<c>Database.SqlQuery&lt;T&gt;</c>) rather than a
        /// hand-rolled reader that could mask a column/property mismatch.
        /// </summary>
        private sealed class RawSqlContext : DbContext
        {
            static RawSqlContext()
            {
                // Nothing here owns a model, and the scratch database is built by the test itself.
                Database.SetInitializer<RawSqlContext>(null);
            }

            public RawSqlContext(string connectionString) : base(connectionString)
            {
            }
        }


        #region Licensed users

        [TestMethod]
        public void LicensedUsersQuery_RunsAndSplitsWindowFromHistory()
        {
            using (var db = ScratchDatabase.Create("CopilotAdoptLic"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);
                CreateCopilotReportTable(db);

                db.Execute(
                    @"INSERT INTO dbo.user_departments (id, name)
                          VALUES (1, N'Engineering'), (2, N'Καλημέρα κόσμε');
                      INSERT INTO dbo.license_types (id, name, sku_id)
                          VALUES (1, N'Microsoft Copilot for Microsoft 365', N'Microsoft_365_Copilot'),
                                 (2, N'Office 365 E3', N'ENTERPRISEPACK');

                      -- 'greekdept' carries the Unicode guard on a column that is genuinely Unicode in
                      -- production: its department name. Its UPN stays ASCII because Entra does not
                      -- permit anything else (#402/#414).
                      INSERT INTO dbo.users (id, user_name, mail, account_enabled, department_id)
                          VALUES (10, N'active@contoso.com',  N'active@contoso.com',  1, 1),
                                 (11, N'dormant@contoso.com', N'dormant@contoso.com', 1, 1),
                                 (12, N'never@contoso.com',   N'never@contoso.com',   0, 1),
                                 (13, N'unlicensed@contoso.com', N'unlicensed@contoso.com', 1, 1),
                                 (14, N'greekdept@contoso.com', N'greekdept@contoso.com', 1, 2);

                      INSERT INTO dbo.user_license_type_lookups (id, user_id, license_type_id)
                          VALUES (1, 10, 1), (2, 11, 1), (3, 12, 1), (4, 14, 1),
                                 (5, 13, 2);   -- E3 only: not a Copilot seat

                      INSERT INTO dbo.copilot_agents (id, name, agent_id)
                          VALUES (1, N'Copilot Cowork', N'Copilot.M365Copilot.CoworkChat');");

                // 'active' used Copilot on two days inside the window across two app hosts, one of
                // them Cowork; 'dormant' only used it before the window; 'never' has nothing at all.
                SeedCopilotInteraction(db, userId: 10, daysAgo: 1, appHost: "Teams");
                SeedCopilotInteraction(db, userId: 10, daysAgo: 1, appHost: "Teams");
                SeedCopilotInteraction(db, userId: 10, daysAgo: 5, appHost: "cowork", agentId: 1);
                SeedCopilotInteraction(db, userId: 11, daysAgo: 120, appHost: "Word");
                SeedCopilotInteraction(db, userId: 13, daysAgo: 2, appHost: "Copilot Chat");

                var sql = CopilotAdoptionSql.LicensedUsersSql(
                    new[] { 1 },
                    new[] { 1 },
                    includeCopilotReport: false);

                var rows = Query<LicensedUserUsageRow>(db, sql,
                    new SqlParameter("@from", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@historyFrom", DateTime.UtcNow.Date.AddDays(-365)),
                    new SqlParameter("@maxRows", 1000));

                Assert.AreEqual(4, rows.Count, "Only the four Copilot-licensed users should be returned.");
                Assert.IsFalse(rows.Any(r => r.UserPrincipalName == "unlicensed@contoso.com"),
                    "A user with only an E3 licence must not be counted as a Copilot seat.");
                Assert.AreEqual("Καλημέρα κόσμε",
                    rows.Single(r => r.UserPrincipalName == "greekdept@contoso.com").Department,
                    "A non-ASCII department name must survive the query unchanged. This is a real "
                    + "encoding guard: user_departments.name is nvarchar, so a varchar column or a "
                    + "non-N literal anywhere on this path would return question marks.");

                var active = rows.Single(r => r.UserPrincipalName == "active@contoso.com");
                Assert.AreEqual(3, active.Interactions);
                Assert.AreEqual(2, active.ActiveDays, "Two distinct days inside the window.");
                Assert.AreEqual(2, active.AppsUsed, "Teams and Cowork are two distinct app hosts.");
                Assert.AreEqual(1, active.CoworkInteractions, "The Cowork interaction must be identified.");
                Assert.AreEqual(0, active.PriorInteractions);
                Assert.AreEqual("Engineering", active.Department);

                var dormant = rows.Single(r => r.UserPrincipalName == "dormant@contoso.com");
                Assert.AreEqual(0, dormant.Interactions, "Nothing inside the window...");
                Assert.AreEqual(1, dormant.PriorInteractions, "...but history within the lookback, which is what makes them dormant rather than never-used.");
                Assert.IsNotNull(dormant.LastInteractionUtc);

                var never = rows.Single(r => r.UserPrincipalName == "never@contoso.com");
                Assert.AreEqual(0, never.Interactions);
                Assert.AreEqual(0, never.PriorInteractions);
                Assert.IsNull(never.LastInteractionUtc);
                Assert.AreEqual(false, never.AccountEnabled,
                    "A disabled account still holding a seat is the clearest reclaim there is, so the flag must survive.");
            }
        }

        /// <summary>
        /// Guards the Unicode requirement end to end on the columns that are genuinely Unicode in
        /// production: a varchar column or a non-N string literal anywhere on this path turns a Greek
        /// department or display name into question marks, in an export about to be sent to an executive.
        ///
        /// The UPN is deliberately ASCII. Entra restricts userPrincipalName to ASCII (#402/#414), so
        /// dbo.users.user_name is varchar(250) by design; asserting a Greek UPN round-trips here would
        /// only pass by declaring the fixture column wider than production, which proves nothing.
        /// </summary>
        [TestMethod]
        public void LicensedUsersQuery_PreservesNonLatinMetadata()
        {
            using (var db = ScratchDatabase.Create("CopilotAdoptUni"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);

                db.Execute(
                    @"INSERT INTO dbo.user_departments (id, name) VALUES (1, N'Καλημέρα κόσμε');
                      INSERT INTO dbo.license_types (id, name, sku_id)
                          VALUES (1, N'Microsoft Copilot for Microsoft 365', N'Microsoft_365_Copilot');
                      INSERT INTO dbo.users (id, user_name, mail, account_enabled, department_id)
                          VALUES (1, N'greek.dept@contoso.com', N'greek.dept@contoso.com', 1, 1);
                      INSERT INTO dbo.user_license_type_lookups (id, user_id, license_type_id) VALUES (1, 1, 1);");

                var rows = Query<LicensedUserUsageRow>(
                    db,
                    CopilotAdoptionSql.LicensedUsersSql(new[] { 1 }, new int[0], includeCopilotReport: false),
                    new SqlParameter("@from", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@historyFrom", DateTime.UtcNow.Date.AddDays(-365)),
                    new SqlParameter("@maxRows", 1000));

                Assert.AreEqual(1, rows.Count);
                Assert.AreEqual("greek.dept@contoso.com", rows[0].UserPrincipalName);
                Assert.AreEqual("Καλημέρα κόσμε", rows[0].Department);
            }
        }

        [TestMethod]
        public void LicensedUsersQuery_ReadsMicrosoftsPerUserReportSnapshot()
        {
            using (var db = ScratchDatabase.Create("CopilotAdoptRpt"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);
                CreateCopilotReportTable(db);

                var snapshot = DateTime.UtcNow.Date.AddDays(-3);
                var inWindow = DateTime.UtcNow.Date.AddDays(-5);
                var beforeWindow = DateTime.UtcNow.Date.AddDays(-200);

                db.Execute(
                    $@"INSERT INTO dbo.license_types (id, name, sku_id)
                           VALUES (1, N'Microsoft Copilot for Microsoft 365', N'Microsoft_365_Copilot');
                       INSERT INTO dbo.users (id, user_name, account_enabled) VALUES (1, N'rpt@contoso.com', 1);
                       INSERT INTO dbo.user_license_type_lookups (id, user_id, license_type_id) VALUES (1, 1, 1);

                       INSERT INTO dbo.copilot_usage_user_activity_log
                           (id, [date], user_id, last_activity_date, report_period_days,
                            prompts_all_apps, active_usage_days, report_version,
                            teams_last_activity_date, word_last_activity_date, excel_last_activity_date,
                            chat_last_activity_date)
                       VALUES (1, '{snapshot:yyyy-MM-dd}', 1, '{inWindow:yyyy-MM-dd}', 28,
                               47, 12, N'v2',
                               '{inWindow:yyyy-MM-dd}', '{inWindow:yyyy-MM-dd}', '{beforeWindow:yyyy-MM-dd}',
                               '{inWindow:yyyy-MM-dd}');");

                var rows = Query<LicensedUserUsageRow>(
                    db,
                    CopilotAdoptionSql.LicensedUsersSql(new[] { 1 }, new int[0], includeCopilotReport: true),
                    new SqlParameter("@from", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@historyFrom", DateTime.UtcNow.Date.AddDays(-365)),
                    new SqlParameter("@copilotReportDate", snapshot),
                    new SqlParameter("@copilotReportPeriodDays", 28),
                    new SqlParameter("@maxRows", 1000));

                Assert.AreEqual(1, rows.Count);
                var row = rows[0];

                Assert.AreEqual(47, row.ReportPrompts);
                Assert.AreEqual(12, row.ReportActiveDays);
                Assert.AreEqual(3, row.ReportAppsUsed,
                    "Report app breadth is derived from documented per-app last-activity dates; chat work/web collapse to one surface.");
                Assert.AreEqual(inWindow, row.ReportLastActivityUtc,
                    "The last-activity date is the latest across every per-app column.");
            }
        }

        [TestMethod]
        public void LicensedUsersQuery_PinsTheReportPeriodSoUsersAreNotDuplicated()
        {
            // Regression guard. copilot_usage_user_activity_log holds one row per
            // (date, user_id, report_period_days), and Graph publishes D7/D28/D90/D180 - so a snapshot
            // selected by date alone returns several rows for the same user. Joined to dbo.users that
            // multiplies every licensed user by the number of stored periods, which inflated adoption
            // counts past the licensed population and burned the row cap on duplicates.
            using (var db = ScratchDatabase.Create("CopilotAdoptPeriod"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);
                CreateCopilotReportTable(db);

                var snapshot = DateTime.UtcNow.Date.AddDays(-3);
                var inWindow = DateTime.UtcNow.Date.AddDays(-5);

                db.Execute(
                    $@"INSERT INTO dbo.license_types (id, name, sku_id)
                           VALUES (1, N'Microsoft Copilot for Microsoft 365', N'Microsoft_365_Copilot');
                       INSERT INTO dbo.users (id, user_name, account_enabled) VALUES (1, N'period@contoso.com', 1);
                       INSERT INTO dbo.user_license_type_lookups (id, user_id, license_type_id) VALUES (1, 1, 1);

                       -- The SAME user and date, published for two different periods.
                       INSERT INTO dbo.copilot_usage_user_activity_log
                           (id, [date], user_id, last_activity_date, report_period_days,
                            prompts_all_apps, active_usage_days,
                            teams_last_activity_date, word_last_activity_date, excel_last_activity_date,
                            chat_last_activity_date)
                       VALUES
                           (1, '{snapshot:yyyy-MM-dd}', 1, '{inWindow:yyyy-MM-dd}', 7,
                            11, 3,
                            '{inWindow:yyyy-MM-dd}', NULL, NULL, NULL),
                           (2, '{snapshot:yyyy-MM-dd}', 1, '{inWindow:yyyy-MM-dd}', 28,
                            47, 12,
                            '{inWindow:yyyy-MM-dd}', NULL, NULL, NULL);");

                var sql = CopilotAdoptionSql.LicensedUsersSql(new[] { 1 }, new int[0], includeCopilotReport: true);

                var d28 = Query<LicensedUserUsageRow>(
                    db, sql,
                    new SqlParameter("@from", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@historyFrom", DateTime.UtcNow.Date.AddDays(-365)),
                    new SqlParameter("@copilotReportDate", snapshot),
                    new SqlParameter("@copilotReportPeriodDays", 28),
                    new SqlParameter("@maxRows", 1000));

                Assert.AreEqual(1, d28.Count, "One licensed user must produce exactly one row, not one per period.");
                Assert.AreEqual(47, d28[0].ReportPrompts, "The pinned D28 figures must be the ones returned.");

                // Asking for the other period returns that period's numbers for the same single user.
                var d7 = Query<LicensedUserUsageRow>(
                    db, sql,
                    new SqlParameter("@from", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@historyFrom", DateTime.UtcNow.Date.AddDays(-365)),
                    new SqlParameter("@copilotReportDate", snapshot),
                    new SqlParameter("@copilotReportPeriodDays", 7),
                    new SqlParameter("@maxRows", 1000));

                Assert.AreEqual(1, d7.Count);
                Assert.AreEqual(11, d7[0].ReportPrompts);
            }
        }

        #endregion

        #region Licence opportunities

        [TestMethod]
        public void OpportunitiesQuery_RanksUnlicensedUsersAndAgreesWithTheCsharpScore()
        {
            // The database ranks candidates so a 200,000-user tenant does not have to be pulled into
            // memory, but the score shown to the user is always recomputed in C#. If the two ever drift
            // apart the report would return the wrong people while still looking self-consistent, so
            // the agreement is asserted explicitly here.
            using (var db = ScratchDatabase.Create("CopilotAdoptOpp"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);
                CreateM365UsageTables(db);

                var snapshot = DateTime.UtcNow.Date.AddDays(-3);

                db.Execute(
                    $@"INSERT INTO dbo.user_departments (id, name) VALUES (1, N'Finance');
                       INSERT INTO dbo.license_types (id, name, sku_id)
                           VALUES (1, N'Microsoft Copilot for Microsoft 365', N'Microsoft_365_Copilot');

                       INSERT INTO dbo.users (id, user_name, account_enabled, department_id)
                           VALUES (1, N'licensed@contoso.com',   1, 1),
                                  (2, N'heavy@contoso.com',      1, 1),
                                  (3, N'light@contoso.com',      1, 1),
                                  (4, N'disabled@contoso.com',   0, 1),
                                  (5, N'inactive@contoso.com',   1, 1);

                       INSERT INTO dbo.user_license_type_lookups (id, user_id, license_type_id) VALUES (1, 1, 1);

                       INSERT INTO dbo.teams_user_activity_log
                           (id, [date], user_id, last_activity_date, private_chat_count, team_chat_count,
                            post_messages, reply_messages, meetings_attended_count, meetings_organized_count)
                       VALUES (1, '{snapshot:yyyy-MM-dd}', 1, '{snapshot:yyyy-MM-dd}', 10, 10, 0, 0, 5, 5),
                              (2, '{snapshot:yyyy-MM-dd}', 2, '{snapshot:yyyy-MM-dd}', 40, 20, 10, 10, 20, 10),
                              (3, '{snapshot:yyyy-MM-dd}', 3, '{snapshot:yyyy-MM-dd}',  2,  0,  0,  0,  1,  0),
                              (4, '{snapshot:yyyy-MM-dd}', 4, '{snapshot:yyyy-MM-dd}', 90, 90, 90, 90, 90, 90);

                       INSERT INTO dbo.outlook_user_activity_log
                           (id, [date], user_id, last_activity_date, email_send_count, email_receive_count, email_read_count)
                       VALUES (1, '{snapshot:yyyy-MM-dd}', 2, '{snapshot:yyyy-MM-dd}', 60, 100, 90),
                              (2, '{snapshot:yyyy-MM-dd}', 3, '{snapshot:yyyy-MM-dd}',  1,   2,   1);

                       INSERT INTO dbo.sharepoint_user_activity_log (id, [date], user_id, last_activity_date, viewed_or_edited)
                       VALUES (1, '{snapshot:yyyy-MM-dd}', 2, '{snapshot:yyyy-MM-dd}', 30);

                       INSERT INTO dbo.onedrive_user_activity_log (id, [date], user_id, last_activity_date, viewed_or_edited)
                       VALUES (1, '{snapshot:yyyy-MM-dd}', 2, '{snapshot:yyyy-MM-dd}', 25);");

                // The heavy user is already using Copilot Chat without a seat - the strongest signal.
                SeedCopilotInteraction(db, userId: 2, daysAgo: 1, appHost: "Copilot Chat");
                SeedCopilotInteraction(db, userId: 2, daysAgo: 4, appHost: "Copilot Chat");
                SeedCopilotInteraction(db, userId: 1, daysAgo: 1, appHost: "Teams");

                var options = CopilotAdoptionOptions.Default;
                var sql = CopilotAdoptionSql.LicenceOpportunitiesSql(
                    new[] { 1 }, options, includeCopilotAudit: true, includeM365Usage: true);

                var rows = Query<UnlicensedUserSignalRow>(db, sql,
                    new SqlParameter("@from", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@m365From", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@m365ReportDate", snapshot),
                    new SqlParameter("@maxRows", 1000));

                var upns = rows.Select(r => r.UserPrincipalName).ToList();
                CollectionAssert.DoesNotContain(upns, "licensed@contoso.com",
                    "A user who already holds a Copilot seat is not a licence opportunity.");
                CollectionAssert.DoesNotContain(upns, "disabled@contoso.com",
                    "A disabled account cannot use a licence, and proposing one would discredit the list.");
                CollectionAssert.DoesNotContain(upns, "inactive@contoso.com",
                    "A user with no activity at all is not a candidate and must not be scanned into the result.");
                CollectionAssert.Contains(upns, "heavy@contoso.com");
                CollectionAssert.Contains(upns, "light@contoso.com");

                var heavy = rows.Single(r => r.UserPrincipalName == "heavy@contoso.com");
                Assert.AreEqual(2, heavy.UnlicensedCopilotInteractions);
                Assert.AreEqual(2, heavy.UnlicensedCopilotActiveDays);
                Assert.AreEqual(80, heavy.TeamsMessages, "40 + 20 chat plus 10 + 10 channel messages.");
                Assert.AreEqual(30, heavy.TeamsMeetings, "20 attended plus 10 organised.");
                Assert.AreEqual(60, heavy.EmailsSent);
                Assert.AreEqual(90, heavy.EmailsRead);
                Assert.AreEqual(55, heavy.FilesViewedOrEdited, "SharePoint and OneDrive are one document signal.");
                Assert.AreEqual("Finance", heavy.Department);

                // The database returned the candidates in its own ranked order. Re-scoring in C# must
                // reproduce that order, which is the guarantee that the SQL selects the right people.
                var scored = rows.Select(r => CopilotAdoptionScoring.ScoreOpportunity(r, options)).ToList();

                CollectionAssert.AreEqual(
                    rows.Select(r => r.UserPrincipalName).ToArray(),
                    scored.OrderByDescending(s => s.OpportunityScore)
                          .ThenBy(s => s.UserId)
                          .Select(s => s.UserPrincipalName)
                          .ToArray(),
                    "The SQL ranking and the C# score must agree - otherwise the report returns the wrong candidates.");

                var heavyScored = scored.Single(s => s.UserPrincipalName == "heavy@contoso.com");
                Assert.IsTrue(heavyScored.Recommended, "A heavy user across every workload is a clear recommendation.");
                Assert.IsFalse(
                    scored.Single(s => s.UserPrincipalName == "light@contoso.com").Recommended,
                    "A barely-active user must not be recommended for a paid seat.");
            }
        }

        [TestMethod]
        public void OpportunitiesQuery_FindsUsersWhoWereNotActiveOnTheLatestReportDate()
        {
            // The regression this covers: dbo.teams_user_activity_log and friends are Graph's DAILY
            // user-detail reports, which return only the users who did something on that one day. The
            // query used to seek a single [date], so the candidate list was "whoever happened to be
            // active on the most recent settled report date" - a snapshot landing on a Saturday emptied
            // the tab entirely, and anyone on leave that day was invisible however heavy a user they
            // normally are. Reported as "despite there being other users, there's nothing in this
            // screen" against a tenant whose newest Teams report date held exactly one row.
            using (var db = ScratchDatabase.Create("CopilotAdoptOppWindow"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);
                CreateM365UsageTables(db);

                var snapshot = DateTime.UtcNow.Date.AddDays(-3);
                var earlier = snapshot.AddDays(-10);
                var alsoEarlier = snapshot.AddDays(-11);

                db.Execute(
                    $@"INSERT INTO dbo.license_types (id, name, sku_id)
                           VALUES (1, N'Microsoft Copilot for Microsoft 365', N'Microsoft_365_Copilot');

                       INSERT INTO dbo.users (id, user_name, account_enabled)
                           VALUES (1, N'onlatestday@contoso.com', 1),
                                  (2, N'onleave@contoso.com', 1);

                       -- Only user 1 appears on the newest report date. User 2 was active earlier in
                       -- the window, across two days, and must still be ranked.
                       INSERT INTO dbo.teams_user_activity_log
                           (id, [date], user_id, last_activity_date, private_chat_count, team_chat_count,
                            post_messages, reply_messages, meetings_attended_count, meetings_organized_count)
                       VALUES (1, '{snapshot:yyyy-MM-dd}',     1, '{snapshot:yyyy-MM-dd}',     10, 0, 0, 0, 0, 0),
                              (2, '{earlier:yyyy-MM-dd}',      2, '{earlier:yyyy-MM-dd}',      30, 0, 0, 0, 4, 0),
                              (3, '{alsoEarlier:yyyy-MM-dd}',  2, '{alsoEarlier:yyyy-MM-dd}',  50, 0, 0, 0, 2, 0);");

                var rows = Query<UnlicensedUserSignalRow>(
                    db,
                    CopilotAdoptionSql.LicenceOpportunitiesSql(
                        new[] { 1 }, CopilotAdoptionOptions.Default, includeCopilotAudit: false, includeM365Usage: true),
                    new SqlParameter("@m365From", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@m365ReportDate", snapshot),
                    new SqlParameter("@maxRows", 1000));

                CollectionAssert.AreEquivalent(
                    new[] { "onlatestday@contoso.com", "onleave@contoso.com" },
                    rows.Select(r => r.UserPrincipalName).ToList(),
                    "Every unlicensed user active anywhere in the period is a candidate, not only those "
                    + "who appeared in the single most recent daily report.");

                // Averaged per ACTIVE day, not per calendar day: the targets describe what a heavy user
                // does on a working day, so weekends and leave must not dilute the figure.
                var onLeave = rows.Single(r => r.UserPrincipalName == "onleave@contoso.com");
                Assert.AreEqual(40, onLeave.TeamsMessages, "(30 + 50) messages over the 2 days they were active.");
                Assert.AreEqual(3, onLeave.TeamsMeetings, "(4 + 2) meetings over the 2 days they were active.");
                Assert.AreEqual(earlier, onLeave.LastM365ActivityUtc, "The most recent activity date in the period.");

                var onLatest = rows.Single(r => r.UserPrincipalName == "onlatestday@contoso.com");
                Assert.AreEqual(10, onLatest.TeamsMessages, "A single active day still reports that day's figure.");
            }
        }

        [TestMethod]
        public void OpportunitiesQuery_WorksWithoutTheMicrosoft365UsageReports()
        {
            // A deployment that imports only the Copilot audit log must still be able to find the
            // people already using Copilot Chat without a seat - the highest-value candidates.
            using (var db = ScratchDatabase.Create("CopilotAdoptOppNoUsage"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);

                db.Execute(
                    @"INSERT INTO dbo.license_types (id, name, sku_id)
                          VALUES (1, N'Microsoft Copilot for Microsoft 365', N'Microsoft_365_Copilot');
                      INSERT INTO dbo.users (id, user_name, account_enabled)
                          VALUES (1, N'chatuser@contoso.com', 1);");

                SeedCopilotInteraction(db, userId: 1, daysAgo: 2, appHost: "Copilot Chat");

                var rows = Query<UnlicensedUserSignalRow>(
                    db,
                    CopilotAdoptionSql.LicenceOpportunitiesSql(
                        new[] { 1 }, CopilotAdoptionOptions.Default, includeCopilotAudit: true, includeM365Usage: false),
                    new SqlParameter("@from", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@maxRows", 1000));

                Assert.AreEqual(1, rows.Count);
                Assert.AreEqual(1, rows[0].UnlicensedCopilotInteractions);
                Assert.AreEqual(0, rows[0].TeamsMessages, "The Microsoft 365 columns must still materialise, as zero.");
                Assert.IsNull(rows[0].LastM365ActivityUtc);
            }
        }

        #endregion

        #region Charts and lookups

        [TestMethod]
        public void CoworkReadinessQuery_JoinsWorkloadToSeatHoldersAndAgreesWithTheCsharpScore()
        {
            // The whole point of this query is joining the four Microsoft 365 daily workload tables to
            // Copilot SEAT HOLDERS - something no other query in the product does. If any of those joins
            // is wrong the Cowork tab silently reports a coordination load of zero for everybody, which
            // reads as "nobody is a candidate" rather than as a fault.
            using (var db = ScratchDatabase.Create("CopilotAdoptCowork"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);
                CreateM365UsageTables(db);

                var snapshot = DateTime.UtcNow.Date.AddDays(-3);

                db.Execute(
                    $@"INSERT INTO dbo.user_departments (id, name) VALUES (1, N'Operations');
                       INSERT INTO dbo.license_types (id, name, sku_id)
                           VALUES (1, N'Microsoft Copilot for Microsoft 365', N'Microsoft_365_Copilot');

                       INSERT INTO dbo.users (id, user_name, account_enabled, department_id)
                           VALUES (1, N'established@contoso.com', 1, 1),
                                  (2, N'prime@contoso.com',       1, 1),
                                  (3, N'quiet@contoso.com',       1, 1),
                                  (4, N'unlicensed@contoso.com',  1, 1),
                                  (5, N'guest_contoso.com#EXT#@fabrikam.onmicrosoft.com', 1, 1);

                       -- Everyone except the unlicensed user holds a seat, including the guest: a guest
                       -- holding a seat is a real (if odd) state, and the query must drop it from the
                       -- list because a guest cannot be put in a Cowork spending policy.
                       INSERT INTO dbo.user_license_type_lookups (id, user_id, license_type_id)
                           VALUES (1, 1, 1), (2, 2, 1), (3, 3, 1), (4, 5, 1);

                       INSERT INTO dbo.teams_user_activity_log
                           (id, [date], user_id, last_activity_date, private_chat_count, team_chat_count,
                            post_messages, reply_messages, meetings_attended_count, meetings_organized_count)
                       VALUES (1, '{snapshot:yyyy-MM-dd}', 2, '{snapshot:yyyy-MM-dd}', 40, 20, 10, 10, 20, 10),
                              (2, '{snapshot:yyyy-MM-dd}', 3, '{snapshot:yyyy-MM-dd}',  2,  0,  0,  0,  1,  0),
                              (3, '{snapshot:yyyy-MM-dd}', 4, '{snapshot:yyyy-MM-dd}', 99, 99, 99, 99, 99, 99);

                       INSERT INTO dbo.outlook_user_activity_log
                           (id, [date], user_id, last_activity_date, email_send_count, email_receive_count, email_read_count)
                       VALUES (1, '{snapshot:yyyy-MM-dd}', 2, '{snapshot:yyyy-MM-dd}', 60, 100, 90);

                       INSERT INTO dbo.sharepoint_user_activity_log (id, [date], user_id, last_activity_date, viewed_or_edited)
                       VALUES (1, '{snapshot:yyyy-MM-dd}', 2, '{snapshot:yyyy-MM-dd}', 30);

                       INSERT INTO dbo.onedrive_user_activity_log (id, [date], user_id, last_activity_date, viewed_or_edited)
                       VALUES (1, '{snapshot:yyyy-MM-dd}', 2, '{snapshot:yyyy-MM-dd}', 25);

                       INSERT INTO dbo.copilot_agents (id, name, agent_id)
                           VALUES (1, N'Copilot Cowork', N'Copilot.M365Copilot.CoworkChat');");

                // Cowork use on three separate days - the habit, not one long afternoon.
                SeedCopilotInteraction(db, userId: 1, daysAgo: 2, appHost: "cowork");
                SeedCopilotInteraction(db, userId: 1, daysAgo: 4, appHost: "cowork");
                SeedCopilotInteraction(db, userId: 1, daysAgo: 6, appHost: "cowork", agentId: 1);
                // Ordinary Copilot use must NOT count as Cowork.
                SeedCopilotInteraction(db, userId: 2, daysAgo: 2, appHost: "Teams");

                var options = CopilotAdoptionOptions.Default;
                var sql = CopilotAdoptionSql.CoworkReadinessSql(
                    new[] { 1 }, new[] { 1 }, options, includeCopilotAudit: true, includeM365Usage: true);

                var rows = Query<CoworkReadinessSignalRow>(db, sql,
                    new SqlParameter("@from", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@m365From", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@m365ReportDate", snapshot),
                    new SqlParameter("@maxRows", 1000));

                var upns = rows.Select(r => r.UserPrincipalName).ToList();
                CollectionAssert.Contains(upns, "established@contoso.com");
                CollectionAssert.Contains(upns, "prime@contoso.com");
                CollectionAssert.Contains(upns, "quiet@contoso.com",
                    "A seat holder with a light workload still belongs on the list - they are a seat "
                    + "being paid for, and the tab has to be able to report that Cowork is not indicated.");
                Assert.IsFalse(upns.Any(u => u.StartsWith("unlicensed")),
                    "Cowork needs a Copilot licence as a prerequisite, so an unlicensed user cannot be a candidate.");
                Assert.IsFalse(upns.Any(u => u.Contains("#EXT#")),
                    "A guest cannot be put in a Cowork spending policy, so proposing one would discredit the list.");

                var prime = rows.Single(r => r.UserPrincipalName == "prime@contoso.com");
                Assert.AreEqual(80, prime.TeamsMessages, "40 + 20 chat plus 10 + 10 channel messages.");
                Assert.AreEqual(30, prime.TeamsMeetings, "20 attended plus 10 organised.");
                Assert.AreEqual(60, prime.EmailsSent);
                Assert.AreEqual(90, prime.EmailsRead);
                Assert.AreEqual(55, prime.FilesViewedOrEdited, "SharePoint and OneDrive are one document signal.");
                Assert.AreEqual("Operations", prime.Department);
                Assert.AreEqual(0, prime.CoworkInteractions, "Ordinary Copilot use is not Cowork use.");

                var established = rows.Single(r => r.UserPrincipalName == "established@contoso.com");
                Assert.AreEqual(3, established.CoworkInteractions);
                Assert.AreEqual(3, established.CoworkActiveDays,
                    "Active DAYS is what the 'regular use' verdict rests on, not the interaction count.");
                Assert.IsTrue(established.LastCoworkInteractionUtc.HasValue);

                // The database ranks by coordination load so a large tenant is not pulled into memory;
                // the published score is always recomputed in C#. Assert the two agree on who is heaviest.
                var scored = rows
                    .Select(r => CopilotAdoptionScoring.ScoreCoworkReadiness(r, options))
                    .ToList();

                var heaviest = scored.OrderByDescending(s => s.CoordinationLoadScore).First();
                Assert.AreEqual("prime@contoso.com", heaviest.UserPrincipalName,
                    "The SQL ranking and the C# score must agree about who carries the most delegable work.");

                Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.Established,
                    scored.Single(s => s.UserPrincipalName == "established@contoso.com").Tier);
                Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.NotIndicated,
                    scored.Single(s => s.UserPrincipalName == "quiet@contoso.com").Tier);
            }
        }

        [TestMethod]
        public void CoworkReadinessQuery_DegradesWhenTheUsageReportsAreMissing()
        {
            // A brand-new installation has the Copilot audit import running long before the Microsoft 365
            // usage reports have landed. The query must still parse and run - with the workload CTEs
            // omitted entirely rather than joined against an absent snapshot.
            using (var db = ScratchDatabase.Create("CopilotAdoptCoworkNoM365"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);

                db.Execute(
                    @"INSERT INTO dbo.license_types (id, name, sku_id)
                          VALUES (1, N'Microsoft Copilot for Microsoft 365', N'Microsoft_365_Copilot');
                      INSERT INTO dbo.users (id, user_name, account_enabled) VALUES (1, N'a@contoso.com', 1);
                      INSERT INTO dbo.user_license_type_lookups (id, user_id, license_type_id) VALUES (1, 1, 1);");

                SeedCopilotInteraction(db, userId: 1, daysAgo: 2, appHost: "cowork");

                var sql = CopilotAdoptionSql.CoworkReadinessSql(
                    new[] { 1 }, new int[0], CopilotAdoptionOptions.Default,
                    includeCopilotAudit: true, includeM365Usage: false);

                var rows = Query<CoworkReadinessSignalRow>(db, sql,
                    new SqlParameter("@from", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@maxRows", 1000));

                Assert.AreEqual(1, rows.Count);
                Assert.AreEqual(1, rows[0].CoworkInteractions,
                    "Cowork use is still observable from the audit log without the usage reports.");
                Assert.AreEqual(0, rows[0].TeamsMeetings, "Coordination load collapses to zero, not to an error.");
            }
        }

        [TestMethod]
        public void CoworkReadinessQuery_RunsWithoutTheCopilotAuditImport()
        {
            // The mirror case: the usage reports are present but the audit import has nothing. Existing
            // Cowork use is then unobservable, and its columns must collapse to zero rather than joining
            // a CTE that was never declared.
            using (var db = ScratchDatabase.Create("CopilotAdoptCoworkNoAudit"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);
                CreateM365UsageTables(db);

                var snapshot = DateTime.UtcNow.Date.AddDays(-3);

                db.Execute(
                    $@"INSERT INTO dbo.license_types (id, name, sku_id)
                           VALUES (1, N'Microsoft Copilot for Microsoft 365', N'Microsoft_365_Copilot');
                       INSERT INTO dbo.users (id, user_name, account_enabled) VALUES (1, N'a@contoso.com', 1);
                       INSERT INTO dbo.user_license_type_lookups (id, user_id, license_type_id) VALUES (1, 1, 1);
                       INSERT INTO dbo.teams_user_activity_log
                           (id, [date], user_id, last_activity_date, private_chat_count, team_chat_count,
                            post_messages, reply_messages, meetings_attended_count, meetings_organized_count)
                       VALUES (1, '{snapshot:yyyy-MM-dd}', 1, '{snapshot:yyyy-MM-dd}', 10, 10, 5, 5, 4, 2);");

                var sql = CopilotAdoptionSql.CoworkReadinessSql(
                    new[] { 1 }, new int[0], CopilotAdoptionOptions.Default,
                    includeCopilotAudit: false, includeM365Usage: true);

                var rows = Query<CoworkReadinessSignalRow>(db, sql,
                    new SqlParameter("@m365From", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@m365ReportDate", snapshot),
                    new SqlParameter("@maxRows", 1000));

                Assert.AreEqual(1, rows.Count);
                Assert.AreEqual(0, rows[0].CoworkInteractions);
                Assert.AreEqual(0, rows[0].CoworkActiveDays);
                Assert.IsNull(rows[0].LastCoworkInteractionUtc);
                Assert.AreEqual(6, rows[0].TeamsMeetings, "4 attended plus 2 organised.");
            }
        }

        [TestMethod]
        public void CoworkReadinessQuery_PreservesNonLatinMetadata()
        {
            // Department is free text from a customer tenant and routinely non-Latin. The scratch tables
            // declare the SAME column types as production (see CreateUserTables) so this assertion
            // actually crosses the encoding boundary rather than being self-fulfilling.
            using (var db = ScratchDatabase.Create("CopilotAdoptCoworkUni"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);

                db.Execute(
                    @"INSERT INTO dbo.user_departments (id, name) VALUES (1, N'Πωλήσεις');
                      INSERT INTO dbo.user_job_titles (id, name) VALUES (1, N'Διευθυντής');
                      INSERT INTO dbo.license_types (id, name, sku_id)
                          VALUES (1, N'Microsoft Copilot for Microsoft 365', N'Microsoft_365_Copilot');
                      INSERT INTO dbo.users (id, user_name, account_enabled, department_id, job_title_id)
                          VALUES (1, N'a@contoso.com', 1, 1, 1);
                      INSERT INTO dbo.user_license_type_lookups (id, user_id, license_type_id) VALUES (1, 1, 1);");

                SeedCopilotInteraction(db, userId: 1, daysAgo: 2, appHost: "cowork");

                var sql = CopilotAdoptionSql.CoworkReadinessSql(
                    new[] { 1 }, new int[0], CopilotAdoptionOptions.Default,
                    includeCopilotAudit: true, includeM365Usage: false);

                var rows = Query<CoworkReadinessSignalRow>(db, sql,
                    new SqlParameter("@from", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@maxRows", 1000));

                Assert.AreEqual("Πωλήσεις", rows[0].Department);
                Assert.AreEqual("Διευθυντής", rows[0].JobTitle);
            }
        }

        [TestMethod]
        public void CoworkCreditQueries_AreSkippedWhenTheAgentCostTablesDoNotExist()
        {
            // The credit figures come from a SEPARATE, optional import whose tables a database predating
            // the agent-cost migration does not have. Without the probe, every Cowork analysis on such a
            // database raised "Invalid object name" as a warning - which reads as a fault rather than as
            // an import that was never switched on, and a page whose warnings cry wolf stops being read.
            using (var db = ScratchDatabase.Create("CopilotAdoptCoworkNoCredits"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);

                var probe = Query<CopilotAdoptionService.IntValueRow>(db, CopilotAdoptionSql.HasCreditTablesSql);

                Assert.AreEqual(1, probe.Count);
                Assert.AreEqual(0, probe[0].Value,
                    "The probe must report the credit tables as absent so the queries are skipped entirely.");
            }
        }

        [TestMethod]
        public void CoworkCreditQueries_RunAgainstTheRealAgentCostSchema()
        {
            // The mirror case: the tables exist, so both credit queries must bind against their real
            // column names. These are hand-written SQL, so a typo is a broken panel in a customer browser.
            using (var db = ScratchDatabase.Create("CopilotAdoptCoworkCredits"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);
                CreateAgentCostTables(db);

                db.Execute(
                    @"INSERT INTO dbo.license_types (id, name, sku_id)
                          VALUES (1, N'Microsoft Copilot for Microsoft 365', N'Microsoft_365_Copilot');
                      INSERT INTO dbo.users (id, user_name, account_enabled)
                          VALUES (1, N'spender@contoso.com', 1), (2, N'nospend@contoso.com', 1),
                                 (3, N'nettozero@contoso.com', 1);
                      INSERT INTO dbo.user_license_type_lookups (id, user_id, license_type_id)
                          VALUES (1, 1, 1), (2, 2, 1), (3, 3, 1);

                      INSERT INTO dbo.copilot_studio_credit_user_daily
                          (id, usage_date, entra_object_id, user_id, billed_credits, dimension_hash, imported_utc)
                      VALUES (1, DATEADD(day, -2, GETUTCDATE()), N'obj-1', 1, 12.5, N'h1', GETUTCDATE()),
                             (2, DATEADD(day, -3, GETUTCDATE()), N'obj-1', 1,  7.5, N'h2', GETUTCDATE()),
                             -- An unresolved row: real, but not attributable to a person.
                             (3, DATEADD(day, -2, GETUTCDATE()), N'obj-9', NULL, 99.0, N'h3', GETUTCDATE()),
                             -- Imported rows that net to exactly zero. This user IS attributable and their
                             -- answer is 0, which is a different statement from having no data at all.
                             (4, DATEADD(day, -2, GETUTCDATE()), N'obj-3', 3,  5.0, N'h4', GETUTCDATE()),
                             (5, DATEADD(day, -3, GETUTCDATE()), N'obj-3', 3, -5.0, N'h5', GETUTCDATE());

                      INSERT INTO dbo.copilot_studio_credit_capacity
                          (id, snapshot_utc, entitled, consumed, available, status)
                      VALUES (1, DATEADD(day, -5, GETUTCDATE()), 1000, 100, 900, N'WithinCapacity'),
                             (2, DATEADD(day, -1, GETUTCDATE()), 2000, 400, 1600, N'WithinCapacity');");

                var probe = Query<CopilotAdoptionService.IntValueRow>(db, CopilotAdoptionSql.HasCreditTablesSql);
                Assert.AreEqual(1, probe[0].Value);

                var credits = Query<CopilotAdoptionService.UserCreditRow>(
                    db, CopilotAdoptionSql.CoworkUserCreditsSql(new[] { 1 }),
                    new SqlParameter("@from", DateTime.UtcNow.Date.AddDays(-28)));

                Assert.AreEqual(2, credits.Count,
                    "The spender and the net-zero user are both attributable. The seat holder with NO rows "
                    + "must be ABSENT so the UI can render 'not attributable' rather than a zero.");

                var spender = credits.Single(c => c.UserId == 1);
                Assert.AreEqual(20m, spender.BilledCredits, "12.5 + 7.5 across the two days.");

                // Regression guard. An earlier revision ended this query with HAVING SUM(...) > 0, which
                // dropped this user and made the UI say "not attributable" about someone the importer had
                // measured and found to be zero. "We do not know" and "it is nothing" are different claims,
                // and the whole credit section of this tab is built on keeping them apart.
                var netZero = credits.SingleOrDefault(c => c.UserId == 3);
                Assert.IsNotNull(netZero,
                    "A seat holder whose imported rows total zero must be RETURNED as zero, not filtered "
                    + "out into the same bucket as a user the importer never saw.");
                Assert.AreEqual(0m, netZero.BilledCredits);

                Assert.IsFalse(credits.Any(c => c.UserId == 2),
                    "The seat holder with no imported rows at all stays absent.");

                var capacity = Query<CopilotAdoptionService.CreditCapacityRow>(
                    db, CopilotAdoptionSql.CoworkCreditCapacitySql);

                Assert.AreEqual(1, capacity.Count, "Only the latest snapshot - it is a point-in-time total.");
                Assert.AreEqual(2000m, capacity[0].Entitled);
                Assert.AreEqual(1600m, capacity[0].AvailableCredits);
                Assert.AreEqual("WithinCapacity", capacity[0].Status);
            }
        }

        [TestMethod]
        public void LicenceTypesQuery_ReturnsPurchasedCapacityAndKeepsMissingInventoryUnknown()
        {
            using (var db = ScratchDatabase.Create("CopilotLicenceTypesCapacity"))
            {
                CreateUserTables(db);
                db.Execute(
                    @"INSERT INTO dbo.license_types
                          (id, name, sku_id, prepaid_enabled_units, prepaid_warning_units, prepaid_suspended_units, subscribed_sku_refreshed_utc)
                      VALUES (1, N'Microsoft Copilot for Microsoft 365', N'Microsoft_365_Copilot', 10, 2, 1, '2026-09-01T00:00:00'),
                             (2, N'Microsoft Copilot for Microsoft 365 EDU', N'Microsoft_365_Copilot_EDU', NULL, NULL, NULL, NULL);
                      INSERT INTO dbo.users (id, user_name, account_enabled) VALUES (1, 'a@contoso.com', 1), (2, 'b@contoso.com', 1);
                      INSERT INTO dbo.user_license_type_lookups (id, user_id, license_type_id)
                          VALUES (1, 1, 1), (2, 2, 1);");

                var rows = Query<LicenceTypeRow>(db, CopilotAdoptionSql.LicenceTypesSql);

                var known = rows.Single(r => r.Id == 1);
                Assert.AreEqual(2, known.AssignedUsers);
                Assert.AreEqual(13, known.PurchasedUnits, "Enabled, warning and suspended prepaid units all contribute to purchased seats.");
                Assert.AreEqual(new DateTime(2026, 9, 1, 0, 0, 0), known.PurchasedUnitsRefreshedUtc);

                var unknown = rows.Single(r => r.Id == 2);
                Assert.AreEqual(0, unknown.AssignedUsers);
                Assert.IsNull(unknown.PurchasedUnits, "No subscribedSkus snapshot is unknown, not zero.");
                Assert.IsNull(unknown.PurchasedUnitsRefreshedUtc);
            }
        }

        [TestMethod]
        public void SupportingQueries_AllRunAgainstTheRealSchema()
        {
            // These are small, but they are hand-written SQL against real column names, so a typo in
            // any of them is a broken chart that only shows up in a customer's browser.
            using (var db = ScratchDatabase.Create("CopilotAdoptCharts"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);
                CreateCopilotReportTable(db);
                CreateM365UsageTables(db);
                CreateCopilotImportLogTable(db);

                db.Execute(
                    @"INSERT INTO dbo.license_types (id, name, sku_id)
                          VALUES (1, N'Microsoft Copilot for Microsoft 365', N'Microsoft_365_Copilot');
                      INSERT INTO dbo.users (id, user_name, account_enabled) VALUES (1, N'a@contoso.com', 1);
                      INSERT INTO dbo.user_license_type_lookups (id, user_id, license_type_id) VALUES (1, 1, 1);
                      INSERT INTO dbo.copilot_agents (id, name, agent_id)
                          VALUES (1, N'Copilot Cowork', N'Copilot.M365Copilot.CoworkChat'),
                                 (2, N'Sales helper', N'SPO_1234'),
                                 -- A tenant's OWN agent that happens to have 'Cowork' in its name. Agent
                                 -- display names are customer free text, so matching on them would let a
                                 -- customer promote their own users into the observed-use evidence tiers.
                                 (3, N'Contoso Cowork Helper', N'SPO_9999');");

                SeedCopilotInteraction(db, userId: 1, daysAgo: 7, appHost: "Teams");
                SeedCopilotInteraction(db, userId: 1, daysAgo: 7, appHost: "cowork", agentId: 1);

                var from = DateTime.UtcNow.Date.AddDays(-28);
                var settled = DateTime.UtcNow.Date.AddDays(-3);

                var licenceTypes = Query<LicenceTypeRow>(db, CopilotAdoptionSql.LicenceTypesSql);
                Assert.AreEqual(1, licenceTypes.Count);
                Assert.AreEqual(1, licenceTypes[0].AssignedUsers);
                Assert.IsTrue(CopilotLicenceClassifier.IsCopilotSeat(licenceTypes[0].SkuPartNumber, licenceTypes[0].Name));

                var coworkAgents = Query<CopilotAdoptionService.IntValueRow>(db, CopilotAdoptionSql.CoworkAgentIdsSql);
                CollectionAssert.AreEqual(new[] { 1 }, coworkAgents.Select(a => a.Value).ToArray(),
                    "Cowork audit reconciliation must use the documented first-party prefix only. The ids flow "
                    + "into the observed-use path, so a customer's own agent whose name contains 'Cowork' must "
                    + "not promote its users into evidence tiers or remove them from the candidate list.");

                var seats = Query<CopilotAdoptionService.SeatAssignmentRow>(db, CopilotAdoptionSql.SeatAssignmentsSql(new[] { 1 }));
                Assert.AreEqual(1, seats.Count);
                Assert.AreEqual(1, seats[0].LicenceTypeId);
                Assert.AreEqual("Microsoft_365_Copilot", seats[0].SkuPartNumber);
                Assert.AreEqual("Microsoft Copilot for Microsoft 365", seats[0].LicenceName);

                var byApp = Query<CopilotAdoptionService.CategoryQueryRow>(db,
                    CopilotAdoptionSql.UsageByAppSql(new[] { 1 }),
                    new SqlParameter("@from", from),
                    new SqlParameter("@top", 10));
                Assert.AreEqual(2, byApp.Count, "Teams and Cowork.");

                var trend = Query<CopilotAdoptionService.NamedWeekRow>(db,
                    CopilotAdoptionSql.WeeklyAdoptionTrendSql(new[] { 1 }, new[] { 1 }),
                    new SqlParameter("@trendFrom", DateTime.UtcNow.Date.AddMonths(-6)),
                    new SqlParameter("@trendTo", CopilotAdoptionService.MondayOf(DateTime.UtcNow.Date)));
                Assert.IsTrue(trend.Any(t => t.SeriesName == "Active licensed users"));
                Assert.IsTrue(trend.Any(t => t.SeriesName == "Cowork users"),
                    "The Cowork series must be produced when Cowork interactions exist.");

                // Values, not just presence. Both series are computed in one pass with a conditional
                // COUNT(DISTINCT ...) (#295), so a regression there would still produce two correctly-named
                // series carrying the wrong numbers. This fixture has one user with two interactions in the
                // same week - one Teams, one Cowork - so both series are exactly 1 for that week: the user is
                // counted once despite two interactions, and the Cowork condition selects the same user.
                var activeWeeks = trend.Where(t => t.SeriesName == "Active licensed users").ToList();
                var coworkWeeks = trend.Where(t => t.SeriesName == "Cowork users").ToList();

                Assert.AreEqual(1, activeWeeks.Count, "Both interactions fall in the same week.");
                Assert.AreEqual(1d, activeWeeks[0].Value,
                    "One distinct user, counted once - not once per interaction.");

                Assert.AreEqual(1, coworkWeeks.Count);
                Assert.AreEqual(1d, coworkWeeks[0].Value,
                    "The Cowork conditional count must select the same user.");
                Assert.AreEqual(activeWeeks[0].WeekStart, coworkWeeks[0].WeekStart,
                    "Both series are bucketed from the same rows, so the week must match.");

                var trendCoverage = Query<CopilotAdoptionService.WeekCoverageRow>(db,
                    CopilotAdoptionSql.WeeklyCopilotAuditCoverageSql,
                    new SqlParameter("@trendFrom", DateTime.UtcNow.Date.AddMonths(-6)),
                    new SqlParameter("@trendTo", CopilotAdoptionService.MondayOf(DateTime.UtcNow.Date)));
                Assert.IsTrue(trendCoverage.Any(c => c.WeekStart == activeWeeks[0].WeekStart),
                    "A week with imported Audit.General events must be marked as covered even before #542 period facts exist.");

                var unlicensed = Query<int?>(db,
                    CopilotAdoptionSql.UnlicensedActiveUsersSql(new[] { 1 }),
                    new SqlParameter("@from", from));
                Assert.AreEqual(0, unlicensed.Single(), "The only Copilot user in this fixture holds a seat.");

                var hasAudit = Query<int?>(db, CopilotAdoptionSql.HasCopilotAuditDataSql, new SqlParameter("@from", from), new SqlParameter("@toExclusive", DateTime.UtcNow));
                Assert.AreEqual(1, hasAudit.Single());

                // These two return NULL against an empty table, which must materialise rather than throw.
                Assert.IsNull(Query<DateTime?>(db, CopilotAdoptionSql.LatestCopilotReportDateSql,
                    new SqlParameter("@settled", settled)).Single());
                Assert.IsNull(Query<DateTime?>(db, CopilotAdoptionSql.LatestM365ReportDateSql,
                    new SqlParameter("@settled", settled)).Single());
                Assert.AreEqual(0, Query<int?>(db, CopilotAdoptionSql.CopilotReportObfuscatedSql).Count,
                    "With no import log rows, the anonymisation probe returns nothing rather than failing.");
            }
        }

        [TestMethod]
        public void QueriesSurviveATenantWithNoCopilotLicencesAtAll()
        {
            // "Should we buy Copilot?" is a legitimate use of this tool, so no seats must produce an
            // empty report rather than an IN () syntax error.
            using (var db = ScratchDatabase.Create("CopilotAdoptNoSeats"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);
                CreateM365UsageTables(db);

                db.Execute(@"INSERT INTO dbo.users (id, user_name, account_enabled) VALUES (1, N'a@contoso.com', 1);");
                SeedCopilotInteraction(db, userId: 1, daysAgo: 1, appHost: "Copilot Chat");

                var licensed = Query<LicensedUserUsageRow>(
                    db,
                    CopilotAdoptionSql.LicensedUsersSql(new int[0], new int[0], includeCopilotReport: false),
                    new SqlParameter("@from", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@historyFrom", DateTime.UtcNow.Date.AddDays(-365)),
                    new SqlParameter("@maxRows", 1000));
                Assert.AreEqual(0, licensed.Count);

                var opportunities = Query<UnlicensedUserSignalRow>(
                    db,
                    CopilotAdoptionSql.LicenceOpportunitiesSql(
                        new int[0], CopilotAdoptionOptions.Default, includeCopilotAudit: true, includeM365Usage: false),
                    new SqlParameter("@from", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@maxRows", 1000));

                Assert.AreEqual(1, opportunities.Count,
                    "With no seats at all, everyone using Copilot Chat is a licence opportunity.");
            }
        }

        [TestMethod]
        public void AgentAndUnlicensedQueries_ResolveDepartmentsThroughTheLookupTable()
        {
            // These two queries shipped referencing a non-existent `u.department` column. `dbo.users`
            // has `department_id`, an FK to `dbo.user_departments`. Both failed at bind time, and
            // because every query is individually guarded the failure degraded to a warning - so an
            // entire population (unlicensed users) and a whole chart silently went missing rather than
            // the page breaking. Neither builder had any test at all, which is why it shipped.
            using (var db = ScratchDatabase.Create("CopilotAdoptDepartments"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);

                db.Execute(
                    @"INSERT INTO dbo.user_departments (id, name) VALUES (1, N'Finance'), (2, N'Καλημέρα κόσμε');

                      INSERT INTO dbo.license_types (id, name, sku_id)
                          VALUES (1, N'Microsoft Copilot for Microsoft 365', N'Microsoft_365_Copilot');

                      INSERT INTO dbo.users (id, user_name, account_enabled, department_id) VALUES
                          (1, N'seat@contoso.com', 1, 1),
                          (2, N'greek@contoso.com', 1, 2),
                          (3, N'nodept@contoso.com', 1, NULL);

                      INSERT INTO dbo.user_license_type_lookups (id, user_id, license_type_id) VALUES (1, 1, 1);

                      INSERT INTO dbo.copilot_agents (id, name, agent_id, is_custom_agent)
                          VALUES (1, N'Contoso Expenses Agent', N'Contoso.Expenses', 1);");

                // Licensed user in Finance: two agent interactions on different days, plus one without.
                SeedCopilotInteraction(db, userId: 1, daysAgo: 2, appHost: "Teams", agentId: 1);
                SeedCopilotInteraction(db, userId: 1, daysAgo: 3, appHost: "Word", agentId: 1);
                SeedCopilotInteraction(db, userId: 1, daysAgo: 3, appHost: "Teams");

                // Unlicensed, in the Greek-named department: one agent interaction.
                SeedCopilotInteraction(db, userId: 2, daysAgo: 1, appHost: "Copilot Chat", agentId: 1);

                // Unlicensed, with no department at all - the fallback path.
                SeedCopilotInteraction(db, userId: 3, daysAgo: 1, appHost: "Copilot Chat");

                var from = DateTime.UtcNow.Date.AddDays(-28);

                var byDepartment = Query<CopilotAdoptionService.CategoryQueryRow>(
                    db,
                    CopilotAdoptionSql.AgentUsageByDepartmentSql(),
                    new SqlParameter("@from", from),
                    new SqlParameter("@top", 10));

                Assert.AreEqual(2, byDepartment.Count,
                    "Only agent-attributed interactions count, and only two departments produced any.");
                Assert.AreEqual(2d, byDepartment.Single(r => r.Label == "Finance").Value,
                    "Finance ran two agent interactions; its third interaction carried no agent.");
                Assert.AreEqual(1d, byDepartment.Single(r => r.Label == "Καλημέρα κόσμε").Value,
                    "A non-Latin department name must survive the join and the grouping.");

                var unlicensed = Query<UnlicensedUsageQueryRow>(
                    db,
                    CopilotAdoptionSql.UnlicensedUsageRowsSql(new[] { 1 }),
                    new SqlParameter("@from", from),
                    new SqlParameter("@maxRows", 1000));

                Assert.AreEqual(2, unlicensed.Count, "The seat holder must be excluded.");

                var greek = unlicensed.Single(r => r.UserId == 2);
                Assert.AreEqual("Καλημέρα κόσμε", greek.Department,
                    "The Department column must map to the property, resolved through user_departments.");
                Assert.AreEqual(1, greek.Interactions);
                Assert.AreEqual(1, greek.ActiveDays);
                Assert.AreEqual(1, greek.AgentsUsed);

                var noDepartment = unlicensed.Single(r => r.UserId == 3);
                Assert.AreEqual(string.Empty, noDepartment.Department,
                    "A user with no department must fall back to blank rather than NULL or an error.");
                Assert.AreEqual(0, noDepartment.AgentsUsed);

                var agents = Query<AgentUsageQueryRow>(
                    db,
                    CopilotAdoptionSql.AgentUsageSql(new[] { 1 }),
                    new SqlParameter("@from", from),
                    new SqlParameter("@historyFrom", DateTime.UtcNow.Date.AddDays(-120)),
                    new SqlParameter("@maxRows", 500));

                Assert.AreEqual(1, agents.Count);
                Assert.AreEqual("Contoso Expenses Agent", agents[0].Name);
                Assert.AreEqual(3, agents[0].Interactions, "Three interactions carried this agent.");
                Assert.AreEqual(3, agents[0].WindowInteractions,
                    "All three fall inside the reporting window here, so the two scopes agree.");
                Assert.AreEqual(2, agents[0].Users, "Two distinct people used it.");
                Assert.AreEqual(1, agents[0].LicensedUsers, "Only one of them holds a seat.");
                Assert.IsTrue(agents[0].IsCustomAgent);

                // The scopes must genuinely differ when the history reaches further back than the
                // window - that separation is what stops the per-user KPI being inflated.
                var narrow = Query<AgentUsageQueryRow>(
                    db,
                    CopilotAdoptionSql.AgentUsageSql(new[] { 1 }),
                    new SqlParameter("@from", DateTime.UtcNow.Date.AddDays(-2)),
                    new SqlParameter("@historyFrom", DateTime.UtcNow.Date.AddDays(-120)),
                    new SqlParameter("@maxRows", 500));

                Assert.AreEqual(3, narrow[0].Interactions, "History is unchanged by a narrower window.");
                Assert.AreEqual(2, narrow[0].WindowInteractions,
                    "Only the interactions inside the two-day window count towards the period figure.");

                var unlicensedApps = Query<CopilotAdoptionService.CategoryQueryRow>(
                    db,
                    CopilotAdoptionSql.UnlicensedUsageByAppSql(new[] { 1 }),
                    new SqlParameter("@from", from),
                    new SqlParameter("@top", 10));

                Assert.AreEqual(1, unlicensedApps.Count, "Both unlicensed users were in Copilot Chat.");
                Assert.AreEqual("Copilot Chat", unlicensedApps[0].Label);
                Assert.AreEqual(2d, unlicensedApps[0].Value);
            }
        }

        [TestMethod]
        public void TopResourceTypes_RunsAgainstTheRealAccessedResourceSchema()
        {
            // The other builder that had no test. It joins the largest Copilot table through two
            // further hops, so a wrong column name is equally invisible until a customer sees it.
            using (var db = ScratchDatabase.Create("CopilotAdoptResources"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);
                CreateCopilotResourceTables(db);

                db.Execute(
                    @"INSERT INTO dbo.users (id, user_name, account_enabled) VALUES (1, N'a@contoso.com', 1);
                      INSERT INTO dbo.copilot_event_accessed_resource_types (id, name)
                          VALUES (1, N'docx'), (2, N'TeamsMeeting');");

                var first = SeedCopilotInteractionReturningId(db, userId: 1, daysAgo: 2, appHost: "Word");
                var second = SeedCopilotInteractionReturningId(db, userId: 1, daysAgo: 3, appHost: "Teams");

                db.Execute(
                    $@"INSERT INTO dbo.copilot_event_accessed_resources (copilot_chat_id, resource_type_id) VALUES
                           ('{first}', 1), ('{first}', 1), ('{second}', 2), ('{second}', NULL);");

                var rows = Query<CopilotAdoptionService.CategoryQueryRow>(
                    db,
                    CopilotAdoptionSql.TopResourceTypesSql(),
                    new SqlParameter("@from", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@top", 10));

                Assert.AreEqual(3, rows.Count, "docx, TeamsMeeting and the unknown fallback.");
                Assert.AreEqual(2d, rows.Single(r => r.Label == "docx").Value,
                    "One interaction referenced two documents - this counts references, not interactions.");
                Assert.AreEqual(1d, rows.Single(r => r.Label == "TeamsMeeting").Value);
                Assert.AreEqual(1d, rows.Single(r => r.Label == "(unknown)").Value,
                    "A resource with no type must fall back rather than being dropped.");
            }
        }

        #endregion

        #region Fixture

        private static List<T> Query<T>(ScratchDatabase db, string sql, params SqlParameter[] parameters)
        {
            using (var context = new RawSqlContext(db.ConnectionString))
            {
                context.Database.CommandTimeout = 120;
                return context.Database.SqlQuery<T>(sql, parameters).ToList();
            }
        }

        /// <summary>Users plus the metadata lookup tables the detail queries join to.</summary>

        private static void CreateUserTables(ScratchDatabase db)
        {
            db.Execute(
                @"CREATE TABLE dbo.user_departments (id int NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
                  CREATE TABLE dbo.user_job_titles (id int NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
                  CREATE TABLE dbo.user_country_or_region (id int NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
                  CREATE TABLE dbo.user_office_locations (id int NOT NULL PRIMARY KEY, name nvarchar(100) NULL);
                  CREATE TABLE dbo.user_company_name (id int NOT NULL PRIMARY KEY, name nvarchar(100) NULL);

                  CREATE TABLE dbo.users (
                      id int NOT NULL PRIMARY KEY,
                      -- These types must mirror production, or an assertion made against them proves
                      -- nothing. user_name is varchar(250) NOT NULL (Create DB.sql) because Entra UPNs
                      -- are ASCII (#402/#414); mail is nvarchar(max) (migration ExtendedUsageReports,
                      -- c.String() with no MaxLength). The lookup-table names above are nvarchar(100)
                      -- from AbstractEFEntityWithName.Name's [MaxLength(100)]. A fixture that widened
                      -- user_name to nvarchar would make a Unicode assertion on it self-fulfilling and
                      -- unable to catch the varchar regression it claims to guard.
                      user_name varchar(250) NOT NULL,
                      mail nvarchar(max) NULL,
                      account_enabled bit NULL,
                      created_utc datetime2(7) NULL,
                      department_id int NULL,
                      job_title_id int NULL,
                      country_or_region_id int NULL,
                      office_location_id int NULL,
                      company_name_id int NULL,
                      manager_id int NULL);

                  CREATE TABLE dbo.license_types (
                      id int NOT NULL PRIMARY KEY,
                      name nvarchar(100) NULL,
                      sku_id nvarchar(max) NULL,
                      prepaid_enabled_units int NULL,
                      prepaid_warning_units int NULL,
                      prepaid_suspended_units int NULL,
                      subscribed_sku_refreshed_utc datetime NULL);

                  CREATE TABLE dbo.user_license_type_lookups (
                      id int NOT NULL PRIMARY KEY,
                      user_id int NOT NULL,
                      license_type_id int NOT NULL);

                  CREATE UNIQUE NONCLUSTERED INDEX IX_license_type_id_user_id
                      ON dbo.user_license_type_lookups (license_type_id, user_id);

                  CREATE TABLE dbo.copilot_adoption_reclaim_exclusions (
                      id int NOT NULL PRIMARY KEY,
                      user_id int NOT NULL,
                      reason nvarchar(100) NOT NULL,
                      note nvarchar(1000) NULL,
                      excluded_by nvarchar(256) NOT NULL,
                      excluded_utc datetime2(7) NOT NULL,
                      review_after_utc datetime2(7) NULL);

                  CREATE NONCLUSTERED INDEX IX_copilot_adoption_reclaim_exclusions_user_review
                      ON dbo.copilot_adoption_reclaim_exclusions (user_id, review_after_utc, excluded_utc DESC);");
        }

        private static void CreateCopilotTables(ScratchDatabase db)
        {
            db.Execute(
                @"CREATE TABLE dbo.audit_events (
                      id uniqueidentifier NOT NULL PRIMARY KEY,
                      time_stamp datetime NOT NULL,
                      operation_id int NULL,
                      user_id int NULL,
                      event_data nvarchar(max) NULL);

                CREATE TABLE dbo.event_meta_general (
                    event_id uniqueidentifier NOT NULL PRIMARY KEY,
                    json nvarchar(max) NULL,
                    workload nvarchar(max) NULL);

                CREATE TABLE dbo.copilot_chats (
                      event_id uniqueidentifier NOT NULL PRIMARY KEY,
                      app_host nvarchar(max) NULL,
                      agent_id int NULL,
                      -- Denormalised copies of the parent audit event's columns; see migration
                      -- DenormaliseCopilotChatUserAndTime. Every Copilot query reads these instead of
                      -- joining dbo.audit_events.
                      user_id int NULL,
                      time_stamp datetime NULL);

                  CREATE NONCLUSTERED INDEX IX_copilot_chats_time_stamp_user_id
                      ON dbo.copilot_chats ([time_stamp], [user_id])
                      INCLUDE ([app_host], [agent_id]);

                  CREATE TABLE dbo.copilot_agents (
                      id int NOT NULL PRIMARY KEY,
                      name nvarchar(100) NULL,
                      agent_id nvarchar(max) NULL,
                      is_custom_agent bit NULL);");
        }

        private static void CreateCopilotReportTable(ScratchDatabase db)
        {
            db.Execute(
                @"CREATE TABLE dbo.copilot_usage_user_activity_log (
                      id int NOT NULL PRIMARY KEY,
                      [date] datetime NOT NULL,
                      user_id int NOT NULL,
                      last_activity_date datetime NULL,
                      report_period_days int NOT NULL,
                      prompts_all_apps int NULL,
                      prompts_chat_work int NULL,
                      prompts_chat_web int NULL,
                      active_usage_days int NULL,
                      report_version nvarchar(10) NULL,
                      chat_last_activity_date datetime NULL,
                      teams_last_activity_date datetime NULL,
                      word_last_activity_date datetime NULL,
                      excel_last_activity_date datetime NULL,
                      powerpoint_last_activity_date datetime NULL,
                      outlook_last_activity_date datetime NULL,
                      onenote_last_activity_date datetime NULL,
                      loop_last_activity_date datetime NULL,
                      chat_work_last_activity_date datetime NULL,
                      chat_web_last_activity_date datetime NULL,
                      m365_copilot_last_activity_date datetime NULL,
                      edge_last_activity_date datetime NULL,
                      agent_last_activity_date datetime NULL,
                      is_upn_obfuscated bit NOT NULL DEFAULT(0));");
        }

        private static void CreateCopilotImportLogTable(ScratchDatabase db)
        {
            db.Execute(
                @"CREATE TABLE dbo.copilot_usage_report_import_log (
                      id int NOT NULL PRIMARY KEY,
                      report_name nvarchar(100) NULL,
                      report_refresh_date datetime NULL,
                      report_version nvarchar(10) NULL,
                      report_period nvarchar(10) NULL,
                      imported_utc datetime NOT NULL,
                      rows_read int NOT NULL,
                      rows_saved int NOT NULL,
                      is_upn_obfuscated bit NOT NULL,
                      error nvarchar(1000) NULL);");
        }

        private static void CreateM365UsageTables(ScratchDatabase db)
        {
            db.Execute(
                @"CREATE TABLE dbo.teams_user_activity_log (
                      id int NOT NULL PRIMARY KEY,
                      [date] datetime NOT NULL,
                      user_id int NOT NULL,
                      last_activity_date datetime NULL,
                      private_chat_count bigint NOT NULL DEFAULT(0),
                      team_chat_count bigint NOT NULL DEFAULT(0),
                      post_messages bigint NOT NULL DEFAULT(0),
                      reply_messages bigint NOT NULL DEFAULT(0),
                      meetings_attended_count bigint NOT NULL DEFAULT(0),
                      meetings_organized_count bigint NOT NULL DEFAULT(0));

                  CREATE TABLE dbo.outlook_user_activity_log (
                      id int NOT NULL PRIMARY KEY,
                      [date] datetime NOT NULL,
                      user_id int NOT NULL,
                      last_activity_date datetime NULL,
                      email_send_count bigint NOT NULL DEFAULT(0),
                      email_receive_count bigint NOT NULL DEFAULT(0),
                      email_read_count bigint NOT NULL DEFAULT(0));

                  CREATE TABLE dbo.sharepoint_user_activity_log (
                      id int NOT NULL PRIMARY KEY,
                      [date] datetime NOT NULL,
                      user_id int NOT NULL,
                      last_activity_date datetime NULL,
                      viewed_or_edited bigint NOT NULL DEFAULT(0));

                  CREATE TABLE dbo.onedrive_user_activity_log (
                      id int NOT NULL PRIMARY KEY,
                      [date] datetime NOT NULL,
                      user_id int NOT NULL,
                      last_activity_date datetime NULL,
                      viewed_or_edited bigint NOT NULL DEFAULT(0));");
        }



        /// <summary>One Copilot interaction: an audit event plus its copilot_chats row.</summary>
        /// <summary>
        /// The accessed-resource tables, needed only by <see cref="CopilotAdoptionSql.TopResourceTypesSql"/>.
        /// Kept out of <see cref="CreateCopilotTables"/> because most fixtures do not need them.
        /// </summary>
        /// <summary>
        /// The Copilot Studio credit tables, with the SAME column types as production
        /// (<c>Common.Entities.Entities.AgentCosts.AgentCostClasses</c>). Declaring looser types here
        /// would let a query bind in the test and fail against a real database.
        /// </summary>
        private static void CreateAgentCostTables(ScratchDatabase db)
        {
            db.Execute(
                @"CREATE TABLE dbo.copilot_studio_credit_user_daily (
                      id int NOT NULL PRIMARY KEY,
                      usage_date datetime NOT NULL,
                      entra_object_id nvarchar(200) NULL,
                      user_id int NULL,
                      environment_id nvarchar(200) NULL,
                      environment_name nvarchar(255) NULL,
                      agent_id nvarchar(200) NULL,
                      billed_credits decimal(18,4) NOT NULL,
                      unit nvarchar(50) NULL,
                      dimension_hash nvarchar(64) NOT NULL,
                      imported_utc datetime NOT NULL);

                  CREATE TABLE dbo.copilot_studio_credit_capacity (
                      id int NOT NULL PRIMARY KEY,
                      snapshot_utc datetime NOT NULL,
                      consumption_as_of datetime NULL,
                      entitled decimal(18,4) NULL,
                      consumed decimal(18,4) NULL,
                      consumption_type nvarchar(50) NULL,
                      allocated decimal(18,4) NULL,
                      available decimal(18,4) NULL,
                      pay_as_you_go_consumed decimal(18,4) NULL,
                      status nvarchar(50) NULL);");
        }

        private static void CreateCopilotResourceTables(ScratchDatabase db)
        {            db.Execute(
                @"CREATE TABLE dbo.copilot_event_accessed_resource_types (
                      id int NOT NULL PRIMARY KEY, name nvarchar(100) NULL);

                  CREATE TABLE dbo.copilot_event_accessed_resources (
                      id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                      copilot_chat_id uniqueidentifier NOT NULL,
                      resource_type_id int NULL);");
        }

        private static void SeedCopilotInteraction(
            ScratchDatabase db, int userId, int daysAgo, string appHost, int? agentId = null)
        {
            SeedCopilotInteractionReturningId(db, userId, daysAgo, appHost, agentId);
        }

        /// <summary>Seeds one interaction and returns its id, so accessed resources can be attached.</summary>
        private static Guid SeedCopilotInteractionReturningId(
            ScratchDatabase db, int userId, int daysAgo, string appHost, int? agentId = null)
        {
            var id = Guid.NewGuid();
            var when = DateTime.UtcNow.Date.AddDays(-daysAgo).AddHours(9);

            db.Execute(
                $@"INSERT INTO dbo.audit_events (id, time_stamp, user_id)
                       VALUES ('{id}', '{when:yyyy-MM-dd HH:mm:ss}', {userId});
                   INSERT INTO dbo.event_meta_general (event_id, workload)
                       VALUES ('{id}', N'Copilot');
                   -- user_id / time_stamp are denormalised onto the chat row by the real importer merge
                   -- (common_upsert_copilot_agents.sql), which sources them from the audit event it has
                   -- just inserted. Seeded the same way here so these tests exercise the real read path.
                   INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, user_id, time_stamp)
                       SELECT '{id}', N'{appHost}', {(agentId.HasValue ? agentId.Value.ToString() : "NULL")},
                              ae.user_id, ae.time_stamp
                       FROM dbo.audit_events AS ae WHERE ae.id = '{id}';");

            return id;
        }

        #endregion
    }
}
