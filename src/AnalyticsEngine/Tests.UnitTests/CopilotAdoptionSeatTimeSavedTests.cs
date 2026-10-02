using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;

namespace Tests.UnitTests
{
    [TestClass]
    public class CopilotAdoptionSeatTimeSavedTests
    {
        [TestMethod]
        public void Estimate_MatchesThePublishedActionCredits()
        {
            var options = CopilotAdoptionOptions.Default.Clone();
            options.CopilotSeatOutlookMinutesPerAction = 6;
            options.CopilotSeatOfficeMinutesPerAction = 6;
            options.CopilotSeatMeetingMinutesPerAction = 30;
            options.CopilotSeatUncreditedMinutesPerAction = 0;
            options.CoworkEstimateLowerBoundRatio = 0.5;

            var estimate = CopilotAdoptionScoring.ModelSeatHolderTimeSaved(10, 2, 100, 50, 4, 999, options);

            // 100*6 + 50*6 + 4*30 + 999*0 = 1,020 minutes = 17 hours; low end 8.5 -> 9.
            Assert.AreEqual(17d, estimate.HoursPerMonthHigh);
            Assert.AreEqual(9d, estimate.HoursPerMonthLow);
            Assert.AreEqual(999d, estimate.ObservedUncreditedActions, "Uncredited actions are counted and visible, even when their default credit is zero.");
            Assert.AreEqual(2, estimate.ExcludedUsageReportSourcedUsers);
        }

        [TestMethod]
        public void Estimate_NeverProducesAMonetaryFigure()
        {
            var estimate = CopilotAdoptionScoring.ModelSeatHolderTimeSaved(10, 2, 100, 50, 4, 999, CopilotAdoptionOptions.Default);
            var fields = JObject.FromObject(estimate).Properties().Select(p => p.Name).ToList();

            foreach (var banned in new[] { "currency", "cost", "price", "spend", "value" })
            {
                Assert.IsFalse(fields.Any(n => n.IndexOf(banned, StringComparison.OrdinalIgnoreCase) >= 0),
                    $"No monetary field may be serialised. Found: {string.Join(", ", fields)}");
            }

            Assert.IsTrue(estimate.Assumptions.Any(a => a.IndexOf("No monetary value is shown", StringComparison.Ordinal) >= 0));
        }

        [TestMethod]
        public void Estimate_ExcludesUsageReportOnlyUsersInItsAssumptions()
        {
            var estimate = CopilotAdoptionScoring.ModelSeatHolderTimeSaved(3, 7, 1, 1, 1, 1, CopilotAdoptionOptions.Default);

            Assert.IsTrue(estimate.Assumptions.Any(a => a.IndexOf("7 seat holders were scored from Microsoft's usage report", StringComparison.Ordinal) >= 0));
        }


        [TestMethod]
        public void Sql_UsesExactAppHostBuckets_AndMeetingPrecedence()
        {
            var sql = CopilotAdoptionSeatTimeSql.SeatHolderTimeSavedSql(new[] { 1 }, new[] { 99 });

            StringAssert.Contains(sql, "SELECT DISTINCT user_id, meeting_id FROM #seat_time_grain WHERE meeting_id IS NOT NULL");
            StringAssert.Contains(sql, "a.meeting_id IS NULL AND a.app_host IN ('outlook')");
            StringAssert.Contains(sql, "a.meeting_id IS NULL AND a.app_host IN ('word','powerpoint','excel')");
            StringAssert.Contains(sql, "LOWER(CAST(ISNULL(c.app_host, '') AS nvarchar(100))) AS app_host");
            Assert.IsFalse(sql.IndexOf("LIKE '%", StringComparison.OrdinalIgnoreCase) >= 0,
                "The realised-value query must use the same exact app-host buckets the usage-by-app chart reports, not new wildcard rules.");
        }

