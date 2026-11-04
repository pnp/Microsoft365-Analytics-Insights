using App.ControlPanel.Engine.InstallerTasks.Tasks;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using CloudInstallEngine;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// Re-running the installer used to remove the Hybrid Worker extension, wipe the worker's registry keys and service,
    /// and restart the VM every time - including a healthy worker, and the VM the installer itself was running on.
    /// </summary>
    [TestClass]
    public class HybridWorkerExtensionPolicyTests
    {
        const string SUB = "00000000-0000-0000-0000-000000000000";
        const string VM_NAME = "contoso-worker";
        const string VM_ID = "/subscriptions/" + SUB + "/resourceGroups/contoso-rg/providers/Microsoft.Compute/virtualMachines/" + VM_NAME;
        const string WORKER_VM_GUID = "11111111-1111-1111-1111-111111111111";
        const string OTHER_VM_GUID = "22222222-2222-2222-2222-222222222222";
        const string GROUP = "contoso-automation-vnet-workers";
        const string OUR_URL = "https://00000000-0000-0000-0000-000000000000.jrds.weu.azure-automation.net/automationAccounts/00000000-0000-0000-0000-000000000000";
        const string OTHER_URL = "https://00000000-0000-0000-0000-000000000000.jrds.weu.azure-automation.net/automationAccounts/99999999-9999-9999-9999-999999999999";

        static readonly InstallerHostCheck[] AllHosts = { InstallerHostCheck.Unknown, InstallerHostCheck.ThisVm, InstallerHostCheck.OtherMachine };

        static HybridWorkerVmFacts Facts(bool found = true, string state = "Succeeded", string extensionUrl = OUR_URL, bool registered = true,
            InstallerHostCheck host = InstallerHostCheck.OtherMachine) => new HybridWorkerVmFacts
            {
                VmName = VM_NAME,
                GroupName = GROUP,
                ExtensionFound = found,
                ExtensionProvisioningState = state,
                ExtensionAutomationAccountUrl = extensionUrl,
                ExpectedAutomationAccountUrl = OUR_URL,
                WorkerWasRegistered = registered,
                InstallerHost = host
            };

        #region Decision

        [TestMethod]
        public void HealthyRegisteredWorker_IsLeftAlone_WhereverTheInstallerRuns()
        {
            foreach (var host in AllHosts)
            {
                var decision = HybridWorkerExtensionPolicy.Decide(Facts(host: host));

                Assert.AreEqual(HybridWorkerExtensionAction.LeaveAlone, decision.Action, host.ToString());
                Assert.AreEqual(LogLevel.Information, decision.LogLevel);
                StringAssert.Contains(decision.Reason, "nothing was removed, cleaned up or restarted");
            }
        }

        [TestMethod]
        public void HealthyExtension_WhoseVmHadToBeRegisteredAgain_IsRerun_WithoutARestart()
        {
            foreach (var host in AllHosts)
            {
                var decision = HybridWorkerExtensionPolicy.Decide(Facts(registered: false, host: host));

                Assert.AreEqual(HybridWorkerExtensionAction.Reapply, decision.Action, host.ToString());
                StringAssert.Contains(decision.Reason, "not restarted");
            }
        }

        [TestMethod]
        public void MissingExtension_IsInstalled_WithoutARestart()
        {
            foreach (var host in AllHosts)
            {
                foreach (var registered in new[] { true, false })
                {
                    var decision = HybridWorkerExtensionPolicy.Decide(Facts(found: false, state: null, extensionUrl: null, registered: registered, host: host));

                    Assert.AreEqual(HybridWorkerExtensionAction.Install, decision.Action);
                    StringAssert.Contains(decision.Reason, "not restarted");
                }
            }
        }

        [TestMethod]
        public void BrokenExtension_IsRepairedWithARestart_OnlyWhenTheInstallerIsKnownToRunElsewhere()
        {
            foreach (var state in new[] { "Failed", "Canceled", null, "" })
            {
                var elsewhere = HybridWorkerExtensionPolicy.Decide(Facts(state: state, host: InstallerHostCheck.OtherMachine));
                Assert.AreEqual(HybridWorkerExtensionAction.RepairWithRestart, elsewhere.Action, $"state '{state}'");
                Assert.AreEqual(LogLevel.Warning, elsewhere.LogLevel, "A restart must show up in the install summary");
                StringAssert.Contains(elsewhere.Reason, "RESTARTS the VM");

                var here = HybridWorkerExtensionPolicy.Decide(Facts(state: state, host: InstallerHostCheck.ThisVm));
                Assert.AreEqual(HybridWorkerExtensionAction.ManualRepair, here.Action, $"state '{state}'");
                Assert.AreEqual(LogLevel.Error, here.LogLevel);
                StringAssert.Contains(here.Reason, "The installer is running on that VM");
                StringAssert.Contains(here.Reason, "restart the VM, then run the installer again");

                var unknown = HybridWorkerExtensionPolicy.Decide(Facts(state: state, host: InstallerHostCheck.Unknown));
                Assert.AreEqual(HybridWorkerExtensionAction.ManualRepair, unknown.Action, $"state '{state}'");
                Assert.AreEqual(LogLevel.Error, unknown.LogLevel);
                StringAssert.Contains(unknown.Reason, "couldn't confirm that it isn't running on that VM");
            }
        }

        [TestMethod]
        public void ExtensionThatIsStillBeingChanged_IsLeftAlone()
        {
            foreach (var state in new[] { "Creating", "Updating", "Deleting" })
            {
                var decision = HybridWorkerExtensionPolicy.Decide(Facts(state: state, registered: false));

                Assert.AreEqual(HybridWorkerExtensionAction.LeaveAlone, decision.Action, state);
                Assert.AreEqual(LogLevel.Warning, decision.LogLevel);
            }
        }

        [TestMethod]
        public void ExtensionRegisteredWithAnotherAutomationAccount_IsNeverChanged()
        {
            foreach (var state in new[] { "Succeeded", "Failed" })
            {
                foreach (var registered in new[] { true, false })
                {
                    var decision = HybridWorkerExtensionPolicy.Decide(Facts(state: state, extensionUrl: OTHER_URL, registered: registered));

                    Assert.AreEqual(HybridWorkerExtensionAction.LeaveAlone, decision.Action, state);
                    Assert.AreEqual(LogLevel.Error, decision.LogLevel);
                    StringAssert.Contains(decision.Reason, "different Automation account");
                }
            }
        }

        [TestMethod]
        public void OurOwnUrlWrittenDifferently_OrUnreadable_IsNotMistakenForAnotherAccount()
        {
            foreach (var url in new[] { OUR_URL.ToUpperInvariant(), OUR_URL + "/", " " + OUR_URL, null, "" })
            {
                var decision = HybridWorkerExtensionPolicy.Decide(Facts(extensionUrl: url));

                Assert.AreEqual(HybridWorkerExtensionAction.LeaveAlone, decision.Action, $"'{url}'");
                Assert.AreEqual(LogLevel.Information, decision.LogLevel, $"'{url}'");
            }
        }

        /// <summary>The safety property, over every combination: only a broken extension of ours, repaired from another machine, restarts the VM.</summary>
        [TestMethod]
        public void OnlyABrokenExtensionOfOurs_RepairedFromAnotherMachine_EverRestartsTheVm()
        {
            var states = new[] { "Succeeded", "succeeded", "Failed", "Canceled", "Creating", "Updating", "Deleting", null };
            foreach (var found in new[] { true, false })
                foreach (var state in states)
                    foreach (var url in new[] { OUR_URL, OTHER_URL, null })
                        foreach (var registered in new[] { true, false })
                            foreach (var host in AllHosts)
                            {
                                var decision = HybridWorkerExtensionPolicy.Decide(Facts(found, state, url, registered, host));

                                var broken = found && state != null && !new[] { "Succeeded", "Creating", "Updating", "Deleting" }.Contains(state, StringComparer.OrdinalIgnoreCase)
                                    || found && state == null;
                                var ours = url != OTHER_URL;
                                var expectRestart = broken && ours && host == InstallerHostCheck.OtherMachine;

                                Assert.AreEqual(expectRestart, decision.Action == HybridWorkerExtensionAction.RepairWithRestart,
                                    $"found={found} state={state ?? "null"} url={url ?? "null"} registered={registered} host={host}: {decision.Action}");
                            }
        }

        #endregion

        #region Where the installer is running

        [TestMethod]
        public void InstallerOnTheWorkerVm_IsRecognisedFromItsVmIdOrResourceId()
        {
            Assert.AreEqual(InstallerHostCheck.ThisVm,
                HybridWorkerExtensionPolicy.CheckInstallerHost(VM_ID, WORKER_VM_GUID, null, WORKER_VM_GUID.ToUpperInvariant()));
            Assert.AreEqual(InstallerHostCheck.ThisVm,
                HybridWorkerExtensionPolicy.CheckInstallerHost(VM_ID, null, VM_ID.ToLowerInvariant() + "/", null),
                "The resource ID alone is enough to recognise the VM");
            Assert.AreEqual(InstallerHostCheck.ThisVm,
                HybridWorkerExtensionPolicy.CheckInstallerHost(VM_ID, WORKER_VM_GUID, VM_ID, OTHER_VM_GUID),
                "Either identifier matching means it may be this VM");
        }

        [TestMethod]
        public void InstallerOnAnotherAzureVm_IsRecognised()
        {
            Assert.AreEqual(InstallerHostCheck.OtherMachine,
                HybridWorkerExtensionPolicy.CheckInstallerHost(VM_ID, WORKER_VM_GUID, VM_ID.Replace(VM_NAME, "contoso-admin"), OTHER_VM_GUID));
        }

        [TestMethod]
        public void WithoutAnAnswerFromTheMetadataService_ItIsUnknown()
        {
            // A laptop, or an Azure VM where the metadata service is blocked
            Assert.AreEqual(InstallerHostCheck.Unknown, HybridWorkerExtensionPolicy.CheckInstallerHost(VM_ID, WORKER_VM_GUID, null, null));
            Assert.AreEqual(InstallerHostCheck.Unknown, HybridWorkerExtensionPolicy.CheckInstallerHost(VM_ID, WORKER_VM_GUID, "", "not-a-guid"));
            // A different resource ID can't rule the VM out when ARM didn't say what the worker's vmId is
            Assert.AreEqual(InstallerHostCheck.Unknown,
                HybridWorkerExtensionPolicy.CheckInstallerHost(VM_ID, null, VM_ID.Replace(VM_NAME, "contoso-admin"), OTHER_VM_GUID));
        }

        #endregion

        #region Extension settings

        [TestMethod]
        public void AutomationAccountUrl_IsReadFromTheExtensionsPublicSettings()
        {
            Assert.AreEqual(OUR_URL, HybridWorkerGroupTask.ReadAutomationAccountUrl(BinaryData.FromString("{\"automationAccountUrl\":\"" + OUR_URL + "\"}")));
            Assert.IsNull(HybridWorkerGroupTask.ReadAutomationAccountUrl(null));
            Assert.IsNull(HybridWorkerGroupTask.ReadAutomationAccountUrl(BinaryData.FromString("{}")));
            Assert.IsNull(HybridWorkerGroupTask.ReadAutomationAccountUrl(BinaryData.FromString("not json")));
        }

        [TestMethod]
        public void SettingTheUrl_KeepsTheRestOfTheSettings()
        {
            var before = BinaryData.FromString("{\"automationaccounturl\":\"" + OTHER_URL + "\",\"ProxySettings\":{\"ProxyServer\":\"10.0.0.4:3128\"}}");

            var after = JObject.Parse(HybridWorkerGroupTask.WithAutomationAccountUrl(before, OUR_URL).ToString());

            Assert.AreEqual(OUR_URL, after["AutomationAccountURL"].ToString());
            Assert.AreEqual("10.0.0.4:3128", after["ProxySettings"]["ProxyServer"].ToString());
            Assert.AreEqual(2, after.Properties().Count(), "The URL must be set once, whatever case it was stored in");
        }

        #endregion

        #region What the task does to the VM

        [TestMethod]
        public async Task HealthyWorker_ReRun_SendsNoChangesToTheVm()
        {
            var arm = new FakeArm { Extension = ExtensionJson("HybridWorkerExtension", "Succeeded") };
            var task = new TestableHybridWorkerGroupTask(arm, new CapturingLogger());

            Assert.IsTrue(await task.EnsureHybridWorkerExtensionInstalled(VM_ID, OUR_URL, GROUP, workerWasRegistered: true));

            CollectionAssert.AreEqual(new string[0], arm.Changes, "A healthy worker must not be removed, cleaned up or restarted");
            Assert.AreEqual(0, task.ImdsCalls, "Nothing needs a restart, so there's no need to ask where the installer runs");
        }

        [TestMethod]
        public async Task BrokenExtension_OnTheVmTheInstallerRunsOn_SendsNoChangesToTheVm()
        {
            var logger = new CapturingLogger();
            var arm = new FakeArm { Extension = ExtensionJson("HybridWorkerForWindows", "Failed") };
            var task = new TestableHybridWorkerGroupTask(arm, logger) { Imds = (VM_ID, WORKER_VM_GUID) };

            await task.EnsureHybridWorkerExtensionInstalled(VM_ID, OUR_URL, GROUP, workerWasRegistered: true);

            CollectionAssert.AreEqual(new string[0], arm.Changes);
            Assert.IsTrue(logger.Lines.Any(l => l.Level == LogLevel.Error && l.Message.Contains("The installer is running on that VM")));
        }

        [TestMethod]
        public async Task BrokenExtension_WhenTheInstallerCantTellWhereItRuns_SendsNoChangesToTheVm()
        {
            var arm = new FakeArm { Extension = ExtensionJson("HybridWorkerForWindows", "Failed") };
            var task = new TestableHybridWorkerGroupTask(arm, new CapturingLogger()) { Imds = (null, null) };

            await task.EnsureHybridWorkerExtensionInstalled(VM_ID, OUR_URL, GROUP, workerWasRegistered: true);

            CollectionAssert.AreEqual(new string[0], arm.Changes);
        }

        [TestMethod]
        public async Task BrokenExtension_RepairedFromAnotherVm_IsRemovedCleanedRestartedAndReinstalled()
        {
            var arm = new FakeArm { Extension = ExtensionJson("HybridWorkerExtension", "Failed") };
            var task = new TestableHybridWorkerGroupTask(arm, new CapturingLogger()) { Imds = (VM_ID.Replace(VM_NAME, "contoso-admin"), OTHER_VM_GUID) };

            Assert.IsTrue(await task.EnsureHybridWorkerExtensionInstalled(VM_ID, OUR_URL, GROUP, workerWasRegistered: true));

            CollectionAssert.AreEqual(new[]
            {
                "DELETE extensions/HybridWorkerExtension",
                "POST runCommand",
                "POST restart",
                "PUT extensions/HybridWorkerExtension"
            }, arm.Changes);
            Assert.AreEqual("10.0.0.4:3128", arm.LastBody["properties"]["settings"]["ProxySettings"]["ProxyServer"].ToString(),
                "The reinstalled extension keeps the admin's other settings");
        }

        [TestMethod]
        public async Task MissingExtension_TaskInstallsIt_WithoutRestartingTheVm()
        {
            var arm = new FakeArm { Extension = null };
            var task = new TestableHybridWorkerGroupTask(arm, new CapturingLogger());

            Assert.IsTrue(await task.EnsureHybridWorkerExtensionInstalled(VM_ID, OUR_URL, GROUP, workerWasRegistered: false));

            CollectionAssert.AreEqual(new[] { "POST runCommand", "PUT extensions/HybridWorkerForWindows" }, arm.Changes);
            Assert.AreEqual(OUR_URL, arm.LastBody["properties"]["settings"]["AutomationAccountURL"].ToString());
            Assert.AreEqual(0, task.ImdsCalls);
        }

        [TestMethod]
        public async Task HealthyExtension_WhoseVmWasJustRegistered_IsRerunInPlace()
        {
            var arm = new FakeArm { Extension = ExtensionJson("HybridWorkerExtension", "Succeeded") };
            var task = new TestableHybridWorkerGroupTask(arm, new CapturingLogger());

            Assert.IsTrue(await task.EnsureHybridWorkerExtensionInstalled(VM_ID, OUR_URL, GROUP, workerWasRegistered: false));

            CollectionAssert.AreEqual(new[] { "PATCH extensions/HybridWorkerExtension" }, arm.Changes);
            Assert.IsFalse(string.IsNullOrEmpty(arm.LastBody["properties"]?["forceUpdateTag"]?.ToString()));
            Assert.IsNull(arm.LastBody["properties"]?["protectedSettings"], "Re-running must not touch the protected settings");
        }

        static JObject ExtensionJson(string name, string provisioningState) => JObject.Parse(
            "{\"id\":\"" + VM_ID + "/extensions/" + name + "\",\"name\":\"" + name + "\",\"type\":\"Microsoft.Compute/virtualMachines/extensions\",\"location\":\"westeurope\"," +
            "\"properties\":{\"publisher\":\"Microsoft.Azure.Automation.HybridWorker\",\"type\":\"HybridWorkerForWindows\",\"typeHandlerVersion\":\"1.1\"," +
            "\"autoUpgradeMinorVersion\":true,\"provisioningState\":\"" + provisioningState + "\"," +
            "\"settings\":{\"AutomationAccountURL\":\"" + OUR_URL + "\",\"ProxySettings\":{\"ProxyServer\":\"10.0.0.4:3128\"}}}}");

        sealed class TestableHybridWorkerGroupTask : HybridWorkerGroupTask
        {
            readonly HttpClient _http;

            public TestableHybridWorkerGroupTask(FakeArm arm, ILogger logger)
                : base(TaskConfig.GetConfigForName(GROUP), logger, new AzureLocation("westeurope"), new Dictionary<string, string>(), new SyntheticCredential())
            {
                _http = new HttpClient(arm);
            }

            public (string ResourceId, string VmId) Imds { get; set; }

            public int ImdsCalls { get; private set; }

            protected override ArmClient CreateArmClient()
            {
                var options = new ArmClientOptions { Transport = new HttpClientTransport(_http) };
                options.Retry.MaxRetries = 0;
                return new ArmClient(new SyntheticCredential(), SUB, options);
            }

            protected override Task<(string ResourceId, string VmId)> ReadImdsComputeIdentityAsync()
            {
                ImdsCalls++;
                return Task.FromResult(Imds);
            }
        }

        /// <summary>
        /// Azure Resource Manager for one running VM: answers reads, and records every request that would change it.
        /// </summary>
        sealed class FakeArm : HttpMessageHandler
        {
            public JObject Extension { get; set; }
            public List<string> Changes { get; } = new List<string>();
            public JObject LastBody { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri.AbsolutePath.TrimEnd('/');
                var vmPath = VM_ID;
                if (!path.StartsWith(vmPath, StringComparison.OrdinalIgnoreCase))
                {
                    return Json(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"ResourceNotFound\",\"message\":\"Synthetic: unexpected " + path + "\"}}");
                }
                var rest = path.Substring(vmPath.Length).TrimStart('/');

                if (request.Method == HttpMethod.Get)
                {
                    if (rest == string.Empty)
                    {
                        return Json(HttpStatusCode.OK, "{\"id\":\"" + VM_ID + "\",\"name\":\"" + VM_NAME + "\",\"type\":\"Microsoft.Compute/virtualMachines\",\"location\":\"westeurope\"," +
                            "\"properties\":{\"vmId\":\"" + WORKER_VM_GUID + "\",\"provisioningState\":\"Succeeded\"}}");
                    }
                    if (rest.Equals("extensions", StringComparison.OrdinalIgnoreCase))
                    {
                        return Json(HttpStatusCode.OK, new JObject { ["value"] = Extension == null ? new JArray() : new JArray(Extension.DeepClone()) }.ToString());
                    }
                    if (rest.Equals("instanceView", StringComparison.OrdinalIgnoreCase))
                    {
                        return Json(HttpStatusCode.OK, "{\"statuses\":[{\"code\":\"ProvisioningState/succeeded\"},{\"code\":\"PowerState/running\"}]}");
                    }
                    if (rest.StartsWith("extensions/", StringComparison.OrdinalIgnoreCase) && Extension != null)
                    {
                        return Json(HttpStatusCode.OK, Extension.ToString());
                    }
                    return Json(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"NotFound\",\"message\":\"Synthetic\"}}");
                }

                Changes.Add($"{request.Method.Method} {rest}");
                LastBody = request.Content == null ? null : TryParse(await request.Content.ReadAsStringAsync());
                if (rest.StartsWith("extensions/", StringComparison.OrdinalIgnoreCase) && (request.Method.Method == "PUT" || request.Method.Method == "PATCH"))
                {
                    var name = rest.Substring("extensions/".Length);
                    var stored = ExtensionJson(name, "Succeeded");
                    return Json(HttpStatusCode.OK, stored.ToString());
                }
                if (rest.Equals("runCommand", StringComparison.OrdinalIgnoreCase))
                {
                    return Json(HttpStatusCode.OK, "{\"value\":[{\"code\":\"ComponentStatus/StdOut/succeeded\",\"message\":\"Cleanup complete.\"}]}");
                }
                return Json(HttpStatusCode.OK, "{}");
            }

            static JObject TryParse(string body)
            {
                try
                {
                    return string.IsNullOrWhiteSpace(body) ? null : JObject.Parse(body);
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    return null;
                }
            }

            static HttpResponseMessage Json(HttpStatusCode status, string body) => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        sealed class SyntheticCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                new AccessToken("synthetic", DateTimeOffset.MaxValue);
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
        }

        #endregion
    }
}
