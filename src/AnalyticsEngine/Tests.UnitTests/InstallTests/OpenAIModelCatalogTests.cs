using App.ControlPanel;
using App.ControlPanel.Frames.InstallWizard;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using CloudInstallEngine;
using CloudInstallEngine.Azure;
using CloudInstallEngine.Azure.InstallTasks;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static Tests.UnitTests.InstallTests.InstallerWorkspaceTests;

namespace Tests.UnitTests.InstallTests
{
    [TestClass]
    public class OpenAIModelCatalogTests
    {
        private static JObject Model(string name, string version, string type = "Standard",
            string lifecycle = "GenerallyAvailable", bool chat = true, bool isDefault = true)
        {
            return new JObject
            {
                ["kind"] = "OpenAI", ["skuName"] = "S0",
                ["model"] = new JObject
                {
                    ["format"] = "OpenAI", ["name"] = name, ["version"] = version,
                    ["isDefaultVersion"] = isDefault, ["lifecycleStatus"] = lifecycle,
                    ["capabilities"] = new JObject { ["chatCompletion"] = chat ? "true" : "false" },
                    ["skus"] = new JArray(new JObject
                    {
                        ["name"] = type,
                        ["capacity"] = new JObject { ["minimum"] = 1, ["maximum"] = 10, ["step"] = 1, ["default"] = 1 }
                    })
                }
            };
        }

        private static string Page(IEnumerable<JObject> models, string nextLink = null) =>
            new JObject { ["value"] = new JArray(models), ["nextLink"] = nextLink }.ToString();

        private static async Task<IReadOnlyList<OpenAIModelChoice>> LoadAsync(CatalogueHandler handler, CancellationToken cancellation = default)
        {
            using (var http = new HttpClient(handler))
            {
                var options = new ArmClientOptions { Transport = new HttpClientTransport(http) };
                options.Retry.MaxRetries = 0;
                var client = new ArmClient(new SyntheticCredential(), null, options);
                var subscription = client.GetSubscriptionResource(new ResourceIdentifier("/subscriptions/" + Guid.Empty));
                return await OpenAIModelCatalog.LoadAsync(subscription, new AzureLocation("westeurope"), cancellation);
            }
        }

        internal static IReadOnlyList<OpenAIModelChoice> SyntheticChoices() => LoadAsync(new CatalogueHandler(Page(new[]
        {
            Model("gpt-4.1-nano", "2025-04-14"),
            Model("gpt-4o-mini", "2024-07-18"),
            Model("gpt-4.1", "2025-04-14"),
            Model("gpt-4.1-nano", "2025-04-14", "DataZoneStandard"),
            Model("gpt-4o-mini", "2024-07-18", "GlobalStandard"),
            Model("gpt-5-nano", "2025-08-07", "GlobalStandard")
        }))).GetAwaiter().GetResult();

        [TestMethod]
        public async Task Catalogue_UsesRegionAndSubscription_FollowsNextLink_AndSeparatesAccountSku()
        {
            var next = "https://management.azure.com/subscriptions/" + Guid.Empty + "/providers/Microsoft.CognitiveServices/locations/westeurope/models?continuation=synthetic";
            var handler = new CatalogueHandler(
                Page(new[] { Model("gpt-4.1", "2025-04-14"), Model("gpt-4o-mini", "2024-07-18") }, next),
                Page(new[] { Model("gpt-4.1-nano", "2025-04-14", "GlobalStandard") }));
            var choices = await LoadAsync(handler);
            Assert.AreEqual(2, handler.Requests.Count);
            StringAssert.Contains(handler.Requests[0], "/subscriptions/" + Guid.Empty + "/providers/Microsoft.CognitiveServices/locations/westeurope/models");
            Assert.AreEqual(next, handler.Requests[1]);
            Assert.AreEqual(3, choices.Count);
            Assert.IsTrue(choices.Take(2).All(x => x.IsSmallModel));
            Assert.IsTrue(choices.Any(x => x.DeploymentType == "GlobalStandard"));
            Assert.IsFalse(choices.Any(x => x.DeploymentType == "S0"));
        }

