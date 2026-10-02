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
    }
}
