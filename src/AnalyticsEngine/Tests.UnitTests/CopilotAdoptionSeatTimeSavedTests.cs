using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;

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
            StringAssert.Contains(sql, "meeting_id IS NULL AND app_host IN ('outlook')");
            StringAssert.Contains(sql, "meeting_id IS NULL AND app_host IN ('word','powerpoint','excel')");
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
                CoworkValueEstimate = CopilotAdoptionScoring.ModelCoworkValue(1, CopilotAdoptionOptions.Default, new CoworkTaskInputs { ActivityVolumes = { [CoworkActivities.SendEmail] = 200 } }),
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
            analysis.SeatHolderTimeSavedAssessed = true;

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

        [TestMethod]
        public void Summary_WithholdsSeatHolderEstimateWhenAuditDataWasUnavailable()
        {
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Summary.WindowDays = 28;
            analysis.Summary.Options = CopilotAdoptionOptions.Default;
            analysis.Summary.DataSources.AuditAvailable = false;
            analysis.LicensedUsers.Add(User(1, "Established", "Finance"));

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(0, analysis.Summary.SeatHolderTimeSavedEstimate.CohortUsers);
            Assert.AreEqual(0d, analysis.Summary.SeatHolderTimeSavedEstimate.HoursPerMonthHigh);
            Assert.IsFalse(analysis.Summary.FiguresIncomplete,
                "A tenant without audit data has no realised-value estimate, but that absence is already explained by data-source warnings.");
        }

        [TestMethod]
        public async Task QueryFailure_WithholdsSeatHolderEstimate_AndMarksFiguresIncomplete()
        {
            var service = new CopilotAdoptionService(contextFactory: new ThrowingAnalyticsDbContextFactory());
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Summary.WindowDays = 28;
            analysis.Summary.Options = CopilotAdoptionOptions.Default;
            analysis.Summary.DataSources.AuditAvailable = true;
            analysis.LicensedUsers.Add(User(1, "Established", "Finance"));

            var output = CreateStepOutput();
            await InvokeBuildSeatHolderTimeSavedAsync(service, analysis, output, new List<int> { 1 });
            MergeStepOutput(analysis, output);
            analysis.Summary.Diagnostics.Record(CopilotAdoptionSteps.SeatHolderTimeSaved, 0, StepOutputQueryFailed(output));

            service.FinaliseSummary(analysis);

            Assert.AreEqual(0, analysis.Summary.SeatHolderTimeSavedEstimate.CohortUsers);
            Assert.IsTrue(analysis.Summary.FiguresIncomplete);
            CollectionAssert.Contains(analysis.Summary.IncompleteReasons.ToArray(), "seat-holder Copilot time-saved inputs");
            Assert.IsTrue(analysis.Summary.Diagnostics.Steps.Single(s => s.Step == CopilotAdoptionSteps.SeatHolderTimeSaved).Failed);
        }

        [TestMethod]
        public void Summary_PublishesZeroWhenSeatHolderQuerySucceededWithNoActions()
        {
            var analysis = new CopilotAdoptionAnalysis();
            analysis.Summary.WindowDays = 28;
            analysis.Summary.Options = CopilotAdoptionOptions.Default;
            analysis.Summary.DataSources.AuditAvailable = true;
            analysis.SeatHolderTimeSavedAssessed = true;
            analysis.LicensedUsers.Add(User(1, "Established", "Finance"));
            analysis.LicensedUsers.Add(User(2, "Established", "Finance"));

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.AreEqual(2, analysis.Summary.SeatHolderTimeSavedEstimate.CohortUsers);
            Assert.AreEqual(0d, analysis.Summary.SeatHolderTimeSavedEstimate.HoursPerMonthHigh);
            Assert.IsFalse(analysis.Summary.FiguresIncomplete);
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

        private static object CreateStepOutput()
        {
            var type = typeof(CopilotAdoptionService).GetNestedType("StepOutput", BindingFlags.NonPublic);
            return Activator.CreateInstance(type);
        }

        private static async Task InvokeBuildSeatHolderTimeSavedAsync(
            CopilotAdoptionService service,
            CopilotAdoptionAnalysis analysis,
            object output,
            List<int> seatIds)
        {
            var method = typeof(CopilotAdoptionService).GetMethod("BuildSeatHolderTimeSavedAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            var task = (Task)method.Invoke(service, new object[]
            {
                analysis,
                output,
                seatIds,
                new DateTime(2025, 12, 1),
                new DateTime(2026, 1, 1),
                false,
                null,
                CancellationToken.None,
            });
            await task.ConfigureAwait(false);
        }

        private static void MergeStepOutput(CopilotAdoptionAnalysis analysis, object output)
        {
            foreach (var reason in StepOutputList<string>(output, "IncompleteReasons"))
            {
                analysis.Summary.MarkFiguresIncomplete(reason);
            }
        }

        private static bool StepOutputQueryFailed(object output)
        {
            return (bool)output.GetType().GetProperty("QueryFailed", BindingFlags.Instance | BindingFlags.Public).GetValue(output);
        }

        private static List<T> StepOutputList<T>(object output, string property)
        {
            return (List<T>)output.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public).GetValue(output);
        }
    }
}