        [TestMethod]
        public async Task Catalogue_ExcludesRetiredAndNonTokenSkus_AndDoesNotRecommendReasoningOrUnsupportedChat()
        {
            var otherAccountKind = Model("gpt-4o-mini", "2024-07-18");
            otherAccountKind["kind"] = "AIServices";
            var choices = await LoadAsync(new CatalogueHandler(Page(new[]
            {
                otherAccountKind,
                Model("gpt-4o-mini", "2024-07-18", lifecycle: "Deprecated"),
                Model("gpt-4o-mini", "2024-07-18", type: "GlobalBatch"),
                Model("gpt-4o-mini", "2024-07-18", lifecycle: "Preview"),
                Model("gpt-5-nano", "2025-08-07"),
                Model("gpt-4o", "2024-05-13"),
                Model("gpt-4.1-mini", "2025-04-14", chat: false)
            })));
            Assert.AreEqual(4, choices.Count);
            Assert.AreEqual(1, choices.Count(x => x.IsClassifierCompatible));
            Assert.AreEqual("Preview", choices.Single(x => x.IsClassifierCompatible).Lifecycle);
        }

        [TestMethod]
        public async Task Catalogue_CapacityHonoursRangeStepsAndExplicitAllowedValues()
        {
            var model = Model("gpt-4o-mini", "2024-07-18");
            model["model"]["skus"][0]["capacity"] = new JObject
                { ["minimum"] = 2, ["maximum"] = 8, ["step"] = 2, ["default"] = 4, ["allowedValues"] = new JArray(4, 8) };
            var choice = (await LoadAsync(new CatalogueHandler(Page(new[] { model })))).Single();
            Assert.AreEqual(4, choice.SuggestedCapacity);
            foreach (var value in new[] { 0, 1, 2, 3, 6, 9, 10001 })
                Assert.IsFalse(choice.SupportsCapacity(value), value.ToString());
            Assert.IsTrue(choice.SupportsCapacity(4));
            Assert.IsTrue(choice.SupportsCapacity(8));
        }

        [TestMethod]
        public void Selection_RequiresExactTupleAndCapacity_ResolvesBlankVersionOnlyToAzureDefault()
        {
            var choices = SyntheticChoices();
            Assert.AreEqual("2025-04-14", OpenAIModelCatalog.ValidateSelection(choices, "gpt-4.1-nano", "", "Standard", 1).Version);
            Assert.ThrowsException<InstallException>(() => OpenAIModelCatalog.ValidateSelection(choices, "gpt-4.1-nano", "2025-04-14", "GlobalStandard", 1));
            Assert.ThrowsException<InstallException>(() => OpenAIModelCatalog.ValidateSelection(choices, "gpt-4.1-nano", "wrong-version", "Standard", 1));
            Assert.ThrowsException<InstallException>(() => OpenAIModelCatalog.ValidateSelection(choices, "gpt-4.1-nano", "", "Standard", 11));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task Install_PreflightsBeforeAccountMutation_UsesExistingAccountLocation(bool existing)
        {
            var prefix = "/subscriptions/" + Guid.Empty + "/resourceGroups/ContosoAnalytics";
            var account = new JObject
            {
                ["id"] = prefix + "/providers/Microsoft.CognitiveServices/accounts/contoso-prompt-ai",
                ["name"] = "contoso-prompt-ai", ["kind"] = "OpenAI", ["location"] = "eastus",
                ["sku"] = new JObject { ["name"] = "S0" }, ["properties"] = new JObject()
            };
            var accounts = new JObject { ["value"] = existing ? new JArray(account) : new JArray() }.ToString();
            var handler = new CatalogueHandler(accounts, Page(new[] { Model("gpt-4o-mini", "2024-07-18", "GlobalStandard") }));
            using (var http = new HttpClient(handler))
            {
                var client = new ArmClient(new SyntheticCredential(), null,
                    new ArmClientOptions { Transport = new HttpClientTransport(http) });
                var subscription = client.GetSubscriptionResource(new ResourceIdentifier("/subscriptions/" + Guid.Empty));
                var config = TaskConfig.GetConfigForName("contoso-prompt-ai")
                    .AddSetting(AzureOpenAIInstallTask.CONFIG_KEY_DEPLOYMENT_NAME, "prompt-categories")
                    .AddSetting(AzureOpenAIInstallTask.CONFIG_KEY_MODEL_NAME, "gpt-4o-mini")
                    .AddSetting(AzureOpenAIInstallTask.CONFIG_KEY_MODEL_VERSION, "2024-07-18")
                    .AddSetting(AzureOpenAIInstallTask.CONFIG_KEY_DEPLOYMENT_TYPE, "Standard")
                    .AddSetting(AzureOpenAIInstallTask.CONFIG_KEY_CAPACITY, "1");
                var task = new AzureOpenAIInstallTask(config, NullLogger.Instance, new AzureLocation("westeurope"),
                    new Dictionary<string, string>(), subscription)
                {
                    Container = client.GetResourceGroupResource(new ResourceIdentifier(prefix))
                };
                await Assert.ThrowsExceptionAsync<InstallException>(() => task.ExecuteTaskReturnResult(null));
                Assert.AreEqual(2, handler.Requests.Count);
                Assert.IsTrue(handler.Methods.All(x => x == HttpMethod.Get), "Unsupported tuple must not change Azure.");
                StringAssert.Contains(handler.Requests[1], "/locations/" + (existing ? "eastus" : "westeurope") + "/models");
            }
        }

        [TestMethod]
        public async Task Catalogue_AccessFailuresAndCancellationPropagateInsteadOfReturningEmptySuccess()
        {
            await Assert.ThrowsExceptionAsync<RequestFailedException>(() => LoadAsync(new CatalogueHandler { Status = HttpStatusCode.Forbidden }));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try
                {
                    await LoadAsync(new CatalogueHandler(Page(Array.Empty<JObject>())), cancelled.Token);
                    Assert.Fail("Cancellation should not be swallowed.");
                }
                catch (OperationCanceledException) { }
            }
            Assert.AreEqual(0, (await LoadAsync(new CatalogueHandler(Page(Array.Empty<JObject>())))).Count);
        }

