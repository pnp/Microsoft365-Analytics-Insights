using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using App.ControlPanel.Engine.Prerequisites;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.KeyVault.Models;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.ServiceBus;
using Azure.ResourceManager.ServiceBus.Models;
using Azure.ResourceManager.Sql;
using CloudInstallEngine;
using CloudInstallEngine.Azure;
using CloudInstallEngine.Azure.InstallTasks;
using CloudInstallEngine.Models;
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
        private const string Sites = Rg + "/providers/Microsoft.Web/sites";
        private const string Plans = Rg + "/providers/Microsoft.Web/serverfarms";
        private const string SharedPlan = "/subscriptions/" + Sub + "/resourceGroups/contoso-hosting/providers/Microsoft.Web/serverfarms/contoso-shared";
        private const string Site = Rg + "/providers/Microsoft.Web/sites/contoso-web";
        private const string AppSettings = Site + "/config/appsettings";
        private const string ConnectionStrings = Site + "/config/connectionstrings";
        private const string FtpPolicy = Site + "/basicPublishingCredentialsPolicies/ftp";
        private const string Vault = Rg + "/providers/Microsoft.KeyVault/vaults/contoso-vault";
        private const string VaultTenant = "00000000-0000-0000-0000-000000000001";
        private const string WebAppPrincipal = "00000000-0000-0000-0000-000000000002";
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

        [DataTestMethod]
        [DataRow("web")]
        [DataRow("contoso-web")]
        public async Task AppService_ReRunOnAHardenedSite_SendsNothing_AndNeverTouchesScmBasicAuth(string configurationName)
        {
            using (var arm = new FakeArm())
            {
                arm.AddSite(minTls: "1.3");
                arm.Resource(Site + "/config/web")["name"] = configurationName;
                arm.Resource(Site)["properties"]["siteConfig"] = new JObject { ["minTlsVersion"] = "1.3" };
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

        [DataTestMethod]
        [DataRow(false, "config", "missing properties")]
        [DataRow(true, "config", "null properties")]
        [DataRow(true, "config", "array properties")]
        [DataRow(true, "config", "wrong ID")]
        [DataRow(false, "config", "empty properties")]
        [DataRow(true, "config", "unknown properties")]
        [DataRow(false, "config", "missing TLS")]
        [DataRow(true, "config", "different TLS")]
        [DataRow(false, "config", "wrong type")]
        [DataRow(true, "config", "wrong name")]
        [DataRow(false, "FTP", "missing properties")]
        [DataRow(true, "FTP", "missing allow")]
        [DataRow(false, "FTP", "null allow")]
        [DataRow(true, "FTP", "invalid allow")]
        [DataRow(true, "FTP", "wrong ID")]
        [DataRow(false, "FTP", "wrong type")]
        [DataRow(true, "FTP", "wrong name")]
        [DataRow(true, "FTP", "parent name")]
        [DataRow(true, "FTP", "denied")]
        [DataRow(true, "config", "cancelled")]
        [DataRow(true, "FTP", "cancelled")]
        public async Task AppService_UnverifiableSecurityReadStopsBeforeAnyChange(bool websiteOnly, string section, string failure)
        {
            using (var arm = new FakeArm())
            using (var cancellation = new CancellationTokenSource())
            {
                const string subnet = Rg + "/providers/Microsoft.Network/virtualNetworks/contoso-vnet/subnets/integration";
                const string privateDetail = "Contoso private diagnostic";
                var config = BackendConfig("CoNtOsO-WeB", "contoso-plan");
                arm.AddBackendInventory(config);
                arm.AddSite("1.3", subnet);
                if (failure != "unknown properties")
                    arm.Resource(Site)["properties"]["siteConfig"] = new JObject { ["minTlsVersion"] = "1.3" };
                arm.Resource(Site)["properties"]["publicNetworkAccess"] = "Disabled"; // A PATCH is pending even with valid TLS.
                var integration = new PrivateNetworkArtifact { Id = Site, TargetId = Site, SubnetId = subnet,
                    Kind = PrivateNetworkProvenance.Integration, CreatedByInstaller = true, CreationConfirmed = true };
                config.PrivateNetworking.AppliedTopology = "Private";
                config.PrivateNetworking.Artifacts.Add(integration);
                var checkpoints = 0;
                var job = new AzurePaaSInstallJob(NullLogger.Instance, config, arm.Subscription, () => checkpoints++)
                    { CancellationToken = cancellation.Token };
                var path = section == "config" ? Site + "/config/web" : FtpPolicy;
                arm.OverrideResponse = (m, p) =>
                {
                    if (m != "GET" || !path.Equals(p, StringComparison.OrdinalIgnoreCase)) return null;
                    var reply = (JObject)arm.Resource(path).DeepClone();
                    switch (failure)
                    {
                        case "missing properties": reply.Property("properties").Remove(); break;
                        case "null properties": reply["properties"] = null; break;
                        case "array properties": reply["properties"] = new JArray(); break;
                        case "empty properties": reply["properties"] = new JObject(); break;
                        case "unknown properties": reply["properties"] = new JObject { ["detail"] = privateDetail }; break;
                        case "missing TLS": ((JObject)reply["properties"]).Property("minTlsVersion").Remove(); break;
                        case "different TLS": reply["properties"]["minTlsVersion"] = "1.0"; break;
                        case "wrong type": reply["type"] = "Microsoft.Web/sites"; break;
                        case "wrong name": reply["name"] = "contoso-other"; break;
                        case "parent name": reply["name"] = "contoso-web"; break;
                        case "missing allow": ((JObject)reply["properties"]).Property("allow").Remove(); break;
                        case "null allow": reply["properties"]["allow"] = null; break;
                        case "invalid allow": reply["properties"]["allow"] = "false"; break;
                        case "wrong ID": reply["id"] = path.Replace("contoso-web", "contoso-other"); break;
                        case "denied": return FakeArm.Json(HttpStatusCode.Forbidden, new JObject { ["error"] = new JObject { ["message"] = privateDetail } });
                        case "cancelled": cancellation.Cancel(); break;
                    }
                    reply["detail"] = privateDetail;
                    return FakeArm.Json(HttpStatusCode.OK, reply);
                };
                var website = job.WebsiteTask;
                website.Container = arm.Group;
                website.CancellationToken = cancellation.Token;
                var plan = await arm.Plan("contoso-web");
                Func<Task> run = () => websiteOnly ? website.ExecuteTaskReturnResult(plan) : job.Install();

                var error = await CaptureDiscoveryFailure(run);
                Assert.IsTrue(arm.Requests.Any(r => r.Method == "GET" && r.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));
                Assert.AreEqual(0, arm.Mutations.Count, string.Join(", ", arm.Mutations.Select(r =>
                    $"{r.Method} {r.Path} (TLS: {r.Body?.SelectToken("properties.siteConfig.minTlsVersion")})")));
                Assert.AreEqual(0, checkpoints);
                Assert.IsFalse(integration.Removed);
                Assert.AreEqual("Private", config.PrivateNetworking.AppliedTopology);
                if (failure == "cancelled")
                    Assert.IsInstanceOfType(error, typeof(OperationCanceledException));
                else
                {
                    Assert.IsNotNull(error);
                    StringAssert.Contains(error.Message, section == "config" ? "site configuration" : "FTP");
                    StringAssert.Contains(error.Message, "retry");
                    Assert.IsFalse(error.ToString().Contains(privateDetail));
                }
            }
        }

        private static async Task<Exception> CaptureDiscoveryFailure(Func<Task> run)
        {
            try { await run(); return null; }
            catch (Exception ex) { return ex; }
        }

        [DataTestMethod]
        [DataRow("1.3", false)]
        [DataRow(null, false)]
        [DataRow(null, true)]
        public async Task AppService_VerifiedSecuritySettingsPreserveOrRaiseTls(string tls, bool omitTls)
        {
            using (var arm = new FakeArm())
            {
                arm.AddSite(tls);
                var properties = (JObject)arm.Resource(Site + "/config/web")["properties"];
                if (omitTls) properties.Property("minTlsVersion").Remove();
                properties["alwaysOn"] = false; // Force a site patch so we can inspect precisely what ARM receives.
                // GetFtpAllowed's documented response has an id and properties.allow, but no type or name.
                arm.OverrideResponse = (m, p) => m == "GET" && p == FtpPolicy
                    ? FakeArm.Json(HttpStatusCode.OK, new JObject { ["id"] = p, ["properties"] = new JObject { ["allow"] = true } }) : null;
                var website = new AzurePaaSInstallJob(NullLogger.Instance, BackendConfig("CONTOSO-WEB", "contoso-plan"), arm.Subscription).WebsiteTask;
                website.Container = arm.Group;

                await website.ExecuteTaskReturnResult(await arm.Plan("contoso-web"));

                var patch = arm.Mutations.Single(r => r.Method == "PATCH" && r.Path == Site).Body;
                Assert.AreEqual(true, (bool)patch.SelectToken("properties.siteConfig.alwaysOn"));
                Assert.AreEqual(tls == null ? "1.2" : null, (string)patch.SelectToken("properties.siteConfig.minTlsVersion"));
                Assert.AreEqual(false, (bool)arm.Mutations.Single(r => r.Method == "PUT" && r.Path == FtpPolicy).Body.SelectToken("properties.allow"));
                Assert.IsTrue(arm.Requests.FindIndex(r => r.Method == "GET" && r.Path == FtpPolicy) < arm.Requests.FindIndex(r => r.Method == "PATCH"));
            }
        }

        [TestMethod]
        public async Task AppService_StandaloneWebsiteChecksTheCurrentHostingSkuBeforeEnablingAlwaysOn()
        {
            using (var arm = new FakeArm())
            {
                arm.AddSite("1.3");
                var previous = await arm.Plan("contoso-web");
                arm.Resource(Plans + "/contoso-web")["sku"] = new JObject { ["name"] = "F1", ["tier"] = "Free" };
                arm.Resource(Site + "/config/web")["properties"]["alwaysOn"] = false;
                var website = new AzurePaaSInstallJob(NullLogger.Instance, BackendConfig("CONTOSO-WEB", "contoso-web"), arm.Subscription).WebsiteTask;
                website.Container = arm.Group;

                await website.ExecuteTaskReturnResult(previous);

                Assert.AreEqual(0, arm.Mutations.Count, "The earlier plan object is not evidence that the site can use Always On now.");
            }
        }

        [TestMethod]
        public async Task AppService_NewWebsiteDoesNotChangeAnUnreadableFtpPolicy()
        {
            using (var arm = new FakeArm())
            {
                arm.KnownLists.Add(Sites);
                arm.AddPlan("contoso-plan");
                var website = new AzurePaaSInstallJob(NullLogger.Instance, BackendConfig("contoso-web", "contoso-plan"), arm.Subscription).WebsiteTask;
                website.Container = arm.Group;
                arm.OverrideResponse = (m, p) =>
                {
                    if (m == "PUT" && p == Site)
                    {
                        arm.AddSite("1.2");
                        arm.Resource(Site)["properties"]["serverFarmId"] = Plans + "/contoso-plan";
                        return FakeArm.Json(HttpStatusCode.OK, arm.Resource(Site));
                    }
                    if (m == "GET" && p == FtpPolicy)
                        return FakeArm.Json(HttpStatusCode.OK, new JObject { ["id"] = FtpPolicy, ["properties"] = new JObject() });
                    return null;
                };
                var plan = await arm.Plan("contoso-plan");

                var failure = await Assert.ThrowsAsync<InstallException>(() => website.ExecuteTaskReturnResult(plan));

                StringAssert.Contains(failure.Message, "FTP");
                CollectionAssert.AreEqual(new[] { "PUT " + Site }, ConfigurationRequests(arm), "Only the confirmed new site can have been created.");
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
                arm.AddBackendInventory(config);
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
        [DataRow(false, false, "contoso-web")]
        [DataRow(false, true, "contoso-web")]
        [DataRow(true, false, "Contoso-Premium")]
        [DataRow(true, false, "cOnToSo-PrEmIuM")]
        public async Task FullInstall_UsesTheRegisteredWebsiteTaskAndOneSnapshotForTheFinalReplacements(bool newSite, bool planConflict, string configuredPlanName)
        {
            using (var arm = new FakeArm())
            {
                const string siteCollection = Rg + "/providers/Microsoft.Web/sites";
                const string premiumPlan = Rg + "/providers/Microsoft.Web/serverfarms/Contoso-Premium";
                if (newSite)
                {
                    arm.AddPlan("Contoso-Premium");
                    arm.Resource(premiumPlan)["sku"] = new JObject { ["name"] = "P1v3", ["tier"] = "PremiumV3", ["family"] = "Pv3" };
                    arm.KnownLists.Add(siteCollection);
                    arm.OverrideResponse = (method, path) =>
                    {
                        if (method != "PUT" || path != Site) return null;
                        arm.AddSite("1.2"); // ARM supplies the host name and config resources on creation.
                        arm.Resource(Site)["properties"]["serverFarmId"] = arm.Requests.Last().Body.SelectToken("properties.serverFarmId");
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
                var config = BackendConfig(newSite ? "contoso-web" : "CONTOSO-WEB", configuredPlanName);
                var installer = new FixtureSolutionInstaller(config, NullLogger.Instance, arm.Subscription);
                var steps = installer.CreateAzureInstallSteps(NullLogger.Instance, arm.Subscription, CancellationToken.None);
                steps.Backend.PlanTask.Container = steps.Backend.WebsiteTask.Container = arm.Group;
                var plan = await steps.Backend.PlanTask.ExecuteTaskReturnResult(null);
                var site = await steps.Backend.WebsiteTask.ExecuteTaskReturnResult(plan);
                await steps.PostCreate.ConfigureWebApp(site, plan, "Contoso SQL", "Contoso storage",
                    null, null, null, null, new Uri("https://contoso-vault.vault.azure.net/"));

                if (newSite)
                {
                    var writes = arm.Mutations.Where(r => r.Path.Equals(premiumPlan, StringComparison.OrdinalIgnoreCase)).ToList();
                    Assert.AreEqual(0, writes.Count, "An existing plan must not be resized: " +
                        string.Join(", ", writes.Select(r => r.Method + " " + r.Body?.SelectToken("sku.name"))));
                    Assert.AreEqual("P1v3", plan.Data.Sku.Name);
                    Assert.AreEqual(premiumPlan, plan.Id.ToString());
                    Assert.AreEqual(premiumPlan, (string)arm.Requests.Single(r => r.Method == "PUT" && r.Path == Site).Body.SelectToken("properties.serverFarmId"));
                }

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
            private readonly WebSiteResource _cleanupSite;
            public FixtureSolutionInstaller(SolutionInstallConfig config, ILogger logger, SubscriptionResource subscription, WebSiteResource cleanupSite = null)
                : base(config, logger, null, null, "Contoso", null)
            {
                _subscription = subscription;
                _cleanupSite = cleanupSite;
            }
            internal override SubscriptionResource ConnectToSubscription(CancellationToken ct) => _subscription;
            internal override Task CheckWebJobsAfterInstallAsync(WebSiteResource webApp, ILogger logger) =>
                base.CheckWebJobsAfterInstallAsync(_cleanupSite ?? webApp, logger);
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
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ConfigureWebAppAsync(arm, logger: logger));
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

                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ConfigureWebAppAsync(arm));
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

                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ConfigureWebAppAsync(arm, logger: logger));
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

        [DataTestMethod]
        [DataRow(true, 409, "ScopeLocked")]
        [DataRow(false, 403, "RequestDisallowedByPolicy")]
        [DataRow(true, 403, "Contoso-private-code")]
        public async Task AppService_FinalCleanupReportsTheFailedArmOperationWithoutPrivateDetails(bool rejectWrite, int status, string code)
        {
            using (var arm = new FakeArm())
            {
                arm.AddSite("1.2");
                var settings = arm.Resource(AppSettings)["properties"];
                settings["WEBJOBS_STOPPED"] = "1";
                settings["ContosoOperator"] = "Contoso-private-setting";
                var site = (await arm.Group.GetWebSiteAsync("contoso-web")).Value;
                var deniedMethod = rejectWrite ? "PUT" : "POST";
                var deniedPath = rejectWrite ? AppSettings : AppSettings + "/list";
                arm.OverrideResponse = (method, path) => method == deniedMethod && path == deniedPath
                    ? FakeArm.Json((HttpStatusCode)status, new JObject { ["error"] = new JObject
                        { ["code"] = code, ["message"] = "Contoso-private-body", ["target"] = "Contoso-private-target" } }) : null;
                var logger = new CapturingLogger();
                var config = BackendConfig("contoso-web", "contoso-web");

                // Skip provisioning; the real final check must still run against the supplied site after Cancel.
                await new FixtureSolutionInstaller(config, logger, arm.Subscription, site).InstallOrUpdate(new CancellationToken(true));

                Assert.AreEqual(3, arm.Requests.Count(r => r.Method == "POST" && r.Path == AppSettings + "/list"));
                Assert.AreEqual(rejectWrite ? 3 : 0, arm.Requests.Count(r => r.Method == "PUT" && r.Path == AppSettings));
                var terminal = logger.Lines.Single(l => l.Level == LogLevel.Error && l.Message.StartsWith("WebJobsCleanup: outcome=Failed")).Message;
                StringAssert.Contains(terminal, $"HTTP {status}");
                StringAssert.Contains(terminal, rejectWrite ? "update" : "read");
                if (code == "ScopeLocked" || code == "RequestDisallowedByPolicy") StringAssert.Contains(terminal, code);
                StringAssert.Contains(terminal, "manually");
                Assert.IsTrue(logger.Lines.Any(l => l.Level == LogLevel.Information && l.Message.Contains(terminal)), "The final summary must retain the diagnosis.");
                Assert.IsFalse(logger.Lines.Any(l => l.Message.Contains("Contoso-private") || l.Message.Contains("outcome=Verified")
                    || l.Message.Contains("No replacement was sent")));
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

        [DataTestMethod]
        [DataRow("contoso-web", true)]
        [DataRow("CoNtOsO-WeB", true)]
        [DataRow("contoso-other", false)]
        public async Task KeyVault_RegisteredWebAppPolicyUsesOnlyTheConfiguredSiteIdentity(string webAppName, bool found)
        {
            using (var arm = new FakeArm())
            {
                var (task, vault) = await RegisteredVaultTask<KeyVaultAddWebAppPermissionsTask>(arm, webAppName, NullLogger.Instance);
                if (!found)
                {
                    var error = await Assert.ThrowsAsync<InstallException>(() => task.ExecuteTask(vault));
                    StringAssert.Contains(error.Message, "Can't find web-app");
                    Assert.AreEqual(0, arm.Mutations.Count);
                    return;
                }

                await task.ExecuteTask(vault);

                var write = arm.Mutations.Single();
                Assert.AreEqual("PUT", write.Method);
                Assert.AreEqual(Vault + "/accessPolicies/add", write.Path, true);
                var policy = write.Body.SelectToken("properties.accessPolicies").Single();
                Assert.AreEqual(VaultTenant, (string)policy["tenantId"]);
                Assert.AreEqual(WebAppPrincipal, (string)policy["objectId"]);
                var grants = ((JObject)policy["permissions"]).Properties()
                    .SelectMany(p => p.Value.Values<string>().Select(v => p.Name + ":" + v.ToLowerInvariant())).ToArray();
                CollectionAssert.AreEquivalent(new[] { "secrets:get", "certificates:get" }, grants);
            }
        }

        [DataTestMethod]
        [DataRow("contoso-web", true)]
        [DataRow("CoNtOsO-WeB", true)]
        [DataRow("contoso-other", false)]
        public async Task KeyVault_RegisteredFirewallUsesOnlyTheConfiguredSiteOutboundIps(string webAppName, bool found)
        {
            using (var arm = new FakeArm())
            {
                var logger = new CapturingLogger();
                var (task, vault) = await RegisteredVaultTask<KeyVaultFirewallConfigTask>(arm, webAppName, logger);
                task.ResolveInstallerIpAsync = (name, log, token) => Task.FromResult("192.0.2.20");

                await task.ExecuteTask(vault);

                var write = arm.Mutations.Single();
                Assert.AreEqual("PATCH", write.Method);
                Assert.AreEqual(Vault, write.Path, true);
                var rules = write.Body.SelectToken("properties.networkAcls");
                Assert.AreEqual("Deny", (string)rules["defaultAction"]);
                Assert.AreEqual("None", (string)rules["bypass"]);
                var ips = rules["ipRules"].Select(ip => (string)ip["value"]).ToArray();
                var expected = new List<string> { "192.0.2.10", "192.0.2.20" };
                if (found) expected.AddRange(new[] { "203.0.113.10", "203.0.113.11" });
                CollectionAssert.AreEquivalent(expected, ips);
                Assert.AreEqual(!found, logger.Lines.Any(l => l.Level == LogLevel.Warning && l.Message.Contains("not found in the resource group")));
            }
        }

        [TestMethod]
        public async Task KeyVault_RegisteredFirewallDoesNotTreatAnOmittedSiteAsAbsent()
        {
            using (var arm = new FakeArm())
            {
                var (firewall, vault) = await RegisteredVaultTask<KeyVaultFirewallConfigTask>(arm, "CONTOSO-WEB", NullLogger.Instance);
                firewall.ResolveInstallerIpAsync = (name, log, token) => Task.FromResult("192.0.2.20");
                arm.OverrideResponse = (m, p) => m == "GET" && p.Equals(Sites, StringComparison.OrdinalIgnoreCase)
                    ? FakeArm.Json(HttpStatusCode.OK, new JObject { ["value"] = new JArray() }) : null;

                var failure = await Assert.ThrowsAsync<InstallException>(() => firewall.ExecuteTask(vault));
                StringAssert.Contains(failure.Message, "disagree");
                Assert.AreEqual(0, arm.Mutations.Count);
            }
        }

        [DataTestMethod]
        [DataRow("UserAssigned", false)]
        [DataRow("UserAssigned", true)]
        [DataRow("SystemAssigned", false)]
        [DataRow("SystemAssigned, UserAssigned", false)]
        public async Task AppService_RegisteredIdentityRepairPreservesOperatorIdentitiesAndGrantsOnlyTheSite(
            string existingType, bool retryWithoutAlwaysOn)
        {
            using (var arm = new FakeArm())
            {
                arm.AddSite("1.3");
                var vault = await AddVault(arm);
                var config = BackendConfig("CoNtOsO-WeB", "contoso-web");
                config.InstallerAccount.DirectoryId = VaultTenant;
                var job = new InspectableBackend(config, NullLogger.Instance, arm.Subscription);
                var siteTask = job.Registered<AppServiceWebsiteTask>();
                var grant = job.Registered<KeyVaultAddWebAppPermissionsTask>();
                var firewall = job.Registered<KeyVaultFirewallConfigTask>();
                siteTask.Container = grant.Container = firewall.Container = arm.Group;
                firewall.ResolveInstallerIpAsync = (name, log, token) => Task.FromResult("192.0.2.20");
                var identity = (JObject)arm.Resource(Site)["identity"];
                identity["type"] = existingType;
                if (existingType == "UserAssigned") identity.Property("principalId").Remove();
                var ids = new[] { "contoso-worker", "contoso-reporting" }
                    .Select(n => Rg + "/providers/Microsoft.ManagedIdentity/userAssignedIdentities/" + n).ToArray();
                if (existingType.Contains("UserAssigned"))
                    identity["userAssignedIdentities"] = new JObject(ids.Select(id =>
                        new JProperty(id, new JObject { ["principalId"] = "00000000-0000-0000-0000-000000000003" })));
                if (retryWithoutAlwaysOn) arm.Resource(Site + "/config/web")["properties"]["alwaysOn"] = false;
                var attempts = 0;
                arm.OverrideResponse = (method, path) =>
                {
                    if (method == "GET" && path.Equals(Sites, StringComparison.OrdinalIgnoreCase))
                    {
                        // List projections need not include all the identity data on the site GET.
                        var projection = (JObject)arm.Resource(Site).DeepClone();
                        ((JObject)projection["identity"]).Property("userAssignedIdentities")?.Remove();
                        if (retryWithoutAlwaysOn) projection.Property("identity").Remove();
                        return FakeArm.Json(HttpStatusCode.OK, new JObject { ["value"] = new JArray(projection) });
                    }
                    if (method == "PATCH" && path.Equals(Site, StringComparison.OrdinalIgnoreCase))
                    {
                        if (++attempts == 1 && retryWithoutAlwaysOn) return FakeArm.Json(HttpStatusCode.Conflict, new JObject());
                        var requested = (JObject)arm.Requests.Last().Body["identity"]?.DeepClone();
                        if (requested != null)
                        {
                            requested["principalId"] = WebAppPrincipal; // ARM assigns the system principal.
                            arm.Resource(Site)["identity"] = requested; // An identity block is replaced, not merged.
                        }
                        var reply = (JObject)arm.Resource(Site).DeepClone();
                        if (retryWithoutAlwaysOn) ((JObject)reply["identity"]).Property("principalId")?.Remove();
                        return FakeArm.Json(HttpStatusCode.OK, reply);
                    }
                    return null;
                };

                var site = await siteTask.ExecuteTaskReturnResult(await arm.Plan("contoso-web"));
                var patches = arm.Requests.Where(r => r.Method == "PATCH" && r.Path.Equals(Site, StringComparison.OrdinalIgnoreCase)).ToArray();
                Assert.AreEqual(existingType == "UserAssigned" ? (retryWithoutAlwaysOn ? 2 : 1) : 0, patches.Length);
                foreach (var patch in patches)
                {
                    var sent = (JObject)patch.Body["identity"];
                    Assert.AreEqual("SystemAssigned, UserAssigned", (string)sent["type"]);
                    var userIds = ((JObject)sent["userAssignedIdentities"]).Properties().ToArray();
                    CollectionAssert.AreEquivalent(ids, userIds.Select(p => p.Name).ToArray());
                    Assert.IsTrue(userIds.All(p => !p.Value.HasValues), "ARM takes resource IDs, not read-only identity fields.");
                }
                if (retryWithoutAlwaysOn) Assert.IsNull(patches[1].Body.SelectToken("properties.siteConfig.alwaysOn"));
                if (retryWithoutAlwaysOn) Assert.IsNull(site.Data.Identity.PrincipalId, "The principal is available only on the subsequent full site read.");
                else Assert.AreEqual(Guid.Parse(WebAppPrincipal), site.Data.Identity.PrincipalId);
                await firewall.ExecuteTask(vault);
                await grant.ExecuteTask(vault);
                var ips = arm.Mutations.Single(r => r.Path == Vault).Body.SelectToken("properties.networkAcls.ipRules");
                CollectionAssert.AreEquivalent(new[] { "192.0.2.10", "192.0.2.20", "203.0.113.10", "203.0.113.11" },
                    ips.Select(ip => (string)ip["value"]).ToArray());
                var policy = arm.Mutations.Single(r => r.Path.Equals(Vault + "/accessPolicies/add", StringComparison.OrdinalIgnoreCase))
                    .Body.SelectToken("properties.accessPolicies").Single();
                Assert.AreEqual(WebAppPrincipal, (string)policy["objectId"]);
                Assert.IsFalse(arm.Mutations.Any(r => r.Path.Contains("roleAssignments")));
            }
        }

        [DataTestMethod]
        [DataRow("SystemAssigned", Sub)]
        [DataRow("UserAssigned", WebAppPrincipal)]
        [DataRow("SystemAssigned, UserAssigned", null)]
        public async Task AppService_RegisteredVaultRefusesAnUnusableSystemPrincipal(string type, string principal)
        {
            using (var arm = new FakeArm())
            {
                var (grant, vault) = await RegisteredVaultTask<KeyVaultAddWebAppPermissionsTask>(arm, "contoso-web", NullLogger.Instance);
                arm.Resource(Site)["identity"] = new JObject { ["type"] = type, ["principalId"] = principal };
                var error = await Assert.ThrowsAsync<InstallException>(() => grant.ExecuteTask(vault));
                StringAssert.Contains(error.Message, "system-assigned");
                Assert.AreEqual(0, arm.Mutations.Count);
            }
        }

        [DataTestMethod]
        [DataRow("incomplete identity", 0)]
        [DataRow("missing identity type", 0)]
        [DataRow("site GET denied", 0)]
        [DataRow("cancelled GET", 0)]
        [DataRow("ScopeLocked", 1)]
        [DataRow("scopelocked", 1)]
        [DataRow("RequestDisallowedByPolicy", 1)]
        [DataRow("Conflict", 1)]
        public async Task AppService_RegisteredIdentityRepairStopsOnIncompleteReadsAndRejectedWrites(string scenario, int expectedWrites)
        {
            using (var arm = new FakeArm())
            using (var cancellation = new CancellationTokenSource())
            {
                arm.AddSite("1.3");
                var identity = new JObject { ["type"] = "UserAssigned" };
                if (scenario != "incomplete identity")
                    identity["userAssignedIdentities"] = new JObject
                        { [Rg + "/providers/Microsoft.ManagedIdentity/userAssignedIdentities/contoso-worker"] = new JObject() };
                if (scenario == "missing identity type") identity.Property("type").Remove();
                arm.Resource(Site)["identity"] = identity;
                if (expectedWrites == 1 && scenario != "Conflict")
                    arm.Resource(Site + "/config/web")["properties"]["alwaysOn"] = false;
                arm.OverrideResponse = (method, path) =>
                {
                    if (!path.Equals(Site, StringComparison.OrdinalIgnoreCase)) return null;
                    if (method == "GET" && scenario == "cancelled GET") cancellation.Cancel();
                    if (method == "GET" && scenario == "site GET denied") return FakeArm.Json(HttpStatusCode.Forbidden, new JObject());
                    if (method == "PATCH" && expectedWrites == 1)
                        return FakeArm.Json(scenario == "RequestDisallowedByPolicy" ? HttpStatusCode.Forbidden : HttpStatusCode.Conflict,
                            new JObject { ["error"] = new JObject { ["code"] = scenario, ["message"] = "Contoso" } });
                    return null;
                };
                var task = new InspectableBackend(BackendConfig("contoso-web", "contoso-web"), NullLogger.Instance, arm.Subscription).WebsiteTask;
                task.CancellationToken = cancellation.Token;
                task.Container = arm.Group;
                var plan = await arm.Plan("contoso-web");
                if (scenario == "cancelled GET")
                    await Assert.ThrowsAsync<OperationCanceledException>(() => task.ExecuteTaskReturnResult(plan));
                else if (expectedWrites == 0)
                    await Assert.ThrowsAsync<InstallException>(() => task.ExecuteTaskReturnResult(plan));
                else
                    await Assert.ThrowsAsync<Azure.RequestFailedException>(() => task.ExecuteTaskReturnResult(plan));
                Assert.AreEqual(expectedWrites, arm.Mutations.Count);
                Assert.IsTrue(JToken.DeepEquals(identity, arm.Resource(Site)["identity"]));
            }
        }

        private static async Task<(T Task, KeyVaultResource Vault)> RegisteredVaultTask<T>(FakeArm arm, string webAppName, ILogger logger)
            where T : InstallTaskInAzResourceGroup<KeyVaultResource>
        {
            arm.AddSite("1.2");
            var config = BackendConfig(webAppName, "contoso-plan");
            config.InstallerAccount.DirectoryId = VaultTenant;
            var task = new InspectableBackend(config, logger, arm.Subscription).Registered<T>();
            task.Container = arm.Group;
            return (task, await AddVault(arm));
        }

        private static async Task<KeyVaultResource> AddVault(FakeArm arm)
        {
            arm.Add(Vault, new JObject
            {
                ["name"] = "contoso-vault", ["type"] = "Microsoft.KeyVault/vaults", ["location"] = "westeurope",
                ["properties"] = new JObject
                {
                    ["tenantId"] = VaultTenant, ["sku"] = new JObject { ["name"] = "standard", ["family"] = "A" },
                    ["accessPolicies"] = new JArray(),
                    ["networkAcls"] = new JObject { ["defaultAction"] = "Deny", ["bypass"] = "None",
                        ["ipRules"] = new JArray(new JObject { ["value"] = "192.0.2.10" }) }
                }
            });
            return (await arm.Group.GetKeyVaultAsync("contoso-vault")).Value;
        }

        private sealed class InspectableBackend : AzurePaaSInstallJob
        {
            public InspectableBackend(SolutionInstallConfig config, ILogger logger, SubscriptionResource subscription)
                : base(logger, config, subscription) { }

            public T Registered<T>() => _installTaskListWithChildren.SelectMany(group => group).OfType<T>().Single();
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

        [DataTestMethod]
        [DataRow("shared plan denied")]
        [DataRow("incomplete site list")]
        [DataRow("omitted site")]
        [DataRow("missing hosting plan ID")]
        [DataRow("configured plan list denied")]
        [DataRow("omitted configured plan")]
        [DataRow("site config denied")]
        [DataRow("FTP policy denied")]
        [DataRow("cancelled plan read")]
        [DataRow("unverified reset")]
        [DataRow("site appeared before cleanup")]
        [DataRow("parent unavailable before cleanup")]
        [DataRow("parent unavailable during public enable")]
        [DataRow("healthy")]
        public async Task Backend_HostingDiscoveryGuardsPrivateToPublic(string scenario)
        {
            using (var arm = new FakeArm())
            using (var cancellation = new CancellationTokenSource())
            {
                var recheckMissing = scenario == "site appeared before cleanup" || scenario == "parent unavailable before cleanup";
                var missingSite = scenario == "configured plan list denied" || scenario == "omitted configured plan" || recheckMissing;
                var config = BackendConfig(missingSite ? "contoso-new" : "CoNtOsO-WeB", "contoso-unused");
                arm.AddBackendInventory(config);
                if (scenario == "omitted configured plan") arm.AddPlan(config.AppServicePlanName);
                var subnet = Rg + "/providers/Microsoft.Network/virtualNetworks/contoso-vnet/subnets/integration";
                arm.AddSite("1.3", subnet);
                arm.Resource(Site)["properties"]["publicNetworkAccess"] = "Disabled";
                arm.Resource(Site)["properties"]["serverFarmId"] = scenario == "missing hosting plan ID" ? null : SharedPlan;
                arm.Add(SharedPlan, (JObject)arm.Resource(Plans + "/contoso-web").DeepClone());
                arm.Resource(SharedPlan)["name"] = "contoso-shared";
                var integration = new PrivateNetworkArtifact { Id = Site, TargetId = Site, SubnetId = subnet,
                    Kind = PrivateNetworkProvenance.Integration, CreatedByInstaller = true, CreationConfirmed = true };
                config.PrivateNetworking.Artifacts.Add(integration);
                config.PrivateNetworking.AppliedTopology = "Private";
                var checkpoints = new List<string>();
                var configReads = 0;
                var missingSiteReads = 0;
                var job = new AzurePaaSInstallJob(NullLogger.Instance, config, arm.Subscription,
                    () => checkpoints.Add(config.PrivateNetworking.AppliedTopology)) { CancellationToken = cancellation.Token };
                arm.OverrideResponse = (method, path) =>
                {
                    if (scenario == "parent unavailable during public enable" && method == "GET" &&
                        path.Equals(Site, StringComparison.OrdinalIgnoreCase) && checkpoints.Count != 0)
                        return FakeArm.Json(HttpStatusCode.NotFound, new JObject { ["error"] = new JObject { ["code"] = "ResourceGroupNotFound" } });
                    if (recheckMissing && method == "GET" && path.Equals(Sites + "/contoso-new", StringComparison.OrdinalIgnoreCase))
                    {
                        if (++missingSiteReads == 1) return new HttpResponseMessage(HttpStatusCode.NotFound);
                        if (scenario == "parent unavailable before cleanup")
                            return FakeArm.Json(HttpStatusCode.NotFound, new JObject { ["error"] = new JObject { ["code"] = "ResourceGroupNotFound" } });
                        var appeared = (JObject)arm.Resource(Site).DeepClone();
                        appeared["id"] = path;
                        appeared["name"] = "contoso-new";
                        return FakeArm.Json(HttpStatusCode.OK, appeared);
                    }
                    if (method == "GET" && path.Equals(Sites, StringComparison.OrdinalIgnoreCase) && scenario == "incomplete site list")
                        return FakeArm.Json(HttpStatusCode.OK, new JObject());
                    if (method == "GET" && path.Equals(Sites, StringComparison.OrdinalIgnoreCase) && scenario == "omitted site")
                        return FakeArm.Json(HttpStatusCode.OK, new JObject { ["value"] = new JArray() });
                    if (method == "GET" && path.Equals(Plans, StringComparison.OrdinalIgnoreCase) && scenario == "omitted configured plan")
                        return FakeArm.Json(HttpStatusCode.OK, new JObject { ["value"] = new JArray() });
                    if (method == "GET" && path.Equals(Site + "/config/web", StringComparison.OrdinalIgnoreCase) && scenario == "unverified reset" && ++configReads == 2)
                        return FakeArm.Json(HttpStatusCode.OK, new JObject { ["id"] = path, ["name"] = "web" });
                    if (method == "GET" && ((path.Equals(Plans, StringComparison.OrdinalIgnoreCase) && scenario == "configured plan list denied") ||
                        (path.Equals(Site + "/config/web", StringComparison.OrdinalIgnoreCase) && scenario == "site config denied") ||
                        (path.Equals(FtpPolicy, StringComparison.OrdinalIgnoreCase) && scenario == "FTP policy denied")))
                        return FakeArm.Json(HttpStatusCode.Forbidden, new JObject());
                    if (method == "GET" && path.Equals(SharedPlan, StringComparison.OrdinalIgnoreCase))
                    {
                        if (scenario == "shared plan denied") return FakeArm.Json(HttpStatusCode.Forbidden, new JObject());
                        if (scenario == "cancelled plan read") cancellation.Cancel();
                    }
                    if (method == "DELETE" && path.Equals(Site + "/networkConfig/virtualNetwork", StringComparison.OrdinalIgnoreCase))
                        arm.Resource(Site)["properties"]["virtualNetworkSubnetId"] = null;
                    return null;
                };

                if (scenario == "parent unavailable during public enable")
                {
                    var error = await Assert.ThrowsAsync<Azure.RequestFailedException>(() => job.Install());
                    Assert.AreEqual("ResourceGroupNotFound", error.ErrorCode);
                    CollectionAssert.AreEqual(new[] { "PublicTransitionPending" }, checkpoints);
                    Assert.AreEqual(0, arm.Mutations.Count, "A failed site recheck must not enable public access or remove private networking.");
                    Assert.IsFalse(integration.Removed);
                    return;
                }
                if (scenario == "unverified reset")
                {
                    var failure = await Assert.ThrowsAsync<InstallException>(() => job.Install());
                    StringAssert.Contains(failure.Message, "site configuration");
                    Assert.IsTrue(arm.Mutations.Any(r => r.Method == "DELETE")); // The readback, not the preflight, was incomplete.
                    Assert.IsFalse(integration.Removed, "A metadata-only config cannot confirm the route-all reset.");
                    Assert.AreNotEqual("Public", config.PrivateNetworking.AppliedTopology);
                    return;
                }
                if (scenario == "healthy")
                {
                    // The fixture ends at the very next registered step: no database or IP resolver is needed.
                    await Assert.ThrowsAsync<Azure.RequestFailedException>(() => job.Install());
                    Assert.IsTrue(arm.Requests.Last().Path.EndsWith("/Microsoft.Sql/servers", StringComparison.OrdinalIgnoreCase));
                    Assert.AreEqual("Public", config.PrivateNetworking.AppliedTopology);
                    Assert.IsTrue(integration.Removed);
                    Assert.AreEqual("Enabled", job.CreatedWebSiteResource.Data.PublicNetworkAccess);
                    Assert.IsNull(job.CreatedWebSiteResource.Data.VirtualNetworkSubnetId);
                    Assert.AreEqual(SharedPlan, job.CreatedAppServicePlan.Id.ToString(), true);
                    Assert.AreEqual(1, arm.Requests.Count(r => r.Path.Equals(SharedPlan, StringComparison.OrdinalIgnoreCase)));
                    Assert.AreEqual(1, arm.Requests.Count(r => r.Path.Equals(Sites, StringComparison.OrdinalIgnoreCase)));
                    Assert.IsTrue(arm.Requests.FindIndex(r => r.Path.Equals(SharedPlan, StringComparison.OrdinalIgnoreCase)) <
                        arm.Requests.FindIndex(r => r.Method == "PATCH"));
                    CollectionAssert.AreEqual(new[] { "PATCH " + Site, "DELETE " + Site + "/networkConfig/virtualNetwork", "PATCH " + Site },
                        ConfigurationRequests(arm), "No second public-access patch or shared-plan write is needed.");
                    return;
                }

                if (scenario == "cancelled plan read")
                    await Assert.ThrowsAsync<OperationCanceledException>(() => job.Install());
                else if (scenario == "site config denied" || scenario == "FTP policy denied")
                    Assert.AreEqual(403, (await Assert.ThrowsAsync<Azure.RequestFailedException>(() => job.Install())).Status);
                else
                    await Assert.ThrowsAsync<InstallException>(() => job.Install());
                Assert.AreEqual(0, arm.Mutations.Count, "Unexpected requests: " + string.Join(", ", ConfigurationRequests(arm)));
                Assert.AreEqual(0, checkpoints.Count);
                if (recheckMissing) Assert.AreEqual(2, missingSiteReads, "Cleanup must corroborate the same missing site before proceeding.");
                if (scenario == "omitted configured plan")
                    Assert.IsTrue(arm.Requests.Any(r => r.Method == "GET" && r.Path.Equals(Plans + "/" + config.AppServicePlanName, StringComparison.OrdinalIgnoreCase)));
                Assert.AreEqual("Private", config.PrivateNetworking.AppliedTopology);
                Assert.IsFalse(integration.Removed);
                Assert.AreEqual("Disabled", (string)arm.Resource(Site)["properties"]["publicNetworkAccess"]);
                Assert.AreEqual(subnet, (string)arm.Resource(Site)["properties"]["virtualNetworkSubnetId"]);
                Assert.IsFalse(arm.Requests.Any(r => r.Path.IndexOf("/providers/Microsoft.Sql/", StringComparison.OrdinalIgnoreCase) >= 0));
            }
        }

        [DataTestMethod]
        [DataRow(false, null)]
        [DataRow(false, "NotFound")]
        [DataRow(false, "ParentResourceNotFound")]
        [DataRow(true, null)]
        [DataRow(true, "SubscriptionNotFound")]
        public async Task Backend_AnUnconfirmedResourceGroup404CannotAuthorizeCreation(bool recheck, string errorCode)
        {
            using (var arm = new FakeArm())
            {
                var config = BackendConfig("contoso-web", "contoso-plan");
                arm.AddBackendInventory(config, existingGroup: false);
                int groupReads = 0;
                arm.OverrideResponse = (method, path) =>
                {
                    if (method != "GET" || !path.Equals(Rg, StringComparison.OrdinalIgnoreCase)) return null;
                    if (++groupReads == 1 && recheck) return null; // The preflight confirmed absence; the create task still rechecks.
                    return errorCode == null
                        ? new HttpResponseMessage(HttpStatusCode.NotFound)
                        : FakeArm.Json(HttpStatusCode.NotFound, new JObject { ["error"] = new JObject { ["code"] = errorCode } });
                };

                var job = new AzurePaaSInstallJob(NullLogger.Instance, config, arm.Subscription);

                Assert.AreEqual(404, (await Assert.ThrowsAsync<Azure.RequestFailedException>(() => job.Install())).Status);
                Assert.AreEqual(recheck ? 2 : 1, groupReads);
                Assert.AreEqual(0, arm.Mutations.Count);
            }
        }

        [DataTestMethod]
        [DataRow(true, true, false)]
        [DataRow(true, false, false)]
        [DataRow(false, false, false)]
        [DataRow(true, false, true)]
        public async Task Backend_NewSiteUsesThePreCreateInventoryForTheLegacyWarning(bool existingGroup, bool localLegacyPlan, bool emptyNotFound)
        {
            using (var arm = new FakeArm())
            {
                var logger = new CapturingLogger();
                var config = BackendConfig("contoso-web", "contoso-new");
                arm.AddBackendInventory(config, existingGroup);
                arm.KnownLists.Add(Sites);
                if (existingGroup) arm.AddPlan(localLegacyPlan ? "CONTOSO-WEB" : "contoso-other");
                var foreign = SharedPlan.Replace("contoso-shared", "contoso-web");
                arm.Add(foreign, new JObject { ["name"] = "contoso-web", ["type"] = "Microsoft.Web/serverfarms" });
                var lists = 0;
                var emptyReads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                arm.OverrideResponse = (method, path) =>
                {
                    if (emptyNotFound && method == "GET" &&
                        (path.Equals(Site, StringComparison.OrdinalIgnoreCase) || path.Equals(Plans + "/contoso-new", StringComparison.OrdinalIgnoreCase)) &&
                        !arm.Mutations.Any())
                    {
                        emptyReads.Add(path);
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    }
                    if (method == "GET" && path.Equals(Plans, StringComparison.OrdinalIgnoreCase) && ++lists > 1)
                        return FakeArm.Json(HttpStatusCode.ServiceUnavailable, new JObject());
                    if (method == "GET" && path.Equals(Plans, StringComparison.OrdinalIgnoreCase) && !localLegacyPlan && existingGroup)
                        return FakeArm.Json(HttpStatusCode.OK, new JObject { ["value"] = new JArray(arm.Resource(Plans + "/contoso-other"), arm.Resource(foreign)) });
                    if (method == "PUT" && path.Equals(Rg, StringComparison.OrdinalIgnoreCase))
                    {
                        arm.AddResourceGroup();
                        return FakeArm.Json(HttpStatusCode.OK, arm.Resource(Rg));
                    }
                    if (method == "PUT" && path == Site)
                    {
                        arm.AddSite("1.2");
                        arm.Resource(Site)["properties"]["serverFarmId"] = arm.Requests.Last().Body.SelectToken("properties.serverFarmId");
                        return FakeArm.Json(HttpStatusCode.OK, arm.Resource(Site));
                    }
                    return null;
                };
                var job = new AzurePaaSInstallJob(logger, config, arm.Subscription);
                await Assert.ThrowsAsync<Azure.RequestFailedException>(() => job.Install());

                Assert.IsTrue(arm.Requests.Last().Path.EndsWith("/Microsoft.Sql/servers", StringComparison.OrdinalIgnoreCase),
                    "A diagnostic lookup must not strand a newly created paid plan before the site is created.");
                Assert.AreEqual(Plans + "/contoso-new", job.CreatedWebSiteResource.Data.AppServicePlanId.ToString(), true);
                if (emptyNotFound) Assert.IsTrue(emptyReads.SetEquals(new[] { Site, Plans + "/contoso-new" }));
                Assert.AreEqual(existingGroup ? 1 : 0, lists);
                Assert.AreEqual(existingGroup, arm.Requests.TakeWhile(r => r.Method == "GET")
                    .Any(r => r.Path.Equals(Plans + "/contoso-new", StringComparison.OrdinalIgnoreCase)));
                Assert.AreEqual(localLegacyPlan, logger.Lines.Any(l => l.Level == LogLevel.Warning && l.Message.Contains("stops being billed")));
            }
        }

        [TestMethod]
        public async Task Plan_ANewWebApp_GetsThePlanNameTheOperatorConfigured()
        {
            using (var arm = new FakeArm())
            {
                arm.KnownLists.Add(Rg + "/providers/Microsoft.Web/sites");
                arm.AddPlan("contoso-unrelated");
                var task = PlanTaskFromTheInstallJob(arm, webAppName: "contoso-web", planName: "contoso-plan");

                var plan = await task.ExecuteTaskReturnResult(null);

                var write = arm.Mutations.Single();
                Assert.IsTrue(arm.Requests.TakeWhile(r => r.Method == "GET").Any(r => r.Path.Equals(write.Path, StringComparison.OrdinalIgnoreCase)),
                    "Only a known 404 on the exact plan authorizes the subsequent create-or-update.");
                Assert.AreEqual("PUT", write.Method);
                Assert.AreEqual(Rg + "/providers/Microsoft.Web/serverfarms/contoso-plan", write.Path, true,
                    "The plan used to be created under the web app's name, whatever the configuration said.");
                Assert.AreEqual("contoso-plan", plan.Data.Name);
                Assert.AreEqual("B1", (string)write.Body.SelectToken("sku.name"));
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

        [DataTestMethod]
        [DataRow("contoso-web")]
        [DataRow("CoNtOsO-WeB")]
        public async Task Plan_AnExistingWebAppOnASharedPlanInAnotherResourceGroup_UsesItAsItIs_WithoutWritingToIt(string webAppName)
        {
            // A plan outside the deployment's resource group is one the operator chose to share. Writing the solution's
            // tags to it needs access the installer's identity need not have there, and a refused write aborted the
            // install. Installers before #828 never touched it.
            using (var arm = new FakeArm())
            {
                arm.AddSite(minTls: "1.2");
                arm.Resource(Site)["properties"]["serverFarmId"] = SharedPlan;
                arm.Add(SharedPlan, new JObject
                {
                    ["name"] = "contoso-shared", ["type"] = "Microsoft.Web/serverfarms", ["location"] = "westeurope", ["kind"] = "app",
                    ["sku"] = new JObject { ["name"] = "P1v3", ["tier"] = "PremiumV3", ["family"] = "Pv3" }, ["properties"] = new JObject()
                });
                var logger = new CapturingLogger();
                var task = PlanTaskFromTheInstallJob(arm, webAppName, planName: "contoso-plan", logger: logger,
                    tags: new[] { new AzTag("CostCentre", "Contoso-42") });

                var plan = await task.ExecuteTaskReturnResult(null);

                Assert.AreEqual(SharedPlan, plan.Id.ToString(), true, "The web app stays on the plan it runs on.");
                Assert.AreEqual("P1v3", plan.Data.Sku.Name);
                Assert.AreEqual(0, arm.Mutations.Count, "Nothing is written to a plan outside the deployment's resource group: no tags, no second plan.");
                Assert.IsTrue(logger.Lines.Any(l => l.Message.Contains("outside this deployment's")));
            }
        }

        [DataTestMethod]
        [DataRow("site list denied", "App Services", "HTTP 403")]
        [DataRow("incomplete site list", "App Services", "retry")]
        [DataRow("unnamed site", "App Services", "retry")]
        [DataRow("missing ID", "hosting plan ID", "retry")]
        [DataRow("malformed ID", "hosting plan ID", "retry")]
        [DataRow("wrong resource type", "hosting plan ID", "retry")]
        [DataRow("plan denied", "hosting plan", "HTTP 403")]
        [DataRow("plan missing", "hosting plan", "HTTP 404")]
        [DataRow("empty plan", "hosting plan", "retry")]
        public async Task Plan_UnresolvedSiteOrHostingPlan_StopsWithoutLookingForOrWritingAnotherPlan(string failure, string operation, string reason)
        {
            const string privateDetail = "Contoso-private-detail";
            using (var arm = new FakeArm())
            {
                arm.AddSite("1.2");
                var properties = (JObject)arm.Resource(Site)["properties"];
                properties["serverFarmId"] = SharedPlan;
                var denied = new JObject { ["error"] = new JObject { ["code"] = privateDetail, ["message"] = privateDetail } };
                switch (failure)
                {
                    case "site list denied":
                        arm.OverrideResponse = (m, p) => m == "GET" && p == Sites ? FakeArm.Json(HttpStatusCode.Forbidden, denied) : null;
                        break;
                    case "incomplete site list":
                        arm.OverrideResponse = (m, p) => m == "GET" && p == Sites ? FakeArm.Json(HttpStatusCode.OK, new JObject()) : null;
                        break;
                    case "unnamed site": arm.Resource(Site).Property("name").Remove(); break;
                    case "missing ID": properties.Property("serverFarmId").Remove(); break;
                    case "malformed ID": properties["serverFarmId"] = privateDetail; break;
                    case "wrong resource type": properties["serverFarmId"] = Site; break;
                    case "plan denied":
                        arm.OverrideResponse = (m, p) => m == "GET" && p == SharedPlan ? FakeArm.Json(HttpStatusCode.Forbidden, denied) : null;
                        break;
                    case "plan missing": break; // Not present in FakeArm.
                    case "empty plan":
                        arm.OverrideResponse = (m, p) => m == "GET" && p == SharedPlan ? FakeArm.Json(HttpStatusCode.OK, new JObject()) : null;
                        break;
                }

                var logger = new CapturingLogger();
                var task = PlanTaskFromTheInstallJob(arm, "CONTOSO-WEB", "contoso-unused", logger);
                var failureSeen = await Assert.ThrowsAsync<InstallException>(() => task.ExecuteTaskReturnResult(null));

                Assert.AreEqual(0, arm.Mutations.Count, "Unexpected writes: " + string.Join(", ", arm.Mutations.Select(m => m.Method + " " + m.Path)));
                Assert.IsFalse(arm.Requests.Any(r => r.Path.Equals(Plans, StringComparison.OrdinalIgnoreCase)));
                StringAssert.Contains(failureSeen.Message, operation);
                StringAssert.Contains(failureSeen.Message, reason);
                Assert.IsFalse(failureSeen.ToString().Contains(privateDetail) || logger.Lines.Any(l => l.Message.Contains(privateDetail)));
            }
        }

        [DataTestMethod]
        [DataRow(403)]
        [DataRow(200)]
        public async Task Plan_UnreadableConfiguredPlanListForAMissingSite_IsNotPermissionToCreate(int status)
        {
            using (var arm = new FakeArm())
            {
                arm.KnownLists.Add(Sites);
                arm.AddPlan("Contoso-Premium");
                arm.Resource(Plans + "/Contoso-Premium")["sku"]["name"] = "P1v3";
                arm.OverrideResponse = (method, path) => method == "GET" && path.Equals(Plans, StringComparison.OrdinalIgnoreCase)
                    ? FakeArm.Json((HttpStatusCode)status, new JObject { ["error"] = "Contoso-private-detail" }) : null;
                var task = PlanTaskFromTheInstallJob(arm, "contoso-web", "contoso-premium");

                var error = await Assert.ThrowsAsync<InstallException>(() => task.ExecuteTaskReturnResult(null));

                StringAssert.Contains(error.Message, "App Service plans");
                Assert.IsFalse(error.ToString().Contains("Contoso-private-detail"));
                Assert.AreEqual(0, arm.Mutations.Count);
                Assert.AreEqual("P1v3", (string)arm.Resource(Plans + "/Contoso-Premium")["sku"]["name"]);
            }
        }

        [DataTestMethod]
        [DataRow("Contoso-Premium", "omitted")]
        [DataRow("cOnToSo-PrEmIuM", "omitted")]
        [DataRow("contoso-premium", "listed but missing")]
        [DataRow("contoso-premium", "listed but empty 404")]
        [DataRow("contoso-premium", "denied")]
        [DataRow("contoso-premium", "ambiguous 404")]
        [DataRow("contoso-premium", "missing parent")]
        [DataRow("contoso-premium", "invalid 404 body")]
        [DataRow("contoso-premium", "empty object 404")]
        [DataRow("contoso-premium", "wrong resource group")]
        [DataRow("contoso-premium", "missing properties")]
        [DataRow("contoso-premium", "missing sku")]
        [DataRow("contoso-premium", "different sku")]
        [DataRow("contoso-premium", "cancelled")]
        public async Task Plan_UnverifiedConfiguredPlanCannotBeCreatedOrReused(string configuredName, string failure)
        {
            using (var arm = new FakeArm())
            using (var cancellation = new CancellationTokenSource())
            {
                const string name = "Contoso-Premium";
                const string path = Plans + "/" + name;
                const string detail = "Contoso private diagnostic";
                arm.AddResourceGroup();
                arm.KnownLists.Add(Sites);
                arm.AddPlan(name);
                arm.Resource(path)["sku"] = new JObject { ["name"] = "P1v3", ["tier"] = "PremiumV3" };
                var original = arm.Resource(path).DeepClone();
                arm.OverrideResponse = (m, p) =>
                {
                    if (m != "GET") return null;
                    if (p.Equals(Plans, StringComparison.OrdinalIgnoreCase) &&
                        (failure == "omitted" || failure == "denied" || failure == "ambiguous 404" || failure == "missing parent" ||
                        failure == "invalid 404 body" || failure == "empty object 404"))
                        return FakeArm.Json(HttpStatusCode.OK, new JObject { ["value"] = new JArray() });
                    if (!p.Equals(path, StringComparison.OrdinalIgnoreCase)) return null;
                    var reply = (JObject)original.DeepClone();
                    switch (failure)
                    {
                        case "listed but missing": return FakeArm.Json(HttpStatusCode.NotFound, new JObject { ["error"] = new JObject { ["code"] = "ResourceNotFound" } });
                        case "listed but empty 404": return new HttpResponseMessage(HttpStatusCode.NotFound);
                        case "invalid 404 body": return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(" ") };
                        case "empty object 404": return FakeArm.Json(HttpStatusCode.NotFound, new JObject());
                        case "denied": return FakeArm.Json(HttpStatusCode.Forbidden, new JObject { ["error"] = new JObject { ["message"] = detail } });
                        case "ambiguous 404": return FakeArm.Json(HttpStatusCode.NotFound, new JObject { ["error"] = new JObject { ["code"] = detail } });
                        case "missing parent": return FakeArm.Json(HttpStatusCode.NotFound, new JObject { ["error"] = new JObject { ["code"] = "ResourceGroupNotFound" } });
                        case "wrong resource group": reply["id"] = path.Replace("resourceGroups/contoso/", "resourceGroups/contoso-other/"); break;
                        case "missing properties": reply.Property("properties").Remove(); break;
                        case "missing sku": reply["sku"] = new JObject(); break;
                        case "different sku": reply["sku"] = new JObject { ["name"] = "F1", ["tier"] = "Free" }; break;
                        case "cancelled": cancellation.Cancel(); break;
                    }
                    return FakeArm.Json(HttpStatusCode.OK, reply);
                };
                var task = PlanTaskFromTheInstallJob(arm, "contoso-new", configuredName);
                task.Container = (await arm.Group.GetAsync()).Value;
                task.CancellationToken = cancellation.Token;

                var error = await CaptureDiscoveryFailure(() => task.ExecuteTaskReturnResult(null));
                Assert.AreEqual(0, arm.Mutations.Count, string.Join(", ", arm.Mutations.Select(r => $"{r.Method} {r.Path} (SKU: {r.Body?.SelectToken("sku.name")})")));
                if (failure == "cancelled")
                    Assert.IsInstanceOfType(error, typeof(OperationCanceledException));
                else
                {
                    Assert.IsInstanceOfType(error, typeof(InstallException));
                    StringAssert.Contains(error.Message, "plan");
                    StringAssert.Contains(error.Message, "retry");
                    if (failure == "denied") StringAssert.Contains(error.Message, "HTTP 403");
                    if (failure == "ambiguous 404" || failure == "missing parent" || failure == "listed but missing") StringAssert.Contains(error.Message, "HTTP 404");
                    Assert.IsFalse(error.ToString().Contains(detail));
                }
                Assert.IsTrue(arm.Requests.Any(r => r.Method == "GET" && r.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(JToken.DeepEquals(original, arm.Resource(path)), "The existing Premium plan must not be resized to Basic.");
            }
        }

        [DataTestMethod]
        [DataRow("omitted site", "warn")]
        [DataRow("denied site", "warn")]
        [DataRow("omitted plan", "warn")]
        [DataRow("denied plan", "warn")]
        [DataRow("cancelled site", "cancelled")]
        [DataRow("unreadable config", "warn")]
        [DataRow("unreadable FTP", "warn")]
        [DataRow("existing site", "Free")]
        [DataRow("partial site list", "Free")]
        [DataRow("existing configured plan", "PremiumV3")]
        [DataRow("new site and plan", "not found yet")]
        public async Task Plan_VerifierUsesTheSameAuthoritativeDiscovery(string scenario, string expected)
        {
            using (var arm = new FakeArm())
            using (var cancellation = new CancellationTokenSource())
            {
                const string premium = Plans + "/Contoso-Premium";
                arm.AddResourceGroup();
                arm.KnownLists.UnionWith(new[] { Sites, Plans });
                if (scenario != "new site and plan")
                {
                    arm.AddPlan("Contoso-Premium");
                    arm.Resource(premium)["sku"] = new JObject { ["name"] = "P1v3", ["tier"] = "PremiumV3" };
                }
                if (new[] { "existing site", "partial site list", "omitted site", "denied site", "cancelled site", "unreadable config", "unreadable FTP" }.Contains(scenario))
                {
                    arm.AddSite("1.3");
                    arm.Resource(Plans + "/contoso-web")["sku"] = new JObject { ["name"] = "F1", ["tier"] = "Free" };
                }
                arm.OverrideResponse = (m, p) =>
                {
                    if (m != "GET") return null;
                    if (scenario == "cancelled site" && p.Equals(Site, StringComparison.OrdinalIgnoreCase)) cancellation.Cancel();
                    if (scenario == "partial site list" && p.Equals(Sites, StringComparison.OrdinalIgnoreCase))
                    {
                        var projection = (JObject)arm.Resource(Site).DeepClone();
                        ((JObject)projection["properties"]).Property("serverFarmId").Remove();
                        return FakeArm.Json(HttpStatusCode.OK, new JObject { ["value"] = new JArray(projection) });
                    }
                    if ((scenario == "omitted site" && p.Equals(Sites, StringComparison.OrdinalIgnoreCase)) ||
                        (scenario == "omitted plan" && p.Equals(Plans, StringComparison.OrdinalIgnoreCase)))
                        return FakeArm.Json(HttpStatusCode.OK, new JObject { ["value"] = new JArray() });
                    if ((scenario == "denied site" && p.Equals(Site, StringComparison.OrdinalIgnoreCase)) ||
                        (scenario == "denied plan" && p.Equals(premium, StringComparison.OrdinalIgnoreCase)))
                        return FakeArm.Json(HttpStatusCode.Forbidden, new JObject { ["error"] = new JObject { ["message"] = "Contoso private diagnostic" } });
                    if ((scenario == "unreadable config" && p.Equals(Site + "/config/web", StringComparison.OrdinalIgnoreCase)) ||
                        (scenario == "unreadable FTP" && p.Equals(FtpPolicy, StringComparison.OrdinalIgnoreCase)))
                    {
                        var reply = (JObject)arm.Resource(p).DeepClone();
                        reply.Property("properties").Remove();
                        return FakeArm.Json(HttpStatusCode.OK, reply);
                    }
                    return null;
                };
                var logger = new CapturingLogger();
                var verifier = new SolutionInstallVerifier(BackendConfig("CoNtOsO-WeB", "cOnToSo-PrEmIuM"), logger, null) { CancellationToken = cancellation.Token };
                var group = (await arm.Group.GetAsync()).Value;
                if (expected == "cancelled")
                    await Assert.ThrowsAsync<OperationCanceledException>(() => verifier.VerifyAppServicePlanCapabilities(group));
                else
                    await verifier.VerifyAppServicePlanCapabilities(group);

                Assert.AreEqual(0, arm.Mutations.Count);
                if (expected == "warn")
                {
                    Assert.IsTrue(logger.Lines.Any(l => l.Level == LogLevel.Warning && l.Message.Contains("Could not check")), string.Join("; ", logger.Lines.Select(l => l.Message)));
                    Assert.IsFalse(logger.Lines.Any(l => l.Message.Contains("supports Always On") || l.Message.Contains("not found yet")));
                    Assert.IsFalse(logger.Lines.Any(l => l.Message.Contains("Contoso private diagnostic")));
                }
                else if (expected == "Free")
                    Assert.IsTrue(logger.Lines.Any(l => l.Level == LogLevel.Warning && l.Message.Contains("Free (F1)") && l.Message.Contains("does not support Always On")));
                else if (expected != "cancelled")
                    Assert.IsTrue(logger.Lines.Any(l => l.Level == LogLevel.Information && l.Message.Contains(expected)));
                Assert.IsTrue(arm.Requests.Any(r => r.Path.Equals(Site, StringComparison.OrdinalIgnoreCase)), "A list miss is not a verified missing site.");
                if (arm.Requests.Any(r => r.Path.Equals(Plans, StringComparison.OrdinalIgnoreCase)))
                    Assert.IsTrue(arm.Requests.Any(r => r.Path.Equals(premium, StringComparison.OrdinalIgnoreCase)), "A list miss is not a verified missing plan.");
            }
        }

        [DataTestMethod]
        [DataRow("CONTOSO", Sub, true)]
        [DataRow("contoso-other", Sub, false)]
        [DataRow("contoso", "00000000-0000-0000-0000-000000000001", false)]
        public async Task ResourceGroup_VerifierNeverHandsAnUnverifiedGroupToItsChecks(string returnedName, string returnedSub, bool matches)
        {
            using (var arm = new FakeArm())
            {
                arm.AddResourceGroup();
                arm.Resource(Rg)["id"] = $"/subscriptions/{returnedSub}/resourceGroups/{returnedName}";
                arm.Resource(Rg)["name"] = returnedName;
                var lookup = new ResourceGroupTestOnlyInstallJob(NullLogger.Instance, new AzureLocation("westeurope"), arm.Subscription, "contoso");

                if (matches) await lookup.Install();
                else await Assert.ThrowsAsync<InstallException>(() => lookup.Install());

                Assert.AreEqual(matches, lookup.ResourceGroupFound != null);
                Assert.AreEqual(1, arm.Requests.Count);
                Assert.AreEqual("GET", arm.Requests.Single().Method);
            }
        }

        [DataTestMethod]
        [DataRow("CONTOSO", Sub, true)]
        [DataRow("contoso-other", Sub, false)]
        [DataRow("contoso", "00000000-0000-0000-0000-000000000001", false)]
        public async Task Plan_VerifierChecksOnlyTheConfiguredResourceGroup(string groupName, string subscriptionId, bool matches)
        {
            using (var arm = new FakeArm())
            {
                arm.AddResourceGroup();
                arm.AddSite("1.3");
                var group = (await arm.Group.GetAsync()).Value;
                var config = BackendConfig("contoso-web", "contoso-plan");
                config.ResourceGroupName = groupName;
                config.Subscription = new AzureSubscription(subscriptionId, "Contoso");
                var logger = new CapturingLogger();
                var verifier = new SolutionInstallVerifier(config, logger, null);
                arm.Requests.Clear();

                await verifier.VerifyAppServicePlanCapabilities(group);

                Assert.AreEqual(matches, arm.Requests.Count > 0, "A scope mismatch must stop before any child lookup.");
                Assert.AreEqual(matches, logger.Lines.Any(l => l.Message.Contains("supports Always On")));
                if (!matches) Assert.IsTrue(logger.Lines.Any(l => l.Level == LogLevel.Warning && l.Message.Contains("resource group")));
                Assert.AreEqual(0, arm.Mutations.Count);
            }
        }

        [TestMethod]
        public async Task Plan_VerifierWarnsInsteadOfCheckingAnUnrelatedConfiguredPlanForAnExistingSite()
        {
            using (var arm = new FakeArm())
            {
                arm.AddSite("1.2");
                arm.AddResourceGroup();
                ((JObject)arm.Resource(Site)["properties"]).Property("serverFarmId").Remove();
                arm.AddPlan("contoso-unused");
                var logger = new CapturingLogger();
                var verifier = new SolutionInstallVerifier(BackendConfig("contoso-web", "contoso-unused"), logger, null);
                await verifier.VerifyAppServicePlanCapabilities((await arm.Group.GetAsync()).Value);

                Assert.AreEqual(0, arm.Mutations.Count);
                Assert.IsFalse(arm.Requests.Any(r => r.Path.Equals(Plans, StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(logger.Lines.Any(l => l.Level == LogLevel.Warning && l.Message.Contains("hosting plan ID")));
            }
        }

        [TestMethod]
        public async Task Plan_CancellationDuringHostingPlanRead_IsNotDiscoveryFailure()
        {
            using (var arm = new FakeArm())
            using (var cancellation = new CancellationTokenSource())
            {
                arm.AddSite("1.2");
                arm.Resource(Site)["properties"]["serverFarmId"] = SharedPlan;
                arm.OverrideResponse = (method, path) =>
                {
                    if (method == "GET" && path == SharedPlan) cancellation.Cancel();
                    return null;
                };
                var task = PlanTaskFromTheInstallJob(arm, "contoso-web", "contoso-unused");
                task.CancellationToken = cancellation.Token;

                await Assert.ThrowsAsync<OperationCanceledException>(() => task.ExecuteTaskReturnResult(null));
                Assert.AreEqual(0, arm.Mutations.Count);
                Assert.IsFalse(arm.Requests.Any(r => r.Path.Equals(Plans, StringComparison.OrdinalIgnoreCase)));
            }
        }

        [TestMethod]
        public async Task Plan_NoSiteRequested_ReusesTheConfiguredPlanWithoutListingSites()
        {
            using (var arm = new FakeArm())
            {
                arm.AddPlan("Contoso-Plan");
                var task = new AppServicePlanTask(TaskConfig.GetConfigForName("contoso-plan"), NullLogger.Instance,
                    AzureLocation.WestEurope, new Dictionary<string, string>()) { Container = arm.Group };

                var plan = await task.ExecuteTaskReturnResult(null);

                Assert.AreEqual("Contoso-Plan", plan.Data.Name);
                Assert.AreEqual(0, arm.Mutations.Count);
                Assert.IsFalse(arm.Requests.Any(r => r.Path.Equals(Sites, StringComparison.OrdinalIgnoreCase)));
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

            public void AddBackendInventory(SolutionInstallConfig config, bool existingGroup = true)
            {
                if (existingGroup) AddResourceGroup();
                KnownLists.UnionWith(new[] { Rg + "/resources", Rg + "/providers/Microsoft.Network/privateEndpoints",
                    Rg + "/providers/Microsoft.Network/privateDnsZones" });
                foreach (var provider in AzureResourceProviderPreflight.RequiredProviders(config))
                    Add("/subscriptions/" + Sub + "/providers/" + provider, new JObject { ["namespace"] = provider, ["registrationState"] = "Registered" });
            }

            public void AddSite(string minTls, string integrationSubnet = null)
            {
                Add(Site, new JObject
                {
                    ["name"] = "contoso-web", ["type"] = "Microsoft.Web/sites", ["location"] = "westeurope", ["kind"] = "app",
                    ["tags"] = new JObject { ["CostCentre"] = "Contoso-42" },
                    ["identity"] = new JObject { ["type"] = "SystemAssigned", ["principalId"] = WebAppPrincipal, ["tenantId"] = VaultTenant },
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
                if (request.Method != HttpMethod.Get) Mutations.Add(entry);
                var response = OverrideResponse?.Invoke(request.Method.Method, path);
                if (response != null) return response;

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
                    var missingCode = path.Equals(Rg, StringComparison.OrdinalIgnoreCase) ? "ResourceGroupNotFound" : "ResourceNotFound";
                    return Json(HttpStatusCode.NotFound, new JObject { ["error"] = new JObject { ["code"] = missingCode, ["message"] = "Synthetic: not found" } });
                }

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
