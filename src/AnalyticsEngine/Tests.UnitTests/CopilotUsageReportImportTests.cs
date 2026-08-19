using Common.Entities;
using Common.Entities.Entities.UsageReports;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
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
    /// The JSON below matches the shapes published on Microsoft Learn for the beta endpoints, with synthetic
    /// values only - no customer or tenant data. Parsing is asserted directly because these reports nest
    /// their numbers a level down and name a property pair per Copilot surface, so a shape change would
    /// otherwise show up as a silently empty import rather than an error.
    /// </summary>
    [TestClass]
    public class CopilotUsageReportImportTests
    {
        // ---- Sample reports (synthetic) ------------------------------------------------------------

        private static List<JObject> Report(string json) => new List<JObject> { JObject.Parse(json) };

        private const string SummaryJsonV1 = @"{
            'reportRefreshDate': '2026-07-03',
            'adoptionByProduct': [
                {
                    'reportPeriod': 7,
                    'microsoftTeamsEnabledUsers': 250, 'microsoftTeamsActiveUsers': 110,
                    'wordEnabledUsers': 250, 'wordActiveUsers': 40,
                    'anyAppEnabledUsers': 250, 'anyAppActiveUsers': 180,
                    'copilotChatEnabledUsers': 250, 'copilotChatActiveUsers': 90
                }
            ]
        }";

        // Version 2 adds Edge / Microsoft 365 Copilot / Copilot Chat work+web, plus the tenant-level prompt
        // totals. Nothing in the parser knows those names - the point of the narrow/tall shape is that they
        // arrive as rows.
        private const string SummaryJsonV2 = @"{
            'reportRefreshDate': '2026-07-03',
            'adoptionByProduct': [
                {
                    'reportPeriod': 28,
                    'microsoftTeamsEnabledUsers': 250, 'microsoftTeamsActiveUsers': 110,
                    'anyAppEnabledUsers': 250, 'anyAppActiveUsers': 180,
                    'edgeEnabledUsers': 250, 'edgeActiveUsers': 20,
                    'microsoft365CopilotEnabledUsers': 250, 'microsoft365CopilotActiveUsers': 175,
                    'copilotChatWorkEnabledUsers': 250, 'copilotChatWorkActiveUsers': 150,
                    'copilotChatWebEnabledUsers': 250, 'copilotChatWebActiveUsers': 30,
                    'totalPromptsSubmitted': 4820, 'averagePromptsSubmitted': 26.8
                }
            ]
        }";

        private const string TrendJsonV2 = @"{
            'reportRefreshDate': '2026-07-03',
            'reportPeriod': 28,
            'adoptionByDate': [
                {
                    'reportDate': '2026-07-03',
                    'microsoftTeamsEnabledUsers': 250, 'microsoftTeamsActiveUsers': 110,
                    'anyAppEnabledUsers': 250, 'anyAppActiveUsers': 180,
                    'promptsSubmitted': 640
                },
                {
                    'reportDate': '2026-07-02',
                    'microsoftTeamsEnabledUsers': 250, 'microsoftTeamsActiveUsers': 105,
                    'anyAppEnabledUsers': 250, 'anyAppActiveUsers': 171,
                    'promptsSubmitted': 610
                }
            ]
        }";

        private const string UserDetailJsonV1 = @"{
            'reportRefreshDate': '2026-07-03',
            'userPrincipalName': 'ada@contoso.onmicrosoft.com',
            'displayName': 'Ada Lovelace',
            'lastActivityDate': '2026-07-02',
            'copilotChatLastActivityDate': '2026-07-02',
            'microsoftTeamsCopilotLastActivityDate': '2026-07-01',
            'wordCopilotLastActivityDate': '',
            'copilotActivityUserDetailsByPeriod': [ { 'reportPeriod': 7 } ]
        }";

        private const string UserDetailJsonV2 = @"{
            'reportRefreshDate': '2026-07-03',
            'userPrincipalName': 'ada@contoso.onmicrosoft.com',
            'displayName': 'Ada Lovelace',
            'lastActivityDate': '2026-07-02',
            'copilotChatLastActivityDate': '2026-07-02',
            'microsoftTeamsCopilotLastActivityDate': '2026-07-01',
            'copilotChatWorkLastActivityDate': '2026-07-02',
            'copilotChatWebLastActivityDate': '2026-06-28',
            'microsoft365CopilotLastActivityDate': '2026-07-02',
            'edgeLastActivityDate': '2026-06-30',
            'copilotAgentLastActivityDate': '2026-07-01',
            'copilotActivityUserDetailsByPeriod': [
                { 'reportPeriod': 28, 'promptsSubmitted': 142, 'activeUsageDays': 19,
                  'promptsSubmittedForCopilotChatWork': 90, 'promptsSubmittedForCopilotChatWeb': 52 }
            ]
        }";

        // Microsoft's documentation example for this report shows hashed identities - a tenant with
        // "concealed user information" switched on. The hashes below are synthetic stand-ins of the same
        // shape (32 hex characters).
        private static List<JObject> ConcealedUserDetail() => new List<JObject>
        {
            JObject.Parse(@"{ 'reportRefreshDate': '2026-07-03', 'userPrincipalName': 'AAAABBBBCCCCDDDDEEEEFFFF00001111',
                              'displayName': '00001111222233334444555566667777', 'lastActivityDate': '2026-07-02',
                              'copilotActivityUserDetailsByPeriod': [ { 'reportPeriod': 28 } ] }"),
            JObject.Parse(@"{ 'reportRefreshDate': '2026-07-03', 'userPrincipalName': '11112222333344445555666677778888',
                              'displayName': '99990000AAAABBBBCCCCDDDDEEEEFFFF', 'lastActivityDate': '2026-07-01',
                              'copilotActivityUserDetailsByPeriod': [ { 'reportPeriod': 28 } ] }"),
        };

        /// <summary>Builds a full-shape version 2 per-user report for a single user and period.</summary>
        private static List<JObject> UserDetailReport(DateTime reportDate, string upn, int periodDays, int prompts,
            int activeDays, string agentLastActivity = null)
        {
            var date = reportDate.ToString("yyyy-MM-dd");
            var user = new JObject
            {
                ["reportRefreshDate"] = date,
                ["userPrincipalName"] = upn,
                ["displayName"] = "Ada Lovelace",
                ["lastActivityDate"] = date,
                ["copilotActivityUserDetailsByPeriod"] = new JArray
                {
                    new JObject
                    {
                        ["reportPeriod"] = periodDays,
                        ["promptsSubmitted"] = prompts,
                        ["activeUsageDays"] = activeDays,
                    }
                },
            };
            if (agentLastActivity != null) user["copilotAgentLastActivityDate"] = agentLastActivity;

            return new List<JObject> { user };
        }

        // ---- Request building ----------------------------------------------------------------------

        [TestMethod]
        public void ReportRequest_UsesTheSameEndpointStyleAsTheOtherUsageReports()
        {
            // version is OPTIONAL on the Graph side and defaults to v1. Omitting it costs every prompt-count
            // and active-usage-day value with no error, so it must always be on the URL.
            var request = new CopilotReportRequest(CopilotReportNames.UsageUserDetail, "D28");

            StringAssert.Contains(request.Url, "version='v2'");
            StringAssert.Contains(request.Url, "period='D28'");
            StringAssert.Contains(request.Url, "$format=application/json");
            StringAssert.StartsWith(request.Url, "https://graph.microsoft.com/beta/copilot/reports/");
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

            Assert.IsNotNull(new CopilotReportRequest(CopilotReportNames.UserCountSummary, "D28", CopilotReportVersions.V2));
            Assert.IsNotNull(new CopilotReportRequest(CopilotReportNames.UserCountSummary, "D30", CopilotReportVersions.V1));
        }

        // ---- Aggregate parsing ---------------------------------------------------------------------

        [TestMethod]
        public void SummaryParser_TurnsTheNestedJsonIntoOneRowPerApp()
        {
            var rows = CopilotUserCountReportParser.ParseSummary(Report(SummaryJsonV1));

            Assert.AreEqual(4, rows.Count, "One row per app property pair.");
            Assert.IsTrue(rows.All(r => r.ReportType == CopilotUserCountReportTypes.Summary));
            Assert.IsTrue(rows.All(r => r.ReportPeriodDays == 7), "The summary period comes from the report entry.");
            Assert.IsTrue(rows.All(r => r.ReportDate == new DateTime(2026, 7, 3)),
                "A summary has no per-day value, so it is dated to the refresh date.");

            var teams = rows.Single(r => r.AppName == "Microsoft Teams");
            Assert.AreEqual(250, teams.EnabledUsers);
            Assert.AreEqual(110, teams.ActiveUsers);
        }

        [TestMethod]
        public void SummaryParser_PicksUpNewMicrosoftAppsWithNoCodeChange()
        {
            // The whole reason for the narrow/tall table: report version 2 introduced four more Copilot
            // surfaces, and they must appear as rows without a schema migration or a parser change.
            var rows = CopilotUserCountReportParser.ParseSummary(Report(SummaryJsonV2));
            var appNames = rows.Select(r => r.AppName).ToList();

            CollectionAssert.Contains(appNames, "Edge");
            CollectionAssert.Contains(appNames, "Microsoft 365 Copilot");
            CollectionAssert.Contains(appNames, "Copilot Chat (work)");
            CollectionAssert.Contains(appNames, "Copilot Chat (web)");

            Assert.AreEqual(150, rows.Single(r => r.AppName == "Copilot Chat (work)").ActiveUsers);
        }

        [TestMethod]
        public void SummaryParser_ImportsAnAppMicrosoftHasNotShippedYet()
        {
            // A surface nobody has coded for must still import, with a readable generated name.
            var json = @"{ 'reportRefreshDate': '2026-07-03', 'adoptionByProduct': [
                { 'reportPeriod': 28, 'brandNewCopilotAppEnabledUsers': 250, 'brandNewCopilotAppActiveUsers': 7 } ] }";

            var row = CopilotUserCountReportParser.ParseSummary(Report(json)).Single();

            Assert.AreEqual("Brand New Copilot App", row.AppName);
            Assert.AreEqual(7, row.ActiveUsers);
        }

        [TestMethod]
        public void SummaryParser_PutsTenantWidePromptTotalsOnTheAnyAppRowOnly()
        {
            var rows = CopilotUserCountReportParser.ParseSummary(Report(SummaryJsonV2));

            var anyApp = rows.Single(r => r.AppName == CopilotAppNames.AnyApp);
            Assert.AreEqual(4820L, anyApp.PromptsSubmitted);
            Assert.AreEqual(26.8, anyApp.AveragePromptsSubmitted.Value, 0.0001);

            Assert.IsTrue(rows.Where(r => r.AppName != CopilotAppNames.AnyApp).All(r => r.PromptsSubmitted == null),
                "Prompt totals are tenant-level, not per app - carrying them on every row would be wrong.");
        }

        [TestMethod]
        public void TrendParser_UsesThePerDayDateAndLeavesThePeriodNull()
        {
            var rows = CopilotUserCountReportParser.ParseTrend(Report(TrendJsonV2));

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
        public void AggregateParser_IgnoresAnEmptyReport()
        {
            Assert.AreEqual(0, CopilotUserCountReportParser.ParseSummary(new List<JObject>()).Count);
            Assert.AreEqual(0, CopilotUserCountReportParser.ParseSummary(null).Count);
            Assert.AreEqual(0, CopilotUserCountReportParser.ParseSummary(
                Report(@"{ 'reportRefreshDate': '2026-07-03', 'adoptionByProduct': [] }")).Count);
        }

        [TestMethod]
        public void AppNames_MatchWhatTheAdminCentreCallsThem()
        {
            // Generic camel-case splitting would produce "Power Point" and "One Note".
            Assert.AreEqual("PowerPoint", CopilotUserCountReportParser.DisplayNameFor("powerPoint"));
            Assert.AreEqual("OneNote", CopilotUserCountReportParser.DisplayNameFor("oneNote"));
            Assert.AreEqual("Microsoft 365 Copilot", CopilotUserCountReportParser.DisplayNameFor("microsoft365Copilot"));
            Assert.AreEqual(CopilotAppNames.AnyApp, CopilotUserCountReportParser.DisplayNameFor("anyApp"));
        }

        // ---- Per-user parsing ----------------------------------------------------------------------

        [TestMethod]
        public void UserDetailParser_ReadsEveryVersion2Value()
        {
            var row = CopilotUsageUserDetailParser.Parse(Report(UserDetailJsonV2)).Single();

            Assert.AreEqual(142, row.PromptsAllApps);
            Assert.AreEqual(90, row.PromptsChatWork);
            Assert.AreEqual(52, row.PromptsChatWeb);
            Assert.AreEqual(19, row.ActiveUsageDays);
            Assert.AreEqual(28, row.ReportPeriodDays);
            Assert.AreEqual(new DateTime(2026, 6, 30), row.EdgeLastActivityDate);
            Assert.AreEqual(new DateTime(2026, 7, 2), row.Microsoft365CopilotLastActivityDate);
            Assert.AreEqual(new DateTime(2026, 7, 1), row.AgentLastActivityDate,
                "Copilot agent last activity is the only agent signal in any Graph usage report.");
            Assert.IsTrue(row.HasVersion2Data);
            Assert.IsFalse(row.IsIdentityConcealed);
        }

        [TestMethod]
        public void UserDetailParser_LeavesVersion2ValuesNullOnAVersion1Response()
        {
            var row = CopilotUsageUserDetailParser.Parse(Report(UserDetailJsonV1)).Single();

            // NULL, not 0: "Graph didn't tell us" and "the user submitted no prompts" mean very different
            // things in an adoption report.
            Assert.IsNull(row.PromptsAllApps);
            Assert.IsNull(row.ActiveUsageDays);
            Assert.IsNull(row.AgentLastActivityDate);
            Assert.IsFalse(row.HasVersion2Data);

            // Version 1 values still parse.
            Assert.AreEqual(7, row.ReportPeriodDays);
            Assert.AreEqual(new DateTime(2026, 7, 1), row.TeamsLastActivityDate);
            Assert.IsNull(row.WordLastActivityDate, "An empty value means 'never', not a parse failure.");
        }

        [TestMethod]
        public void UserDetailParser_ProducesOneRowPerReportPeriod()
        {
            // A single user object can carry several periods, which is exactly the table's grain.
            var json = @"{
                'reportRefreshDate': '2026-07-03',
                'userPrincipalName': 'ada@contoso.onmicrosoft.com',
                'lastActivityDate': '2026-07-02',
                'copilotActivityUserDetailsByPeriod': [
                    { 'reportPeriod': 7, 'promptsSubmitted': 31, 'activeUsageDays': 4 },
                    { 'reportPeriod': 28, 'promptsSubmitted': 142, 'activeUsageDays': 19 }
                ] }";

            var rows = CopilotUsageUserDetailParser.Parse(Report(json));

            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual(31, rows.Single(r => r.ReportPeriodDays == 7).PromptsAllApps);
            Assert.AreEqual(142, rows.Single(r => r.ReportPeriodDays == 28).PromptsAllApps);
        }

        [TestMethod]
        public void UserDetailParser_DetectsConcealedUserIdentities()
        {
            var rows = CopilotUsageUserDetailParser.Parse(ConcealedUserDetail());

            Assert.AreEqual(2, rows.Count);
            Assert.IsTrue(rows.All(r => r.IsIdentityConcealed),
                "Hashed identities must be recognised, otherwise they'd be joined to users as if they were UPNs.");
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
        public void Parser_PreservesNonLatinAppNames()
        {
            // "Καλημέρα κόσμε" - the classic Greek charset sample (synthetic; no customer data). app_name is
            // persisted as nvarchar because Microsoft localises product names.
            const string greekName = "\u039A\u03B1\u03BB\u03B7\u03BC\u03AD\u03C1\u03B1 \u03BA\u03CC\u03C3\u03BC\u03B5";
            var entry = new JObject
            {
                ["reportPeriod"] = 28,
                [greekName + "EnabledUsers"] = 250,
                [greekName + "ActiveUsers"] = 44,
            };
            var report = new JObject
            {
                ["reportRefreshDate"] = "2026-07-03",
                ["adoptionByProduct"] = new JArray { entry },
            };

            var row = CopilotUserCountReportParser.ParseSummary(new List<JObject> { report }).Single();

            StringAssert.Contains(row.AppName, "\u03BA\u03CC\u03C3\u03BC\u03B5");
            Assert.AreEqual(44, row.ActiveUsers);
        }

        // ---- Persistence ---------------------------------------------------------------------------

        [TestMethod]
        public async Task AggregateLoader_ReImportingTheSameWindowDoesNotDuplicateRows()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();

            // A date far enough out that it can't collide with anything else in the shared test database.
            var reportDate = new DateTime(2031, 3, 17);
            var report = TrendReport(reportDate, activeUsers: 180);

            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearAggregateRowsFor(db, reportDate);

                var loader = new CopilotUserCountReportLoader(new FakeCopilotReportSource(report), logger);

                Assert.AreEqual(1, await loader.LoadAndSaveTrendAsync(db, "D28"), "The first import inserts the row.");

                // Graph gap-fills the most recent few days, so overlapping re-imports are normal. Identical
                // data must not be rewritten - at tenant scale that is the difference between a handful of
                // writes and rewriting the whole window every cycle.
                Assert.AreEqual(0, await loader.LoadAndSaveTrendAsync(db, "D28"),
                    "Re-importing unchanged data must write nothing.");

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

            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearAggregateRowsFor(db, reportDate);

                await new CopilotUserCountReportLoader(new FakeCopilotReportSource(TrendReport(reportDate, 180)), logger)
                    .LoadAndSaveTrendAsync(db, "D28");

                var revised = await new CopilotUserCountReportLoader(new FakeCopilotReportSource(TrendReport(reportDate, 191)), logger)
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
        public async Task AggregateLoader_ANewerRefreshDateAloneDoesNotRewriteHistory()
        {
            // The refresh date advances every single day. If it counted as a change, every day in the window
            // (up to 180 days x every app) would be rewritten daily purely to restamp provenance.
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var reportDate = new DateTime(2031, 3, 20);

            using (var db = new AnalyticsEntitiesContext())
            {
                await ClearAggregateRowsFor(db, reportDate);

                await new CopilotUserCountReportLoader(new FakeCopilotReportSource(TrendReport(reportDate, 180, "2031-03-20")), logger)
                    .LoadAndSaveTrendAsync(db, "D28");

                var written = await new CopilotUserCountReportLoader(new FakeCopilotReportSource(TrendReport(reportDate, 180, "2031-03-21")), logger)
                    .LoadAndSaveTrendAsync(db, "D28");

                Assert.AreEqual(0, written,
                    "Only a changed metric should cause a write; a newer refresh date on its own must not.");

                await ClearAggregateRowsFor(db, reportDate);
            }
        }

        [TestMethod]
        public async Task AggregateLoader_FailsRatherThanRecordingAnEmptySnapshotWhenTheShapeChanges()
        {
            // An unrecognised shape must not look like "this tenant has no Copilot licences" - that would
            // mark the one-off D180 backfill complete and lose the history for good.
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var report = Report(@"{ 'reportRefreshDate': '2031-03-24', 'adoptionByDate': [
                { 'reportDate': '2031-03-24', 'somethingCompletelyDifferent': 5 } ] }");

            using (var db = new AnalyticsEntitiesContext())
            {
                var loader = new CopilotUserCountReportLoader(new FakeCopilotReportSource(report), logger);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => loader.LoadAndSaveTrendAsync(db, "D28"));

                var importLog = await db.CopilotUsageReportImportLogs
                    .Where(l => l.ReportName == CopilotReportNames.UserCountTrend)
                    .OrderByDescending(l => l.ID)
                    .FirstAsync();
                Assert.IsFalse(string.IsNullOrEmpty(importLog.Error),
                    "The failure must be visible on the Health page.");
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

                var loader = new CopilotUsageUserDetailLoader(new FakeCopilotReportSource(ConcealedUserDetail()), logger);
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
            var report = UserDetailReport(reportDate, upn, 28, 142, 19, reportDate.ToString("yyyy-MM-dd"));

            using (var db = new AnalyticsEntitiesContext())
            {
                // Users normally arrive from the Graph user-metadata import. Seed one so the domain is known:
                // the loader deliberately refuses to invent users on a domain this database has never seen.
                var user = await SeedUserAsync(db, upn);

                var loader = new CopilotUsageUserDetailLoader(new FakeCopilotReportSource(report), logger);

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

            using (var db = new AnalyticsEntitiesContext())
            {
                var anchor = await SeedUserAsync(db, knownUpn);
                var usersBefore = await db.users.CountAsync();

                var written = await new CopilotUsageUserDetailLoader(
                        new FakeCopilotReportSource(UserDetailReport(reportDate, strangerUpn, 28, 10, 2)), logger)
                    .LoadAndSaveAsync(db, "D28");

                Assert.AreEqual(0, written, "An identity on an unrecognised domain must not be imported.");
                Assert.AreEqual(usersBefore, await db.users.CountAsync(), "...and must not be created.");

                db.users.Remove(anchor);
                await db.SaveChangesAsync();
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

            using (var db = new AnalyticsEntitiesContext())
            {
                var user = await SeedUserAsync(db, upn);

                await new CopilotUsageUserDetailLoader(new FakeCopilotReportSource(UserDetailReport(reportDate, upn, 7, 31, 4)), logger)
                    .LoadAndSaveAsync(db, "D7");
                await new CopilotUsageUserDetailLoader(new FakeCopilotReportSource(UserDetailReport(reportDate, upn, 28, 142, 19)), logger)
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
        public async Task UserDetailLoader_DoesNotBlankVersion2DataWhenAResponseCarriesNone()
        {
            // Microsoft hasn't published the beta JSON schema for version 2, so "no v2 values" can mean the
            // response was v1 OR that the field names differ from the ones we look for. Either way, blanking
            // prompt counts a previous import captured is the more damaging mistake.
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var reportDate = new DateTime(2031, 3, 22);
            var upn = $"copilot.test.{Guid.NewGuid():N}@contoso.onmicrosoft.com";

            var version1Shaped = new List<JObject>
            {
                JObject.Parse($@"{{ 'reportRefreshDate': '{reportDate:yyyy-MM-dd}', 'userPrincipalName': '{upn}',
                                    'lastActivityDate': '{reportDate:yyyy-MM-dd}',
                                    'copilotActivityUserDetailsByPeriod': [ {{ 'reportPeriod': 28 }} ] }}")
            };

            using (var db = new AnalyticsEntitiesContext())
            {
                var user = await SeedUserAsync(db, upn);

                await new CopilotUsageUserDetailLoader(new FakeCopilotReportSource(UserDetailReport(reportDate, upn, 28, 142, 19)), logger)
                    .LoadAndSaveAsync(db, "D28");

                await new CopilotUsageUserDetailLoader(new FakeCopilotReportSource(version1Shaped), logger)
                    .LoadAndSaveAsync(db, "D28");

                var stored = await db.CopilotUsageUserActivityLogs
                    .Where(r => r.UserID == user.ID && r.Date == reportDate)
                    .ToListAsync();

                Assert.AreEqual(1, stored.Count);
                Assert.AreEqual(142, stored[0].PromptsAllApps, "A response with no version 2 values must not blank the stored count.");
                Assert.AreEqual(19, stored[0].ActiveUsageDays);

                db.CopilotUsageUserActivityLogs.RemoveRange(stored);
                db.users.Remove(user);
                await db.SaveChangesAsync();
            }
        }

        // ---- Helpers -------------------------------------------------------------------------------

        private static List<JObject> TrendReport(DateTime reportDate, int activeUsers, string refreshDate = null)
        {
            var date = reportDate.ToString("yyyy-MM-dd");
            return new List<JObject>
            {
                new JObject
                {
                    ["reportRefreshDate"] = refreshDate ?? date,
                    ["reportPeriod"] = 28,
                    ["adoptionByDate"] = new JArray
                    {
                        new JObject
                        {
                            ["reportDate"] = date,
                            ["anyAppEnabledUsers"] = 250,
                            ["anyAppActiveUsers"] = activeUsers,
                        }
                    },
                }
            };
        }

        private static async Task<Common.Entities.User> SeedUserAsync(AnalyticsEntitiesContext db, string upn)
        {
            var user = new Common.Entities.User { UserPrincipalName = upn.ToLowerInvariant() };
            db.users.Add(user);
            await db.SaveChangesAsync();
            return user;
        }

        private static async Task ClearAggregateRowsFor(AnalyticsEntitiesContext db, DateTime reportDate)
        {
            var existing = await db.CopilotUserCountLogs.Where(r => r.ReportDate == reportDate).ToListAsync();
            if (existing.Count == 0) return;

            db.CopilotUserCountLogs.RemoveRange(existing);
            await db.SaveChangesAsync();
        }

        /// <summary>Returns a canned report so the loaders can be exercised with no HTTP and no tenant.</summary>
        private class FakeCopilotReportSource : ICopilotReportSource
        {
            private readonly List<JObject> _report;

            public FakeCopilotReportSource(List<JObject> report)
            {
                _report = report;
            }

            public Task<List<JObject>> LoadReportAsync(CopilotReportRequest request) => Task.FromResult(_report);
        }
    }
}
