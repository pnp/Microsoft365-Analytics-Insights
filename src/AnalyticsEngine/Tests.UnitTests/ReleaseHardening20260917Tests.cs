using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot;

namespace Tests.UnitTests
{
    /// <summary>
    /// Regression guards for defects found while hardening the September 2026 Copilot Adoption release.
    /// Each test names the defect it pins so a later change cannot quietly reintroduce it.
    /// </summary>
    [TestClass]
    public class CoworkReportCsvIngestionTests
    {
        /// <summary>
        /// Production requests the Cowork report through the same GA v1.0 CSV transport as every other
        /// Copilot report (ProductionGraphImportSectionFactory -> CoworkUsageUserDetailLoader ->
        /// GraphCopilotReportSource, whose primary attempt is $format=text/csv). The CSV parser had no
        /// Cowork branch, so it threw ArgumentOutOfRangeException - which is not a Graph HTTP failure and
        /// therefore not eligible for the source's fallback chain. The whole Cowork import failed every
        /// cycle on any tenant where the GA endpoint answers.
        /// </summary>
        [TestMethod]
        public void CoworkUserDetailCsv_IsParsed_NotRejectedAsAnUnknownReport()
        {
            var csv = string.Join("\r\n",
                "Report Refresh Date,User Principal Name,Total tasks,Scheduled tasks,User-initiated tasks,Active days,Last activity date,Retained Cowork user,Report Period",
                "2026-09-15,adele.vance@contoso.onmicrosoft.com,40,10,30,12,2026-09-14,Yes,28");

            var reports = CopilotReportCsvParser.Parse(CopilotReportNames.CoworkUsageUserDetail, csv);

            Assert.AreEqual(1, reports.Count, "The Cowork CSV must produce one report object per row.");

            var rows = CoworkUsageUserDetailParser.Parse(reports);

            Assert.AreEqual(1, rows.Count, "The CSV-derived object must be consumable by the Cowork parser.");
            var row = rows.Single();
            Assert.AreEqual("adele.vance@contoso.onmicrosoft.com", row.UserPrincipalName);
            Assert.AreEqual(40, row.TotalTasks);
            Assert.AreEqual(10, row.ScheduledTasks);
            Assert.AreEqual(30, row.UserInitiatedTasks);
            Assert.AreEqual(12, row.ActiveDays);
            Assert.AreEqual(28, row.ReportPeriodDays);
            Assert.AreEqual(true, row.RetainedUser);
            Assert.AreEqual(new DateTime(2026, 9, 15), row.ReportRefreshDate);
        }

        /// <summary>
        /// The other direction: a genuinely unknown report name must still be rejected loudly rather than
        /// silently returning nothing, or a typo would look like an empty tenant.
        /// </summary>
        [TestMethod]
        public void AnUnknownReportName_IsStillRejected()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(
                () => CopilotReportCsvParser.Parse("getSomeReportThatDoesNotExist", "a,b\r\n1,2"));
        }

