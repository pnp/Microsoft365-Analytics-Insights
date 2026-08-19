using Common.Entities;
using Common.Entities.Entities.UsageReports;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot;

namespace Tests.UnitTests
{
    /// <summary>
    /// Covers the Microsoft Graph Microsoft 365 Copilot usage-report import.
    ///
    /// The CSV samples below are the shapes published on Microsoft Learn for the v1.0 (GA) endpoints, with
    /// synthetic values only - no customer or tenant data. Parsing is asserted directly because these
    /// endpoints stream CSV rather than the JSON every other usage-report loader in this solution consumes,
    /// so an unnoticed column rename would silently produce empty columns rather than an error.
    /// </summary>
    [TestClass]
    public class CopilotUsageReportImportTests
    {
        // ---- Sample reports (synthetic) ------------------------------------------------------------

        private const string SummaryCsvV1 =
            "Report Refresh Date,Report Period,Microsoft Teams Enabled Users,Microsoft Teams Active Users," +
            "Word Enabled Users,Word Active Users,Any App Enabled Users,Any App Active Users," +
            "Copilot Chat Enabled Users,Copilot Chat Active Users\r\n" +
            "2026-07-03,7,250,110,250,40,250,180,250,90\r\n";

        // Version 2 adds Edge / Microsoft 365 Copilot / Copilot Chat (work) / Copilot Chat (web) columns plus
        // the two tenant-level prompt totals. Nothing in the parser knows those names - the point of the
        // narrow/tall shape is that they arrive as rows.
        private const string SummaryCsvV2 =
            "Report Refresh Date,Report Period,Microsoft Teams Enabled Users,Microsoft Teams Active Users," +
            "Any App Enabled Users,Any App Active Users,Edge Enabled Users,Edge Active Users," +
            "Microsoft 365 Copilot Enabled Users,Microsoft 365 Copilot Active Users," +
            "Copilot Chat (work) Enabled Users,Copilot Chat (work) Active Users," +
            "Copilot Chat (web) Enabled Users,Copilot Chat (web) Active Users," +
            "Total prompts submitted,Average prompts submitted\r\n" +
            "2026-07-03,28,250,110,250,180,250,20,250,175,250,150,250,30,4820,26.8\r\n";

        private const string TrendCsvV2 =
            "Report Refresh Date,Report Date,Microsoft Teams Enabled Users,Microsoft Teams Active Users," +
            "Any App Enabled Users,Any App Active Users,Report Period,Prompts submitted\r\n" +
            "2026-07-03,2026-07-03,250,110,250,180,28,640\r\n" +
            "2026-07-03,2026-07-02,250,105,250,171,28,610\r\n";

        private const string UserDetailCsvV1 =
            "Report Refresh Date,User Principal Name,Display Name,Last Activity Date," +
            "Copilot Chat Last Activity Date,Microsoft Teams Copilot Last Activity Date," +
            "Word Copilot Last Activity Date,Excel Copilot Last Activity Date," +
            "PowerPoint Copilot Last Activity Date,Outlook Copilot Last Activity Date," +
            "OneNote Copilot Last Activity Date,Loop Copilot Last Activity Date,Report Period\r\n" +
            "2026-07-03,ada@contoso.onmicrosoft.com,Ada Lovelace,2026-07-02,2026-07-02,2026-07-01,,,,,,,7\r\n";

        private const string UserDetailCsvV2 =
            "Report Refresh Date,User Principal Name,Display Name,Last Activity Date," +
            "Copilot Chat Last Activity Date,Microsoft Teams Copilot Last Activity Date," +
            "Word Copilot Last Activity Date,Excel Copilot Last Activity Date," +
            "PowerPoint Copilot Last Activity Date,Outlook Copilot Last Activity Date," +
            "OneNote Copilot Last Activity Date,Loop Copilot Last Activity Date,Report Period," +
            "Prompts submitted for all apps,Prompts submitted for Copilot Chat (work)," +
            "Prompts submitted for Copilot Chat (web),Active Usage Days for all apps," +
            "Copilot Chat (work) Last Activity Date,Copilot Chat (web) Last Activity Date," +
            "Microsoft 365 Copilot Last Activity Date,Edge Last Activity Date,Copilot Agent Last Activity Date\r\n" +
            "2026-07-03,ada@contoso.onmicrosoft.com,Ada Lovelace,2026-07-02,2026-07-02,2026-07-01,,,,,,,28," +
            "142,90,52,19,2026-07-02,2026-06-28,2026-07-02,2026-06-30,2026-07-01\r\n";

