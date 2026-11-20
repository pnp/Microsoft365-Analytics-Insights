using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;

namespace Tests.UnitTests
{
    [TestClass]
    public class CopilotAdoptionEvidenceTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

        [DataTestMethod]
        [DataRow(0, 0, "review")]
        [DataRow(1, 0, "review")]
        [DataRow(2, 5, "review")]
        [DataRow(0, 30, "probable")]
        [DataRow(1, 120, "probable")]
        public void HoldingAge_NotAccountAge_DecidesGrace(int source, int age, string expectedTier)
        {
            var options = CopilotAdoptionOptions.Default;
            var row = CopilotAdoptionScoring.Score(new LicensedUserUsageRow
            {
                UserId = 1, AccountCreatedUtc = Now.AddDays(-1000), AccountEnabled = true,
                HoldsSeatToday = true, SeatObservedFromUtc = Now.AddDays(-age),
                SeatObservationSource = (byte)source
            }, CopilotAdoptionScoring.WindowStartUtc(Now, options.WindowDays), Now, true, options);
            Assert.AreEqual(expectedTier, row.ReclaimEligibility);
            Assert.AreEqual(age < options.ReclaimGraceDays, row.TooNewToJudge);
            Assert.AreEqual(age, row.DaysSinceTenureStart);
            Assert.AreEqual((byte)source, row.SeatObservationSource);
            Assert.AreEqual("seatObservedLowerBound", row.TenureBasis);
            Assert.AreEqual(expectedTier == "probable" ? 1 : 0, Summarise(options, row).Summary.ReclaimableSeats);
        }

        [TestMethod]
        public void RecentInitialHolding_DoesNotInventAnAssignmentDateForFrequency()
        {
            var row = CopilotAdoptionScoring.Score(new LicensedUserUsageRow
            {
                AccountCreatedUtc = Now.AddDays(-1000), AccountEnabled = true,
                SeatObservedFromUtc = Now, SeatObservationSource = 0,
                ActiveDays = 15, Interactions = 90, AppsUsed = 4, LastInteractionUtc = Now
            }, CopilotAdoptionScoring.WindowStartUtc(Now, 28), Now, true);
            Assert.AreEqual(12d, row.ExpectedActiveDays);
            Assert.AreEqual(AdoptionBand.Champion, row.Band);
            Assert.AreEqual("advocate", row.RecommendedActionCode);
            Assert.AreEqual("", row.ReclaimEligibility);
        }

        [DataTestMethod]
        [DataRow(true, false, "certain")]
        [DataRow(false, true, "excluded")]
        public void UnknownTenure_PreservesDisabledAndAdministratorDecisions(bool disabled, bool excluded, string expectedTier)
        {
            var row = CopilotAdoptionScoring.Score(new LicensedUserUsageRow
            {
                AccountEnabled = !disabled, ReclaimExclusionReason = excluded ? "Leave" : null
            }, CopilotAdoptionScoring.WindowStartUtc(Now, 28), Now, true);
            Assert.AreEqual(expectedTier, row.ReclaimEligibility);
        }

        [DataTestMethod]
        [DataRow(7, -2, false)]
        [DataRow(3, -2, false)]
        [DataRow(7, 0, true)]
        public void InWindowOrUndatedReport_RetainsExplicitReportNativeFallback(int windowDays, int daysAgo, bool unknownDate)
        {
            var options = new CopilotAdoptionOptions { WindowDays = windowDays };
            var row = CopilotAdoptionScoring.Score(new LicensedUserUsageRow
            {
                ReportActiveDays = 10, ReportPrompts = 60,
                ReportLastActivityUtc = unknownDate ? (DateTime?)null : Now.AddDays(daysAgo)
            }, CopilotAdoptionScoring.WindowStartUtc(Now, windowDays), Now, true, options);
            Assert.AreEqual(10, row.ActiveDays, "Native D28 counters are not clipped to fabricate a D7 distribution.");
            Assert.AreEqual(CopilotAdoptionScoring.SignalSourceUsageReport, row.SignalSource);
            Assert.AreNotEqual(AdoptionBand.NeverUsed, row.Band);
            Assert.AreEqual("", row.ReclaimEligibility);
            Assert.IsTrue(Summarise(options, row).Summary.UsageReportWindowMismatch);
        }

        [TestMethod]
        public void StaleReport_DoesNotOverrideCurrentAuditActivity()
        {
            var row = CopilotAdoptionScoring.Score(new LicensedUserUsageRow
            {
                ActiveDays = 2, Interactions = 4, AppsUsed = 1, LastInteractionUtc = Now,
                ReportActiveDays = 10, ReportPrompts = 60, ReportLastActivityUtc = Now.AddDays(-15)
            }, Now.Date.AddDays(-6), Now, true, new CopilotAdoptionOptions { WindowDays = 7 });
            Assert.AreEqual(CopilotAdoptionScoring.SignalSourceAudit, row.SignalSource);
            Assert.AreEqual(2, row.ActiveDays);
            Assert.AreEqual(4L, row.Interactions);
            Assert.IsTrue(row.Band > AdoptionBand.Dormant);
        }