        /// <summary>
        /// Blank cells must stay unknown rather than becoming a measured zero - a false "0 tasks" would
        /// read as a user who was offered Cowork and ignored it.
        /// </summary>
        [TestMethod]
        public void BlankCoworkCells_StayNull_RatherThanBecomingZero()
        {
            var csv = string.Join("\r\n",
                "Report Refresh Date,User Principal Name,Total tasks,Scheduled tasks,Active days,Report Period",
                "2026-09-15,adele.vance@contoso.onmicrosoft.com,,,,28");

            var rows = CoworkUsageUserDetailParser.Parse(
                CopilotReportCsvParser.Parse(CopilotReportNames.CoworkUsageUserDetail, csv));

            var row = rows.Single();
            Assert.IsNull(row.TotalTasks, "A blank cell is unknown, not zero.");
            Assert.IsNull(row.ScheduledTasks);
            Assert.IsNull(row.ActiveDays);
        }
    }

    /// <summary>
    /// #558 moved the Cowork basis from audit heuristics to Microsoft's documented first-party report.
    /// RegularCoworkUser and the rationale text both honoured that; the tier function did not.
    /// </summary>
    [TestClass]
    public class CoworkTierSourceTests
    {
        private static CopilotAdoptionOptions Options()
        {
            var o = CopilotAdoptionOptions.Default;
            o.CoworkRegularMinActiveDays = 3;
            return o;
        }

        /// <summary>
        /// A user Microsoft's report shows as active on 10 days, with no audit signal at all, is
        /// established. Before the fix the tier read only the audit count, so this user was tiered
        /// "Trialling" and the rationale then printed the self-contradiction "on 10 active days ...
        /// short of the 3 needed to count as regular use".
        /// </summary>
        [TestMethod]
        public void ReportOnlyUser_AboveTheRegularBar_IsEstablished()
        {
            var row = new CoworkReadinessRow
            {
                CoworkActiveDays = 0,
                CoworkInteractions = 0,
                CoworkReportActiveDays = 10,
                CoworkReportTotalTasks = 40,
                UsedCowork = true,
            };

            Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.Established,
                CopilotAdoptionScoring.CoworkTierFor(row, Options()),
                "The first-party report is the documented basis for this tab; ignoring it demotes a real user.");
        }

        /// <summary>
        /// The other direction - the audit fallback must survive. A user with no report row but a strong
        /// audit signal is still established, otherwise the fix would have traded one blind spot for another.
        /// </summary>
        [TestMethod]
        public void AuditOnlyUser_AboveTheRegularBar_IsStillEstablished()
        {
            var row = new CoworkReadinessRow
            {
                CoworkActiveDays = 7,
                CoworkInteractions = 25,
                CoworkReportActiveDays = null,
                UsedCowork = true,
            };

            Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.Established,
                CopilotAdoptionScoring.CoworkTierFor(row, Options()));
        }

        /// <summary>
        /// And the report must be able to say "not regular" too: a present-but-low report count is
        /// authoritative and must not fall back to a higher audit number.
        /// </summary>
        [TestMethod]
        public void ReportActiveDays_WhenPresent_TakePrecedenceOverAudit()
        {
            var row = new CoworkReadinessRow
            {
                CoworkActiveDays = 9,
                CoworkReportActiveDays = 1,
                UsedCowork = true,
            };

            Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.Trialling,
                CopilotAdoptionScoring.CoworkTierFor(row, Options()),
                "A present report value is the documented source and must win over the audit heuristic.");
        }

        /// <summary>
        /// Tiering on report active days while UsedCowork ignored them produced a row that was
        /// simultaneously "Established" and "has not used Cowork" - contradicting the tier, the
        /// "Used Cowork" CSV column and the workbook. Report active days are evidence of use.
        /// </summary>
        [TestMethod]
        public void ReportActiveDaysAlone_CountAsHavingUsedCowork()
        {
            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(
                new CoworkReadinessSignalRow
                {
                    UserId = 1,
                    CoworkActiveDays = 0,
                    CoworkInteractions = 0,
                    CoworkReportActiveDays = 12,
                    CoworkReportTotalTasks = null,
                },
                Options());

            Assert.AreEqual(CopilotAdoptionScoring.CoworkTiers.Established, scored.Tier);
            Assert.IsTrue(scored.UsedCowork,
                "A user the first-party report shows as active on 12 days has used Cowork, whatever the "
                + "task cell says - otherwise the tier and the Used Cowork column contradict each other.");
            Assert.IsTrue(scored.RegularCoworkUser);
        }

        /// <summary>
        /// The other direction: a user with no Cowork signal at all must still read as not having used it.
        /// </summary>
        [TestMethod]
        public void NoCoworkSignal_IsStillNotAUser()
        {
            var scored = CopilotAdoptionScoring.ScoreCoworkReadiness(
                new CoworkReadinessSignalRow { UserId = 2, CoworkActiveDays = 0, CoworkInteractions = 0 },
                Options());

            Assert.IsFalse(scored.UsedCowork);
            Assert.AreNotEqual(CopilotAdoptionScoring.CoworkTiers.Established, scored.Tier);
        }
    }

    /// <summary>
    /// Seat assignment dates arrive with #277. Until then the period publisher writes
    /// seat_first_observed_utc as NULL for every row, so the activation denominator is always zero.
    /// </summary>
    [TestClass]
    public class ActivationRateSuppressionTests
    {
        private static CopilotAdoptionCohortUserRow Row(string transition, string activationState, string department = "Sales")
        {
            return new CopilotAdoptionCohortUserRow
            {
                Transition = transition,
                ActivationState = activationState,
                Department = department,
            };
        }

        /// <summary>
        /// With no known seat date anywhere, the rate is not measurable. Publishing 0 asserted a failed
        /// onboarding programme - a number an admin would act on - where the honest answer is "unknown".
        /// </summary>
        [TestMethod]
        public void ActivationRate_IsNull_WhenNoSeatDateIsKnown()
        {
            var rows = new List<CopilotAdoptionCohortUserRow>
            {
                Row(CopilotAdoptionCohortTransitions.NewlyAssigned, "seatDateUnknown"),
                Row(CopilotAdoptionCohortTransitions.NewlyAssigned, "seatDateUnknown"),
            };

            var activation = CopilotAdoptionService.BuildActivationSummary(rows, 30);

            Assert.IsNull(activation.ActivationRatePct,
                "Unknown must not render as 0% - this is the production state until seat dates land.");
            Assert.AreEqual(2, activation.SeatDateUnknownUsers, "The reason must still be reported.");

            foreach (var segment in activation.ByDepartment)
            {
                Assert.IsNull(segment.ActivationRatePct,
                    "A department whose seats all have unknown dates must suppress its rate too.");
            }
        }

        /// <summary>
        /// The other direction: a real denominator must still produce a real rate, or the fix would have
        /// suppressed the feature rather than made it honest.
        /// </summary>
        [TestMethod]
        public void ActivationRate_IsStillCalculated_WhenSeatDatesAreKnown()
        {
            var rows = new List<CopilotAdoptionCohortUserRow>
            {
                Row(CopilotAdoptionCohortTransitions.NewlyAssigned, "activatedWithinWindow"),
                Row(CopilotAdoptionCohortTransitions.NewlyAssigned, "neverActivated"),
            };

            var activation = CopilotAdoptionService.BuildActivationSummary(rows, 30);

            Assert.AreEqual(2, activation.NewSeatsAssignedInPeriod);
            Assert.AreEqual(50d, activation.ActivationRatePct);
        }

        /// <summary>
        /// Departments whose rate is unknown must not be ranked as though they were worse than a measured
        /// 0%. LINQ orders nulls first by default, which would have put the unmeasurable department at the
        /// top of a list an admin reads top-down looking for problems.
        /// </summary>
        [TestMethod]
        public void DepartmentsWithAnUnknownRate_SortAfterMeasuredOnes()
        {
            var rows = new List<CopilotAdoptionCohortUserRow>
            {
                // Measured department: one new seat with a known date, never activated -> 0%.
                Row(CopilotAdoptionCohortTransitions.NewlyAssigned, "neverActivated", "Measured"),
                // Unknown department: same NeverActivatedUsers count, but no known seat date.
                Row(CopilotAdoptionCohortTransitions.NewlyAssigned, "seatDateUnknown", "Unknown"),
                Row("existing", "neverActivated", "Unknown"),
            };

            var activation = CopilotAdoptionService.BuildActivationSummary(rows, 30);

            var measured = activation.ByDepartment.Single(s => s.Segment == "Measured");
            var unknown = activation.ByDepartment.Single(s => s.Segment == "Unknown");

            Assert.AreEqual(0d, measured.ActivationRatePct);
            Assert.IsNull(unknown.ActivationRatePct);
            Assert.IsTrue(
                activation.ByDepartment.IndexOf(measured) < activation.ByDepartment.IndexOf(unknown),
                "A measured 0% is a worse result than an unmeasurable one and must rank first.");
        }
    }

    /// <summary>
    /// The team leaderboard aggregates dbo.teams_channel_stats_log, whose grain is one row per CHANNEL
    /// per date, but groups by team. Summing a per-row flag therefore counted channel-days: a team with
    /// ten busy channels reported 300 "active days" in a 30-day window.
    /// </summary>
    [TestClass]
    public class TeamsTeamActiveDaysSqlTests
    {
        [TestMethod]
        public void TeamLeaderboard_CountsDistinctDates_NotChannelDays()
        {
            var sql = Common.Entities.TeamsExplorer.TeamsExplorerSql.TeamLeaderboard;

            StringAssert.Contains(sql,
                "COUNT(DISTINCT CASE WHEN ISNULL(s.chats_count, 0) > 0 THEN s.[date] END) AS MessageDays",
                "Grouping per-channel-per-date rows by team must count distinct dates, or ActiveDays can "
                + "exceed the length of the reporting window.");

            Assert.IsFalse(
                Regex.IsMatch(sql, @"SUM\(CASE WHEN ISNULL\(s\.chats_count, 0\) > 0 THEN 1 ELSE 0 END\) AS MessageDays[\s\S]*GROUP BY ch\.team_id"),
                "The per-row flag sum must not be reintroduced on the team-level aggregation.");
        }
    }

    /// <summary>
    /// The manual DBA scripts form a strict prerequisite chain: each hard-fails if its predecessor is not
    /// stamped. One script named a migration two steps back, so a DBA who skipped the intervening
    /// migration would still have been allowed to stamp - leaving a hole in __MigrationHistory.
    /// This test covers every script at once, which is what the per-migration tests could not do.
    /// </summary>
    [TestClass]
    public class MigrationChainPredecessorTests
    {
        [TestMethod]
        public void EveryManualScript_NamesItsImmediatePredecessor()
        {
            var dir = MigrationsDirectory();

            var migrationIds = Directory.GetFiles(dir, "*.cs")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !n.EndsWith(".Designer", StringComparison.OrdinalIgnoreCase))
                .Where(n => Regex.IsMatch(n, @"^\d{15}_"))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            Assert.IsTrue(migrationIds.Count > 10, "Expected to discover the migration chain.");

            var checkedAny = false;
            var failures = new List<string>();

            for (var i = 1; i < migrationIds.Count; i++)
            {
                var id = migrationIds[i];
                var manualPath = Path.Combine(dir, id + ".manual.sql");
                if (!File.Exists(manualPath)) continue;

                var expectedPredecessor = migrationIds[i - 1];
                var script = File.ReadAllText(manualPath);

                // Strip SQL comments first. The predecessor must be named in EXECUTABLE SQL - a script
                // whose guard points at the wrong migration while a comment happens to mention the right
                // one is exactly the defect this test exists to catch.
                var executable = Regex.Replace(script, @"/\*.*?\*/", " ", RegexOptions.Singleline);
                executable = Regex.Replace(executable, @"--[^\r\n]*", " ");

                // Scripts name their predecessor in different but equivalent ways - inline in the stamp's
                // WHERE clause, or bound to a @predecessor/@prev variable first. So assert on the set of
                // migration ids quoted as SQL literals rather than on one exact phrasing.
                var quotedIds = Regex.Matches(executable, @"N?'(\d{15}_\w+)'")
                    .Cast<Match>()
                    .Select(m => m.Groups[1].Value)
                    .Where(v => v != id)
                    .Distinct()
                    .ToList();

                if (!quotedIds.Contains(expectedPredecessor))
                {
                    failures.Add($"{id}: expected predecessor '{expectedPredecessor}', executable SQL references "
                                 + (quotedIds.Count == 0 ? "(none)" : string.Join(", ", quotedIds)));
                }

                checkedAny = true;
            }

            Assert.IsTrue(checkedAny, "No manual scripts were discovered to check.");
            Assert.AreEqual(0, failures.Count,
                "A manual script that names the wrong predecessor lets a DBA stamp a migration while "
                + "skipping the one before it, leaving a hole in __MigrationHistory:\r\n"
                + string.Join("\r\n", failures));
        }

        /// <summary>
        /// A severity-16 RAISERROR does not abort a batch, so a stamp that is not inside the schema check
        /// records a partial apply as complete - after which EF never retries it. The Cowork script emitted
        /// its checks as independent statements and then stamped unconditionally.
        /// </summary>
        [TestMethod]
        public void CoworkUsageReportTablesManualScript_GatesItsStampBehindTheSchemaCheck()
        {
            var script = File.ReadAllText(Path.Combine(MigrationsDirectory(),
                "202609170940001_CoworkUsageReportTables.manual.sql"));

            var stampIndex = script.IndexOf("INSERT dbo.__MigrationHistory", StringComparison.OrdinalIgnoreCase);
            Assert.IsTrue(stampIndex > 0, "The script must stamp __MigrationHistory.");

            var preamble = script.Substring(0, stampIndex);

            StringAssert.Contains(preamble,
                "IF OBJECT_ID(N'dbo.cowork_usage_user_activity_log', N'U') IS NOT NULL",
                "The stamp must sit inside a positive schema check, not after a bare RAISERROR.");

            StringAssert.Contains(preamble, "IX_cowork_usage_user_activity_log_date_user_period",
                "The index the migration creates must also be proven present before stamping.");

            // The "already applied" path must still reach a stamp decision rather than returning early.
            StringAssert.Contains(script, "already recorded in __MigrationHistory",
                "An already-applied database must fall through to the no-op branch, not be treated as a failure.");

            // Schema-only: the guard must not inspect row state.
            Assert.IsFalse(Regex.IsMatch(preamble, @"(?i)SELECT\s+COUNT\s*\(|IS\s+NULL\s*\)\s*>\s*0"),
                "Guards must check schema only - a data-state guard cannot be satisfied reliably when the "
                + "importer is running during an upgrade.");
        }

        private static string MigrationsDirectory()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Common", "Entities", "Migrations");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            Assert.Fail("Could not locate Common\\Entities\\Migrations above the test assembly.");
            return null;
        }
    }
}