        // Microsoft's own documentation example for this report shows hashed identities - a tenant with
        // "concealed user information" switched on. The hashes below are synthetic stand-ins of the same shape
        // (32 hex characters).
        private const string UserDetailCsvConcealed =
            "Report Refresh Date,User Principal Name,Display Name,Last Activity Date,Report Period\r\n" +
            "2026-07-03,AAAABBBBCCCCDDDDEEEEFFFF00001111,00001111222233334444555566667777,2026-07-02,28\r\n" +
            "2026-07-03,11112222333344445555666677778888,99990000AAAABBBBCCCCDDDDEEEEFFFF,2026-07-01,28\r\n";

        // ---- Request building ----------------------------------------------------------------------

        [TestMethod]
        public void ReportRequest_AlwaysSendsTheVersion()
        {
            // version is OPTIONAL on the Graph side and defaults to v1. Omitting it costs every prompt-count
            // and active-usage-day column with no error, so it must always be on the URL.
            var request = new CopilotReportRequest(CopilotReportNames.UsageUserDetail, "D28");

            StringAssert.Contains(request.Url, "version='v2'");
            StringAssert.Contains(request.Url, "period='D28'");
            StringAssert.StartsWith(request.Url, "https://graph.microsoft.com/v1.0/copilot/reports/");
        }

        [TestMethod]
        public void ReportRequest_RejectsPeriodsFromTheWrongReportVersion()
        {
            // v1 uses D30; v2 replaced it with D28. Mixing them is a 400 from Graph.
            Assert.ThrowsException<ArgumentOutOfRangeException>(
                () => new CopilotReportRequest(CopilotReportNames.UserCountSummary, "D30", CopilotReportVersions.V2),
                "D30 is a version 1 period and must be rejected for version 2.");

            Assert.ThrowsException<ArgumentOutOfRangeException>(
                () => new CopilotReportRequest(CopilotReportNames.UserCountSummary, "D28", CopilotReportVersions.V1),
                "D28 is a version 2 period and must be rejected for version 1.");

            // The valid pairings must still build.
            Assert.IsNotNull(new CopilotReportRequest(CopilotReportNames.UserCountSummary, "D28", CopilotReportVersions.V2));
            Assert.IsNotNull(new CopilotReportRequest(CopilotReportNames.UserCountSummary, "D30", CopilotReportVersions.V1));
        }

        // ---- Aggregate parsing ---------------------------------------------------------------------

        [TestMethod]
        public void SummaryParser_TurnsTheWideCsvIntoOneRowPerApp()
        {
            var rows = CopilotUserCountReportParser.ParseSummary(CsvReportTable.Parse(SummaryCsvV1));

            Assert.AreEqual(4, rows.Count, "One row per app column pair.");
            Assert.IsTrue(rows.All(r => r.ReportType == CopilotUserCountReportTypes.Summary));
            Assert.IsTrue(rows.All(r => r.ReportPeriodDays == 7), "The summary period comes from the CSV as a day count.");
            Assert.IsTrue(rows.All(r => r.ReportDate == new DateTime(2026, 7, 3)),
                "A summary has no per-day column, so it is dated to the refresh date.");

            var teams = rows.Single(r => r.AppName == "Microsoft Teams");
            Assert.AreEqual(250, teams.EnabledUsers);
            Assert.AreEqual(110, teams.ActiveUsers);
        }

        [TestMethod]
        public void SummaryParser_PicksUpNewMicrosoftAppsWithNoCodeChange()
        {
            // The whole reason for the narrow/tall table: report version 2 introduced four more Copilot
            // surfaces, and they must appear as rows without a schema migration or a parser change.
            var rows = CopilotUserCountReportParser.ParseSummary(CsvReportTable.Parse(SummaryCsvV2));
            var appNames = rows.Select(r => r.AppName).ToList();

            CollectionAssert.Contains(appNames, "Edge");
            CollectionAssert.Contains(appNames, "Microsoft 365 Copilot");
            CollectionAssert.Contains(appNames, "Copilot Chat (work)");
            CollectionAssert.Contains(appNames, "Copilot Chat (web)");

            var chatWork = rows.Single(r => r.AppName == "Copilot Chat (work)");
            Assert.AreEqual(150, chatWork.ActiveUsers);
        }

