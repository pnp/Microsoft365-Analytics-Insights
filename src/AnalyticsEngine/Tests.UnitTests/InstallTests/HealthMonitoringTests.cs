using App.ControlPanel.Engine;
using Azure.Core;
using Azure.ResourceManager.Resources;
using CloudInstallEngine;
using CloudInstallEngine.Azure.InstallTasks;
using CloudInstallEngine.Models;
using Common.Entities.Installer;
using DataUtils;
using DataUtils.Health;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests.InstallTests
{
    [TestClass]
    public class HealthMonitoringTests
    {
        [TestMethod]
        public void Config_OldJsonOptsOut_NewWizardRequiresEmailOrExplicitOptOut()
        {
            var legacy = JsonConvert.DeserializeObject<SolutionInstallConfig>("{}");
            Assert.IsNotNull(legacy.HealthMonitoring);
            Assert.IsFalse(legacy.HealthMonitoring.Enabled);
            Assert.AreEqual(0, legacy.HealthMonitoring.ValidatInputAndGetErrors().Count);
            var fresh = SolutionInstallConfig.NewConfig();
            Assert.IsTrue(fresh.HealthMonitoring.Enabled);
            Assert.AreEqual(1, fresh.HealthMonitoring.ValidatInputAndGetErrors().Count);
            fresh.HealthMonitoring.NotificationEmail = "operator@contoso.example";
            Assert.AreEqual(0, fresh.HealthMonitoring.ValidatInputAndGetErrors().Count);
            var roundTrip = JsonConvert.DeserializeObject<SolutionInstallConfig>(JsonConvert.SerializeObject(fresh));
            Assert.IsTrue(roundTrip.HealthMonitoring.Enabled);
            Assert.AreEqual("operator@contoso.example", roundTrip.HealthMonitoring.NotificationEmail);
            fresh.HealthMonitoring.Enabled = false;
            fresh.HealthMonitoring.NotificationEmail = "";
            Assert.AreEqual(0, fresh.HealthMonitoring.ValidatInputAndGetErrors().Count);
        }

        [DataTestMethod]
        [DataRow("not an address", 15)]
        [DataRow("Operator <operator@contoso.example>", 15)]
        [DataRow("one@contoso.example;two@contoso.example", 15)]
        [DataRow("operator@contoso.example", 14)]
        [DataRow("operator@contoso.example", 1441)]
        public void Config_RejectsInvalidEnabledSettings(string email, int minutes)
        {
            var config = new HealthMonitoringConfig { Enabled = true, NotificationEmail = email, MissingHeartbeatMinutes = minutes };
            Assert.IsTrue(config.ValidatInputAndGetErrors().Count > 0);
        }

        [TestMethod]
        public void Heartbeat_DuringLongCycle_HasNoInventedCompletion_ThenDisposalStopsIt()
        {
            var channel = new RecordingChannel();
            using (var configuration = new TelemetryConfiguration
            {
                TelemetryChannel = channel,
                ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000"
            })
            {
                var logger = new AnalyticsLogger(new TelemetryClient(configuration), ImporterHeartbeat.ActivityJob);
                using (var heartbeat = new ImporterHeartbeat(logger, ImporterHeartbeat.ActivityJob, TimeSpan.FromMilliseconds(20)))
                {
                    Assert.IsTrue(SpinWait.SpinUntil(() => channel.Events.Count >= 3, TimeSpan.FromSeconds(5)));
                    Assert.IsTrue(channel.Events.All(e => !e.Properties.ContainsKey("LastCycleUtc")));
                    Assert.IsTrue(channel.Events.All(e => !e.Properties.ContainsKey("LastCycleDurationSeconds")));
                    heartbeat.CycleCompleted(TimeSpan.FromSeconds(123));
                    var completed = channel.Events.Last();
                    Assert.AreEqual(ImporterHeartbeat.ActivityJob, completed.Properties["JobName"]);
                    Assert.AreEqual("123", completed.Properties["LastCycleDurationSeconds"]);
                    Assert.IsTrue(DateTime.TryParse(completed.Properties["LastCycleUtc"], out _));
                }
                var count = channel.Events.Count;
                Thread.Sleep(100);
                Assert.AreEqual(count, channel.Events.Count);
            }
        }

        [TestMethod]
        public async Task Provisioning_CreatesOnlyExpectedRules_WithEmptyInputDetectionAndDependencies()
        {
            var task = new FakeTask(true, true, false);
            await task.ExecuteTaskReturnResult(null);
            var resources = (JArray)task.Template["resources"];
            Assert.AreEqual(2, resources.Count);
            var group = (JObject)resources[0];
            var rule = (JObject)resources[1];
            Assert.AreEqual("[parameters('notificationEmail')]", (string)group.SelectToken("properties.emailReceivers[0].emailAddress"));
            Assert.AreEqual("operator@contoso.example", (string)task.Parameters.SelectToken("notificationEmail.value"));
            Assert.AreEqual("PT30M", (string)rule.SelectToken("properties.overrideQueryTimeRange"));
            Assert.AreEqual("PT5M", (string)rule.SelectToken("properties.windowSize"));
            Assert.AreEqual(FakeTask.GroupId + "/providers/Microsoft.Insights/components/contoso-insights",
                (string)rule.SelectToken("properties.scopes[0]"));
            Assert.AreEqual((string)rule.SelectToken("dependsOn[0]"), (string)rule.SelectToken("properties.actions.actionGroups[0]"));
            var query = (string)rule.SelectToken("properties.criteria.allOf[0].query");
            StringAssert.Contains(query, "JobName) == 'Office365ActivityImporter'");
            StringAssert.Contains(query, "| summarize Heartbeats = count() | where Heartbeats == 0");
            Assert.IsFalse(query.Contains(" by "), "Grouping would lose the never-seen-job row.");
            Assert.AreEqual(0, (int)rule.SelectToken("properties.criteria.allOf[0].threshold"));
        }

        [TestMethod]
        public async Task Provisioning_PassesOperatorTagsAsLiteralParameterData_NotTemplateExpressions()
        {
            var tags = new Dictionary<string, string>
            {
                ["CostCenter"] = "[Contoso-Finance]",
                ["Prefix"] = "[Contoso",
                ["[LiteralKey]"] = "[[Nested]]",
                [HealthMonitoringInstallTask.OwnerTag.ToUpperInvariant()] = "not-the-deployment-owner"
            };
            var task = new FakeTask(true, true, true, tags);
            await task.ExecuteTaskReturnResult(null);
            var values = (JObject)task.Parameters["resourceTags"]["value"];
            Assert.AreEqual("[Contoso-Finance]", (string)values["CostCenter"]);
            Assert.AreEqual("[Contoso", (string)values["Prefix"]);
            Assert.AreEqual("[[Nested]]", (string)values["[LiteralKey]"]);
            Assert.AreEqual(HealthMonitoringInstallTask.OwnershipKey(FakeTask.GroupId, "contoso-web"),
                (string)values[HealthMonitoringInstallTask.OwnerTag]);
            Assert.AreEqual(4, values.Count, "An operator tag must not replace or duplicate the reserved ownership tag.");
            Assert.AreEqual("object", (string)task.Template["parameters"]["resourceTags"]["type"]);
            Assert.IsTrue(((JArray)task.Template["resources"])
                .All(r => (string)r["tags"] == "[parameters('resourceTags')]"));
            Assert.IsFalse(task.Template.ToString().Contains("Contoso-Finance"));
        }

        [TestMethod]
        public async Task Provisioning_UpdatesRecipientAndThreshold_AndDisablesDeselectedWorkload()
        {
            var task = new FakeTask(true, false, true);
            task.AddOwnedResources();
            await task.ExecuteTaskReturnResult(null);
            var rules = ((JArray)task.Template["resources"]).OfType<JObject>().Skip(1).ToArray();
            Assert.AreEqual(2, rules.Length);
            Assert.IsFalse((bool)rules[0]["properties"]["enabled"]);
            Assert.IsTrue((bool)rules[1]["properties"]["enabled"]);
            Assert.AreEqual(0, ((JArray)rules[0].SelectToken("properties.actions.actionGroups")).Count);
            StringAssert.Contains((string)rules[1].SelectToken("properties.criteria.allOf[0].query"), "AppInsightsImporter");
            Assert.AreEqual(1, task.Deployments, "Existing owned resources must be updated, not just tagged.");
        }

        [TestMethod]
        public async Task Provisioning_OptOutDisablesOwnedRulesWithoutDeletingGroup_AndRerunConverges()
        {
            var task = new FakeTask(false, true, true);
            task.AddOwnedResources();
            await task.ExecuteTaskReturnResult(null);
            var first = task.Template.ToString();
            var resources = (JArray)task.Template["resources"];
            Assert.AreEqual(2, resources.Count);
            Assert.IsTrue(resources.All(r => (string)r["type"] == "Microsoft.Insights/scheduledQueryRules"));
            Assert.IsTrue(resources.All(r => !(bool)r["properties"]["enabled"]));
            Assert.IsTrue(resources.All(r => ((JArray)r["properties"]["actions"]["actionGroups"]).Count == 0));
            await task.ExecuteTaskReturnResult(null);
            Assert.AreEqual(first, task.Template.ToString());
        }

        [TestMethod]
        public async Task Provisioning_UnownedCollisionBlocksAllUpdates_AndFailuresPropagate()
        {
            var collision = new FakeTask(true, true, true);
            collision.Existing.Add(new HealthMonitoringInstallTask.HealthResource(collision.ResourceNames[1],
                new Dictionary<string, string>()));
            await Assert.ThrowsExceptionAsync<InstallException>(() => collision.ExecuteTaskReturnResult(null));
            Assert.AreEqual(0, collision.Deployments);
            var failure = new FakeTask(true, true, true) { FailDeployment = true };
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => failure.ExecuteTaskReturnResult(null));
            Assert.AreEqual(1, failure.Deployments);
        }

        [TestMethod]
        public async Task Provisioning_DisabledAndNeverInstalled_DoesNotCreateOrAdoptResources()
        {
            var task = new FakeTask(false, true, true);
            await task.ExecuteTaskReturnResult(null);
            task.Existing.Add(new HealthMonitoringInstallTask.HealthResource(task.ResourceNames[1], new Dictionary<string, string>()));
            await task.ExecuteTaskReturnResult(null);
            Assert.AreEqual(0, task.Deployments);
        }

        private sealed class FakeTask : HealthMonitoringInstallTask
        {
            public const string GroupId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/contoso-demo";
            public FakeTask(bool enabled, bool activity, bool web, Dictionary<string, string> tags = null) : base(
                TaskConfig.GetConfigForName("contoso-web").AddSetting(AppInsightsNameSetting, "contoso-insights"),
                NullLogger.Instance, new AzureLocation("westeurope"), tags ?? new Dictionary<string, string>(),
                enabled, enabled ? "operator@contoso.example" : "", 30, activity, web)
            {
            }
            public List<HealthResource> Existing { get; } = new List<HealthResource>();
            public JObject Template { get; private set; }
            public JObject Parameters { get; private set; }
            public int Deployments { get; private set; }
            public bool FailDeployment { get; set; }
            protected override string ContainerId => GroupId;
            protected override string ScopeLocation => "westeurope";
            protected override IReadOnlyList<HealthResource> ReadExistingResources() => Existing;
            public void AddOwnedResources()
            {
                foreach (var name in ResourceNames)
                    Existing.Add(new HealthResource(name, new Dictionary<string, string>
                    {
                        [OwnerTag] = OwnershipKey(GroupId, "contoso-web")
                    }));
            }
            protected override Task<ArmDeploymentResource> DeployTemplate(JObject template, object parameters)
            {
                Deployments++;
                Template = (JObject)template.DeepClone();
                Parameters = JObject.Parse(BinaryData.FromObjectAsJson(parameters).ToString());
                if (FailDeployment) throw new InvalidOperationException("Synthetic deployment failure");
                return Task.FromResult<ArmDeploymentResource>(null);
            }
        }

        private sealed class RecordingChannel : ITelemetryChannel
        {
            public ConcurrentQueue<EventTelemetry> Events { get; } = new ConcurrentQueue<EventTelemetry>();
            public bool? DeveloperMode { get; set; }
            public string EndpointAddress { get; set; }
            public void Send(ITelemetry item)
            {
                if (item is EventTelemetry ev && ev.Name == "ImporterHeartbeat") Events.Enqueue(ev);
            }
            public void Flush() { }
            public void Dispose() { }
        }
    }
}
