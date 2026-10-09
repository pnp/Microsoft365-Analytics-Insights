using Common.Entities.CopilotAdoption;
using Common.Entities.Entities.UsageReports;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// Microsoft's own tenant prompt figures on the Copilot Adoption summary (#642).
    ///
    /// <para>The usage-report import has stored Microsoft's user-count summary since #541, and nothing read it.
    /// These tests drive the new figures from a synthetic summary fixture - a version 2 report, a later
    /// version 1 fallback, an incomplete row set, daily trend rows and a per-user run recorded as concealed -
    /// through the real query, onto the summary and into the workbook's Snapshot facts, and pin the three
    /// rules that matter: the period read is the one stated, a null stays null, and Microsoft's prompts are
    /// never added to the audit-log interaction counts (#534).</para>
    /// </summary>
    [TestClass]
    public class CopilotAdoptionMicrosoftReportTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);

        private static readonly DateTime OlderReport = new DateTime(2026, 9, 28);
        private static readonly DateTime LatestReport = new DateTime(2026, 10, 5);
        private static readonly DateTime Version1Report = new DateTime(2026, 10, 6);
        private static readonly DateTime IncompleteReport = new DateTime(2026, 10, 7);

        /// <summary>A Copilot surface named in Greek: app_name is nvarchar, and the exact match must not trip on it.</summary>
        private const string GreekSurface = "Καλημέρα κόσμε";

        /// <summary>A bare EF context used purely to execute raw SQL through the service's own materialisation path.</summary>
        private sealed class RawSqlContext : DbContext
        {
            static RawSqlContext()
            {
                Database.SetInitializer<RawSqlContext>(null);
            }

            public RawSqlContext(string connectionString) : base(connectionString)
            {
            }
        }

        #region The query, against a real database

        [TestMethod]
        public void ReadsTheLatestCompleteSummaryForThePeriodClosestTo28Days()
        {
            using (var db = ScratchDatabase.Create("MsReportFigures"))
            {
                CreateTables(db);
                SeedMixedHistory(db);

                var row = ReadFigures(db, Now.Date).SingleOrDefault();

                Assert.IsNotNull(row, "A tenant with a stored summary must produce figures.");
                Assert.AreEqual(LatestReport, row.ReportDate,
                    "The latest complete 28-day summary wins: the later 30-day version 1 fallback is a different period, "
                    + "and the later 28-day set without an 'Any App' row is not complete.");
                Assert.AreEqual(28, row.ReportPeriodDays, "The period read is the one stated, never assumed.");
                Assert.AreEqual(12345L, row.PromptsSubmitted);
                Assert.AreEqual(64.97, row.AveragePromptsSubmitted.Value, 0.0001);
                Assert.AreEqual("v2", row.ReportVersion,
                    "The version comes from the successful import of that refresh date - not from a later failed attempt.");

                // The per-user import of the same refresh date was recorded as concealed, and the probe still
                // says so. Concealment hides identities, and Microsoft's summary has none, so the tenant
                // figures above are neither blanked nor zeroed by it.
                Assert.AreEqual(1, Query<int?>(db, CopilotAdoptionSql.CopilotReportObfuscatedSql).Single(),
                    "The fixture's per-user run must be recorded as concealed for this test to mean anything.");

                // The same fixture, driven all the way to the facts a workbook diff reads.
                var analysis = Analysis();
                CopilotAdoptionService.ApplyMicrosoftReportFigures(analysis.Summary, row);

                var facts = SnapshotFacts(analysis);
                Assert.AreEqual("12345", facts["microsoftReportPromptsSubmitted"]);
                Assert.AreEqual(64.97, double.Parse(facts["microsoftReportAveragePromptsPerActiveUser"], CultureInfo.InvariantCulture), 0.0001);
                Assert.AreEqual("28", facts["microsoftReportPeriodDays"]);
                Assert.AreEqual("v2", facts["microsoftReportVersion"]);
                Assert.AreEqual(LatestReport.ToOADate(), double.Parse(facts["microsoftReportDate"], CultureInfo.InvariantCulture), 0.0001);
            }
        }

        [TestMethod]
        public void APastRangeNeverReadsAReportFromAfterIt()
        {
            using (var db = ScratchDatabase.Create("MsReportPast"))
            {
                CreateTables(db);
                SeedMixedHistory(db);

                var row = ReadFigures(db, new DateTime(2026, 10, 1)).Single();

                Assert.AreEqual(OlderReport, row.ReportDate);
                Assert.AreEqual(28, row.ReportPeriodDays);
                Assert.AreEqual(9000L, row.PromptsSubmitted);
                Assert.IsNull(row.ReportVersion,
                    "No import-log row recorded that refresh date, so its version is unknown - null, not guessed.");
            }
        }

        [TestMethod]
        public void AVersion1TenantGetsItsRealPeriodAndBlankPromptFigures()
        {
            using (var db = ScratchDatabase.Create("MsReportV1"))
            {
                CreateTables(db);
                Insert(db, SummaryRow(Version1Report, 30, CopilotAppNames.AnyApp, 250, 185, null, null));
                Insert(db, SummaryRow(Version1Report, 30, "Word", 250, 44, null, null));
                Insert(db, ImportLog(CopilotUsageReportNames.UserCountSummary, Version1Report, "v1", "D30", Version1Report.AddDays(1), null, false));

                var row = ReadFigures(db, Now.Date).Single();

                Assert.AreEqual(Version1Report, row.ReportDate);
                Assert.AreEqual(30, row.ReportPeriodDays,
                    "Version 1 reports 30 days. It is stated as 30 - a 30-day figure is never a 28-day one.");
                Assert.AreEqual("v1", row.ReportVersion);
                Assert.IsNull(row.PromptsSubmitted, "Version 1 has no prompt counts; a null stays null.");
                Assert.IsNull(row.AveragePromptsSubmitted);

                var analysis = Analysis();
                CopilotAdoptionService.ApplyMicrosoftReportFigures(analysis.Summary, row);

                Assert.IsNull(analysis.Summary.MicrosoftReportPromptsSubmitted);
                Assert.IsNull(analysis.Summary.MicrosoftReportAveragePromptsPerActiveUser);

                var json = JsonConvert.SerializeObject(analysis.Summary);
                StringAssert.Contains(json, "\"microsoftReportPromptsSubmitted\":null");
                StringAssert.Contains(json, "\"microsoftReportAveragePromptsPerActiveUser\":null");
                StringAssert.Contains(json, "\"microsoftReportPeriodDays\":30");

                var facts = SnapshotFacts(analysis);
                Assert.AreEqual(string.Empty, facts["microsoftReportPromptsSubmitted"],
                    "An unreported figure is blank on Snapshot facts, never zero - zero would read as a measured fall.");
                Assert.AreEqual(string.Empty, facts["microsoftReportAveragePromptsPerActiveUser"]);
                Assert.AreEqual("30", facts["microsoftReportPeriodDays"]);

                var headline = SheetCells(CopilotAdoptionWorkbook.Build(analysis), "Headline figures");
                AssertFollowedBy(headline, "Microsoft report: prompts submitted", "Not reported");
                Assert.IsTrue(headline.Any(c => c.Contains("the 30-day report period to 2026-10-06")),
                    "The workbook must state the real period next to the figure.");
                Assert.IsTrue(headline.Any(c => c.Contains("Version 1 of Microsoft's report has no prompt counts")));
            }
        }

        [TestMethod]
        public void NoStoredSummaryMeansNoFigures_NotZeroes()
        {
            using (var db = ScratchDatabase.Create("MsReportNone"))
            {
                CreateTables(db);

                // Daily trend rows and a non-tenant surface are not a tenant summary.
                Insert(db, TrendRow(LatestReport, CopilotAppNames.AnyApp, 250, 120, 777));
                Insert(db, SummaryRow(LatestReport, 28, "Word", 250, 40, null, null));

                Assert.AreEqual(0, ReadFigures(db, Now.Date).Count);

                var analysis = Analysis();
                CopilotAdoptionService.ApplyMicrosoftReportFigures(analysis.Summary, null);

                Assert.IsNull(analysis.Summary.MicrosoftReportDate);
                Assert.IsNull(analysis.Summary.MicrosoftReportPeriodDays);
                Assert.IsNull(analysis.Summary.MicrosoftReportVersion);
                Assert.IsNull(analysis.Summary.MicrosoftReportPromptsSubmitted);
                Assert.IsNull(analysis.Summary.MicrosoftReportAveragePromptsPerActiveUser);

                var facts = SnapshotFacts(analysis);
                foreach (var key in new[]
                {
                    "microsoftReportDate", "microsoftReportPeriodDays", "microsoftReportVersion",
                    "microsoftReportPromptsSubmitted", "microsoftReportAveragePromptsPerActiveUser",
                })
                {
                    Assert.AreEqual(string.Empty, facts[key], $"{key} must be present on Snapshot facts and blank.");
                }

                var headline = SheetCells(CopilotAdoptionWorkbook.Build(analysis), "Headline figures");
                AssertFollowedBy(headline, "Microsoft report: prompts submitted", "Not imported");
            }
        }

        [TestMethod]
        public void TheQuerySeeksTheStoredIndexAndNamesNoOtherReport()
        {
            var sql = CopilotAdoptionSql.MicrosoftReportFiguresSql;

            // Plain comparisons on the index's own columns, so each read can seek
            // (report_type, report_period_days, report_date, app_name).
            StringAssert.Contains(sql, "s.report_type = N'Summary'");
            StringAssert.Contains(sql, "s.report_date <= @reportTo");
            StringAssert.Contains(sql, "s.app_name = N'Any App'");
            StringAssert.Contains(sql, "s.report_period_days = p.report_period_days");
            Assert.IsFalse(sql.Contains("CAST(s.report_date") || sql.Contains("CONVERT(") || sql.Contains("ISNULL(s."),
                "No function may wrap an indexed column in a predicate.");
            StringAssert.Contains(sql, "l.report_name = N'getMicrosoft365CopilotUserCountSummary'");
            Assert.AreEqual(28, CopilotAdoptionSql.MicrosoftReportTargetPeriodDays);
        }

        #endregion

        #region On the summary

        [TestMethod]
        public void TheReportDateIsSerialisedAsAUtcCalendarDate()
        {
            var summary = new CopilotAdoptionSummary();

            CopilotAdoptionService.ApplyMicrosoftReportFigures(summary, new CopilotAdoptionService.MicrosoftReportFiguresRow
            {
                ReportDate = LatestReport,
                ReportPeriodDays = 28,
                PromptsSubmitted = 0,
                AveragePromptsSubmitted = 0,
                ReportVersion = " v2 ",
            });

            Assert.AreEqual(DateTimeKind.Utc, summary.MicrosoftReportDate.Value.Kind,
                "Unmarked, the date is serialised without an offset and a browser east of UTC shows the day before.");
            StringAssert.Contains(JsonConvert.SerializeObject(summary), "\"microsoftReportDate\":\"2026-10-05T00:00:00Z\"");
            Assert.AreEqual("v2", summary.MicrosoftReportVersion);
            Assert.AreEqual(0L, summary.MicrosoftReportPromptsSubmitted,
                "A zero Microsoft actually reported is a zero; only a missing figure is null.");
        }

        [TestMethod]
        public void MicrosoftsPromptsAreNeverAddedToAuditInteractions()
        {
            var analysis = Analysis();
            analysis.LicensedUsers.Add(User(1, "ada@contoso.com", 40));
            analysis.LicensedUsers.Add(User(2, "grace@contoso.com", 25));
            CopilotAdoptionService.ApplyMicrosoftReportFigures(analysis.Summary, new CopilotAdoptionService.MicrosoftReportFiguresRow
            {
                ReportDate = LatestReport,
                ReportPeriodDays = 28,
                PromptsSubmitted = 12345,
                AveragePromptsSubmitted = 64.97,
                ReportVersion = "v2",
            });

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(65, analysis.Summary.TotalInteractions,
                "TotalInteractions is audit-log interactions only. A Microsoft prompt is a different unit (#534).");
            Assert.AreEqual(12345L, analysis.Summary.MicrosoftReportPromptsSubmitted,
                "Scoring must leave Microsoft's figures exactly as the report stated them.");
            Assert.AreEqual(64.97, analysis.Summary.MicrosoftReportAveragePromptsPerActiveUser.Value, 0.0001);
        }

        [TestMethod]
        public void ANarrowedViewCarriesTheTenantFiguresAndSaysTheyAreTenantWide()
        {
            var analysis = Analysis();
            analysis.LicensedUsers.Add(User(1, "ada@contoso.com", 40));
            analysis.LicensedUsers.Add(User(2, "zoe@fabrikam.com", 10));
            CopilotAdoptionService.ApplyMicrosoftReportFigures(analysis.Summary, new CopilotAdoptionService.MicrosoftReportFiguresRow
            {
                ReportDate = LatestReport,
                ReportPeriodDays = 28,
                PromptsSubmitted = 12345,
                AveragePromptsSubmitted = 64.97,
                ReportVersion = "v2",
            });
            var service = new CopilotAdoptionService(analysis.Summary.Options);
            service.FinaliseSummary(analysis);

            var scoped = CopilotAdoptionScopeFilter.Apply(
                analysis, CopilotAdoptionScope.ForEmailDomain("fabrikam.com"), service.FinaliseSummary);

            CollectionAssert.Contains(scoped.Summary.UnscopedSections, CopilotAdoptionUnscopedSections.MicrosoftReport,
                "Microsoft's summary has no per-person detail to narrow with, so the view must say the figures are tenant-wide.");
            Assert.AreEqual(12345L, scoped.Summary.MicrosoftReportPromptsSubmitted);
            Assert.AreEqual(LatestReport, scoped.Summary.MicrosoftReportDate.Value.Date);
            Assert.AreEqual(28, scoped.Summary.MicrosoftReportPeriodDays);
            Assert.AreEqual(10, scoped.Summary.TotalInteractions,
                "The audit-derived total is narrowed to the domain; Microsoft's tenant total never joins it.");

            var report = SheetCells(CopilotAdoptionWorkbook.Build(scoped), "Report");
            Assert.IsTrue(report.Any(c => c.Contains("Microsoft's usage-report figures")),
                "A narrowed workbook must name Microsoft's figures among the sections that stayed tenant-wide.");
        }

        #endregion

        #region The workbook explains them

        [TestMethod]
        public void TheMethodSheetExplainsWhyTheyDifferFromTheAuditFigures()
        {
            var text = string.Join("\n", SheetCells(CopilotAdoptionWorkbook.Build(Analysis()), "How this is calculated"));

            StringAssert.Contains(text, "Microsoft's tenant prompt figures");
            StringAssert.Contains(text, "28 days on version 2 of the report, 30 days when the tenant only receives version 1");
            StringAssert.Contains(text, "licensed users");
            StringAssert.Contains(text, "including unlicensed Copilot Chat users");
            StringAssert.Contains(text, "never added together or averaged");
            StringAssert.Contains(text, "not the agent measure charted in Microsoft's 2026 Work Trend Index");
            StringAssert.Contains(text, "no external benchmark is shown beside them");
            StringAssert.Contains(text, "never written as zero");
            StringAssert.Contains(text, "Concealed user information does not affect these figures");
            StringAssert.Contains(text,
                "https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/api/admin-settings/reports/copilotreportroot-getmicrosoft365copilotusercountsummary");
        }

        [TestMethod]
        public void TheReportSheetStatesTheSourcePeriodAndVersion()
        {
            var analysis = Analysis();
            CopilotAdoptionService.ApplyMicrosoftReportFigures(analysis.Summary, new CopilotAdoptionService.MicrosoftReportFiguresRow
            {
                ReportDate = LatestReport,
                ReportPeriodDays = 28,
                PromptsSubmitted = 12345,
                AveragePromptsSubmitted = 64.97,
                ReportVersion = "v2",
            });

            var bytes = CopilotAdoptionWorkbook.Build(analysis);

            var report = SheetCells(bytes, "Report");
            AssertFollowedBy(report, "Microsoft Copilot usage report - tenant summary", "Yes");
            Assert.IsTrue(report.Any(c => c.Contains("the 28-day report period to 2026-10-05, report version v2")));

            var headline = SheetCells(bytes, "Headline figures");
            AssertFollowedBy(headline, "Microsoft report: prompts submitted", "12345");
            Assert.IsTrue(headline.Any(c => c.Contains("Never added to the audit-log interactions above")));
        }

        #endregion

        #region Fixture

        private static void CreateTables(ScratchDatabase db)
        {
            // Column for column the production shape (migration AddCopilotUsageReports), including the
            // unique index the query seeks, so the fixture crosses the same types and the same index.
            db.Execute(
                @"CREATE TABLE dbo.copilot_user_count_log (
                      id int NOT NULL IDENTITY PRIMARY KEY,
                      report_refresh_date datetime NOT NULL,
                      report_date datetime NOT NULL,
                      report_type nvarchar(20) NULL,
                      report_period_days int NULL,
                      app_name nvarchar(100) NULL,
                      enabled_users int NOT NULL,
                      active_users int NOT NULL,
                      prompts_submitted bigint NULL,
                      average_prompts_submitted float NULL);
                  CREATE UNIQUE INDEX IX_report_type_report_period_days_report_date_app_name
                      ON dbo.copilot_user_count_log (report_type, report_period_days, report_date, app_name);
                  CREATE TABLE dbo.copilot_usage_report_import_log (
                      id int NOT NULL IDENTITY PRIMARY KEY,
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

        /// <summary>
        /// Two version 2 summaries, a later version 1 fallback, a later 28-day set with no "Any App" row, daily
        /// trend rows, a failed import attempt and a per-user run recorded as concealed.
        /// </summary>
        private static void SeedMixedHistory(ScratchDatabase db)
        {
            Insert(db, SummaryRow(OlderReport, 28, CopilotAppNames.AnyApp, 250, 180, 9000, 50.0));
            Insert(db, SummaryRow(OlderReport, 28, "Word", 250, 40, null, null));

            Insert(db, SummaryRow(LatestReport, 28, CopilotAppNames.AnyApp, 250, 190, 12345, 64.97));
            Insert(db, SummaryRow(LatestReport, 28, "Word", 250, 45, null, null));
            Insert(db, SummaryRow(LatestReport, 28, "Edge", 250, 20, null, null));
            Insert(db, SummaryRow(LatestReport, 28, "Copilot Chat (work)", 250, 150, null, null));
            Insert(db, SummaryRow(LatestReport, 28, GreekSurface, 250, 3, null, null));

            // The importer fell back to version 1 the next day: D30, no prompt columns.
            Insert(db, SummaryRow(Version1Report, 30, CopilotAppNames.AnyApp, 250, 185, null, null));
            Insert(db, SummaryRow(Version1Report, 30, "Word", 250, 44, null, null));

            // A later 28-day set with no "Any App" row is incomplete and must not be read.
            Insert(db, SummaryRow(IncompleteReport, 28, "Word", 250, 46, null, null));

            // Daily rows carry no period and are not a period roll-up, whatever their prompt count.
            Insert(db, TrendRow(Version1Report, CopilotAppNames.AnyApp, 250, 120, 777));
            Insert(db, TrendRow(IncompleteReport, CopilotAppNames.AnyApp, 250, 118, 701));

            Insert(db, ImportLog(CopilotUsageReportNames.UserCountSummary, LatestReport, "v2", "D28", LatestReport.AddHours(30), null, false));
            // A later attempt at the same refresh date that failed. Its version must not be reported.
            Insert(db, ImportLog(CopilotUsageReportNames.UserCountSummary, LatestReport, "v1", "D28", LatestReport.AddHours(42), "Synthetic persistence failure", false));
            Insert(db, ImportLog(CopilotUsageReportNames.UserCountSummary, Version1Report, "v1", "D30", Version1Report.AddHours(30), null, false));
            Insert(db, ImportLog(CopilotUsageReportNames.UsageUserDetail, LatestReport, "v2", "D28", LatestReport.AddHours(31), null, true));
        }

        private static string SummaryRow(DateTime date, int period, string app, int enabled, int active, long? prompts, double? average)
        {
            return CountRow(CopilotUserCountReportTypes.Summary, date, period, app, enabled, active, prompts, average);
        }

        private static string TrendRow(DateTime date, string app, int enabled, int active, long? prompts)
        {
            return CountRow(CopilotUserCountReportTypes.Trend, date, null, app, enabled, active, prompts, null);
        }

        private static string CountRow(string type, DateTime date, int? period, string app, int enabled, int active, long? prompts, double? average)
        {
            return "INSERT INTO dbo.copilot_user_count_log (report_refresh_date, report_date, report_type, report_period_days, app_name, "
                + "enabled_users, active_users, prompts_submitted, average_prompts_submitted) VALUES ("
                + Date(date) + ", " + Date(date) + ", N'" + type + "', " + Number(period) + ", N'" + app.Replace("'", "''") + "', "
                + enabled.ToString(CultureInfo.InvariantCulture) + ", " + active.ToString(CultureInfo.InvariantCulture) + ", "
                + Number(prompts) + ", " + (average.HasValue ? average.Value.ToString("R", CultureInfo.InvariantCulture) : "NULL") + ");";
        }

        private static string ImportLog(string report, DateTime refreshDate, string version, string period, DateTime importedUtc, string error, bool concealed)
        {
            return "INSERT INTO dbo.copilot_usage_report_import_log (report_name, report_refresh_date, report_version, report_period, "
                + "imported_utc, rows_read, rows_saved, is_upn_obfuscated, error) VALUES (N'" + report + "', " + Date(refreshDate) + ", N'"
                + version + "', N'" + period + "', '" + importedUtc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "', 10, 10, "
                + (concealed ? "1" : "0") + ", " + (error == null ? "NULL" : "N'" + error + "'") + ");";
        }

        private static string Date(DateTime value) => "'" + value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";

        private static string Number(long? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "NULL";

        private static void Insert(ScratchDatabase db, string sql) => db.Execute(sql);

        private static List<CopilotAdoptionService.MicrosoftReportFiguresRow> ReadFigures(ScratchDatabase db, DateTime reportTo)
        {
            return Query<CopilotAdoptionService.MicrosoftReportFiguresRow>(
                db,
                CopilotAdoptionSql.MicrosoftReportFiguresSql,
                new SqlParameter("@reportTo", reportTo),
                new SqlParameter("@targetPeriodDays", CopilotAdoptionSql.MicrosoftReportTargetPeriodDays));
        }

        private static List<T> Query<T>(ScratchDatabase db, string sql, params SqlParameter[] parameters)
        {
            using (var context = new RawSqlContext(db.ConnectionString))
            {
                context.Database.CommandTimeout = 120;
                return context.Database.SqlQuery<T>(sql, parameters).ToList();
            }
        }

        private static CopilotAdoptionAnalysis Analysis()
        {
            var analysis = new CopilotAdoptionAnalysis();
            var summary = analysis.Summary;
            summary.Options = new CopilotAdoptionOptions { WindowDays = 28 };
            summary.GeneratedUtc = Now;
            summary.WindowDays = 28;
            summary.FromUtc = Now.AddDays(-28);
            summary.ToUtc = Now;
            summary.SeatLicenceTypes.Add(new LicenceTypeClassification
            {
                Id = 1,
                Name = "Microsoft 365 Copilot",
                SkuPartNumber = "Microsoft_365_Copilot",
                IsCopilotSeat = true,
                AssignedUsers = 2,
            });
            return analysis;
        }

        private static LicensedUserAdoptionRow User(int id, string upn, long interactions)
        {
            var row = new LicensedUserAdoptionRow
            {
                UserId = id,
                UserPrincipalName = upn,
                EmailDomain = CopilotAdoptionEmailDomain.From(upn),
                AccountEnabled = true,
                AccountCreatedUtc = Now.AddDays(-200),
                TenureStartUtc = Now.AddDays(-200),
                TenureBasis = CopilotAdoptionScoring.TenureBasisAccountAge,
                DaysSinceTenureStart = 200,
                AdoptionScore = 40,
                Band = AdoptionBand.Developing,
                BandName = CopilotAdoptionScoring.BandDisplayName(AdoptionBand.Developing),
                Interactions = interactions,
                ActiveDays = 6,
                SeatLicenceTypeIds = new List<int> { 1 },
            };

            CopilotAdoptionScoring.ApplyReclaimEligibility(row);
            row.RecommendedActionCode = CopilotAdoptionScoring.RecommendedActionCode(row);
            row.RecommendedActionLabel = CopilotAdoptionScoring.ActionLabel(row.RecommendedActionCode);
            return row;
        }

        #endregion

        #region Workbook reading

        /// <summary>Snapshot facts as key to value text, read back out of a real workbook.</summary>
        private static Dictionary<string, string> SnapshotFacts(CopilotAdoptionAnalysis analysis)
        {
            var rows = SheetRows(CopilotAdoptionWorkbook.Build(analysis), "Snapshot facts");
            var facts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                string key;
                if (!row.TryGetValue("A", out key) || string.IsNullOrEmpty(key)) continue;
                string value;
                facts[key] = row.TryGetValue("B", out value) ? value : null;
            }
            return facts;
        }

        private static void AssertFollowedBy(List<string> cells, string value, string expectedNext)
        {
            var index = cells.IndexOf(value);
            Assert.AreNotEqual(-1, index, $"'{value}' is missing from the sheet.");
            Assert.IsTrue(index + 1 < cells.Count, $"'{value}' is the last cell on the sheet.");
            Assert.AreEqual(expectedNext, cells[index + 1], $"'{value}' should be followed by '{expectedNext}'.");
        }

        /// <summary>The decoded text of every cell on one named sheet, in document order.</summary>
        private static List<string> SheetCells(byte[] bytes, string sheetName)
        {
            return SheetCellElements(bytes, sheetName).Select(CellText).ToList();
        }

        /// <summary>Each row of a sheet as column letter to decoded cell text.</summary>
        private static List<Dictionary<string, string>> SheetRows(byte[] bytes, string sheetName)
        {
            return SheetCellElements(bytes, sheetName)
                .GroupBy(c => RowNumber((string)c.Attribute("r")))
                .OrderBy(g => g.Key)
                .Select(g => g.ToDictionary(c => ColumnLetters((string)c.Attribute("r")), CellText, StringComparer.Ordinal))
                .ToList();
        }

        private static string CellText(XElement cell)
        {
            return string.Concat(cell.Descendants().Where(d => !d.HasElements).Select(d => d.Value));
        }

        private static string ColumnLetters(string reference)
        {
            return new string((reference ?? string.Empty).TakeWhile(char.IsLetter).ToArray());
        }

        private static int RowNumber(string reference)
        {
            return int.Parse(new string((reference ?? string.Empty).SkipWhile(char.IsLetter).ToArray()), CultureInfo.InvariantCulture);
        }

        private static List<XElement> SheetCellElements(byte[] bytes, string sheetName)
        {
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

            using (var stream = new MemoryStream(bytes))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                List<string> names;
                using (var workbook = zip.GetEntry("xl/workbook.xml").Open())
                {
                    names = XDocument.Load(workbook).Descendants(ns + "sheet").Select(s => (string)s.Attribute("name")).ToList();
                }

                var index = names.IndexOf(sheetName);
                Assert.AreNotEqual(-1, index, $"Sheet '{sheetName}' is missing from the workbook.");

                var entry = zip.GetEntry("xl/worksheets/sheet" + (index + 1) + ".xml");
                Assert.IsNotNull(entry, $"The worksheet part for '{sheetName}' is missing.");

                using (var part = entry.Open())
                {
                    return XDocument.Load(part).Descendants(ns + "c").ToList();
                }
            }
        }

        #endregion
    }
}