        [TestMethod]
        public void SummaryParser_PutsTenantWidePromptTotalsOnTheAnyAppRowOnly()
        {
            var rows = CopilotUserCountReportParser.ParseSummary(CsvReportTable.Parse(SummaryCsvV2));

            var anyApp = rows.Single(r => r.AppName == CopilotAppNames.AnyApp);
            Assert.AreEqual(4820L, anyApp.PromptsSubmitted);
            Assert.AreEqual(26.8, anyApp.AveragePromptsSubmitted.Value, 0.0001);

            Assert.IsTrue(rows.Where(r => r.AppName != CopilotAppNames.AnyApp).All(r => r.PromptsSubmitted == null),
                "Prompt totals are tenant-level, not per app - carrying them on every row would be wrong.");
        }

        [TestMethod]
        public void TrendParser_UsesThePerDayDateAndLeavesThePeriodNull()
        {
            var rows = CopilotUserCountReportParser.ParseTrend(CsvReportTable.Parse(TrendCsvV2));

            Assert.AreEqual(4, rows.Count, "Two days x two apps.");
            Assert.IsTrue(rows.All(r => r.ReportType == CopilotUserCountReportTypes.Trend));

            // A trend row is a daily count, so it is the same number whichever window asked for it. Leaving
            // the period NULL is what lets a D7 refresh update the rows a D180 backfill created.
            Assert.IsTrue(rows.All(r => r.ReportPeriodDays == null));

            var secondDay = rows.Single(r => r.ReportDate == new DateTime(2026, 7, 2) && r.AppName == CopilotAppNames.AnyApp);
            Assert.AreEqual(171, secondDay.ActiveUsers);
            Assert.AreEqual(610L, secondDay.PromptsSubmitted);
            Assert.AreEqual(new DateTime(2026, 7, 3), secondDay.ReportRefreshDate);
        }

        [TestMethod]
        public void AggregateParser_IgnoresAnEmptyOrHeaderOnlyReport()
        {
            Assert.AreEqual(0, CopilotUserCountReportParser.ParseSummary(CsvReportTable.Parse(string.Empty)).Count);
            Assert.AreEqual(0, CopilotUserCountReportParser.ParseSummary(
                CsvReportTable.Parse("Report Refresh Date,Report Period,Any App Enabled Users,Any App Active Users\r\n")).Count);
        }

        // ---- Per-user parsing ----------------------------------------------------------------------

        [TestMethod]
        public void UserDetailParser_ReadsEveryVersion2Column()
        {
            var rows = CopilotUsageUserDetailParser.Parse(CsvReportTable.Parse(UserDetailCsvV2));
            var row = rows.Single();

            Assert.AreEqual(142, row.PromptsAllApps);
            Assert.AreEqual(90, row.PromptsChatWork);
            Assert.AreEqual(52, row.PromptsChatWeb);
            Assert.AreEqual(19, row.ActiveUsageDays);
            Assert.AreEqual(28, row.ReportPeriodDays);
            Assert.AreEqual(new DateTime(2026, 6, 30), row.EdgeLastActivityDate);
            Assert.AreEqual(new DateTime(2026, 7, 2), row.Microsoft365CopilotLastActivityDate);
            Assert.AreEqual(new DateTime(2026, 7, 1), row.AgentLastActivityDate,
                "Copilot Agent Last Activity Date is the only agent signal in any Graph usage report.");
            Assert.IsFalse(row.IsIdentityConcealed);
        }

