using DataUtils.Health;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the overall system-health roll-up (the traffic-light shown on the in-app Health page).
    /// Pure logic - no database / App Insights. See HEALTH-MONITORING-DESIGN.md (#144).
    /// </summary>
    [TestClass]
    public class HealthRollupTests
    {
        private static readonly DateTime Now = new DateTime(2026, 07, 05, 12, 00, 00, DateTimeKind.Utc);

        /// <summary>A baseline "everything fine" input: schema current, one job that just completed a cycle.</summary>
        private static HealthRollupInput HealthyInput() => new HealthRollupInput
        {
            NowUtc = Now,
            SchemaUpToDate = true,
            AppInsightsConfigured = true,
            Jobs = new List<JobLivenessInput>
            {
                new JobLivenessInput { JobName = "Office365ActivityImporter", LastCycleUtc = Now.AddMinutes(-30) }
            }
        };

        [TestMethod]
        public void AllGood_IsHealthy()
        {
            var status = HealthRollup.Evaluate(HealthyInput(), out var reasons);
            Assert.AreEqual(HealthStatus.Healthy, status);
            CollectionAssert.Contains(reasons, "All checks passing.");
        }

        [TestMethod]
        public void DataError_IsUnhealthy()
        {
            var input = HealthyInput();
            input.DataError = "boom";
            var status = HealthRollup.Evaluate(input, out _);
            Assert.AreEqual(HealthStatus.Unhealthy, status);
        }

        [TestMethod]
        public void SchemaBehind_IsUnhealthy()
        {
            var input = HealthyInput();
            input.SchemaUpToDate = false;
            input.PendingMigrationCount = 3;
            var status = HealthRollup.Evaluate(input, out var reasons);
            Assert.AreEqual(HealthStatus.Unhealthy, status);
            StringAssert.Contains(string.Join("|", reasons), "schema is behind");
        }

        [TestMethod]
        public void ComponentDegraded_IsDegraded()
        {
            var input = HealthyInput();
            input.Components.Add(new ComponentStatusInput { Component = "Credential", Status = "Degraded", Detail = "expiring soon" });
            var status = HealthRollup.Evaluate(input, out _);
            Assert.AreEqual(HealthStatus.Degraded, status);
        }

        [TestMethod]
        public void ComponentUnhealthy_IsUnhealthy_AndBeatsDegraded()
        {
            var input = HealthyInput();
            input.Components.Add(new ComponentStatusInput { Component = "ServiceBus", Status = "Degraded", Detail = "dead-letter" });
            input.Components.Add(new ComponentStatusInput { Component = "Credential", Status = "Unhealthy", Detail = "expired" });
            var status = HealthRollup.Evaluate(input, out _);
            Assert.AreEqual(HealthStatus.Unhealthy, status);
        }

        [TestMethod]
        public void CycleOlderThanSla_IsDegraded()
        {
            var input = HealthyInput();
            input.Jobs[0].LastCycleUtc = Now.AddHours(-30); // > 24h SLA, < 48h
            var status = HealthRollup.Evaluate(input, out _);
            Assert.AreEqual(HealthStatus.Degraded, status);
        }

        [TestMethod]
        public void CycleOlderThanTwiceSla_IsUnhealthy()
        {
            var input = HealthyInput();
            input.Jobs[0].LastCycleUtc = Now.AddHours(-50); // > 48h
            var status = HealthRollup.Evaluate(input, out _);
            Assert.AreEqual(HealthStatus.Unhealthy, status);
        }

        [TestMethod]
        public void NoCycleSeen_IsDegraded()
        {
            var input = HealthyInput();
            input.Jobs[0].LastCycleUtc = null;
            var status = HealthRollup.Evaluate(input, out _);
            Assert.AreEqual(HealthStatus.Degraded, status);
        }

        [TestMethod]
        public void SqlCapacityExceptions_IsDegraded()
        {
            var input = HealthyInput();
            input.SqlCapacityExceptions24h = 5;
            var status = HealthRollup.Evaluate(input, out _);
            Assert.AreEqual(HealthStatus.Degraded, status);
        }

        [TestMethod]
        public void WebhookMissing_OnlyMattersWhenCallsEnabled()
        {
            var offInput = HealthyInput();
            offInput.CallsImportEnabled = false;
            offInput.WebhookState = "Missing";
            Assert.AreEqual(HealthStatus.Healthy, HealthRollup.Evaluate(offInput, out _));

            var onInput = HealthyInput();
            onInput.CallsImportEnabled = true;
            onInput.WebhookState = "Missing";
            Assert.AreEqual(HealthStatus.Degraded, HealthRollup.Evaluate(onInput, out _));
        }

        [TestMethod]
        public void TelemetryQueryError_DowngradesOnlyWhenAppInsightsConfigured()
        {
            var notConfigured = HealthyInput();
            notConfigured.AppInsightsConfigured = false;
            notConfigured.AnyTelemetryQueryError = true;
            Assert.AreEqual(HealthStatus.Healthy, HealthRollup.Evaluate(notConfigured, out _));

            var configured = HealthyInput();
            configured.AppInsightsConfigured = true;
            configured.AnyTelemetryQueryError = true;
            Assert.AreEqual(HealthStatus.Degraded, HealthRollup.Evaluate(configured, out _));
        }
    }
}
