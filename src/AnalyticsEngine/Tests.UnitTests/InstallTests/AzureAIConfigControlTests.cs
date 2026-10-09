using App.ControlPanel.Engine;
using App.ControlPanel.Frames;
using App.ControlPanel.Frames.InstallWizard;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;

namespace Tests.UnitTests.InstallTests
{
    [TestClass]
    public class AzureAIConfigControlTests
    {
        [TestMethod]
        public void AzureAI_BothServicesRoundTripThroughInstallerUI()
        {
            RunOnSta(() =>
            {
                var config = SolutionInstallConfig.NewConfig();
                config.CognitiveServicesEnabled = true;
                config.CognitiveServiceName = "contoso-language";
                config.FoundryPromptEnabled = true;
                config.FoundryPromptResourceName = "contoso-prompt-ai";
                config.FoundryPromptDeploymentName = "prompt-categories";
                config.FoundryPromptModelName = "gpt-4o-mini";
                config.FoundryPromptModelVersion = "2024-07-18";
                config.FoundryPromptCapacity = 2;
                using (var wizard = new InstallSPOSitesControl())
                {
                    wizard.ConfigureUI(config);
                    var result = wizard.GetConfigurationState();
                    AssertAIEqual(config, result);
                    var ai = Find<AzureAIConfigControl>(wizard, "azureAIConfigControl1");
                    Assert.IsTrue(Find<TextBox>(ai, "txtCognitiveName").Enabled);
                    Assert.IsTrue(Find<TextBox>(ai, "txtFoundryResourceName").Enabled);
                    ai.CognitiveServiceName = "contoso-language-new";
                    ai.FoundryPromptDeploymentName = "prompt-categories-new";
                    result = wizard.GetConfigurationState();
                    Assert.AreEqual("contoso-language-new", result.CognitiveServiceName);
                    Assert.AreEqual("prompt-categories-new", result.FoundryPromptDeploymentName);
                }
            });
        }

        [TestMethod]
        public void AzureAI_LegacyAndDisabledSettingsPreserveDefaultsAndValues()
        {
            RunOnSta(() =>
            {
                var config = SolutionInstallConfig.LoadFromJson("{}", "synthetic-password").Config;
                using (var wizard = new InstallSPOSitesControl())
                {
                    wizard.ConfigureUI(config);
                    AssertAIEqual(config, wizard.GetConfigurationState());
                    Assert.IsFalse(wizard.GetConfigurationState().FoundryPromptEnabled);
                    config.CognitiveServicesEnabled = false;
                    config.CognitiveServiceName = "contoso-language";
                    config.FoundryPromptEnabled = false;
                    config.FoundryPromptResourceName = "contoso-prompt-ai";
                    wizard.ConfigureUI(config);
                    AssertAIEqual(config, wizard.GetConfigurationState());
                    var ai = Find<AzureAIConfigControl>(wizard, "azureAIConfigControl1");
                    Assert.IsFalse(Find<TextBox>(ai, "txtCognitiveName").Enabled);
                    foreach (var name in new[] { "txtFoundryResourceName", "txtFoundryDeploymentName",
                        "txtFoundryModelName", "txtFoundryModelVersion", "txtFoundryCapacity" })
                        Assert.IsFalse(Find<TextBox>(ai, name).Enabled, name);
                }
            });
        }