        [TestMethod]
        public void UserDetailParser_LeavesVersion2ColumnsNullOnAVersion1Report()
        {
            var row = CopilotUsageUserDetailParser.Parse(CsvReportTable.Parse(UserDetailCsvV1)).Single();

            // NULL, not 0: "Graph didn't tell us" and "the user submitted no prompts" mean very different
            // things in an adoption report.
            Assert.IsNull(row.PromptsAllApps);
            Assert.IsNull(row.ActiveUsageDays);
            Assert.IsNull(row.AgentLastActivityDate);

            // Version 1 columns still parse.
            Assert.AreEqual(new DateTime(2026, 7, 1), row.TeamsLastActivityDate);
            Assert.IsNull(row.WordLastActivityDate, "An empty cell means 'never', not a parse failure.");

            Assert.IsFalse(CopilotUsageUserDetailParser.IsVersion2(CsvReportTable.Parse(UserDetailCsvV1)));
            Assert.IsTrue(CopilotUsageUserDetailParser.IsVersion2(CsvReportTable.Parse(UserDetailCsvV2)));
        }

        [TestMethod]
        public void UserDetailParser_DetectsConcealedUserIdentities()
        {
            var rows = CopilotUsageUserDetailParser.Parse(CsvReportTable.Parse(UserDetailCsvConcealed));

            Assert.AreEqual(2, rows.Count);
            Assert.IsTrue(rows.All(r => r.IsIdentityConcealed),
                "Hashed identities must be recognised, otherwise they'd be joined to users as if they were UPNs.");
        }

        [TestMethod]
        public void CsvParser_PreservesNonLatinText()
        {
            // "Καλημέρα κόσμε" - the classic Greek charset sample (synthetic; no customer data). Display names
            // and localised Microsoft app names routinely contain non-Latin scripts, and app_name is persisted
            // as nvarchar for exactly this reason.
            const string greekName = "\u039A\u03B1\u03BB\u03B7\u03BC\u03AD\u03C1\u03B1 \u03BA\u03CC\u03C3\u03BC\u03B5";
            var csv =
                "Report Refresh Date,User Principal Name,Display Name,Last Activity Date,Report Period\r\n" +
                "2026-07-03,ada@contoso.onmicrosoft.com,\"" + greekName + "\",2026-07-02,28\r\n";

            var table = CsvReportTable.Parse(csv);

            Assert.AreEqual(greekName, table.Rows.Single().GetString("Display Name"),
                "Non-Latin text must survive CSV parsing byte-for-byte.");

            // And through to a persisted column: app_name comes straight from the CSV header.
            var aggregateCsv =
                "Report Refresh Date,Report Period," + greekName + " Enabled Users," + greekName + " Active Users\r\n" +
                "2026-07-03,28,250,44\r\n";

            var appRow = CopilotUserCountReportParser.ParseSummary(CsvReportTable.Parse(aggregateCsv)).Single();
            Assert.AreEqual(greekName, appRow.AppName);
            Assert.AreEqual(44, appRow.ActiveUsers);
        }

        [TestMethod]
        public void CsvParser_HandlesQuotedCommasAndAByteOrderMark()
        {
            // Microsoft product names have contained commas before, and Graph's report stream carries a BOM.
            var csv =
                "\uFEFFReport Refresh Date,Report Period,\"Word, Excel and PowerPoint Enabled Users\"," +
                "\"Word, Excel and PowerPoint Active Users\"\r\n" +
                "2026-07-03,28,250,44\r\n";

            var rows = CopilotUserCountReportParser.ParseSummary(CsvReportTable.Parse(csv));

            var row = rows.Single();
            Assert.AreEqual("Word, Excel and PowerPoint", row.AppName);
            Assert.AreEqual(44, row.ActiveUsers);
            Assert.AreEqual(new DateTime(2026, 7, 3), row.ReportRefreshDate,
                "A byte-order mark must not corrupt the first header name.");
        }

        // ---- Persistence ---------------------------------------------------------------------------

        [TestMethod]
        public async Task AggregateLoader_ReImportingTheSameWindowDoesNotDuplicateRows()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();

            // A date far enough out that it can't collide with anything else in the shared test database.
            var reportDate = new DateTime(2031, 3, 17);
            var csv =
                "Report Refresh Date,Report Date,Any App Enabled Users,Any App Active Users,Report Period\r\n" +
                $"{reportDate:yyyy-MM-dd},{reportDate:yyyy-MM-dd},250,180,28\r\n";

            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearAggregateRowsFor(db, reportDate);

                var loader = new CopilotUserCountReportLoader(new FakeCopilotReportCsvSource(csv), logger);

                var firstWrite = await loader.LoadAndSaveTrendAsync(db, "D28");
                Assert.AreEqual(1, firstWrite, "The first import inserts the row.");