        [TestMethod]
        public void Summary_NeverSumsSeatHolderHoursWithLicenceOrCoworkEstimates()
        {
            var summary = new CopilotAdoptionSummary
            {
                LicenceOpportunityEstimate = CopilotAdoptionScoring.ModelLicenceValue(1, 60, 0, 0, CopilotAdoptionOptions.Default),
                CoworkValueEstimate = CopilotAdoptionScoring.ModelCoworkValue(1, CopilotAdoptionOptions.Default, new CoworkTaskInputs { ObservedUsers = 1, ObservedTasksPerMonth = 10 }),
                SeatHolderTimeSavedEstimate = CopilotAdoptionScoring.ModelSeatHolderTimeSaved(1, 0, 10, 0, 0, 0, CopilotAdoptionOptions.Default),
            };

            var fields = JObject.FromObject(summary).Properties().Select(p => p.Name).ToList();
            Assert.IsFalse(fields.Any(f => f.IndexOf("combined", StringComparison.OrdinalIgnoreCase) >= 0 && f.IndexOf("time", StringComparison.OrdinalIgnoreCase) >= 0),
                "The summary must not publish a combined time-saved figure across separate decisions.");
            Assert.AreNotEqual(
                summary.SeatHolderTimeSavedEstimate.HoursPerMonthHigh + summary.LicenceOpportunityEstimate.HoursPerMonthHigh + summary.CoworkValueEstimate.HoursPerMonthHigh,
                summary.SeatHolderTimeSavedEstimate.HoursPerMonthHigh,
                "Control: the estimates are distinct non-zero values, so a combined field would be detectable.");
        }

        [TestMethod]
        public void TimeSavedOverrides_ClampSeatHolderCredits()
        {
            var options = CopilotAdoptionOptions.Default.Clone();
            var overridden = new TimeSavedOverrides
            {
                SeatOutlookMinutesPerAction = 999,
                SeatOfficeMinutesPerAction = 999,
                SeatMeetingMinutesPerAction = 999,
                SeatUncreditedMinutesPerAction = 999,
            }.ApplyTo(options);

            Assert.AreEqual(TimeSavedOverrides.MaxMinutesPerEmail, overridden.CopilotSeatOutlookMinutesPerAction);
            Assert.AreEqual(TimeSavedOverrides.MaxMinutesPerItem, overridden.CopilotSeatOfficeMinutesPerAction);
            Assert.AreEqual(TimeSavedOverrides.MaxMinutesPerItem, overridden.CopilotSeatMeetingMinutesPerAction);
            Assert.AreEqual(TimeSavedOverrides.MaxMinutesPerTask, overridden.CopilotSeatUncreditedMinutesPerAction);
            Assert.AreEqual(6d, options.CopilotSeatOutlookMinutesPerAction, "Overrides must apply to a copy, not the cached options.");
        }


        [TestMethod]
        public void Summary_ExcludesReportSourcedUsers_AndSuppressesSmallDepartments()
        {
            var options = CopilotAdoptionOptions.Default.Clone();
            options.MinSeatsPerSegment = 2;
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Summary.WindowDays = 28;
            analysis.Summary.ToUtc = DateTime.UtcNow;
            analysis.Summary.FromUtc = DateTime.UtcNow.AddDays(-28);
            analysis.Summary.Options = options;
            analysis.Summary.DataSources.AuditAvailable = true;

            for (var i = 1; i <= 3; i++)
            {
                analysis.LicensedUsers.Add(User(i, "Established", "Finance"));
                analysis.SeatHolderTimeSavedRows.Add(new SeatHolderTimeSavedUserRow { UserId = i, Department = "Finance", OutlookActions = 10 });
            }
            analysis.LicensedUsers.Add(User(10, "Trialling", "Tiny"));
            analysis.SeatHolderTimeSavedRows.Add(new SeatHolderTimeSavedUserRow { UserId = 10, Department = "Tiny", OutlookActions = 10 });
            analysis.LicensedUsers.Add(User(20, "Established", "Finance", CopilotAdoptionScoring.SignalSourceUsageReport));
            analysis.LicensedUsers.Add(User(21, "Established", "Finance", CopilotAdoptionScoring.SignalSourceUsageReport));

            new CopilotAdoptionService(options).FinaliseSummary(analysis);

            var estimate = analysis.Summary.SeatHolderTimeSavedEstimate;
            Assert.AreEqual(4, estimate.CohortUsers, "Only audit-sourced seat holders can be modelled.");
            Assert.AreEqual(2, estimate.ExcludedUsageReportSourcedUsers);
            Assert.IsTrue(estimate.ByDepartment.Any(r => r.Segment == "Finance"));
            Assert.IsFalse(estimate.ByDepartment.Any(r => r.Segment == "Tiny"),
                "Departments below MinSeatsPerSegment must be suppressed from the aggregate estimate.");
        }

        private static LicensedUserAdoptionRow User(int id, string band, string department, string source = "audit")
        {
            return new LicensedUserAdoptionRow
            {
                UserId = id,
                UserPrincipalName = $"user{id}@contoso.com",
                Department = department,
                Interactions = source == "audit" ? 1 : 0,
                ActiveDays = source == "audit" ? 1 : 0,
                SignalSource = source,
                BandName = band,
                Band = AdoptionBand.Established,
            };
        }
    }
}

