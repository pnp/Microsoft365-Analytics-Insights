extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models.Health;
using Common.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the home page's import-aware figures (api/SystemStatus).
    ///
    /// The point of the gating is that a permanent "Teams calls: 0" on a tenant that never switched the
    /// calls import on is indistinguishable from a broken import - and it is the first thing an admin
    /// sees. It is also a performance decision: a figure that is not shown is never counted, so a
    /// deployment with the web-traffic import off no longer pays for a COUNT(*) over <c>hits</c> on
    /// every home page load.
    /// </summary>
    [TestClass]
    public class SystemStatusOverviewTests
    {
        private static ImportTaskSettings NothingEnabled() => new ImportTaskSettings();

        [TestMethod]
        public void ApplicableCountKeys_NothingEnabled_KeepsOnlyTheImportAgnosticFigures()
        {
            var keys = SystemStatusAPIController.ApplicableCountKeys(NothingEnabled());

            // Users are filled by every import, so the figure is meaningful whatever is switched on.
            CollectionAssert.AreEqual(new List<string> { "users" }, keys);
        }

        [TestMethod]
        public void ApplicableCountKeys_UnknownSettings_ShowsEverything()
        {
            // No saved config yet: showing every figure beats showing an empty home page, and it is the
            // behaviour the page reports to the user ("import settings couldn't be read").
            var keys = SystemStatusAPIController.ApplicableCountKeys(null);

            Assert.IsTrue(keys.Contains("teamsCalls"));
            Assert.IsTrue(keys.Contains("webHits"));
            Assert.IsTrue(keys.Contains("dlpMatches"));
        }

        [TestMethod]
        public void ApplicableCountKeys_ActivityLogOnly_DoesNotOfferWebTrafficFigures()
        {
            var keys = SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { ActivityLog = true });

            CollectionAssert.AreEqual(new List<string> { "users", "auditEvents", "sharePointSites" }, keys);
        }

        [TestMethod]
        public void ApplicableCountKeys_WebTrafficOnly_AlsoOffersSharePointSites()
        {
            // sites are populated by either the audit feed or the web tracker, so either switch is enough.
            var keys = SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { WebTraffic = true });

            CollectionAssert.AreEqual(new List<string> { "users", "webHits", "trackedUrls", "sharePointSites" }, keys);
        }

        [TestMethod]
        public void ApplicableCountKeys_EachImportUnlocksItsOwnFigure()
        {
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { Copilot = true }).Contains("copilotInteractions"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { CopilotInteractionHistory = true }).Contains("copilotAiInteractions"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { SentEmails = true }).Contains("sentEmails"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { GraphTeams = true }).Contains("teams"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { GraphTeams = true }).Contains("teamsTracked"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { Calls = true }).Contains("teamsCalls"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { ImportPowerPlatform = true }).Contains("powerApps"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { ImportDlp = true }).Contains("dlpMatches"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { GraphUsersMetadata = true }).Contains("licenceTypes"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { CopilotStudioCredits = true }).Contains("copilotStudioCreditDays"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { AzureCostManagement = true }).Contains("azureCostDays"));
        }

        [TestMethod]
        public void ApplicableCountKeys_AreUnique()
        {
            // The SPA keys its icon map - and the server keys its per-shape cache entry - off these, so a
            // duplicate would silently collide.
            var keys = SystemStatusAPIController.ApplicableCountKeys(null);

            Assert.AreEqual(keys.Count, keys.Distinct().Count());
        }

        [TestMethod]
        public void DescribeEnabledImports_NullSettings_IsEmptyRatherThanThrowing()
        {
            CollectionAssert.AreEqual(new List<string>(), HealthService.DescribeEnabledImports(null));
        }

        [TestMethod]
        public void DescribeEnabledImports_ReportsOnlyTheSwitchedOnImports()
        {
            var labels = HealthService.DescribeEnabledImports(new ImportTaskSettings { ActivityLog = true, Copilot = true });

            CollectionAssert.AreEqual(new List<string> { "Activity/audit", "Copilot" }, labels);
        }

        [TestMethod]
        public void DescribeEnabledImports_UsesTheSameLabelsAsTheHealthPage()
        {
            // The home page and Health -> Configuration must never disagree about what is switched on, so
            // they share one map. This guards the sharing, not the wording.
            var all = new ImportTaskSettings
            {
                ActivityLog = true,
                Copilot = true,
                WebTraffic = true,
                Calls = true,
            };

            var labels = HealthService.DescribeEnabledImports(all);

            foreach (var label in labels)
            {
                Assert.IsTrue(
                    HealthService.ImportLabelsBySettingProperty.Values.Contains(label),
                    $"'{label}' is not one of the shared import labels");
            }
        }
    }
}