        [TestMethod]
        public void OldAccountWithoutHoldingEvidence_IsReviewNotProbable()
        {
            var options = CopilotAdoptionOptions.Default;
            var row = CopilotAdoptionScoring.Score(new LicensedUserUsageRow
            {
                UserId = 1, AccountCreatedUtc = Now.AddDays(-1000), AccountEnabled = true,
                HoldsSeatToday = true
            }, CopilotAdoptionScoring.WindowStartUtc(Now, options.WindowDays), Now, true, options);
            var analysis = Summarise(options, row);

            Assert.AreEqual("review", row.ReclaimEligibility);
            Assert.AreEqual("unknown", row.TenureBasis);
            Assert.AreEqual("review", row.RecommendedActionCode);
            Assert.AreEqual(0, analysis.Summary.ReclaimableSeats);
        }

        [DataTestMethod]
        [DataRow(-1, 0)]
        [DataRow(120, 99)]
        [DataRow(120, -1)]
        public void InvalidHoldingEvidence_StaysUnknown(int age, int source)
        {
            var row = CopilotAdoptionScoring.Score(new LicensedUserUsageRow
            {
                AccountEnabled = true, AccountCreatedUtc = Now.AddDays(-1000),
                SeatObservedFromUtc = Now.AddDays(-age),
                SeatObservationSource = source < 0 ? (byte?)null : (byte)source
            }, Now.AddDays(-27), Now, true);
            Assert.AreEqual("unknown", row.TenureBasis);
            Assert.IsNull(row.TenureStartUtc);
            Assert.IsNull(row.DaysSinceTenureStart);
            Assert.AreEqual("review", row.ReclaimEligibility);
        }

        [DataTestMethod]
        [DataRow(7, true)]
        [DataRow(3, false)]
        public void StaleReportCounters_DoNotCreateCurrentActivity(int windowDays, bool auditAvailable)
        {
            var options = new CopilotAdoptionOptions { WindowDays = windowDays, UsesExplicitDates = windowDays == 3 };
            var row = CopilotAdoptionScoring.Score(new LicensedUserUsageRow
            {
                UserId = 1, AccountCreatedUtc = Now.AddDays(-1000), AccountEnabled = true,
                PriorInteractions = 5, Department = "Contoso",
                ReportActiveDays = 10, ReportPrompts = 60, ReportAppsUsed = 0,
                ReportLastActivityUtc = Now.AddDays(-15)
            }, CopilotAdoptionScoring.WindowStartUtc(Now, windowDays), Now, auditAvailable, options);
            var analysis = Summarise(options, row);

            Assert.AreEqual(AdoptionBand.Dormant, row.Band);
            Assert.AreEqual(0, row.ActiveDays);
            Assert.AreEqual(0L, row.Interactions);
            Assert.AreEqual(10, row.ReportActiveDays, "Keep report-native evidence.");
            Assert.AreEqual(60, row.ReportPrompts);
            Assert.AreEqual(0, analysis.Summary.ActiveUsers);
            Assert.AreEqual(0, analysis.Summary.HabitualUsers);
            Assert.AreEqual(1, analysis.Summary.DormantUsers);
            Assert.AreEqual(0d, analysis.Summary.AdoptionRatePct);
            Assert.AreNotEqual("advocate", row.RecommendedActionCode);
            Assert.IsTrue(analysis.Summary.UsageReportWindowMismatch);
            Assert.AreEqual(1, analysis.Summary.Funnel.Single(f => f.Label == "Ever used Copilot").Value);
            Assert.AreEqual(0, analysis.Summary.Funnel.Single(f => f.Label == "Active this period").Value);
            Assert.AreEqual(0, analysis.Summary.Funnel.Single(f => f.Label == "Champions").Value);
            Assert.AreEqual(0, analysis.Summary.HabitBuckets.Sum(b => b.Users));

            var department = Summarise(options, Enumerable.Range(1, 5).Select(id =>
            {
                var member = CopilotAdoptionScoring.Score(new LicensedUserUsageRow
                {
                    UserId = id, Department = "Contoso", ReportActiveDays = 10, ReportPrompts = 60,
                    ReportLastActivityUtc = Now.AddDays(-15)
                }, CopilotAdoptionScoring.WindowStartUtc(Now, windowDays), Now, auditAvailable, options);
                return member;
            }).ToArray()).Summary.AdoptionByDepartment.Single();
            Assert.AreEqual(0, department.ActiveUsers);
            Assert.AreEqual(0d, department.AdoptionRatePct);
        }

        private static CopilotAdoptionAnalysis Summarise(CopilotAdoptionOptions options, params LicensedUserAdoptionRow[] rows)
        {
            var analysis = new CopilotAdoptionAnalysis { LicensedUsers = rows.ToList() };
            analysis.Summary.WindowDays = options.WindowDays;
            analysis.Summary.Options = options;
            analysis.Summary.LicensedUsers = rows.Length;
            analysis.Summary.DataSources.CopilotUsageReportPeriodDays = 28;
            new CopilotAdoptionService(options).FinaliseSummary(analysis);
            return analysis;
        }
    }
}