        [TestMethod]
        public void Browser_DefaultsToRegionalSmallModels_NeverChangesGeographyForAnEmptyList()
        {
            var choices = SyntheticChoices();
            RunSta(() =>
            {
                using (var browser = new OpenAIModelBrowserForm())
                {
                    browser.LoadModelsAsync = ct => Task.FromResult(choices);
                    browser.Show();
                    Application.DoEvents();
                    Assert.IsFalse(browser.ShowIcon);
                    Assert.IsFalse(browser.ShowInTaskbar);
                    Assert.AreEqual("Standard", Find<ComboBox>(browser, "deploymentType").SelectedItem);
                    Assert.AreEqual(2, Find<ListView>(browser, "models").Items.Count);
                    Assert.IsFalse(Find<Button>(browser, "applyModel").Enabled);
                    Find<CheckBox>(browser, "smallOnly").Checked = false;
                    Assert.AreEqual(3, Find<ListView>(browser, "models").Items.Count);
                    Find<CheckBox>(browser, "smallOnly").Checked = true;
                    Find<ComboBox>(browser, "deploymentType").SelectedItem = "GlobalStandard";
                    Assert.AreEqual(1, Find<ListView>(browser, "models").Items.Count);
                    StringAssert.Contains(Find<Label>(browser, "residency").Text, "worldwide");
                    browser.LoadModelsAsync = ct => Task.FromResult<IReadOnlyList<OpenAIModelChoice>>(Array.Empty<OpenAIModelChoice>());
                    browser.RefreshModelsAsync().GetAwaiter().GetResult();
                    Assert.AreEqual("GlobalStandard", Find<ComboBox>(browser, "deploymentType").SelectedItem);
                    StringAssert.Contains(Find<Label>(browser, "catalogueStatus").Text, "No compatible models");
                    Assert.IsFalse(Find<Button>(browser, "applyModel").Enabled);
                    Assert.IsNull(browser.SelectedModel);
                }
            });
        }

        [TestMethod]
        public void Browser_AppliesOnlyConfirmedSelection_AndRefusesOutOfRangeCapacity()
        {
            var choices = SyntheticChoices();
            RunSta(() =>
            {
                using (var browser = new OpenAIModelBrowserForm("DataZoneStandard", "gpt-4.1-nano", "2025-04-14"))
                {
                    browser.LoadModelsAsync = ct => Task.FromResult(choices);
                    browser.Show();
                    Application.DoEvents();
                    Assert.IsNull(browser.SelectedModel);
                    var capacity = Find<NumericUpDown>(browser, "capacity");
                    capacity.Value = 11;
                    Assert.IsFalse(Find<Button>(browser, "applyModel").Enabled);
                    capacity.Value = 2;
                    Assert.IsTrue(Find<Button>(browser, "applyModel").Enabled);
                    Find<Button>(browser, "applyModel").PerformClick();
                    Assert.AreEqual(DialogResult.OK, browser.DialogResult);
                    Assert.AreEqual("DataZoneStandard", browser.SelectedModel.DeploymentType);
                    Assert.AreEqual("gpt-4.1-nano", browser.SelectedModel.Name);
                    Assert.AreEqual(2, browser.SelectedCapacity);
                }
            });
        }