                // Graph gap-fills the most recent few days, so overlapping re-imports are normal. Identical
                // data must not be rewritten - at tenant scale that is the difference between a handful of
                // writes and rewriting the whole window every cycle.
                var secondWrite = await loader.LoadAndSaveTrendAsync(db, "D28");
                Assert.AreEqual(0, secondWrite, "Re-importing unchanged data must write nothing.");

                var stored = await db.CopilotUserCountLogs
                    .Where(r => r.ReportDate == reportDate && r.ReportType == CopilotUserCountReportTypes.Trend)
                    .ToListAsync();
                Assert.AreEqual(1, stored.Count, "The unique key must keep this to a single row.");
                Assert.AreEqual(180, stored[0].ActiveUsers);

                await ClearAggregateRowsFor(db, reportDate);
            }
        }

        [TestMethod]
        public async Task AggregateLoader_UpdatesRowsWhenGraphRevisesTheNumbers()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var reportDate = new DateTime(2031, 3, 18);

            string CsvWithActiveUsers(int activeUsers) =>
                "Report Refresh Date,Report Date,Any App Enabled Users,Any App Active Users,Report Period\r\n" +
                $"{reportDate:yyyy-MM-dd},{reportDate:yyyy-MM-dd},250,{activeUsers},28\r\n";

            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearAggregateRowsFor(db, reportDate);

                await new CopilotUserCountReportLoader(new FakeCopilotReportCsvSource(CsvWithActiveUsers(180)), logger)
                    .LoadAndSaveTrendAsync(db, "D28");

                var revised = await new CopilotUserCountReportLoader(new FakeCopilotReportCsvSource(CsvWithActiveUsers(191)), logger)
                    .LoadAndSaveTrendAsync(db, "D28");

                Assert.AreEqual(1, revised, "A revised figure must be written.");

                var stored = await db.CopilotUserCountLogs
                    .Where(r => r.ReportDate == reportDate && r.ReportType == CopilotUserCountReportTypes.Trend)
                    .ToListAsync();
                Assert.AreEqual(1, stored.Count);
                Assert.AreEqual(191, stored[0].ActiveUsers, "Graph's 3-day gap-fill revision must land in SQL.");

                await ClearAggregateRowsFor(db, reportDate);
            }
        }

        [TestMethod]
        public async Task UserDetailLoader_ConcealedIdentitiesImportNothingAndCreateNoUsers()
        {
            // The important guarantee: on a tenant with concealed user information we must NOT create one
            // placeholder user per licensed account (200,000 of them on a large tenant) with hashes for UPNs.
            var logger = AnalyticsLogger.ConsoleOnlyTracer();

            using (var db = new AnalyticsEntitiesContext())
            {
                var usersBefore = await db.users.CountAsync();

                var loader = new CopilotUsageUserDetailLoader(new FakeCopilotReportCsvSource(UserDetailCsvConcealed), logger);
                var written = await loader.LoadAndSaveAsync(db, "D28");

                Assert.AreEqual(0, written, "Nothing should be imported when identities are hashed.");
                Assert.AreEqual(usersBefore, await db.users.CountAsync(),
                    "No user records may be created from hashed identities.");

                var importLog = await db.CopilotUsageReportImportLogs
                    .Where(l => l.ReportName == CopilotReportNames.UsageUserDetail)
                    .OrderByDescending(l => l.ID)
                    .FirstAsync();

                Assert.IsTrue(importLog.IsUpnObfuscated,
                    "The Health page needs this flag to tell 'no Copilot usage' apart from 'identities are concealed'.");
                Assert.AreEqual(2, importLog.RowsRead);
                Assert.AreEqual(0, importLog.RowsSaved);
            }
        }

        [TestMethod]
        public async Task UserDetailLoader_ImportsRealUpnsAndIsIdempotent()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var reportDate = new DateTime(2031, 3, 19);
            var upn = $"copilot.test.{Guid.NewGuid():N}@contoso.onmicrosoft.com";
            var storedUpn = upn.ToLowerInvariant();

            var csv = UserDetailCsv(reportDate, upn, periodDays: 28, prompts: 142, activeDays: 19,
                agentLastActivity: reportDate.ToString("yyyy-MM-dd"));

            using (var db = new AnalyticsEntitiesContext())
            {
                // Users normally arrive from the Graph user-metadata import. Seed one so the domain is known:
                // the loader deliberately refuses to invent users on a domain this database has never seen.
                var user = await SeedUserAsync(db, upn);

                var loader = new CopilotUsageUserDetailLoader(new FakeCopilotReportCsvSource(csv), logger);

                Assert.AreEqual(1, await loader.LoadAndSaveAsync(db, "D28"));
                Assert.AreEqual(0, await loader.LoadAndSaveAsync(db, "D28"),
                    "An unchanged re-import must not rewrite every licensed user.");

                var stored = await db.CopilotUsageUserActivityLogs
                    .Where(r => r.UserID == user.ID && r.Date == reportDate)
                    .ToListAsync();

                Assert.AreEqual(1, stored.Count);
                Assert.AreEqual(142, stored[0].PromptsAllApps);
                Assert.AreEqual(19, stored[0].ActiveUsageDays);
                Assert.AreEqual(reportDate, stored[0].AgentLastActivityDate);
                Assert.IsFalse(stored[0].IsUpnObfuscated);

                // Clean up so re-runs against the shared test database stay independent.
                db.CopilotUsageUserActivityLogs.RemoveRange(stored);
                db.users.Remove(user);
                await db.SaveChangesAsync();
            }
        }

        [TestMethod]
        public async Task UserDetailLoader_DoesNotInventUsersOnAnUnrecognisedDomain()
        {
            // The real safety boundary behind "hashed identities must never create users": syntax alone can
            // never prove an identity belongs to the tenant, so an identity on a domain this database holds no
            // users for is skipped rather than created.
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var reportDate = new DateTime(2031, 3, 23);
            var knownUpn = $"copilot.test.{Guid.NewGuid():N}@contoso.onmicrosoft.com";
            var strangerUpn = $"copilot.test.{Guid.NewGuid():N}@not-this-tenant.example";

            var csv = UserDetailCsv(reportDate, strangerUpn, periodDays: 28, prompts: 10, activeDays: 2);

            using (var db = new AnalyticsEntitiesContext())
            {
                var anchor = await SeedUserAsync(db, knownUpn);
                var usersBefore = await db.users.CountAsync();

                var written = await new CopilotUsageUserDetailLoader(new FakeCopilotReportCsvSource(csv), logger)
                    .LoadAndSaveAsync(db, "D28");

                Assert.AreEqual(0, written, "An identity on an unrecognised domain must not be imported.");
                Assert.AreEqual(usersBefore, await db.users.CountAsync(), "...and must not be created.");

                db.users.Remove(anchor);
                await db.SaveChangesAsync();
            }
        }

        private static async Task<Common.Entities.User> SeedUserAsync(AnalyticsEntitiesContext db, string upn)
        {
            var user = new Common.Entities.User { UserPrincipalName = upn.ToLowerInvariant() };
            db.users.Add(user);
            await db.SaveChangesAsync();
            return user;
        }

        [TestMethod]
        public void UserDetailParser_RejectsEmailShapedPseudonyms()
        {
            // The concealment guard must be more than "does MailAddress accept this?" - anything it lets
            // through goes on to create a user record and an activity row.
            Assert.IsFalse(CopilotUsageUserDetailRow.LooksLikeRealUpn("hash@hash"),
                "A domain with no dot is not a routable UPN suffix.");
            Assert.IsFalse(CopilotUsageUserDetailRow.LooksLikeRealUpn("AAAABBBBCCCCDDDDEEEEFFFF00001111"),
                "A bare hash has no @ at all.");
            Assert.IsFalse(CopilotUsageUserDetailRow.LooksLikeRealUpn("aaaabbbbccccddddeeeeffff00001111@contoso.com"),
                "A 32-character all-hex local part is a concealed-identity hash, not a user name.");
            Assert.IsFalse(CopilotUsageUserDetailRow.LooksLikeRealUpn(null));
            Assert.IsFalse(CopilotUsageUserDetailRow.LooksLikeRealUpn(""));

            Assert.IsTrue(CopilotUsageUserDetailRow.LooksLikeRealUpn("ada@contoso.onmicrosoft.com"));
            Assert.IsTrue(CopilotUsageUserDetailRow.LooksLikeRealUpn("ada.lovelace@contoso.co.uk"));
            Assert.IsTrue(CopilotUsageUserDetailRow.LooksLikeRealUpn("beefed@contoso.com"),
                "A short word that happens to be hex is still a real user name.");
            Assert.IsTrue(CopilotUsageUserDetailRow.LooksLikeRealUpn("0123456789abcdef@contoso.com"),
                "A genuine account that happens to be spellable in hex must not be mistaken for a hash.");
        }

        [TestMethod]
        public void Parsers_FailLoudlyWhenAnExpectedColumnIsMissing()
        {
            // A renamed Microsoft column otherwise yields zero rows, which is indistinguishable from the
            // perfectly normal "this tenant has no Copilot licences" case - so the import would look
            // successful while quietly storing nothing.
            var request = new CopilotReportRequest(CopilotReportNames.UsageUserDetail, "D28");
            var table = CsvReportTable.Parse("Refresh Date,User,Report Period\r\n2026-07-03,ada@contoso.onmicrosoft.com,28\r\n");

            var ex = Assert.ThrowsException<InvalidOperationException>(
                () => CsvReportTable.RequireHeaders(table.Headers, request, CopilotUsageUserDetailParser.RequiredHeaders));

            StringAssert.Contains(ex.Message, "Report Refresh Date");
            StringAssert.Contains(ex.Message, "User Principal Name");
        }

        [TestMethod]
        public async Task AggregateLoader_ANewerRefreshDateAloneDoesNotRewriteHistory()
        {
            // The refresh date advances every single day. If it counted as a change, every day in the window
            // (up to 180 days x every app) would be rewritten daily purely to restamp provenance.
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var reportDate = new DateTime(2031, 3, 20);

            string CsvWithRefreshDate(string refreshDate) =>
                "Report Refresh Date,Report Date,Any App Enabled Users,Any App Active Users,Report Period\r\n" +
                $"{refreshDate},{reportDate:yyyy-MM-dd},250,180,28\r\n";

            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearAggregateRowsFor(db, reportDate);

                await new CopilotUserCountReportLoader(new FakeCopilotReportCsvSource(CsvWithRefreshDate("2031-03-20")), logger)
                    .LoadAndSaveTrendAsync(db, "D28");

                var written = await new CopilotUserCountReportLoader(new FakeCopilotReportCsvSource(CsvWithRefreshDate("2031-03-21")), logger)
                    .LoadAndSaveTrendAsync(db, "D28");

                Assert.AreEqual(0, written,
                    "Only a changed metric should cause a write; a newer refresh date on its own must not.");

                await ClearAggregateRowsFor(db, reportDate);
            }
        }

        [TestMethod]
        public async Task UserDetailLoader_KeepsDifferentPeriodsForTheSameUserAndDate()
        {
            // D7 and D28 describe the same user and date with different prompt counts, active-day counts and
            // last-activity values. They are different facts, so one must not overwrite the other.
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var reportDate = new DateTime(2031, 3, 21);
            var upn = $"copilot.test.{Guid.NewGuid():N}@contoso.onmicrosoft.com";
            var storedUpn = upn.ToLowerInvariant();

            string CsvFor(int periodDays, int prompts) =>
                UserDetailCsv(reportDate, upn, periodDays, prompts, activeDays: 5);

            using (var db = new AnalyticsEntitiesContext())
            {
                var user = await SeedUserAsync(db, upn);

                await new CopilotUsageUserDetailLoader(new FakeCopilotReportCsvSource(CsvFor(7, 31)), logger)
                    .LoadAndSaveAsync(db, "D7");
                await new CopilotUsageUserDetailLoader(new FakeCopilotReportCsvSource(CsvFor(28, 142)), logger)
                    .LoadAndSaveAsync(db, "D28");

                var stored = await db.CopilotUsageUserActivityLogs
                    .Where(r => r.UserID == user.ID && r.Date == reportDate)
                    .ToListAsync();

                Assert.AreEqual(2, stored.Count, "Each period is a separate row.");
                Assert.AreEqual(31, stored.Single(r => r.ReportPeriodDays == 7).PromptsAllApps);
                Assert.AreEqual(142, stored.Single(r => r.ReportPeriodDays == 28).PromptsAllApps);

                db.CopilotUsageUserActivityLogs.RemoveRange(stored);
                db.users.Remove(user);
                await db.SaveChangesAsync();
            }
        }

        [TestMethod]
        public async Task UserDetailLoader_RefusesToImportAVersion1SnapshotWhenVersion2WasRequested()
        {
            // Persisting a v1-shaped response would write NULL over prompt counts and active usage days that a
            // previous v2 import had already stored.
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var csv =
                "Report Refresh Date,User Principal Name,Display Name,Last Activity Date,Report Period\r\n" +
                "2031-03-22,ada@contoso.onmicrosoft.com,Ada Lovelace,2031-03-21,28\r\n";

            using (var db = new AnalyticsEntitiesContext())
            {
                var loader = new CopilotUsageUserDetailLoader(new FakeCopilotReportCsvSource(csv), logger);

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => loader.LoadAndSaveAsync(db, "D28"));

                var importLog = await db.CopilotUsageReportImportLogs
                    .Where(l => l.ReportName == CopilotReportNames.UsageUserDetail)
                    .OrderByDescending(l => l.ID)
                    .FirstAsync();
                Assert.IsFalse(string.IsNullOrEmpty(importLog.Error),
                    "The Health page must be able to see why the import refused to run.");
            }
        }

        // The complete version 2 header set. The loader requires ALL of it before it will treat a response as
        // v2, so tests that exercise the loader must supply the real shape rather than a convenient subset.
        private static readonly string[] UserDetailHeadersV2 =
        {
            "Report Refresh Date", "User Principal Name", "Display Name", "Last Activity Date",
            "Copilot Chat Last Activity Date", "Microsoft Teams Copilot Last Activity Date",
            "Word Copilot Last Activity Date", "Excel Copilot Last Activity Date",
            "PowerPoint Copilot Last Activity Date", "Outlook Copilot Last Activity Date",
            "OneNote Copilot Last Activity Date", "Loop Copilot Last Activity Date", "Report Period",
            "Prompts submitted for all apps", "Prompts submitted for Copilot Chat (work)",
            "Prompts submitted for Copilot Chat (web)", "Active Usage Days for all apps",
            "Copilot Chat (work) Last Activity Date", "Copilot Chat (web) Last Activity Date",
            "Microsoft 365 Copilot Last Activity Date", "Edge Last Activity Date",
            "Copilot Agent Last Activity Date",
        };

        /// <summary>Builds a full-shape version 2 per-user CSV with a single row.</summary>
        private static string UserDetailCsv(DateTime reportDate, string upn, int periodDays, int prompts, int activeDays,
            string agentLastActivity = "")
        {
            var date = reportDate.ToString("yyyy-MM-dd");
            var values = new[]
            {
                date, upn, "Ada Lovelace", date,
                "", "", "", "", "", "", "", "",          // per-app v1 last-activity dates
                periodDays.ToString(),
                prompts.ToString(), "0", "0",
                activeDays.ToString(),
                "", "", "", "",                          // chat work/web, M365 Copilot, Edge
                agentLastActivity,
            };

            return string.Join(",", UserDetailHeadersV2) + "\r\n" + string.Join(",", values) + "\r\n";
        }

        private static async Task ClearAggregateRowsFor(AnalyticsEntitiesContext db, DateTime reportDate)
        {
            var existing = await db.CopilotUserCountLogs.Where(r => r.ReportDate == reportDate).ToListAsync();
            if (existing.Count == 0) return;

            db.CopilotUserCountLogs.RemoveRange(existing);
            await db.SaveChangesAsync();
        }

        /// <summary>Returns a canned report so the loaders can be exercised with no HTTP and no tenant.</summary>
        private class FakeCopilotReportCsvSource : ICopilotReportCsvSource
        {
            private readonly string _csv;

            public FakeCopilotReportCsvSource(string csv)
            {
                _csv = csv;
            }

            public Task<CopilotReportCsvStream> OpenReportCsvAsync(CopilotReportRequest request)
            {
                var bytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(_csv ?? string.Empty);
                return Task.FromResult(new CopilotReportCsvStream(new System.IO.MemoryStream(bytes)));
            }
        }
    }
}
