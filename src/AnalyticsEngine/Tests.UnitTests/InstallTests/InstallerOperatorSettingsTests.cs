using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using App.ControlPanel.Engine.Prerequisites;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.KeyVault.Models;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.ServiceBus;
using Azure.ResourceManager.ServiceBus.Models;
using Azure.ResourceManager.Sql;
using CloudInstallEngine;
using CloudInstallEngine.Azure;
using CloudInstallEngine.Azure.InstallTasks;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// A re-run of the installer against resources that already exist must change only what the installer owns, and
    /// must never weaken a setting an operator tightened: R11-5 (TLS 1.3, the SQL Azure-services exception, the Key Vault
    /// bypass, SCM basic authentication), R11-6 (sparse PUT bodies that dropped tags), R11-8 (data-plane roles on the
    /// whole resource group) and X4-N4 (the configured App Service plan name). The Azure-facing tests run the real tasks
    /// against a fake Azure Resource Manager and assert on the requests they send. All names and addresses are synthetic.
    /// </summary>
    [TestClass]
    public class InstallerOperatorSettingsTests
    {
        private const string Sub = "00000000-0000-0000-0000-000000000000";
        private const string Rg = "/subscriptions/" + Sub + "/resourceGroups/contoso";
        private const string Site = Rg + "/providers/Microsoft.Web/sites/contoso-web";
        private const string AppSettings = Site + "/config/appsettings";
        private const string ConnectionStrings = Site + "/config/connectionstrings";
        private const string FtpPolicy = Site + "/basicPublishingCredentialsPolicies/ftp";
        private const string SqlServer = Rg + "/providers/Microsoft.Sql/servers/contoso-sql";
        private const string Storage = Rg + "/providers/Microsoft.Storage/storageAccounts/contosoanalytics";

        // ---------------------------------------------------------------- TLS (R11-5)

        [DataTestMethod]
        [DataRow(null, true)]
        [DataRow("", true)]
        [DataRow("None", true)]
        [DataRow("1.0", true)]
        [DataRow("1.1", true)]
        [DataRow("TLS1_0", true)]
        [DataRow("1.2", false)]
        [DataRow("TLS1_2", false)]
        [DataRow("1.3", false)]
        [DataRow("TLS1_3", false)]
        [DataRow("Tls13", false)]
        [DataRow("SomethingStricter", false)]
        public void TlsPolicy_RaisesOnlyWhatIsBelowTls12(string current, bool expected)
        {
            Assert.AreEqual(expected, TlsVersionPolicy.NeedsRaising(current), current ?? "(null)");
        }

        [TestMethod]
        public void ServiceBus_ANamespaceAtTls13_IsLeftAlone_AndOneBelow12IsStillRaised()
        {
            Assert.IsFalse(ServiceBusNamespaceInstallTask.NeedsMinimumTlsUpdate(new ServiceBusMinimumTlsVersion("1.3")),
                "A namespace an operator raised to TLS 1.3 must not be put back to 1.2.");
            Assert.IsTrue(ServiceBusNamespaceInstallTask.NeedsMinimumTlsUpdate(new ServiceBusMinimumTlsVersion("1.1")));
        }

        [TestMethod]
        public void ServiceBus_TheUpdateBodyCarriesTheOperatorsLocalAuthAndIdentitySettings()
        {
            var current = new ServiceBusNamespaceData(new AzureLocation("westeurope")) { DisableLocalAuth = true, PremiumMessagingPartitions = 2 };
            var update = new ServiceBusNamespaceData(new AzureLocation("westeurope"));

            ServiceBusNamespaceInstallTask.CopyOperatorSettings(current, update);

            Assert.AreEqual(true, update.DisableLocalAuth, "A PUT without disableLocalAuth could turn SAS keys back on.");
            Assert.AreEqual(2, update.PremiumMessagingPartitions);
        }

        [TestMethod]
        public void AppService_SiteConfigChanges_LeaveTls13Alone_AndOnlyTightenWhatIsWeaker()
        {
            var hardened = new SiteConfigData { MinTlsVersion = new Azure.ResourceManager.AppService.Models.AppServiceSupportedTlsVersion("1.3"),
                FtpsState = Azure.ResourceManager.AppService.Models.AppServiceFtpsState.Disabled, IsAlwaysOn = true };
            Assert.IsNull(AppServiceWebsiteTask.BuildSiteConfigChanges(hardened, supportsAlwaysOn: true),
                "Nothing to change on a site at TLS 1.3 with FTP off and Always On.");

            var weak = new SiteConfigData { MinTlsVersion = Azure.ResourceManager.AppService.Models.AppServiceSupportedTlsVersion.Tls1_0,
                FtpsState = Azure.ResourceManager.AppService.Models.AppServiceFtpsState.Disabled, IsAlwaysOn = true };
            var changes = new List<string>();
            var patch = AppServiceWebsiteTask.BuildSiteConfigChanges(weak, supportsAlwaysOn: true, changes);
            Assert.AreEqual("1.2", patch.MinTlsVersion?.ToString());
            Assert.IsNull(patch.FtpsState, "Settings that are already right are not sent.");
            Assert.IsNull(patch.IsAlwaysOn);
            Assert.AreEqual(1, changes.Count);
        }

        [TestMethod]
        public async Task AppService_ReRunOnAHardenedSite_SendsNothing_AndNeverTouchesScmBasicAuth()
        {
            using (var arm = new FakeArm())
            {
                arm.AddSite(minTls: "1.3");
                var task = new AppServiceWebsiteTask(TaskConfig.GetConfigForName("contoso-web"), NullLogger.Instance,
                    new AzureLocation("westeurope"), new Dictionary<string, string>()) { Container = arm.Group };

                await task.ExecuteTaskReturnResult(await arm.Plan("contoso-web"));

                Assert.AreEqual(0, arm.Mutations.Count, "Unexpected writes: " + string.Join(", ", arm.Mutations.Select(m => m.Method + " " + m.Path)));
                Assert.IsFalse(arm.Requests.Any(r => r.Path.EndsWith("/basicPublishingCredentialsPolicies/scm", StringComparison.OrdinalIgnoreCase)),
                    "The installer deploys with a Microsoft Entra token, so it never reads or switches on SCM basic authentication.");
            }
        }

        [TestMethod]
        public async Task AppService_ATooWeakTlsMinimum_IsRaisedByPatch_WithoutDroppingTheOperatorsTags()
        {
            using (var arm = new FakeArm())
            {
                arm.AddSite(minTls: "1.0");
                var task = new AzurePaaSInstallJob(NullLogger.Instance, BackendConfig("contoso-web", "contoso-web"), arm.Subscription).WebsiteTask;
                task.Container = arm.Group;

                await task.ExecuteTaskReturnResult(await arm.Plan("contoso-web"));

                var write = arm.Mutations.Single();
                Assert.AreEqual("PATCH", write.Method, "A PUT of a fresh site body dropped every tag it did not carry.");
                Assert.AreEqual(Site, write.Path, true);
                Assert.AreEqual("1.2", (string)write.Body.SelectToken("properties.siteConfig.minTlsVersion"));
                Assert.IsNull(write.Body["tags"], "The update must not send (and so replace) the site's tags.");
                Assert.AreEqual("Contoso-42", (string)arm.Resource(Site)["tags"]["CostCentre"]);
            }
        }

        [TestMethod]
        public async Task AppService_ConfigurationReplaysPreserveOperatorSettingsAndRefreshOnlyOwnedNames()
        {
            using (var arm = new FakeArm())
            {
                arm.AddSite("1.2");
                arm.Resource(AppSettings)["properties"] = new JObject
                {
                    ["UserGroupsFilter"] = "Contoso pilot", ["ClientID"] = "old", ["ContosoEmpty"] = "",
                    ["iMpOrTjObSeTtInGs"] = "stale"
                };
                var operatorPair = new JObject { ["type"] = "PostgreSQL", ["value"] = "Contoso; preserve exactly " };
                var otherBus = new JObject { ["type"] = "ServiceBus", ["value"] = "Contoso operator bus" };
                arm.Resource(ConnectionStrings)["properties"] = new JObject
                {
                    ["ContosoReporting"] = operatorPair, ["ServiceBusBackup"] = otherBus,
                    ["sToRaGe"] = otherBus, ["rEdIs"] = otherBus, ["sErViCeBuS"] = otherBus
                };

                foreach (var bus in new[] { "Contoso managed bus", null, null })
                {
                    arm.Requests.Clear();
                    await ConfigureWebAppAsync(arm, bus);
                    CollectionAssert.AreEqual(new[] { "POST " + AppSettings + "/list", "POST " + ConnectionStrings + "/list",
                        "PATCH " + Site, "PUT " + AppSettings, "PUT " + ConnectionStrings }, ConfigurationRequests(arm));
                    var settings = arm.Resource(AppSettings)["properties"];
                    Assert.AreEqual("Contoso pilot", (string)settings["UserGroupsFilter"]);
                    Assert.AreEqual("", (string)settings["ContosoEmpty"]);
                    Assert.AreEqual(Sub, (string)settings["ClientID"]);
                    Assert.IsNotNull(settings["ImportJobSettings"]);
                    Assert.IsNull(settings["iMpOrTjObSeTtInGs"], "Windows setting names are case-insensitive. Send only the managed value.");
                    var properties = (JObject)arm.Resource(ConnectionStrings)["properties"];
                    Assert.IsTrue(JToken.DeepEquals(operatorPair, properties["ContosoReporting"]));
                    Assert.IsTrue(JToken.DeepEquals(otherBus, properties["ServiceBusBackup"]));
                    Assert.AreEqual("SQLAzure", (string)properties["SPOInsightsEntities"]["type"]);
                    Assert.AreEqual("Contoso SQL", (string)properties["SPOInsightsEntities"]["value"]);
                    foreach (var name in new[] { "Storage", "AzureWebJobsStorage", "AzureWebJobsDashboard" })
                    {
                        Assert.AreEqual("Custom", (string)properties[name]["type"]);
                        Assert.AreEqual("Contoso storage", (string)properties[name]["value"]);
                    }
                    Assert.IsNull(properties["sToRaGe"]);
                    Assert.IsNull(properties["sErViCeBuS"]);
                    Assert.IsNull(properties["rEdIs"]);
                    if (bus == null)
                        Assert.IsNull(properties["ServiceBus"]);
                    else
                        Assert.AreEqual(bus, (string)properties["ServiceBus"]["value"]);

                    // The next run must also preserve an operator edit made since the previous one.
                    operatorPair["value"] = "Contoso revised";
                    properties["ContosoReporting"] = operatorPair.DeepClone();
                }
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task FullUpgrade_DeniedConnectionStringsLeaveTheExistingSiteUnchanged(bool privateToPublic)
        {
            using (var arm = new FakeArm())
            {
                var config = BackendConfig("contoso-web", "contoso-web");
                var logger = new CapturingLogger();
                var subnet = Rg + "/providers/Microsoft.Network/virtualNetworks/contoso-vnet/subnets/integration";
                arm.AddResourceGroup();
                arm.AddSite("1.0", privateToPublic ? subnet : null);
                arm.Resource(Site)["properties"]["publicNetworkAccess"] = "Disabled";
                arm.Resource(Site).Property("identity").Remove();
                arm.Resource(Site + "/config/web")["properties"]["alwaysOn"] = false;
                arm.Resource(FtpPolicy)["properties"]["allow"] = true;
                arm.Resource(AppSettings)["properties"]["WEBJOBS_STOPPED"] = "1";
                if (privateToPublic)
                    config.PrivateNetworking.Artifacts.Add(new PrivateNetworkArtifact
                    {
                        Id = Site, TargetId = Site, SubnetId = subnet, Kind = PrivateNetworkProvenance.Integration,
                        CreatedByInstaller = true, CreationConfirmed = true
                    });
                arm.KnownLists.UnionWith(new[] { Rg + "/resources", Rg + "/providers/Microsoft.Network/privateEndpoints",
                    Rg + "/providers/Microsoft.Network/privateDnsZones" });
                foreach (var provider in AzureResourceProviderPreflight.RequiredProviders(config))
                    arm.Add("/subscriptions/" + Sub + "/providers/" + provider, new JObject { ["namespace"] = provider, ["registrationState"] = "Registered" });
                arm.PostStatuses[ConnectionStrings + "/list"] = HttpStatusCode.Forbidden;
                arm.PostResponses[ConnectionStrings + "/list"] = new JObject { ["error"] = new JObject { ["message"] = "Contoso do not log" } };
                var before = arm.Resource(Site).DeepClone();

                await new FixtureSolutionInstaller(config, logger, arm.Subscription).InstallOrUpdate();

                CollectionAssert.AreEqual(new[] { "POST " + AppSettings + "/list", "POST " + ConnectionStrings + "/list" }, ConfigurationRequests(arm));
                Assert.IsTrue(JToken.DeepEquals(before, arm.Resource(Site)));
                Assert.AreEqual(false, (bool)arm.Resource(Site + "/config/web")["properties"]["alwaysOn"]);
                Assert.AreEqual(true, (bool)arm.Resource(FtpPolicy)["properties"]["allow"]);
                Assert.AreEqual("1", (string)arm.Resource(AppSettings)["properties"]["WEBJOBS_STOPPED"]);
                Assert.IsTrue(logger.Lines.Any(l => l.Message.Contains("HTTP 403") && l.Message.Contains("No replacement was sent")));
                Assert.IsFalse(logger.Lines.Any(l => l.Message.Contains("Contoso do not log")));
            }
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        public async Task FullInstall_UsesTheRegisteredWebsiteTaskAndOneSnapshotForTheFinalReplacements(bool newSite, bool planConflict)
        {
            using (var arm = new FakeArm())
            {
                const string siteCollection = Rg + "/providers/Microsoft.Web/sites";
                arm.AddPlan("contoso-web");
                if (newSite)
                {
                    arm.KnownLists.Add(siteCollection);
                    arm.OverrideResponse = (method, path) =>
                    {
                        if (method != "PUT" || path != Site) return null;
                        arm.AddSite("1.2"); // ARM supplies the host name and config resources on creation.
                        return FakeArm.Json(HttpStatusCode.OK, arm.Resource(Site));
                    };
                }
                else
                {
                    arm.AddSite("1.0");
                    arm.Resource(Site).Property("identity").Remove();
                    arm.Resource(Site + "/config/web")["properties"]["alwaysOn"] = false;
                    arm.Resource(AppSettings)["properties"]["UserGroupsFilter"] = "Contoso pilot";
                    arm.Resource(ConnectionStrings)["properties"]["ContosoReporting"] = new JObject { ["type"] = "Custom", ["value"] = "keep" };
                    if (planConflict)
                    {
                        var patches = 0;
                        arm.OverrideResponse = (method, path) => method == "PATCH" && path == Site && ++patches == 1
                            ? FakeArm.Json(HttpStatusCode.Conflict, new JObject()) : null;
                    }
                }
                // Azure site names are case-insensitive. A fresh-site PUT must not bypass the existing-site preflight.
                var config = BackendConfig(newSite ? "contoso-web" : "CONTOSO-WEB", "contoso-web");
                var installer = new FixtureSolutionInstaller(config, NullLogger.Instance, arm.Subscription);
                var steps = installer.CreateAzureInstallSteps(NullLogger.Instance, arm.Subscription, CancellationToken.None);
                steps.Backend.PlanTask.Container = steps.Backend.WebsiteTask.Container = arm.Group;
                var plan = await steps.Backend.PlanTask.ExecuteTaskReturnResult(null);
                var site = await steps.Backend.WebsiteTask.ExecuteTaskReturnResult(plan);
                await steps.PostCreate.ConfigureWebApp(site, plan, "Contoso SQL", "Contoso storage",
                    null, null, null, null, new Uri("https://contoso-vault.vault.azure.net/"));

                var acquireSite = (newSite ? "PUT " : "PATCH ") + Site;
                var expected = new List<string> { "POST " + AppSettings + "/list", "POST " + ConnectionStrings + "/list" };
                expected.Insert(newSite ? 0 : 2, acquireSite);
                if (planConflict) expected.Add("PATCH " + Site);
                expected.AddRange(new[] { "PATCH " + Site, "PUT " + AppSettings, "PUT " + ConnectionStrings });
                CollectionAssert.AreEqual(expected, ConfigurationRequests(arm));
                var provision = arm.Requests.First(r => r.Path == Site && (r.Method == "PATCH" || r.Method == "PUT")).Body;
                Assert.AreEqual("SystemAssigned", (string)provision.SelectToken("identity.type"));
                Assert.AreEqual(true, (bool)provision.SelectToken("properties.siteConfig.alwaysOn"));
                Assert.AreEqual("1.2", (string)provision.SelectToken("properties.siteConfig.minTlsVersion"));
                if (planConflict)
                {
                    var retry = arm.Requests.Where(r => r.Method == "PATCH" && r.Path == Site).Skip(1).First().Body;
                    Assert.IsNull(retry.SelectToken("properties.siteConfig.alwaysOn"));
                    Assert.AreEqual("1.2", (string)retry.SelectToken("properties.siteConfig.minTlsVersion"));
                    Assert.AreEqual("SystemAssigned", (string)retry.SelectToken("identity.type"));
                }
                if (!newSite)
                {
                    Assert.AreEqual("Contoso pilot", (string)arm.Resource(AppSettings)["properties"]["UserGroupsFilter"]);
                    Assert.AreEqual("keep", (string)arm.Resource(ConnectionStrings)["properties"]["ContosoReporting"]["value"]);
                }
                Assert.AreEqual("Contoso SQL", (string)arm.Resource(ConnectionStrings)["properties"]["SPOInsightsEntities"]["value"]);
            }
        }

        private sealed class FixtureSolutionInstaller : SolutionInstaller
        {
            private readonly SubscriptionResource _subscription;
            public FixtureSolutionInstaller(SolutionInstallConfig config, ILogger logger, SubscriptionResource subscription)
                : base(config, logger, null, null, "Contoso", null) { _subscription = subscription; }
            internal override SubscriptionResource ConnectToSubscription(CancellationToken ct) => _subscription;
        }

        [DataTestMethod]
        [DataRow(AppSettings, "denied")]
        [DataRow(AppSettings, "policy")]
        [DataRow(AppSettings, "missing properties")]
        [DataRow(AppSettings, "null properties")]
        [DataRow(AppSettings, "invalid setting")]
        [DataRow(ConnectionStrings, "denied")]
        [DataRow(ConnectionStrings, "missing properties")]
        [DataRow(ConnectionStrings, "incomplete pair")]
        public async Task AppService_RefusedOrIncompleteReadMakesNoConfigurationWrites(string collection, string failure)
        {
            const string privateValue = "Contoso do not log";
            using (var arm = new FakeArm())
            {
                arm.AddSite("1.2");
                arm.Resource(AppSettings)["properties"]["ContosoOperatorSetting"] = privateValue;
                var list = collection + "/list";
                switch (failure)
                {
                    case "denied":
                    case "policy":
                        arm.PostStatuses[list] = HttpStatusCode.Forbidden;
                        arm.PostResponses[list] = new JObject { ["error"] = new JObject
                            { ["code"] = failure == "policy" ? "RequestDisallowedByPolicy" : "Forbidden", ["message"] = privateValue } };
                        break;
                    case "missing properties": arm.PostResponses[list] = new JObject(); break;
                    case "null properties": arm.PostResponses[list] = new JObject { ["properties"] = JValue.CreateNull() }; break;
                    case "invalid setting": arm.PostResponses[list] = new JObject { ["properties"] = new JObject { ["UserGroupsFilter"] = JValue.CreateNull() } }; break;
                    case "incomplete pair": arm.PostResponses[list] = new JObject { ["properties"] = new JObject { ["Contoso"] = new JObject { ["value"] = privateValue } } }; break;
                }

                var logger = new CapturingLogger();
                var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => ConfigureWebAppAsync(arm, logger: logger));
                CollectionAssert.AreEqual(collection == AppSettings ? new[] { "POST " + list } :
                    new[] { "POST " + AppSettings + "/list", "POST " + list }, ConfigurationRequests(arm));
                StringAssert.Contains(error.Message, "No replacement was sent");
                if (failure == "denied")
                {
                    StringAssert.Contains(error.Message, "HTTP 403");
                    StringAssert.Contains(error.Message, "access");
                }
                else if (failure == "policy") StringAssert.Contains(error.Message, "RequestDisallowedByPolicy");
                else StringAssert.Contains(error.Message, "complete collection", "Invalid list data is not a permissions diagnosis.");
                Assert.IsFalse(error.ToString().Contains(privateValue) || logger.Lines.Any(l => l.Message.Contains(privateValue)),
                    "Neither the exception chain nor the installer log may contain a setting value or SDK error body.");
            }
        }

        [TestMethod]
        public async Task AppService_ExplicitlyEmptyCollectionsAreReadBeforeTheSitePatchAndItsPlanRetry()
        {
            using (var arm = new FakeArm())
            {
                arm.AddSite("1.2");
                var patches = 0;
                arm.OverrideResponse = (method, path) => method == "PATCH" && path == Site && ++patches == 1
                    ? FakeArm.Json(HttpStatusCode.Conflict, new JObject()) : null;

                await ConfigureWebAppAsync(arm);

                CollectionAssert.AreEqual(new[] { "POST " + AppSettings + "/list", "POST " + ConnectionStrings + "/list",
                    "PATCH " + Site, "PATCH " + Site, "PUT " + AppSettings, "PUT " + ConnectionStrings }, ConfigurationRequests(arm));
                var siteConfigs = arm.Requests.Where(r => r.Method == "PATCH").Select(r => r.Body["properties"]["siteConfig"]).ToList();
                Assert.AreEqual(true, (bool)siteConfigs[0]["alwaysOn"]);
                Assert.AreEqual(false, (bool)siteConfigs[0]["use32BitWorkerProcess"]);
                Assert.IsNull(siteConfigs[1]["alwaysOn"]);
                Assert.IsNull(siteConfigs[1]["use32BitWorkerProcess"]);
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AppService_CancellationDuringTheLastReadMakesNoConfigurationWrites(bool pausing)
        {
            using (var arm = new FakeArm())
            using (var cancellation = new CancellationTokenSource())
            {
                arm.AddSite("1.2");
                arm.OverrideResponse = (method, path) =>
                {
                    if (method == "POST" && path == (pausing ? AppSettings : ConnectionStrings) + "/list") cancellation.Cancel();
                    return null;
                };

                var site = (await arm.Group.GetWebSiteAsync("contoso-web")).Value;
                await Assert.ThrowsAsync<OperationCanceledException>(() => pausing
                    ? ConfigureAzureComponentsTasks.PauseUpgradeWebJobsAsync(site, cancellation.Token)
                    : ConfigureWebAppAsync(arm, token: cancellation.Token));
                CollectionAssert.AreEqual(pausing ? new[] { "POST " + AppSettings + "/list" } :
                    new[] { "POST " + AppSettings + "/list", "POST " + ConnectionStrings + "/list" }, ConfigurationRequests(arm));
            }
        }

        [DataTestMethod]
        [DataRow(AppSettings, 403, "Forbidden")]
        [DataRow(ConnectionStrings, 409, "ScopeLocked")]
        [DataRow(ConnectionStrings, 403, "Contoso do not log")]
        public async Task AppService_RefusedConfigurationWriteDoesNotLeakItsBodyOrClaimNoWritesWereSent(string collection, int status, string code)
        {
            using (var arm = new FakeArm())
            {
                arm.AddSite("1.2");
                const string privateValue = "Contoso do not log";
                arm.Resource(AppSettings)["properties"]["Storage"] = "Contoso operator setting";
                var operatorPair = new JObject { ["type"] = "PostgreSQL", ["value"] = "Καλημέρα; retain  " };
                arm.Resource(ConnectionStrings)["properties"]["ImportJobSettings"] = operatorPair;
                arm.OverrideResponse = (method, path) => method == "PUT" && path == collection
                    ? FakeArm.Json((HttpStatusCode)status, new JObject { ["error"] = new JObject { ["code"] = code, ["message"] = privateValue } })
                    : null;

                var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => ConfigureWebAppAsync(arm));
                Assert.AreEqual("PUT " + collection, ConfigurationRequests(arm).Last());
                StringAssert.Contains(error.Message, "Could not update");
                StringAssert.Contains(error.Message, $"HTTP {status}");
                if (code == "ScopeLocked") StringAssert.Contains(error.Message, code);
                Assert.IsFalse(error.ToString().Contains(privateValue) || error.Message.Contains("No replacement was sent"));
                Assert.AreEqual(collection == ConnectionStrings, arm.Resource(AppSettings)["properties"]["ClientID"] != null);
                Assert.IsNull(arm.Resource(ConnectionStrings)["properties"]["SPOInsightsEntities"]);

                arm.OverrideResponse = null;
                await ConfigureWebAppAsync(arm);
                Assert.AreEqual("Contoso operator setting", (string)arm.Resource(AppSettings)["properties"]["Storage"]);
                Assert.IsTrue(JToken.DeepEquals(operatorPair, arm.Resource(ConnectionStrings)["properties"]["ImportJobSettings"]));
                Assert.AreEqual("Contoso SQL", (string)arm.Resource(ConnectionStrings)["properties"]["SPOInsightsEntities"]["value"]);
            }
        }

        private static string[] ConfigurationRequests(FakeArm arm) =>
            arm.Requests.Where(r => r.Method != "GET").Select(r => r.Method + " " + r.Path).ToArray();

        [TestMethod]
        public async Task AppService_LockedSitePatchDoesNotRetryWithoutPlanSettings()
        {
            using (var arm = new FakeArm())
            {
                arm.AddSite("1.2");
                var logger = new CapturingLogger();
                arm.OverrideResponse = (method, path) => method == "PATCH" && path == Site
                    ? FakeArm.Json(HttpStatusCode.Conflict, new JObject { ["error"] = new JObject
                        { ["code"] = "ScopeLocked", ["message"] = "Contoso private detail" } }) : null;

                var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => ConfigureWebAppAsync(arm, logger: logger));
                CollectionAssert.AreEqual(new[] { "POST " + AppSettings + "/list", "POST " + ConnectionStrings + "/list",
                    "PATCH " + Site }, ConfigurationRequests(arm), string.Join(", ", ConfigurationRequests(arm)));
                StringAssert.Contains(error.Message, "ScopeLocked");
                Assert.IsFalse(error.ToString().Contains("Contoso private detail"));
                Assert.IsFalse(logger.Lines.Any(l => l.Message.Contains("Scale the plan")));
            }
        }

        [TestMethod]
        public async Task AppService_PausingReplacesOnlyTheWindowsPauseKeyAndCleanupPreservesOperatorValues()
        {
            using (var arm = new FakeArm())
            {
                arm.AddSite("1.2");
                arm.Resource(AppSettings)["properties"] = new JObject { ["wEbJoBs_sToPpEd"] = "0", ["Storage"] = "Καλημέρα; app  " };
                var operatorConnection = new JObject { ["type"] = "MySql", ["value"] = "Καλημέρα; db  " };
                arm.Resource(ConnectionStrings)["properties"]["wEbJoBs_sToPpEd"] = operatorConnection;
                var site = (await arm.Group.GetWebSiteAsync("contoso-web")).Value;

                await ConfigureAzureComponentsTasks.PauseUpgradeWebJobsAsync(site, CancellationToken.None);
                var sent = (JObject)arm.Resource(AppSettings)["properties"];
                Assert.AreEqual("1", (string)sent["WEBJOBS_STOPPED"]);
                Assert.IsNull(sent["wEbJoBs_sToPpEd"], "Do not send conflicting pause values to Windows.");
                Assert.AreEqual("Καλημέρα; app  ", (string)sent["Storage"]);
                await ConfigureAzureComponentsTasks.EnsureWebJobsRunningAsync(site, NullLogger.Instance);
                Assert.IsTrue(JToken.DeepEquals(new JObject { ["Storage"] = "Καλημέρα; app  " }, arm.Resource(AppSettings)["properties"]));
                Assert.IsTrue(JToken.DeepEquals(operatorConnection, arm.Resource(ConnectionStrings)["properties"]["wEbJoBs_sToPpEd"]));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AppService_PauseRejectsIncompleteReadsAndRedactsRejectedWrites(bool refuseWrite)
        {
            using (var arm = new FakeArm())
            {
                arm.AddSite("1.2");
                const string value = "Contoso private detail";
                arm.Resource(AppSettings)["properties"]["ContosoOperator"] = value;
                var site = (await arm.Group.GetWebSiteAsync("contoso-web")).Value;
                arm.OverrideResponse = (method, path) =>
                {
                    if (!refuseWrite && method == "POST" && path == AppSettings + "/list")
                        return FakeArm.Json(HttpStatusCode.OK, new JObject());
                    if (refuseWrite && method == "PUT" && path == AppSettings)
                        return FakeArm.Json(HttpStatusCode.Forbidden, new JObject { ["error"] = new JObject { ["code"] = value, ["message"] = value } });
                    return null;
                };

                Exception error = null;
                try { await ConfigureAzureComponentsTasks.PauseUpgradeWebJobsAsync(site, CancellationToken.None); }
                catch (Exception ex) { error = ex; }
                Assert.AreEqual(value, (string)arm.Resource(AppSettings)["properties"]["ContosoOperator"],
                    "A missing list must not turn the pause into a replacement of every operator setting.");
                Assert.IsNotNull(error);
                Assert.IsFalse(error.ToString().Contains(value), "The pause failure must not include the ARM body or an arbitrary error code.");
                Assert.IsInstanceOfType(error, typeof(InvalidOperationException));
                CollectionAssert.AreEqual(refuseWrite ? new[] { "POST " + AppSettings + "/list", "PUT " + AppSettings } :
                    new[] { "POST " + AppSettings + "/list" }, ConfigurationRequests(arm));
                StringAssert.Contains(error.Message, refuseWrite ? "HTTP 403" : "complete collection");
                Assert.IsFalse(error.Message.Contains("No replacement was sent"), "Earlier configuration writes may already have succeeded.");
            }
        }

        [TestMethod]
        public async Task AppService_CleanupDoesNotCertifyAMissingListAsAnEmptyCollection()
        {
            using (var arm = new FakeArm())
            {
                arm.AddSite("1.2");
                var settings = arm.Resource(AppSettings);
                settings["properties"]["WEBJOBS_STOPPED"] = "1";
                var site = (await arm.Group.GetWebSiteAsync("contoso-web")).Value;
                var reads = 0;
                arm.OverrideResponse = (method, path) =>
                {
                    if (method == "POST" && path == AppSettings + "/list" && ++reads == 2)
                        return FakeArm.Json(HttpStatusCode.OK, new JObject());
                    // The first PUT was acknowledged but the pause is still present.
                    if (method == "PUT" && path == AppSettings && reads == 1)
                        return FakeArm.Json(HttpStatusCode.OK, settings);
                    return null;
                };
                var logger = new CapturingLogger();

                await ConfigureAzureComponentsTasks.EnsureWebJobsRunningAsync(site, logger);

                Assert.IsTrue(JToken.DeepEquals(new JObject(), arm.Resource(AppSettings)["properties"]),
                    "An incomplete verification must retry, not report running while the WebJobs are still paused.");
                Assert.AreEqual(4, reads);
                Assert.IsTrue(logger.Lines.Any(l => l.Message.Contains("outcome=Verified")));
            }
        }

        private static async Task ConfigureWebAppAsync(FakeArm arm, string bus = null, ILogger logger = null, CancellationToken token = default)
        {
            var config = new SolutionInstallConfig
            {
                ActivityAccount = new AppRegistrationCredentials(Sub, "synthetic-secret", Sub),
                CognitiveServicesEnabled = false,
                FoundryPromptEnabled = false
            };
            var tasks = new ConfigureAzureComponentsTasks(config, logger ?? NullLogger.Instance, null, null, null) { CancellationToken = token };
            var site = (await arm.Group.GetWebSiteAsync("contoso-web")).Value;
            await tasks.ConfigureWebApp(site, await arm.Plan("contoso-web"), "Contoso SQL", "Contoso storage",
                null, null, null, bus, new Uri("https://contoso-vault.vault.azure.net/"));
        }

        // ---------------------------------------------------------------- Kudu without SCM basic auth (R11-5)

        [TestMethod]
        public async Task Kudu_IsCalledWithAnEntraToken_OnTheSitesOwnScmHost()
        {
            using (var arm = new FakeArm())
            using (var kuduSite = new FakeKudu(HttpStatusCode.OK))
            {
                arm.AddSite(minTls: "1.2");
                var site = (await arm.Group.GetWebSiteAsync("contoso-web")).Value;
                using (var kudu = new KuduAccess(site, new SyntheticCredential(), kuduSite))
                using (var client = new HttpClient(kudu, disposeHandler: false))
                {
                    await kudu.InitialiseAsync(CancellationToken.None);
                    (await client.GetAsync(AppServiceWebJobHealthVerifier.BuildKuduContinuousWebJobsUri(kudu.ScmRootUrl))).Dispose();

                    Assert.AreEqual("https://contoso-web.scm.azurewebsites.net", kudu.ScmRootUrl);
                    Assert.AreEqual("Bearer synthetic", kuduSite.Authorizations.Single());
                    Assert.IsTrue(kudu.UsesEntraToken);
                    Assert.IsFalse(arm.Requests.Any(r => r.Path.IndexOf("basicPublishingCredentialsPolicies/scm", StringComparison.OrdinalIgnoreCase) >= 0
                        || r.Path.EndsWith("/publishxml", StringComparison.OrdinalIgnoreCase)),
                        "With a token there is no need for the publishing profile or the SCM basic-auth policy.");
                }
            }
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task Kudu_RefusingTheToken_FallsBackToBasicOnlyWhileItIsOn_AndNeverSwitchesItOn(bool basicAuthOn)
        {
            using (var arm = new FakeArm())
            using (var kuduSite = new FakeKudu(HttpStatusCode.Unauthorized))
            {
                arm.AddSite(minTls: "1.2");
                arm.Add(Site + "/basicPublishingCredentialsPolicies/scm", new JObject
                {
                    ["name"] = "scm", ["type"] = "Microsoft.Web/sites/basicPublishingCredentialsPolicies", ["properties"] = new JObject { ["allow"] = basicAuthOn }
                });
                arm.PostText[Site + "/publishxml"] = "<publishData><publishProfile publishMethod=\"MSDeploy\" publishUrl=\"contoso-web.scm.azurewebsites.net:443\" " +
                    "userName=\"$contoso-web\" userPWD=\"not-a-real-password\" /></publishData>";
                var site = (await arm.Group.GetWebSiteAsync("contoso-web")).Value;
                using (var kudu = new KuduAccess(site, new SyntheticCredential(), kuduSite))
                using (var client = new HttpClient(kudu, disposeHandler: false))
                {
                    await kudu.InitialiseAsync(CancellationToken.None);
                    var refused = await client.GetAsync(kudu.ScmRootUrl + "/api/continuouswebjobs");
                    Assert.IsTrue(KuduAccess.IsAuthRejection(refused.StatusCode));

                    if (basicAuthOn)
                    {
                        await kudu.UseBasicCredentialsAsync(CancellationToken.None);
                        (await client.GetAsync(kudu.ScmRootUrl + "/api/continuouswebjobs")).Dispose();
                        StringAssert.StartsWith(kuduSite.Authorizations.Last(), "Basic ");
                    }
                    else
                    {
                        var ex = await Assert.ThrowsExceptionAsync<CloudInstallEngine.Models.InstallException>(() => kudu.UseBasicCredentialsAsync(CancellationToken.None));
                        StringAssert.Contains(ex.Message, "does not turn SCM basic authentication back on");
                        Assert.AreEqual(1, kuduSite.Authorizations.Count, "Nothing else is sent to Kudu.");
                    }
                    Assert.IsFalse(arm.Mutations.Any(m => m.Method != "POST"),
                        "The installer never writes the SCM basic-auth policy: " + string.Join(", ", arm.Mutations.Select(m => m.Method + " " + m.Path)));
                }
            }
        }

        /// <summary>A Kudu site that answers every request with one status and records the Authorization headers.</summary>
        private sealed class FakeKudu : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            public readonly List<string> Authorizations = new List<string>();
            public FakeKudu(HttpStatusCode status) { _status = status; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Authorizations.Add(request.Headers.Authorization?.ToString());
                return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
            }
        }

        // ---------------------------------------------------------------- SQL server (R11-5, R11-6)

        [TestMethod]
        public async Task SqlServer_NoMinimumTls_IsRaisedByPatch_AndTheServersTagsSurvive()
        {
            using (var arm = new FakeArm())
            {
                arm.AddSqlServer(minTls: "None");
                var task = new SqlServerTask(TaskConfig.GetConfigForName("contoso-sql")
                        .AddSetting(SqlServerTask.CONFIG_KEY_USERNAME, "contoso-admin").AddSetting(SqlServerTask.CONFIG_KEY_PASSWORD, "synthetic"),
                    NullLogger.Instance, new AzureLocation("westeurope"), new Dictionary<string, string>()) { Container = arm.Group };

                await task.ExecuteTaskReturnResult(null);

                var write = arm.Mutations.Single();
                Assert.AreEqual("PATCH", write.Method);
                Assert.AreEqual("1.2", (string)write.Body.SelectToken("properties.minimalTlsVersion"),
                    "'None' (no minimum at all) is weaker than TLS 1.2, but compared as a string it never was.");
                Assert.IsNull(write.Body["tags"]);
                Assert.AreEqual("Contoso-42", (string)arm.Resource(SqlServer)["tags"]["CostCentre"]);
            }
        }

        [TestMethod]
        public async Task SqlServer_AtTls13_IsLeftAlone()
        {
            using (var arm = new FakeArm())
            {
                arm.AddSqlServer(minTls: "1.3");
                var task = new SqlServerTask(TaskConfig.GetConfigForName("contoso-sql")
                        .AddSetting(SqlServerTask.CONFIG_KEY_USERNAME, "contoso-admin").AddSetting(SqlServerTask.CONFIG_KEY_PASSWORD, "synthetic"),
                    NullLogger.Instance, new AzureLocation("westeurope"), new Dictionary<string, string>()) { Container = arm.Group };

                await task.ExecuteTaskReturnResult(null);

                Assert.AreEqual(0, arm.Mutations.Count);
            }
        }

        // ---------------------------------------------------------------- SQL Azure-services exception (R11-5)

        [DataTestMethod]
        [DataRow("vnet+endpoint", false)]
        [DataRow("covered-ips", false)]
        [DataRow("no-path", true)]
        public async Task SqlFirewall_AnExceptionTheOperatorRemoved_StaysRemovedUnlessTheAppWouldBeCutOff(string appPath, bool expectRecreated)
        {
            using (var arm = new FakeArm())
            {
                arm.AddSqlServer(minTls: "1.2", approvedEndpoint: appPath == "vnet+endpoint",
                    firewallRules: appPath == "covered-ips" ? new[] { ("Contoso office", "203.0.113.0", "203.0.113.255") } : new (string, string, string)[0]);
                arm.AddSite(minTls: "1.2", integrationSubnet: appPath == "vnet+endpoint"
                    ? Rg + "/providers/Microsoft.Network/virtualNetworks/contoso-vnet/subnets/integration" : null);
                var logger = new CapturingLogger();
                var task = new SqlServerFirewallConfigTask(TaskConfig.GetConfigForName("Contoso setup rule"), logger, new AzureLocation("westeurope"),
                    appServiceName: "contoso-web", serverCreatedThisRun: () => false) { Container = arm.Group };

                await task.EnsureAzureServicesRuleAsync(await arm.SqlServerResource());

                var recreated = arm.Mutations.Any(m => m.Method == "PUT" && m.Path.EndsWith("/firewallRules/AllowAllWindowsAzureIps", StringComparison.OrdinalIgnoreCase));
                Assert.AreEqual(expectRecreated, recreated);
                if (expectRecreated)
                    StringAssert.Contains(logger.Single(LogLevel.Warning), "no other way to reach the server");
                else
                    Assert.IsTrue(logger.Lines.Any(l => l.Level == LogLevel.Information && l.Message.Contains("Leaving it removed")));
            }
        }

        [TestMethod]
        public async Task SqlFirewall_ANewServer_GetsTheAzureServicesException()
        {
            using (var arm = new FakeArm())
            {
                arm.AddSqlServer(minTls: "1.2", approvedEndpoint: true);
                var task = new SqlServerFirewallConfigTask(TaskConfig.GetConfigForName("Contoso setup rule"), NullLogger.Instance, new AzureLocation("westeurope"),
                    appServiceName: "contoso-web", serverCreatedThisRun: () => true) { Container = arm.Group };

                await task.EnsureAzureServicesRuleAsync(await arm.SqlServerResource());

                var write = arm.Mutations.Single();
                Assert.AreEqual("PUT", write.Method);
                StringAssert.EndsWith(write.Path, "/firewallRules/AllowAllWindowsAzureIps");
                Assert.AreEqual("0.0.0.0", (string)write.Body.SelectToken("properties.startIpAddress"));
            }
        }

        // ---------------------------------------------------------------- Key Vault bypass (R11-5)

        [TestMethod]
        public void KeyVault_AVaultHardenedToNoBypass_KeepsIt()
        {
            var existing = new KeyVaultNetworkRuleSet { DefaultAction = KeyVaultNetworkRuleAction.Deny, Bypass = KeyVaultNetworkRuleBypassOption.None };

            var ruleSet = KeyVaultFirewallConfigTask.BuildFirewallRuleSet(existing, new[] { "203.0.113.10" }, null);

            Assert.AreEqual(KeyVaultNetworkRuleBypassOption.None, ruleSet.Bypass, "Trusted-services bypass must not be switched back on.");
            Assert.AreEqual(KeyVaultNetworkRuleBypassOption.AzureServices,
                KeyVaultFirewallConfigTask.BuildFirewallRuleSet(null, null, null).Bypass, "A new vault still allows trusted services.");
        }

        // ---------------------------------------------------------------- Azure AI Language (R11-6)

        [TestMethod]
        public async Task Language_AMissingSubdomainIsSetByPatch_NotByAPutThatDropsTagsAndNetworkRules()
        {
            using (var arm = new FakeArm())
            {
                var account = Rg + "/providers/Microsoft.CognitiveServices/accounts/contoso-language";
                arm.Add(account, new JObject
                {
                    ["name"] = "contoso-language", ["type"] = "Microsoft.CognitiveServices/accounts", ["location"] = "westeurope",
                    ["kind"] = "TextAnalytics", ["sku"] = new JObject { ["name"] = "S" },
                    ["tags"] = new JObject { ["CostCentre"] = "Contoso-42" },
                    ["properties"] = new JObject
                    {
                        ["publicNetworkAccess"] = "Enabled", ["disableLocalAuth"] = true,
                        ["networkAcls"] = new JObject { ["defaultAction"] = "Deny" }
                    }
                });
                arm.PostResponses[account + "/listKeys"] = new JObject { ["key1"] = "synthetic", ["key2"] = "synthetic" };
                var task = new TextAnalyticsInstallTask(TaskConfig.GetConfigForName("contoso-language"), NullLogger.Instance,
                    new AzureLocation("westeurope"), new Dictionary<string, string>()) { Container = arm.Group };

                await task.ExecuteTaskReturnResult(null);

                var write = arm.Mutations.Single(m => m.Method != "POST");
                Assert.AreEqual("PATCH", write.Method);
                Assert.AreEqual("contoso-language", (string)write.Body.SelectToken("properties.customSubDomainName"));
                Assert.IsNull(write.Body["tags"]);
                Assert.IsNull(write.Body.SelectToken("properties.networkAcls"), "The update sends only what it changes.");
                Assert.AreEqual("Contoso-42", (string)arm.Resource(account)["tags"]["CostCentre"]);
                Assert.AreEqual(true, (bool)arm.Resource(account).SelectToken("properties.disableLocalAuth"));
            }
        }

        // ---------------------------------------------------------------- App Service plan (X4-N4)

        [TestMethod]
        public async Task Plan_ANewWebApp_GetsThePlanNameTheOperatorConfigured()
        {
            using (var arm = new FakeArm())
            {
                arm.KnownLists.Add(Rg + "/providers/Microsoft.Web/sites");
                arm.KnownLists.Add(Rg + "/providers/Microsoft.Web/serverfarms");
                var task = PlanTaskFromTheInstallJob(arm, webAppName: "contoso-web", planName: "contoso-plan");

                var plan = await task.ExecuteTaskReturnResult(null);

                var write = arm.Mutations.Single();
                Assert.AreEqual("PUT", write.Method);
                Assert.AreEqual(Rg + "/providers/Microsoft.Web/serverfarms/contoso-plan", write.Path, true,
                    "The plan used to be created under the web app's name, whatever the configuration said.");
                Assert.AreEqual("contoso-plan", plan.Data.Name);
            }
        }

        [TestMethod]
        public async Task Plan_AnExistingWebApp_StaysOnItsWebAppNamedPlan_AndNoSecondPlanIsCreated()
        {
            using (var arm = new FakeArm())
            {
                arm.AddSite(minTls: "1.2");
                arm.KnownLists.Add(Rg + "/providers/Microsoft.Web/serverfarms");
                var logger = new CapturingLogger();
                var task = PlanTaskFromTheInstallJob(arm, webAppName: "contoso-web", planName: "contoso-plan", logger: logger);

                var plan = await task.ExecuteTaskReturnResult(null);

                Assert.AreEqual("contoso-web", plan.Data.Name, "Existing deployments run on a plan named after the web app.");
                Assert.AreEqual(0, arm.Mutations.Count, "No plan may be created or changed for an existing web app.");
                Assert.IsTrue(logger.Lines.Any(l => l.Message.Contains("is only used when the installer creates the web app")));
            }
        }

        [TestMethod]
        public async Task Plan_AnExistingWebAppOnASharedPlanInAnotherResourceGroup_UsesItAsItIs_WithoutWritingToIt()
        {
            // A plan outside the deployment's resource group is one the operator chose to share. Writing the solution's
            // tags to it needs access the installer's identity need not have there, and a refused write aborted the
            // install. Installers before #828 never touched it.
            const string sharedPlan = "/subscriptions/" + Sub + "/resourceGroups/contoso-hosting/providers/Microsoft.Web/serverfarms/contoso-shared";
            using (var arm = new FakeArm())
            {
                arm.AddSite(minTls: "1.2");
                arm.Resource(Site)["properties"]["serverFarmId"] = sharedPlan;
                arm.Add(sharedPlan, new JObject
                {
                    ["name"] = "contoso-shared", ["type"] = "Microsoft.Web/serverfarms", ["location"] = "westeurope", ["kind"] = "app",
                    ["sku"] = new JObject { ["name"] = "P1v3", ["tier"] = "PremiumV3", ["family"] = "Pv3" }, ["properties"] = new JObject()
                });
                var logger = new CapturingLogger();
                var task = PlanTaskFromTheInstallJob(arm, webAppName: "contoso-web", planName: "contoso-plan", logger: logger,
                    tags: new[] { new AzTag("CostCentre", "Contoso-42") });

                var plan = await task.ExecuteTaskReturnResult(null);

                Assert.AreEqual(sharedPlan, plan.Id.ToString(), true, "The web app stays on the plan it runs on.");
                Assert.AreEqual(0, arm.Mutations.Count, "Nothing is written to a plan outside the deployment's resource group: no tags, no second plan.");
                Assert.IsTrue(logger.Lines.Any(l => l.Message.Contains("outside this deployment's")));
            }
        }

        [TestMethod]
        public async Task Plan_AnExistingWebAppOnAPlanInTheDeploymentResourceGroup_StillGetsTheConfiguredTags()
        {
            using (var arm = new FakeArm())
            {
                arm.AddSite(minTls: "1.2");
                const string ownPlan = Rg + "/providers/Microsoft.Web/serverfarms/contoso-web";
                arm.Add(ownPlan + "/providers/Microsoft.Resources/tags/default", new JObject
                {
                    ["name"] = "default", ["type"] = "Microsoft.Resources/tags", ["properties"] = new JObject { ["tags"] = new JObject() }
                });
                var task = PlanTaskFromTheInstallJob(arm, webAppName: "contoso-web", planName: "contoso-plan",
                    tags: new[] { new AzTag("CostCentre", "Contoso-42") });

                var plan = await task.ExecuteTaskReturnResult(null);

                Assert.AreEqual("contoso-web", plan.Data.Name);
                var write = arm.Mutations.Single();
                Assert.AreEqual("PATCH", write.Method);
                Assert.AreEqual(ownPlan + "/providers/Microsoft.Resources/tags/default", write.Path, true);
                Assert.AreEqual("Contoso-42", (string)write.Body.SelectToken("properties.tags.CostCentre"), "The deployment's own plan is tagged, as before.");
            }
        }

        private static AppServicePlanTask PlanTaskFromTheInstallJob(FakeArm arm, string webAppName, string planName, ILogger logger = null,
            IEnumerable<AzTag> tags = null)
        {
            var job = new AzurePaaSInstallJob(logger ?? NullLogger.Instance, BackendConfig(webAppName, planName, tags), arm.Subscription);
            var task = job.PlanTask;
            task.Container = arm.Group;
            return task;
        }

        private static SolutionInstallConfig BackendConfig(string webAppName, string planName, IEnumerable<AzTag> tags = null) =>
            new SolutionInstallConfig
            {
                ServiceBusEnabled = false, CognitiveServicesEnabled = false, FoundryPromptEnabled = false,
                HealthMonitoring = new HealthMonitoringConfig { Enabled = false },
                Subscription = new AzureSubscription(Sub, "Contoso"),
                InstallerAccount = new AppRegistrationCredentials(Sub, "synthetic-secret", Sub),
                ActivityAccount = new AppRegistrationCredentials(Sub, "synthetic-secret", Sub),
                ResourceGroupName = "contoso", AzureLocationName = "westeurope",
                AppServiceWebAppName = webAppName, AppServicePlanName = planName,
                SQLServerName = "contoso-sql", SQLServerDatabaseName = "contoso-analytics", KeyVaultName = "contoso-vault",
                StorageAccountName = "contosoanalytics", AppInsightsName = "contoso-insights", AppInsightsWorkspaceName = "contoso-workspace",
                Tags = tags?.ToList() ?? new List<AzTag>()
            };

        // ---------------------------------------------------------------- Runtime RBAC (R11-8)

        [TestMethod]
        public async Task Rbac_DataRolesAreAssignedOnTheirOneResource_AndReaderStaysOnTheResourceGroup()
        {
            using (var arm = new FakeArm())
            {
                arm.AddResourceGroup();
                arm.AddRoleDefinitions("Reader", "Storage Blob Data Contributor", "Storage Table Data Contributor",
                    "Cognitive Services User", ResourceSecurityInstallJob.FOUNDRY_OPENAI_USER_ROLE_NAME, "Azure Service Bus Data Owner");
                var config = RbacConfig();
                SeedRuntimePrincipal(config);

                var job = new ResourceSecurityInstallJob(NullLogger.Instance, config, arm.Subscription);
                await job.Install();

                var assignments = arm.Mutations.Where(m => m.Method == "PUT")
                    .Select(m => m.Path.Substring(0, m.Path.IndexOf("/providers/Microsoft.Authorization/", StringComparison.OrdinalIgnoreCase)).ToLowerInvariant())
                    .ToList();
                var expected = new[]
                {
                    Rg,
                    Storage, Storage,
                    Rg + "/providers/Microsoft.CognitiveServices/accounts/contoso-language",
                    Rg + "/providers/Microsoft.CognitiveServices/accounts/contoso-openai",
                    Rg + "/providers/Microsoft.ServiceBus/namespaces/contoso-bus/queues/" + AzurePaaSInstallJob.CALLS_QUEUE_NAME
                }.Select(s => s.ToLowerInvariant()).ToList();
                CollectionAssert.AreEquivalent(expected, assignments, "Only Reader may be assigned on the resource group.");
                Assert.IsFalse(arm.Requests.Any(r => r.Method == "DELETE"), "The installer never removes a role assignment.");
            }
        }

        [TestMethod]
        public async Task Rbac_ABroadAssignmentFromAnEarlierInstaller_IsReportedAndKept_AfterTheNarrowOneIsAdded()
        {
            using (var arm = new FakeArm())
            {
                var role = arm.AddRoleDefinitions("Storage Table Data Contributor").Single();
                var principal = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
                var legacy = Rg + "/providers/Microsoft.Authorization/roleAssignments/00000000-0000-0000-0000-0000000000b1";
                arm.InheritedAssignments[Storage] = new JArray(Assignment(legacy, Rg, role, principal));
                var logger = new CapturingLogger();
                var task = new RoleAssignmentTask(TaskConfig.GetConfigForPropAndVal(RoleAssignmentTask.CONFIG_KEY_ROLE_NAME, "Storage Table Data Contributor")
                        .AddSetting(RoleAssignmentTask.CONFIG_KEY_PRINCIPAL_ID, principal.ToString())
                        .AddSetting(RoleAssignmentTask.CONFIG_KEY_SCOPE_SUFFIX, "providers/Microsoft.Storage/storageAccounts/contosoanalytics"),
                    logger, new AzureLocation("westeurope"), new Dictionary<string, string>()) { Container = arm.Group };

                await task.ExecuteTaskReturnResult(null);

                var write = arm.Mutations.Single();
                Assert.AreEqual("PUT", write.Method, "The narrow assignment is created even though the broad one already grants the role.");
                StringAssert.StartsWith(write.Path.ToLowerInvariant(), (Storage + "/providers/Microsoft.Authorization/roleAssignments/").ToLowerInvariant());
                Assert.AreEqual(RoleAssignmentTask.INSTALLER_ASSIGNMENT_DESCRIPTION, (string)write.Body.SelectToken("properties.description"));
                StringAssert.Contains(logger.Single(LogLevel.Warning), legacy);

                // A second run finds the narrow assignment and writes nothing.
                arm.Mutations.Clear();
                await task.ExecuteTaskReturnResult(null);
                Assert.AreEqual(0, arm.Mutations.Count);
            }
        }

        private static SolutionInstallConfig RbacConfig() => new SolutionInstallConfig
        {
            ServiceBusEnabled = true, CognitiveServicesEnabled = true, FoundryPromptEnabled = true,
            HealthMonitoring = new HealthMonitoringConfig { Enabled = false },
            Subscription = new AzureSubscription(Sub, "Contoso"),
            InstallerAccount = new AppRegistrationCredentials(Sub, "synthetic-secret", Sub),
            ActivityAccount = new AppRegistrationCredentials("00000000-0000-0000-0000-0000000000c1", "synthetic-secret", "00000000-0000-0000-0000-0000000000c2"),
            ResourceGroupName = "contoso", AzureLocationName = "westeurope",
            StorageAccountName = "contosoanalytics", ServiceBusName = "contoso-bus",
            CognitiveServiceName = "contoso-language", FoundryPromptResourceName = "contoso-openai"
        };

        /// <summary>The runtime principal's object ID, so the role tasks don't ask Microsoft Entra for it.</summary>
        private static void SeedRuntimePrincipal(SolutionInstallConfig config)
        {
            var cache = (System.Collections.Concurrent.ConcurrentDictionary<string, string>)typeof(ServicePrincipalResolver)
                .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            cache[config.RuntimeAccountOffice365.DirectoryId + "|" + config.RuntimeAccountOffice365.ClientId] = "00000000-0000-0000-0000-0000000000a2";
        }

        private static JObject Assignment(string id, string scope, string roleDefinitionId, Guid principal) => new JObject
        {
            ["id"] = id, ["name"] = id.Substring(id.LastIndexOf('/') + 1), ["type"] = "Microsoft.Authorization/roleAssignments",
            ["properties"] = new JObject
            {
                ["scope"] = scope, ["roleDefinitionId"] = roleDefinitionId, ["principalId"] = principal.ToString(), ["principalType"] = "ServicePrincipal"
            }
        };

        // ---------------------------------------------------------------- Fake Azure Resource Manager

        private sealed class FakeArm : HttpMessageHandler
        {
            private readonly Dictionary<string, JObject> _resources = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            private readonly HttpClient _http;
            public readonly HashSet<string> KnownLists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, JObject> PostResponses = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, HttpStatusCode> PostStatuses = new Dictionary<string, HttpStatusCode>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> PostText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Func<string, string, HttpResponseMessage> OverrideResponse;
            public readonly Dictionary<string, JArray> InheritedAssignments = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
            public readonly List<(string Method, string Path, JObject Body)> Requests = new List<(string, string, JObject)>();
            public readonly List<(string Method, string Path, JObject Body)> Mutations = new List<(string, string, JObject)>();
            public readonly SubscriptionResource Subscription;
            public readonly ResourceGroupResource Group;

            public FakeArm()
            {
                _http = new HttpClient(this, disposeHandler: false);
                var options = new ArmClientOptions { Transport = new HttpClientTransport(_http) };
                options.Retry.MaxRetries = 0;
                var client = new ArmClient(new SyntheticCredential(), Sub, options);
                Subscription = client.GetSubscriptionResource(new ResourceIdentifier("/subscriptions/" + Sub));
                Group = client.GetResourceGroupResource(new ResourceIdentifier(Rg));
            }

            public JObject Resource(string path) => _resources[path];

            public void Add(string path, JObject resource)
            {
                resource["id"] = path;
                _resources[path] = resource;
                KnownLists.Add(Parent(path));
            }

            public void AddResourceGroup() =>
                Add(Rg, new JObject { ["name"] = "contoso", ["type"] = "Microsoft.Resources/resourceGroups", ["location"] = "westeurope", ["properties"] = new JObject() });

            public void AddSite(string minTls, string integrationSubnet = null)
            {
                Add(Site, new JObject
                {
                    ["name"] = "contoso-web", ["type"] = "Microsoft.Web/sites", ["location"] = "westeurope", ["kind"] = "app",
                    ["tags"] = new JObject { ["CostCentre"] = "Contoso-42" },
                    ["identity"] = new JObject { ["type"] = "SystemAssigned", ["principalId"] = Sub, ["tenantId"] = Sub },
                    ["properties"] = new JObject
                    {
                        ["serverFarmId"] = Rg + "/providers/Microsoft.Web/serverfarms/contoso-web",
                        ["publicNetworkAccess"] = "Enabled", ["httpsOnly"] = true,
                        ["defaultHostName"] = "contoso-web.azurewebsites.net",
                        ["hostNames"] = new JArray("contoso-web.azurewebsites.net"),
                        ["hostNameSslStates"] = new JArray(
                            new JObject { ["name"] = "contoso-web.azurewebsites.net", ["hostType"] = "Standard", ["sslState"] = "Disabled" },
                            new JObject { ["name"] = "contoso-web.scm.azurewebsites.net", ["hostType"] = "Repository", ["sslState"] = "Disabled" }),
                        ["possibleOutboundIpAddresses"] = "203.0.113.10,203.0.113.11",
                        ["virtualNetworkSubnetId"] = integrationSubnet
                    }
                });
                Add(Site + "/config/web", new JObject
                {
                    ["name"] = "web", ["type"] = "Microsoft.Web/sites/config",
                    ["properties"] = new JObject { ["minTlsVersion"] = minTls, ["ftpsState"] = "Disabled", ["alwaysOn"] = true }
                });
                foreach (var name in new[] { "appsettings", "connectionstrings" })
                    Add(Site + "/config/" + name, new JObject { ["name"] = name, ["type"] = "Microsoft.Web/sites/config", ["properties"] = new JObject() });
                Add(Site + "/basicPublishingCredentialsPolicies/ftp", new JObject
                {
                    ["name"] = "ftp", ["type"] = "Microsoft.Web/sites/basicPublishingCredentialsPolicies", ["properties"] = new JObject { ["allow"] = false }
                });
                AddPlan("contoso-web");
            }

            public void AddPlan(string name) => Add(Rg + "/providers/Microsoft.Web/serverfarms/" + name, new JObject
            {
                ["name"] = name, ["type"] = "Microsoft.Web/serverfarms", ["location"] = "westeurope", ["kind"] = "app",
                ["sku"] = new JObject { ["name"] = "B1", ["tier"] = "Basic", ["family"] = "B" }, ["properties"] = new JObject()
            });

            public async Task<AppServicePlanResource> Plan(string name) => (await Group.GetAppServicePlanAsync(name)).Value;

            public void AddSqlServer(string minTls, bool approvedEndpoint = false, IEnumerable<(string Name, string Start, string End)> firewallRules = null)
            {
                Add(SqlServer, new JObject
                {
                    ["name"] = "contoso-sql", ["type"] = "Microsoft.Sql/servers", ["location"] = "westeurope",
                    ["tags"] = new JObject { ["CostCentre"] = "Contoso-42" },
                    ["properties"] = new JObject
                    {
                        ["minimalTlsVersion"] = minTls, ["publicNetworkAccess"] = "Enabled",
                        ["fullyQualifiedDomainName"] = "contoso-sql.database.windows.net",
                        ["privateEndpointConnections"] = approvedEndpoint
                            ? new JArray(new JObject
                            {
                                ["id"] = SqlServer + "/privateEndpointConnections/contoso-endpoint",
                                ["properties"] = new JObject { ["privateLinkServiceConnectionState"] = new JObject { ["status"] = "Approved" } }
                            })
                            : new JArray()
                    }
                });
                KnownLists.Add(SqlServer + "/firewallRules");
                KnownLists.Add(SqlServer + "/virtualNetworkRules");
                foreach (var rule in firewallRules ?? Enumerable.Empty<(string, string, string)>())
                    Add(SqlServer + "/firewallRules/" + rule.Name, new JObject
                    {
                        ["name"] = rule.Name, ["type"] = "Microsoft.Sql/servers/firewallRules",
                        ["properties"] = new JObject { ["startIpAddress"] = rule.Start, ["endIpAddress"] = rule.End }
                    });
            }

            public async Task<SqlServerResource> SqlServerResource() => (await Group.GetSqlServerAsync("contoso-sql")).Value;

            public List<string> AddRoleDefinitions(params string[] roleNames)
            {
                var ids = new List<string>();
                var list = new JArray();
                foreach (var roleName in roleNames)
                {
                    var id = "/subscriptions/" + Sub + "/providers/Microsoft.Authorization/roleDefinitions/" + Guid.NewGuid();
                    ids.Add(id);
                    list.Add(new JObject
                    {
                        ["id"] = id, ["name"] = id.Substring(id.LastIndexOf('/') + 1), ["type"] = "Microsoft.Authorization/roleDefinitions",
                        ["properties"] = new JObject { ["roleName"] = roleName, ["type"] = "BuiltInRole", ["permissions"] = new JArray(), ["assignableScopes"] = new JArray("/") }
                    });
                }
                _roleDefinitions = list;
                return ids;
            }
            private JArray _roleDefinitions = new JArray();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath).TrimEnd('/');
                var text = request.Content == null ? null : await request.Content.ReadAsStringAsync();
                var body = string.IsNullOrWhiteSpace(text) ? null : JObject.Parse(text);
                var entry = (request.Method.Method, path, body);
                Requests.Add(entry);

                if (request.Method == HttpMethod.Get)
                {
                    if (path.EndsWith("/providers/Microsoft.Authorization/roleDefinitions", StringComparison.OrdinalIgnoreCase))
                        return Json(HttpStatusCode.OK, new JObject { ["value"] = _roleDefinitions });
                    if (path.EndsWith("/providers/Microsoft.Authorization/roleAssignments", StringComparison.OrdinalIgnoreCase))
                    {
                        var scope = path.Substring(0, path.Length - "/providers/Microsoft.Authorization/roleAssignments".Length);
                        var value = new JArray(Children(path));
                        if (InheritedAssignments.TryGetValue(scope, out var inherited))
                            foreach (var item in inherited) value.Add(item);
                        return Json(HttpStatusCode.OK, new JObject { ["value"] = value });
                    }
                    if (_resources.TryGetValue(path, out var resource)) return Json(HttpStatusCode.OK, resource);
                    if (KnownLists.Contains(path)) return Json(HttpStatusCode.OK, new JObject { ["value"] = new JArray(Children(path)) });
                    return Json(HttpStatusCode.NotFound, new JObject { ["error"] = new JObject { ["code"] = "ResourceNotFound", ["message"] = "Synthetic: not found" } });
                }

                Mutations.Add(entry);
                var response = OverrideResponse?.Invoke(request.Method.Method, path);
                if (response != null) return response;
                if (request.Method.Method == "POST")
                {
                    if (PostText.TryGetValue(path, out var xml)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
                    if (!PostResponses.TryGetValue(path, out var posted) && (path == AppSettings + "/list" || path == ConnectionStrings + "/list"))
                        posted = Resource(Parent(path));
                    return Json(PostStatuses.TryGetValue(path, out var status) ? status : HttpStatusCode.OK, posted ?? new JObject());
                }
                if (request.Method == HttpMethod.Delete)
                {
                    _resources.Remove(path);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                // ARM semantics: a PUT replaces the whole resource with the body sent ("properties that aren't included
                // ... are reset to the default values"); a PATCH changes only what it names.
                var stored = request.Method == HttpMethod.Put || !_resources.TryGetValue(path, out var current)
                    ? new JObject { ["name"] = path.Substring(path.LastIndexOf('/') + 1), ["location"] = "westeurope" }
                    : (JObject)current.DeepClone();
                if (request.Method == HttpMethod.Put && _resources.TryGetValue(path, out var replaced))
                {
                    stored["type"] = replaced["type"];
                    if (replaced["kind"] != null) stored["kind"] = replaced["kind"];
                }
                if (body != null) stored.Merge(body, new Newtonsoft.Json.Linq.JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
                if (path.IndexOf("/providers/Microsoft.Authorization/roleAssignments/", StringComparison.OrdinalIgnoreCase) > 0)
                {
                    stored["type"] = "Microsoft.Authorization/roleAssignments";
                    stored["properties"]["scope"] = path.Substring(0, path.IndexOf("/providers/Microsoft.Authorization/roleAssignments/", StringComparison.OrdinalIgnoreCase));
                }
                if (path.IndexOf("/providers/Microsoft.Web/serverfarms/", StringComparison.OrdinalIgnoreCase) > 0) stored["type"] = "Microsoft.Web/serverfarms";
                Add(path, stored);
                return Json(HttpStatusCode.OK, stored);
            }

            private IEnumerable<JObject> Children(string collection) =>
                _resources.Where(r => string.Equals(Parent(r.Key), collection, StringComparison.OrdinalIgnoreCase)).Select(r => r.Value).ToList();

            private static string Parent(string path) => path.Substring(0, Math.Max(0, path.LastIndexOf('/')));

            public static HttpResponseMessage Json(HttpStatusCode status, JObject value) =>
                new HttpResponseMessage(status) { Content = new StringContent(value.ToString(), Encoding.UTF8, "application/json") };

            protected override void Dispose(bool disposing)
            {
                if (disposing) _http.Dispose();
                base.Dispose(disposing);
            }
        }

        private sealed class SyntheticCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                new AccessToken("synthetic", DateTimeOffset.MaxValue);
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
        }
    }
}