        [TestMethod]
        public void Browser_ShowsLookupFailure_AndCancelDoesNotApply()
        {
            RunSta(() =>
            {
                using (var browser = new OpenAIModelBrowserForm())
                {
                    browser.LoadModelsAsync = ct => Task.FromException<IReadOnlyList<OpenAIModelChoice>>(new InvalidOperationException("Synthetic permission failure"));
                    browser.Show();
                    Application.DoEvents();
                    StringAssert.Contains(Find<Label>(browser, "catalogueStatus").Text, "Synthetic permission failure");
                    Assert.IsFalse(Find<Button>(browser, "applyModel").Enabled);
                    Assert.IsTrue(Find<Button>(browser, "refresh").Enabled);
                    Find<ComboBox>(browser, "deploymentType").SelectedItem = "GlobalStandard";
                    StringAssert.Contains(Find<Label>(browser, "catalogueStatus").Text, "Synthetic permission failure");
                    Assert.IsTrue(Find<LinkLabel>(browser, "lookupErrorDetails").Visible);
                    browser.CancelButton.PerformClick();
                    Assert.IsNull(browser.SelectedModel);
                    Assert.AreEqual(DialogResult.Cancel, browser.DialogResult);
                }
            });
        }

        [TestMethod]
        public void Browser_LoadingAndCloseCancellationNeverApplyAStaleResult()
        {
            RunSta(() =>
            {
                var result = new TaskCompletionSource<IReadOnlyList<OpenAIModelChoice>>();
                CancellationToken lookupToken = default;
                using (var browser = new OpenAIModelBrowserForm())
                {
                    browser.Show();
                    browser.LoadModelsAsync = ct => { lookupToken = ct; return result.Task; };
                    var lookup = browser.RefreshModelsAsync();
                    Assert.IsFalse(Find<Button>(browser, "refresh").Enabled);
                    Assert.IsFalse(Find<Button>(browser, "applyModel").Enabled);
                    browser.Close();
                    Assert.IsTrue(lookupToken.IsCancellationRequested);
                    result.SetResult(Array.Empty<OpenAIModelChoice>());
                    for (var i = 0; i < 100 && !lookup.IsCompleted; i++)
                    {
                        Application.DoEvents();
                        Thread.Sleep(5);
                    }
                    Assert.IsTrue(lookup.IsCompleted);
                    lookup.GetAwaiter().GetResult();
                    Assert.IsNull(browser.SelectedModel);
                }
            });
        }

        [TestMethod]
        public void Browser_MinimumWindowHasUsableModelListAndActions_OptionalSyntheticScreenshots()
        {
            var choices = SyntheticChoices();
            RunSta(() =>
            {
                using (var browser = new OpenAIModelBrowserForm("Standard", "gpt-4.1-nano", "2025-04-14"))
                {
                    browser.ContextDescription = "Region: synthetic test region. Uses the selected subscription and installer identity.";
                    browser.LoadModelsAsync = ct => Task.FromResult(choices);
                    browser.Size = browser.MinimumSize;
                    browser.Show();
                    Application.DoEvents();
                    var list = Find<ListView>(browser, "models");
                    Assert.IsTrue(list.Height >= 100, $"List should remain usable: {list.Height}px.");
                    foreach (var name in new[] { "applyModel", "refresh", "capacity" })
                    {
                        var control = browser.Controls.Find(name, true).Single();
                        Assert.IsTrue(browser.RectangleToScreen(browser.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), name);
                    }

                    var directory = Environment.GetEnvironmentVariable("INSTALLER_PREVIEW_DIR");
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Capture(browser, directory, "model-browser-minimum");
                        Find<ComboBox>(browser, "deploymentType").SelectedItem = "GlobalStandard";
                        Capture(browser, directory, "model-browser-global");
                        browser.LoadModelsAsync = ct => Task.FromException<IReadOnlyList<OpenAIModelChoice>>(new InvalidOperationException("Synthetic read-access failure"));
                        browser.RefreshModelsAsync().GetAwaiter().GetResult();
                        Capture(browser, directory, "model-browser-error");
                    }
                }
            });
        }

