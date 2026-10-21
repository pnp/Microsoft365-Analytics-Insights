using App.ControlPanel.Engine;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
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
using Kusto.Language;
using Kusto.Language.Symbols;
using System.ClientModel.Primitives;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
            Assert.AreEqual(7, resources.Count);
            var group = (JObject)resources[0];
            var rule = (JObject)resources[1];
            Assert.AreEqual("[parameters('notificationEmail')]", (string)group.SelectToken("properties.emailReceivers[0].emailAddress"));
            Assert.AreEqual("operator@contoso.example", (string)task.Parameters.SelectToken("notificationEmail.value"));
            Assert.AreEqual("P2D", (string)rule.SelectToken("properties.overrideQueryTimeRange"));
            Assert.AreEqual("PT5M", (string)rule.SelectToken("properties.windowSize"));
            Assert.AreEqual(FakeTask.GroupId + "/providers/Microsoft.Insights/components/contoso-insights",
                (string)rule.SelectToken("properties.scopes[0]"));
            Assert.AreEqual((string)rule.SelectToken("dependsOn[0]"), (string)rule.SelectToken("properties.actions.actionGroups[0]"));
            var query = (string)rule.SelectToken("properties.criteria.allOf[0].query");
            StringAssert.Contains(query, "JobName) == 'Office365ActivityImporter'");
            StringAssert.Contains(query, "| summarize Heartbeats=count() | where Heartbeats == 0");
            StringAssert.Contains(query, "arg_max(timestamp, *)");
            StringAssert.Contains(query, "join kind=leftanti");
            StringAssert.Contains(query, "FirstStart < ago(24h)");
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
            var rules = ((JArray)task.Template["resources"]).OfType<JObject>()
                .Where(r => (string)r["name"] == task.ResourceNames[1] || (string)r["name"] == task.ResourceNames[2]).ToArray();
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
            Assert.AreEqual(7, resources.Count);
            Assert.IsTrue(resources.All(r => !(bool)r["properties"]["enabled"]));
            Assert.IsTrue(resources.All(r => r["properties"]["actions"] is JArray actions
                ? actions.Count == 0 : ((JArray)r["properties"]["actions"]["actionGroups"]).Count == 0));
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

        [TestMethod]
        public async Task Provisioning_FailureDoesNotAbortRemainingInstallerTasks()
        {
            var job = new FakeSequentialJob(NullLogger.Instance);
            var monitoring = new FakeTask(true, true, true) { FailDeployment = true };
            var after = new FakeMarkerTask(TaskConfig.NoConfig, NullLogger.Instance);
            job.AddTask(monitoring);
            job.AddTask(after);
            await job.Install();
            Assert.AreEqual(1, monitoring.Deployments);
            Assert.IsTrue(after.WasRun, "A monitoring failure must not abort the solution deployment.");
        }

        [TestMethod]
        public async Task Provisioning_SdkSerializesRealDeploymentPayload_WithTypedParametersAndCapacityScopes()
        {
            var task = new FakeTask(true, true, true);
            await task.ExecuteTaskReturnResult(null);
            var deployment = GenericARMInstallTask.BuildDeploymentContent(task.RawParameters, task.Template.ToString());
            var wire = JObject.Parse(ModelReaderWriter.Write(deployment).ToString());
            Assert.AreEqual("Incremental", (string)wire.SelectToken("properties.mode"));
            var template = (JObject)wire.SelectToken("properties.template");
            Assert.IsNotNull(template, "The SDK must send a JSON object, not an escaped template string.");
            Assert.AreEqual("operator@contoso.example", (string)wire.SelectToken("properties.parameters.notificationEmail.value"));
            var metrics = ((JArray)template["resources"]).Where(r => (string)r["type"] == "Microsoft.Insights/metricAlerts").ToArray();
            Assert.AreEqual(4, metrics.Length);
            CollectionAssert.AreEquivalent(new[] { "cpu_percent", "storage_percent", "CpuPercentage", "MemoryPercentage" },
                metrics.Select(r => (string)r.SelectToken("properties.criteria.allOf[0].metricName")).ToArray());
            foreach (var metric in metrics)
            {
                var scope = (string)metric.SelectToken("properties.scopes[0]");
                Assert.IsTrue(scope == FakeTask.GroupId + "/providers/Microsoft.Sql/servers/contoso-sql/databases/contoso-analytics"
                    || scope == FakeTask.GroupId + "/providers/Microsoft.Web/serverfarms/contoso-plan");
                Assert.IsFalse((bool)metric.SelectToken("properties.criteria.allOf[0].skipMetricValidation"));
                Assert.AreEqual((string)metric.SelectToken("dependsOn[0]"), (string)metric.SelectToken("properties.actions[0].actionGroupId"));
            }
        }

        [TestMethod]
        public void Queries_ParseAndBindAgainstApplicationInsightsSchema_AndBoundInputs()
        {
            var globals = GlobalState.Default.WithDatabase(new DatabaseSymbol("SyntheticInsights",
                new TableSymbol("customEvents", "(timestamp:datetime, name:string, customDimensions:dynamic)"),
                new TableSymbol("exceptions", "(timestamp:datetime, outerType:string, innermostType:string, outerMessage:string, innermostMessage:string)"),
                new TableSymbol("traces", "(timestamp:datetime, operation_Name:string, message:string)")));
            foreach (var query in new[]
            {
                HealthMonitoringInstallTask.ImporterHealthQuery(ImporterHeartbeat.ActivityJob, 15),
                HealthMonitoringInstallTask.ImporterHealthQuery(ImporterHeartbeat.WebTrafficJob, 1440),
                HealthMonitoringInstallTask.MissingHeartbeatQuery(ImporterHeartbeat.ActivityJob, 30),
                HealthMonitoringInstallTask.LoggedFailuresQuery
            })
            {
                var diagnostics = KustoCode.ParseAndAnalyze(query, globals).GetDiagnostics();
                Assert.AreEqual(0, diagnostics.Count, string.Join(Environment.NewLine, diagnostics.Select(d => d.Message)));
            }
            Assert.ThrowsException<ArgumentException>(() => HealthMonitoringInstallTask.ImporterHealthQuery("'; union *", 30));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => HealthMonitoringInstallTask.ImporterHealthQuery(ImporterHeartbeat.ActivityJob, 14));
        }

        [DataTestMethod]
        [DataRow(true, false)]
        [DataRow(false, true)]
        public async Task Provisioning_RealSdkListsAllOwnedResourceTypes_AndSendsIncrementalDeployment(bool enabled, bool existing)
        {
            using (var handler = new MonitoringHandler { ExistingOwned = existing })
            using (var http = new HttpClient(handler))
            {
                var task = SdkTask(http, enabled);
                await task.ExecuteTaskReturnResult(null);
                Assert.AreEqual(1, handler.Deployments);
                CollectionAssert.IsSubsetOf(new[]
                {
                    "Microsoft.Insights/scheduledQueryRules", "Microsoft.Insights/actionGroups", "Microsoft.Insights/metricAlerts"
                }, handler.ListedTypes);
                var resources = (JArray)handler.Payload.SelectToken("properties.template.resources");
                Assert.AreEqual(enabled ? 8 : 7, resources.Count);
                Assert.IsTrue(resources.Where(r => (string)r["type"] != "Microsoft.Insights/actionGroups")
                    .All(r => (bool)r["properties"]["enabled"] == enabled));
                Assert.AreEqual("object", (string)handler.Payload.SelectToken("properties.template.parameters.resourceTags.type"));
                Assert.AreEqual("operator@contoso.example",
                    (string)handler.Payload.SelectToken("properties.parameters.notificationEmail.value"));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Provisioning_RealSdkNeverMutatesAfterOwnershipCollisionOrReadFailure(bool readFailure)
        {
            using (var handler = new MonitoringHandler { ExistingOwned = true, Collision = !readFailure, FailReads = readFailure })
            using (var http = new HttpClient(handler))
            {
                var task = SdkTask(http, true);
                if (readFailure)
                    await Assert.ThrowsExceptionAsync<Azure.RequestFailedException>(() => task.ExecuteTaskReturnResult(null));
                else
                    await Assert.ThrowsExceptionAsync<InstallException>(() => task.ExecuteTaskReturnResult(null));
                Assert.AreEqual(0, handler.Deployments);
                Assert.AreEqual(0, handler.WebSiteReads, "Ownership/read guards must run before looking up any plan.");
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Provisioning_UsesTheActualHostingPlanRatherThanTheConfiguredName(bool existingPlanElsewhere)
        {
            var actualPlan = existingPlanElsewhere
                ? FakeTask.GroupId.Replace("contoso-demo", "contoso-hosting") + "/providers/Microsoft.Web/serverfarms/contoso-existing-plan"
                : FakeTask.GroupId + "/providers/Microsoft.Web/serverfarms/contoso-web";
            using (var handler = new MonitoringHandler { HostingPlanId = actualPlan })
            using (var http = new HttpClient(handler))
            {
                await SdkTask(http, true).ExecuteTaskReturnResult(null);
                var planAlerts = ((JArray)handler.Payload.SelectToken("properties.template.resources"))
                    .Where(r => (string)r.SelectToken("properties.criteria.allOf[0].metricNamespace") == "Microsoft.Web/serverfarms")
                    .ToList();

                Assert.AreEqual(2, planAlerts.Count);
                Assert.IsTrue(planAlerts.All(r => (string)r.SelectToken("properties.scopes[0]") == actualPlan),
                    "Capacity alerts must monitor the web app's real host, including an existing plan in another resource group.");
                Assert.AreEqual(1, handler.WebSiteReads);
            }
        }

        [DataTestMethod]
        [DataRow("forbidden")]
        [DataRow("missing")]
        [DataRow("wrong-type")]
        public async Task Provisioning_HostingPlanReadProblemsNeverDeployGuessedScopes(string failure)
        {
            using (var handler = new MonitoringHandler
            {
                FailHostingRead = failure == "forbidden",
                OmitHostingPlan = failure == "missing",
                HostingPlanId = failure == "wrong-type"
                    ? FakeTask.GroupId + "/providers/Microsoft.Sql/servers/contoso-sql"
                    : FakeTask.GroupId + "/providers/Microsoft.Web/serverfarms/contoso-plan"
            })
            using (var http = new HttpClient(handler))
            {
                var task = SdkTask(http, true);
                if (failure == "forbidden")
                    await Assert.ThrowsExceptionAsync<Azure.RequestFailedException>(() => task.ExecuteTaskReturnResult(null));
                else
                    await Assert.ThrowsExceptionAsync<InstallException>(() => task.ExecuteTaskReturnResult(null));
                Assert.AreEqual(1, handler.WebSiteReads);
                Assert.AreEqual(0, handler.Deployments);
            }
        }

        [TestMethod]
        public async Task Provisioning_OptOutWithOnlySqlAlertsDoesNotRequireAHostingPlan()
        {
            var task = new FakeTask(false, false, false) { FailHostingRead = true };
            task.Existing.Add(new HealthMonitoringInstallTask.HealthResource(task.ResourceNames[4], new Dictionary<string, string>
            {
                [HealthMonitoringInstallTask.OwnerTag] = HealthMonitoringInstallTask.OwnershipKey(FakeTask.GroupId, "contoso-web")
            }));

            await task.ExecuteTaskReturnResult(null);

            Assert.AreEqual(0, task.HostingPlanReads);
            Assert.AreEqual(1, ((JArray)task.Template["resources"]).Count);
            Assert.IsFalse((bool)task.Template.SelectToken("resources[0].properties.enabled"));
        }

        private static HealthMonitoringInstallTask SdkTask(HttpClient http, bool enabled)
        {
            var options = new ArmClientOptions { Transport = new HttpClientTransport(http) };
            options.Retry.MaxRetries = 0;
            var group = new ArmClient(new SyntheticCredential(), "00000000-0000-0000-0000-000000000000", options)
                .GetResourceGroupResource(new ResourceIdentifier(FakeTask.GroupId));
            return new HealthMonitoringInstallTask(MonitoringTaskConfig(), NullLogger.Instance,
                new AzureLocation("westeurope"), new Dictionary<string, string>(), enabled,
                "operator@contoso.example", 30, true, true) { Container = group };
        }

        private static TaskConfig MonitoringTaskConfig() => TaskConfig.GetConfigForName("contoso-web")
            .AddSetting(HealthMonitoringInstallTask.AppInsightsNameSetting, "contoso-insights")
            .AddSetting(HealthMonitoringInstallTask.SqlServerNameSetting, "contoso-sql")
            .AddSetting(HealthMonitoringInstallTask.SqlDatabaseNameSetting, "contoso-analytics")
            .AddSetting(HealthMonitoringInstallTask.AppServicePlanNameSetting, "contoso-plan");

        private sealed class SyntheticCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new AccessToken("synthetic", DateTimeOffset.MaxValue);
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
        }

        private sealed class MonitoringHandler : HttpMessageHandler
        {
            public bool ExistingOwned, Collision, FailReads;
            public bool FailHostingRead, OmitHostingPlan;
            public int Deployments;
            public int WebSiteReads;
            public string HostingPlanId = FakeTask.GroupId + "/providers/Microsoft.Web/serverfarms/contoso-plan";
            public JObject Payload;
            public List<string> ListedTypes { get; } = new List<string>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Put && request.RequestUri.AbsolutePath.Contains("/deployments/"))
                {
                    Deployments++;
                    Payload = JObject.Parse(await request.Content.ReadAsStringAsync());
                    return Json(HttpStatusCode.OK, new JObject
                    {
                        ["id"] = request.RequestUri.AbsolutePath, ["name"] = "contoso-deployment",
                        ["type"] = "Microsoft.Resources/deployments",
                        ["properties"] = new JObject { ["provisioningState"] = "Succeeded", ["mode"] = "Incremental" }
                    });
                }
                Assert.AreEqual(HttpMethod.Get, request.Method, "The fake must reject every unexpected SDK mutation.");
                if (request.RequestUri.AbsolutePath.EndsWith("/Microsoft.Web/sites/contoso-web", StringComparison.OrdinalIgnoreCase))
                {
                    WebSiteReads++;
                    if (FailHostingRead) return Json(HttpStatusCode.Forbidden, new JObject
                    {
                        ["error"] = new JObject { ["code"] = "AuthorizationFailed", ["message"] = "Synthetic hosting-plan read failure" }
                    });
                    return Json(HttpStatusCode.OK, new JObject
                    {
                        ["id"] = FakeTask.GroupId + "/providers/Microsoft.Web/sites/contoso-web",
                        ["name"] = "contoso-web", ["type"] = "Microsoft.Web/sites", ["location"] = "westeurope",
                        ["properties"] = new JObject { ["serverFarmId"] = OmitHostingPlan ? null : HostingPlanId }
                    });
                }
                if (FailReads) return Json(HttpStatusCode.Forbidden, new JObject
                {
                    ["error"] = new JObject { ["code"] = "AuthorizationFailed", ["message"] = "Synthetic read failure" }
                });
                var filter = Uri.UnescapeDataString(request.RequestUri.Query);
                var resources = new JArray();
                var types = new[] { "Microsoft.Insights/scheduledQueryRules", "Microsoft.Insights/actionGroups",
                    "Microsoft.Insights/metricAlerts", "Microsoft.Insights/components" };
                var type = types.Single(t => filter.Contains(t));
                ListedTypes.Add(type);
                if (type == "Microsoft.Insights/components")
                    resources.Add(Resource("contoso-insights", type, true));
                else if (ExistingOwned)
                {
                    var suffixes = type == types[0] ? new[] { "activity", "webtraffic", "failures" }
                        : type == types[1] ? new[] { "notifications" }
                        : new[] { "sql-cpu", "sql-storage", "plan-cpu", "plan-memory" };
                    foreach (var suffix in suffixes)
                        resources.Add(Resource("contoso-web-health-" + suffix, type, !(Collision && suffix == "sql-cpu")));
                    resources.Add(Resource("contoso-unrelated-alert", type, false));
                }
                return Json(HttpStatusCode.OK, new JObject { ["value"] = resources });
            }

            private static JObject Resource(string name, string type, bool owned) => new JObject
            {
                ["id"] = FakeTask.GroupId + "/providers/" + type + "/" + name,
                ["name"] = name, ["type"] = type, ["location"] = "westeurope",
                ["tags"] = owned ? new JObject
                {
                    [HealthMonitoringInstallTask.OwnerTag] = HealthMonitoringInstallTask.OwnershipKey(FakeTask.GroupId, "contoso-web")
                } : new JObject(),
                ["properties"] = new JObject()
            };
            private static HttpResponseMessage Json(HttpStatusCode status, JObject value)
                => new HttpResponseMessage(status) { Content = new StringContent(value.ToString(), Encoding.UTF8, "application/json") };
        }

        private sealed class FakeTask : HealthMonitoringInstallTask
        {
            public const string GroupId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/contoso-demo";
            public FakeTask(bool enabled, bool activity, bool web, Dictionary<string, string> tags = null) : base(
                MonitoringTaskConfig(),
                NullLogger.Instance, new AzureLocation("westeurope"), tags ?? new Dictionary<string, string>(),
                enabled, enabled ? "operator@contoso.example" : "", 30, activity, web)
            {
            }
            public List<HealthResource> Existing { get; } = new List<HealthResource>();
            public JObject Template { get; private set; }
            public JObject Parameters { get; private set; }
            public object RawParameters { get; private set; }
            public int Deployments { get; private set; }
            public bool FailDeployment { get; set; }
            public bool FailHostingRead { get; set; }
            public int HostingPlanReads { get; private set; }
            protected override string ContainerId => GroupId;
            protected override string ScopeLocation => "westeurope";
            protected override Task<ResourceIdentifier> ReadHostingPlanIdAsync()
            {
                HostingPlanReads++;
                if (FailHostingRead) throw new InvalidOperationException("Unexpected hosting-plan read.");
                return Task.FromResult(new ResourceIdentifier(GroupId + "/providers/Microsoft.Web/serverfarms/contoso-plan"));
            }
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
                RawParameters = parameters;
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
