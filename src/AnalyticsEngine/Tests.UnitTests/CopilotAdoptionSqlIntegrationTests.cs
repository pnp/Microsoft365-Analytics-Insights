using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.SqlClient;
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
                    @"INSERT INTO dbo.user_departments (id, name) VALUES (1, N'Engineering');
                      INSERT INTO dbo.license_types (id, name, sku_id)
                          VALUES (1, N'Microsoft Copilot for Microsoft 365', N'Microsoft_365_Copilot'),
                                 (2, N'Office 365 E3', N'ENTERPRISEPACK');

                      -- Deliberately non-ASCII: real tenants have Greek, Cyrillic and CJK display
                      -- names, and a varchar column anywhere in this path would silently mangle them.
                      INSERT INTO dbo.users (id, user_name, mail, account_enabled, department_id)
                          VALUES (10, N'active@contoso.com',  N'active@contoso.com',  1, 1),
                                 (11, N'dormant@contoso.com', N'dormant@contoso.com', 1, 1),
                                 (12, N'never@contoso.com',   N'never@contoso.com',   0, 1),
                                 (13, N'unlicensed@contoso.com', N'unlicensed@contoso.com', 1, 1),
                                 (14, N'καλημέρα@contoso.com', N'καλημέρα@contoso.com', 1, 1);

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
                Assert.IsTrue(rows.Any(r => r.UserPrincipalName == "καλημέρα@contoso.com"),
                    "A non-ASCII user principal name must survive the query unchanged.");

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

        [TestMethod]
        public void LicensedUsersQuery_PreservesNonLatinMetadata()
        {
            // Guards the Unicode requirement end to end: a varchar column or a non-N string literal
            // anywhere on this path turns a Greek display name into question marks, in an export that
            // is about to be sent to an executive.
            using (var db = ScratchDatabase.Create("CopilotAdoptUni"))
            {
                CreateUserTables(db);
                CreateCopilotTables(db);

                db.Execute(
                    @"INSERT INTO dbo.user_departments (id, name) VALUES (1, N'Καλημέρα κόσμε');
                      INSERT INTO dbo.license_types (id, name, sku_id)
                          VALUES (1, N'Microsoft Copilot for Microsoft 365', N'Microsoft_365_Copilot');
                      INSERT INTO dbo.users (id, user_name, mail, account_enabled, department_id)
                          VALUES (1, N'καλημέρα@contoso.com', N'καλημέρα@contoso.com', 1, 1);
                      INSERT INTO dbo.user_license_type_lookups (id, user_id, license_type_id) VALUES (1, 1, 1);");

                var rows = Query<LicensedUserUsageRow>(
                    db,
                    CopilotAdoptionSql.LicensedUsersSql(new[] { 1 }, new int[0], includeCopilotReport: false),
                    new SqlParameter("@from", DateTime.UtcNow.Date.AddDays(-28)),
                    new SqlParameter("@historyFrom", DateTime.UtcNow.Date.AddDays(-365)),
                    new SqlParameter("@maxRows", 1000));

                Assert.AreEqual(1, rows.Count);
                Assert.AreEqual("καλημέρα@contoso.com", rows[0].UserPrincipalName);
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
                            prompts_all_apps, active_usage_days,
                            teams_last_activity_date, word_last_activity_date, excel_last_activity_date,
                            chat_last_activity_date)
                       VALUES (1, '{snapshot:yyyy-MM-dd}', 1, '{inWindow:yyyy-MM-dd}', 28,
                               47, 12,
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
                    "Teams, Word and the collapsed chat surface are inside the window; Excel is not.");
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
                                 (2, N'Sales helper', N'SPO_1234');");

                SeedCopilotInteraction(db, userId: 1, daysAgo: 2, appHost: "Teams");
                SeedCopilotInteraction(db, userId: 1, daysAgo: 2, appHost: "cowork", agentId: 1);

                var from = DateTime.UtcNow.Date.AddDays(-28);
                var settled = DateTime.UtcNow.Date.AddDays(-3);

                var licenceTypes = Query<LicenceTypeRow>(db, CopilotAdoptionSql.LicenceTypesSql);
                Assert.AreEqual(1, licenceTypes.Count);
                Assert.AreEqual(1, licenceTypes[0].AssignedUsers);
                Assert.IsTrue(CopilotLicenceClassifier.IsCopilotSeat(licenceTypes[0].SkuPartNumber, licenceTypes[0].Name));

                var coworkAgents = Query<CopilotAdoptionService.IntValueRow>(db, CopilotAdoptionSql.CoworkAgentIdsSql);
                CollectionAssert.AreEqual(new[] { 1 }, coworkAgents.Select(a => a.Value).ToArray(),
                    "Only the Cowork agent should match - a SharePoint agent must not.");

                var seats = Query<CopilotAdoptionService.SeatAssignmentRow>(db, CopilotAdoptionSql.SeatAssignmentsSql(new[] { 1 }));
                Assert.AreEqual(1, seats.Count);
                Assert.AreEqual("Microsoft Copilot for Microsoft 365", seats[0].LicenceName);

                var byApp = Query<CopilotAdoptionService.CategoryQueryRow>(db,
                    CopilotAdoptionSql.UsageByAppSql(new[] { 1 }),
                    new SqlParameter("@from", from),
                    new SqlParameter("@top", 10));
                Assert.AreEqual(2, byApp.Count, "Teams and Cowork.");

                var trend = Query<CopilotAdoptionService.NamedWeekRow>(db,
                    CopilotAdoptionSql.WeeklyAdoptionTrendSql(new[] { 1 }, new[] { 1 }),
                    new SqlParameter("@trendFrom", DateTime.UtcNow.Date.AddMonths(-6)));
                Assert.IsTrue(trend.Any(t => t.SeriesName == "Active licensed users"));
                Assert.IsTrue(trend.Any(t => t.SeriesName == "Cowork users"),
                    "The Cowork series must be produced when Cowork interactions exist.");

                var unlicensed = Query<int?>(db,
                    CopilotAdoptionSql.UnlicensedActiveUsersSql(new[] { 1 }),
                    new SqlParameter("@from", from));
                Assert.AreEqual(0, unlicensed.Single(), "The only Copilot user in this fixture holds a seat.");

                var hasAudit = Query<int?>(db, CopilotAdoptionSql.HasCopilotAuditDataSql, new SqlParameter("@from", from));
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
                @"CREATE TABLE dbo.user_departments (id int NOT NULL PRIMARY KEY, name nvarchar(200) NULL);
                  CREATE TABLE dbo.user_job_titles (id int NOT NULL PRIMARY KEY, name nvarchar(200) NULL);
                  CREATE TABLE dbo.user_country_or_region (id int NOT NULL PRIMARY KEY, name nvarchar(200) NULL);
                  CREATE TABLE dbo.user_office_locations (id int NOT NULL PRIMARY KEY, name nvarchar(200) NULL);
                  CREATE TABLE dbo.user_company_name (id int NOT NULL PRIMARY KEY, name nvarchar(200) NULL);

                  CREATE TABLE dbo.users (
                      id int NOT NULL PRIMARY KEY,
                      user_name nvarchar(400) NULL,
                      mail nvarchar(400) NULL,
                      account_enabled bit NULL,
                      department_id int NULL,
                      job_title_id int NULL,
                      country_or_region_id int NULL,
                      office_location_id int NULL,
                      company_name_id int NULL,
                      manager_id int NULL);

                  CREATE TABLE dbo.license_types (
                      id int NOT NULL PRIMARY KEY,
                      name nvarchar(200) NULL,
                      sku_id nvarchar(200) NULL);

                  CREATE TABLE dbo.user_license_type_lookups (
                      id int NOT NULL PRIMARY KEY,
                      user_id int NOT NULL,
                      license_type_id int NOT NULL);

                  CREATE UNIQUE NONCLUSTERED INDEX IX_license_type_id_user_id
                      ON dbo.user_license_type_lookups (license_type_id, user_id);");
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

                  CREATE TABLE dbo.copilot_chats (
                      event_id uniqueidentifier NOT NULL PRIMARY KEY,
                      app_host nvarchar(200) NULL,
                      agent_id int NULL);

                  CREATE TABLE dbo.copilot_agents (
                      id int NOT NULL PRIMARY KEY,
                      name nvarchar(400) NULL,
                      agent_id nvarchar(400) NULL,
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
        private static void SeedCopilotInteraction(
            ScratchDatabase db, int userId, int daysAgo, string appHost, int? agentId = null)
        {
            var id = Guid.NewGuid();
            var when = DateTime.UtcNow.Date.AddDays(-daysAgo).AddHours(9);

            db.Execute(
                $@"INSERT INTO dbo.audit_events (id, time_stamp, user_id)
                       VALUES ('{id}', '{when:yyyy-MM-dd HH:mm:ss}', {userId});
                   INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id)
                       VALUES ('{id}', N'{appHost}', {(agentId.HasValue ? agentId.Value.ToString() : "NULL")});");
        }

        #endregion
    }
}