        [TestMethod]
        public void AzureAI_ModelSettingsRemainAccessibleAtMinimumPageSize()
        {
            RunSta(() =>
            {
                using (var page = new AzureAIConfigControl())
                using (var host = new Form { ClientSize = new Size(730, 520), ShowIcon = false, ShowInTaskbar = false, Text = "Synthetic Azure AI settings preview" })
                {
                    page.Dock = DockStyle.Fill;
                    page.FoundryPromptEnabled = true;
                    page.FoundryPromptResourceName = "contoso-prompt-ai";
                    page.FoundryPromptModelName = "gpt-4.1-nano";
                    page.FoundryPromptModelVersion = "2025-04-14";
                    host.Controls.Add(page);
                    host.Show();
                    Application.DoEvents();
                    Assert.IsTrue(Find<Button>(page, "btnBrowseModels").Enabled);
                    var directory = Environment.GetEnvironmentVariable("INSTALLER_PREVIEW_DIR");
                    if (!string.IsNullOrWhiteSpace(directory))
                        Capture(host, directory, "model-settings-minimum-top");
                    page.ScrollControlIntoView(Find<Label>(page, "lblFoundryResidency"));
                    Application.DoEvents();
                    var resourceLeft = Find<TextBox>(page, "txtFoundryResourceName").Left;
                    foreach (var name in new[] { "txtFoundryModelName", "txtFoundryModelVersion", "cmbFoundryDeploymentType" })
                    {
                        var control = page.Controls.Find(name, true).Single();
                        Assert.IsTrue(page.RectangleToScreen(page.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), name);
                    }
                    Assert.AreEqual(resourceLeft, Find<TextBox>(page, "txtFoundryDeploymentName").Left);
                    Assert.AreEqual(resourceLeft, Find<TextBox>(page, "txtFoundryModelName").Left);
                    Assert.AreEqual(resourceLeft, Find<ComboBox>(page, "cmbFoundryDeploymentType").Left);
                    Assert.AreEqual(Find<TextBox>(page, "txtFoundryCapacity").Left, Find<TextBox>(page, "txtFoundryModelVersion").Left);
                    var deploymentType = Find<ComboBox>(page, "cmbFoundryDeploymentType");
                    var residency = Find<Label>(page, "lblFoundryResidency");
                    Assert.IsTrue(residency.Top - deploymentType.Bottom <= 12,
                        "Processing-geography guidance should stay beside the deployment-type selection.");
                    if (!string.IsNullOrWhiteSpace(directory))
                        Capture(host, directory, "model-settings-minimum-bottom");
                }
            });
        }

        private static void Capture(Form form, string directory, string name)
        {
            Application.DoEvents();
            using (var image = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                image.Save(Path.Combine(directory, name + ".png"));
            }
        }

        private sealed class SyntheticCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                new AccessToken("synthetic-not-a-real-token", DateTimeOffset.UtcNow.AddMinutes(5));
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
        }

        private sealed class CatalogueHandler : HttpMessageHandler
        {
            private readonly Queue<string> _pages;
            public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
            public List<string> Requests { get; } = new List<string>();
            public List<HttpMethod> Methods { get; } = new List<HttpMethod>();
            public CatalogueHandler(params string[] pages) { _pages = new Queue<string>(pages); }
            // ARM's synchronous resource enumeration uses HttpMessageHandler.Send on .NET 10.
            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
                SendAsync(request, cancellationToken).GetAwaiter().GetResult();
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Requests.Add(request.RequestUri.AbsoluteUri);
                Methods.Add(request.Method);
                return Task.FromResult(new HttpResponseMessage(Status)
                {
                    Content = new StringContent(Status == HttpStatusCode.OK ? _pages.Dequeue() :
                        "{\"error\":{\"code\":\"AuthorizationFailed\",\"message\":\"Synthetic read-access failure\"}}",
                        System.Text.Encoding.UTF8, "application/json")
                });
            }
        }
    }
}