        [TestMethod]
        public void AzureAI_TabFollowsPaaS_AndSharePointVisibilityDoesNotMoveIt()
        {
            RunOnSta(() =>
            {
                using (var wizard = new InstallSPOSitesControl())
                using (var host = new Form())
                {
                    host.Controls.Add(wizard);
                    host.StartPosition = FormStartPosition.Manual;
                    host.Location = new System.Drawing.Point(-2000, -2000);
                    host.ShowInTaskbar = false;
                    host.Show();
                    Application.DoEvents();
                    var config = SolutionInstallConfig.NewConfig();
                    config.SolutionConfig.ImportTaskSettings.WebTraffic = false;
                    config.SolutionConfig.ImportTaskSettings.ActivityLog = false;
                    wizard.ConfigureUI(config);
                    var tabs = Find<TabControl>(wizard, "tabs");
                    AssertTabOrder(tabs);
                    Assert.IsFalse(tabs.TabPages.ContainsKey("tabSharePoint"));
                    config.SolutionConfig.ImportTaskSettings.WebTraffic = true;
                    wizard.ConfigureUI(config);
                    AssertTabOrder(tabs);
                    Assert.AreEqual(tabs.TabPages.IndexOfKey("tabSources") - 1, tabs.TabPages.IndexOfKey("tabSharePoint"),
                        string.Join(", ", tabs.TabPages.Cast<TabPage>().Select(page => page.Name)) +
                        "; WebTraffic=" + wizard.GetConfigurationState().SolutionConfig.ImportTaskSettings.WebTraffic);
                    config.SolutionConfig.ImportTaskSettings.WebTraffic = false;
                    wizard.ConfigureUI(config);
                    AssertTabOrder(tabs);
                    Assert.IsFalse(tabs.TabPages.ContainsKey("tabSharePoint"));
                    var paas = Find<AzurePaaSConfigControl>(wizard, "azurePaaSConfigControl1");
                    Assert.IsFalse(paas.Controls.Cast<Control>().Any(control =>
                        control.Name.Contains("Foundry") || control.Name.Contains("Cognitive") ||
                        control.Text.Contains("Foundry") || control.Text.Contains("Cognitive")));
                }
            });
        }

        [TestMethod]
        public void AzureAI_BothServicesHaveDistinctEmbeddedIcons()
        {
            RunOnSta(() =>
            {
                using (var ai = new AzureAIConfigControl())
                {
                    var cognitive = Find<PictureBox>(ai, "picCognitive");
                    var foundry = Find<PictureBox>(ai, "picFoundry");
                    Assert.IsNotNull(cognitive.Image);
                    Assert.IsNotNull(foundry.Image);
                    Assert.AreNotSame(cognitive.Image, foundry.Image);
                    foreach (var icon in new[] { cognitive, foundry })
                    {
                        Assert.AreEqual(PictureBoxSizeMode.Zoom, icon.SizeMode);
                        Assert.IsFalse(icon.TabStop);
                        Assert.AreEqual(new System.Drawing.Size(56, 56), icon.Size);
                    }
                }
            });
        }

        [TestMethod]
        public void AzureAI_DescriptionNamesGraphHistoryAndSeparatePortalOptIn()
        {
            RunOnSta(() =>
            {
                using (var ai = new AzureAIConfigControl())
                {
                    var text = Find<Label>(ai, "lblFoundryDescription").Text;
                    StringAssert.Contains(text, "connects the runtime automatically");
                    StringAssert.Contains(text, "'Copilot AI interaction history' on Targets");
                    StringAssert.Contains(text, "Administration > Prompt categories");
                    Assert.IsFalse(ai.FoundryPromptEnabled);
                }
            });
        }

        private static void AssertTabOrder(TabControl tabs)
        {
            Assert.AreEqual(tabs.TabPages.IndexOfKey("tabAzureResources") + 1, tabs.TabPages.IndexOfKey("tabAzureAI"));
            Assert.AreEqual("Azure AI", tabs.TabPages["tabAzureAI"].Text);
            Assert.AreEqual(tabs.TabPages.IndexOfKey("tabAzureAI") + 1, tabs.TabPages.IndexOfKey("tabAzureStorage"));
        }

        private static void AssertAIEqual(SolutionInstallConfig expected, SolutionInstallConfig actual)
        {
            Assert.AreEqual(expected.CognitiveServicesEnabled, actual.CognitiveServicesEnabled);
            Assert.AreEqual(expected.CognitiveServiceName, actual.CognitiveServiceName);
            Assert.AreEqual(expected.FoundryPromptEnabled, actual.FoundryPromptEnabled);
            Assert.AreEqual(expected.FoundryPromptResourceName, actual.FoundryPromptResourceName);
            Assert.AreEqual(expected.FoundryPromptDeploymentName, actual.FoundryPromptDeploymentName);
            Assert.AreEqual(expected.FoundryPromptModelName, actual.FoundryPromptModelName);
            Assert.AreEqual(expected.FoundryPromptModelVersion, actual.FoundryPromptModelVersion);
            Assert.AreEqual(expected.FoundryPromptCapacity, actual.FoundryPromptCapacity);
            Assert.AreEqual(expected.ConfigSchemaVersion, actual.ConfigSchemaVersion);
        }

        private static T Find<T>(Control parent, string name) where T : Control =>
            (T)parent.Controls.Find(name, true).Single();

        private static void RunOnSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "Installer UI test did not finish.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
