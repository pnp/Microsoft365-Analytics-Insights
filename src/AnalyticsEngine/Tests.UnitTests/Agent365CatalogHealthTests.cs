extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.Health;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;

namespace Tests.UnitTests
{
    [TestClass]
    public class Agent365CatalogHealthTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void Agent365ImportNotStarted_DegradesHealthWhenEnabled()
        {
            var section = HealthDataSectionRules.BuildDataSection(
                new DatabaseCountsResult(),
                new RecentVolumeResult(),
                new RecentVolumeResult(),
                Now,
                agent365CatalogEnabled: true);

            Assert.AreEqual(HealthStatusNames.Degraded, section.Status);
            Assert.AreEqual("notStarted", section.Agent365CatalogIssue);
            CollectionAssert.Contains(section.Reasons, "agent365Catalog:notStarted");
        }

        [TestMethod]
        public void Agent365ImportFailure_ExposesSafeErrorAndDegradesHealth()
        {
            var section = HealthDataSectionRules.BuildDataSection(
                new DatabaseCountsResult
                {
                    Agent365CatalogImportHealth = new Common.Entities.Agent365.Agent365CatalogImportHealth
                    {
                        LastAttemptUtc = Now.AddHours(-1),
                        LastAttemptCompletedUtc = Now,
                        LastAttemptSucceeded = false,
                        LastAttemptError = "Graph returned HTTP 403.",
                    }
                },
                new RecentVolumeResult(),
                new RecentVolumeResult(),
                Now,
                agent365CatalogEnabled: true);

            Assert.AreEqual(HealthStatusNames.Degraded, section.Status);
            Assert.AreEqual("failed", section.Agent365CatalogIssue);
            Assert.AreEqual("Graph returned HTTP 403.", section.Agent365CatalogError);
        }

        [TestMethod]
        public void Agent365ImportRunning_IsVisibleWithoutDegradingHealth()
        {
            var section = HealthDataSectionRules.BuildDataSection(
                new DatabaseCountsResult
                {
                    Agent365CatalogImportHealth = new Common.Entities.Agent365.Agent365CatalogImportHealth
                    {
                        LastAttemptUtc = Now.AddMinutes(-1),
                        LastAttemptSucceeded = false,
                    }
                },
                new RecentVolumeResult(),
                new RecentVolumeResult(),
                Now,
                agent365CatalogEnabled: true);

            Assert.AreEqual(HealthStatusNames.Healthy, section.Status);
            Assert.AreEqual("running", section.Agent365CatalogIssue);
            Assert.AreEqual(0, section.Reasons.Count, "A running import is not a failure or an all-clear.");
        }

        [TestMethod]
        public void Agent365StatusReadFailure_DegradesHealthWithTheDatabaseError()
        {
            var section = HealthDataSectionRules.BuildDataSection(
                new DatabaseCountsResult { Agent365CatalogStatusError = "Invalid object name." },
                new RecentVolumeResult(),
                new RecentVolumeResult(),
                Now,
                agent365CatalogEnabled: true);

            Assert.AreEqual(HealthStatusNames.Degraded, section.Status);
            Assert.AreEqual("statusUnavailable", section.Agent365CatalogIssue);
            Assert.AreEqual("Invalid object name.", section.Agent365CatalogError);
        }

        [TestMethod]
        public void Agent365ImportSuccess_ReportsOnlyExplicitlyKnownNeverUsedPackages()
        {
            var section = HealthDataSectionRules.BuildDataSection(
                new DatabaseCountsResult
                {
                    Agent365CatalogImportHealth = new Common.Entities.Agent365.Agent365CatalogImportHealth
                    {
                        LastAttemptUtc = Now.AddMinutes(-2),
                        LastAttemptCompletedUtc = Now.AddMinutes(-1),
                        LastAttemptSucceeded = true,
                        LastSuccessfulImportUtc = Now.AddMinutes(-1),
                        PackageCount = 7,
                        NeverUsedCount = 2,
                    }
                },
                new RecentVolumeResult(),
                new RecentVolumeResult(),
                Now,
                agent365CatalogEnabled: true);

            Assert.AreEqual(HealthStatusNames.Healthy, section.Status);
            Assert.IsNull(section.Agent365CatalogIssue);
            Assert.AreEqual(7, section.Agent365CatalogPackageCount);
            Assert.AreEqual(2, section.Agent365CatalogNeverUsedCount);
        }

        [TestMethod]
        public async Task HealthDataLoad_QueriesAgent365StatusOnlyWhenTheImportIsEnabled()
        {
            var source = new FakeHealthDataSource();
            var service = new HealthService(source, new InMemoryHealthCache());

            await service.LoadDataAsync(agent365CatalogEnabled: true);

            Assert.IsTrue(source.IncludeAgent365CatalogHealthRequested);
        }
    }
}
