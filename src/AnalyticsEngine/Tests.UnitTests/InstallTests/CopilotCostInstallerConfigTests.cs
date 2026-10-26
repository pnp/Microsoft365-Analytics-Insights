using App.ControlPanel.Engine;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Installer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// Installer side of the Copilot credit history preview (#769): the saved toggle, the runtime setting it is
    /// mirrored to, its storage prerequisite and its Test Configuration decision.
    /// </summary>
    [TestClass]
    public class CopilotCostInstallerConfigTests
    {
        [TestMethod]
        public void CopilotCostManagementIsOffByDefaultAndForOlderSettingsStrings()
        {
            Assert.IsFalse(new ImportTaskSettings().CopilotCostManagement);

            // A settings string written before the toggle existed - including one with the similarly named
            // toggles switched on - must not switch the preview on.
            var legacy = new ImportTaskSettings("Copilot=True;AzureCostManagement=True;CopilotStudioCredits=True");
            Assert.IsFalse(legacy.CopilotCostManagement);
            Assert.IsTrue(legacy.Copilot && legacy.AzureCostManagement);
        }

        [TestMethod]
        public void CopilotCostManagementRoundTripsWithoutTouchingOtherToggles()
        {
            var reloaded = new ImportTaskSettings(new ImportTaskSettings { CopilotCostManagement = true }.ToSettingsString());

            Assert.IsTrue(reloaded.CopilotCostManagement);
            foreach (var name in ImportTaskSettings.GetImportPropertyNames().Where(n => n != nameof(ImportTaskSettings.CopilotCostManagement)))
            {
                Assert.IsFalse(reloaded.IsImportEnabled(name), $"'{name}' was switched on by the preview toggle.");
            }
        }

        [TestMethod]
        public void CopilotCostManagementPersistsInTheSavedInstallerConfig()
        {
            var config = SolutionInstallConfig.NewConfig();
            config.SolutionConfig.ImportTaskSettings.CopilotCostManagement = true;

            var json = config.ToJson("p@ss");
            StringAssert.Contains(json, "\"CopilotCostManagement\":true");
            Assert.IsTrue(SolutionInstallConfig.LoadFromJson(json, "p@ss").Config.SolutionConfig.ImportTaskSettings.CopilotCostManagement);

            // A config saved before the toggle existed loads with it off.
            var legacyJson = json.Replace(",\"CopilotCostManagement\":true", string.Empty);
            Assert.IsFalse(legacyJson.Contains("CopilotCostManagement"));
            Assert.IsFalse(SolutionInstallConfig.LoadFromJson(legacyJson, "p@ss").Config.SolutionConfig.ImportTaskSettings.CopilotCostManagement);
        }

        [TestMethod]
        public void ConfigSchemaVersionWasBumpedForCopilotCostManagement()
        {
            Assert.IsTrue(new BaseSolutionInstallConfig().ConfigSchemaVersion >= new Version(2, 7, 0));
        }

        [TestMethod]
        public void DeploymentMirrorsTheToggleIntoTheRuntimeSetting()
        {
            foreach (var enabled in new[] { true, false })
            {
                var settings = new ImportTaskSettings { Copilot = true, CopilotCostManagement = enabled };
                var appSettings = new Dictionary<string, string>();

                ConfigureAzureComponentsTasks.AddImportSelectionAppSettings(appSettings, settings);

                // Written in both states so turning the preview off clears it on the next deployment.
                Assert.AreEqual(enabled ? "true" : "false", appSettings[AppConfig.CopilotCostConnectionEnabledSettingName]);
                Assert.AreEqual(enabled, AppConfig.ParseCopilotCostConnectionEnabled(appSettings[AppConfig.CopilotCostConnectionEnabledSettingName]));
                Assert.AreEqual(enabled, new ImportTaskSettings(appSettings["ImportJobSettings"]).CopilotCostManagement);
                Assert.AreEqual(ImportTaskSettings.CONTENT_TYPE_AUDIT_GENERAL, appSettings["ContentTypesListAsString"],
                    "The preview must not change which audit feeds are subscribed.");
            }
        }

        [TestMethod]
        public void ThePreviewAloneDoesNotSwitchOnOrdinaryImports()
        {
            var settings = new ImportTaskSettings { CopilotCostManagement = true };

            Assert.IsFalse(settings.UsesActivityApi);
            Assert.AreEqual(new ImportTaskSettings().ToActivityApiContentTypesString(), settings.ToActivityApiContentTypesString());
        }

        [TestMethod]
        public void RuntimeSettingParsesStrictly()
        {
            Assert.IsTrue(AppConfig.ParseCopilotCostConnectionEnabled("true"));
            Assert.IsTrue(AppConfig.ParseCopilotCostConnectionEnabled(" True "));
            foreach (var value in new[] { null, "", "false", "1", "yes", "enabled" })
            {
                Assert.IsFalse(AppConfig.ParseCopilotCostConnectionEnabled(value), $"'{value}' must not enable the preview.");
            }
        }

        [TestMethod]
        public void EnablingThePreviewWithoutStorageIsAnExplicitValidationError()
        {
            var config = SolutionInstallConfig.NewConfig();
            config.StorageAccountName = " ";

            Assert.IsFalse(config.ValidatInputAndGetErrors().Contains(SolutionInstallConfig.CopilotCostStorageRequiredError));

            config.SolutionConfig.ImportTaskSettings.CopilotCostManagement = true;
            Assert.IsTrue(config.ValidatInputAndGetErrors().Contains(SolutionInstallConfig.CopilotCostStorageRequiredError));

            config.StorageAccountName = "contosostorage";
            Assert.IsFalse(config.ValidatInputAndGetErrors().Contains(SolutionInstallConfig.CopilotCostStorageRequiredError));
        }

        [TestMethod]
        public void TestConfigurationNeverClaimsToProveDelegatedAccess()
        {
            var coverage = SolutionInstallVerifier.ImportToggleCoverages
                .Single(c => c.PropertyName == nameof(ImportTaskSettings.CopilotCostManagement));

            Assert.IsFalse(coverage.IsVerified, "An app-only verifier cannot prove a delegated connection.");
            StringAssert.Contains(coverage.NotVerifiedReason, "DELEGATED");
            StringAssert.Contains(coverage.NotVerifiedReason, "Administration");

            var reported = SolutionInstallVerifier.GetUnverifiedEnabledImports(new ImportTaskSettings { CopilotCostManagement = true });
            CollectionAssert.AreEqual(new[] { nameof(ImportTaskSettings.CopilotCostManagement) }, reported.Select(c => c.PropertyName).ToArray());
        }
    }
}
