using App.ControlPanel.Engine;
using App.ControlPanel.Engine.InstallerTasks;
using Azure.Core;
using CloudInstallEngine.Azure.InstallTasks;
using CloudInstallEngine.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;

namespace Tests.UnitTests.InstallTests
{
    [TestClass]
    public class FoundryInstallConfigTests
    {
        [TestMethod]
        public void NewInstallConfig_LeavesFoundryDisabled()
        {
            var config = SolutionInstallConfig.NewConfig();

            Assert.IsFalse(config.FoundryPromptEnabled);
            Assert.IsTrue(config.ValidatInputAndGetErrors().All(error => !error.Contains("Azure AI Foundry") && !error.Contains("Azure OpenAI")));
        }

        [TestMethod]
        public void FoundrySettings_RoundTripThroughSavedInstallerConfig()
        {
            var config = SolutionInstallConfig.NewConfig();
            config.FoundryPromptEnabled = true;
            config.FoundryPromptResourceName = "contoso-prompt-ai";
            config.FoundryPromptDeploymentName = "prompt-categories";
            config.FoundryPromptModelName = "gpt-4o-mini";
            config.FoundryPromptModelVersion = "2024-07-18";
            config.FoundryPromptCapacity = 2;

            var json = config.ToJson("synthetic-password");
            var loaded = SolutionInstallConfig.LoadFromJson(json, "synthetic-password").Config;

            Assert.IsTrue(loaded.FoundryPromptEnabled);
            Assert.AreEqual("contoso-prompt-ai", loaded.FoundryPromptResourceName);
            Assert.AreEqual("prompt-categories", loaded.FoundryPromptDeploymentName);
            Assert.AreEqual("gpt-4o-mini", loaded.FoundryPromptModelName);
            Assert.AreEqual("2024-07-18", loaded.FoundryPromptModelVersion);
            Assert.AreEqual(2, loaded.FoundryPromptCapacity);
            Assert.AreEqual("3.2.0", loaded.ConfigSchemaVersion.ToString());
        }

        [TestMethod]
        public void FoundrySettings_MissingFromLegacyConfigUseDisabledDefaults()
        {
            var config = SolutionInstallConfig.LoadFromJson("{}", "synthetic-password").Config;

            Assert.IsFalse(config.FoundryPromptEnabled);
            Assert.AreEqual(string.Empty, config.FoundryPromptResourceName);
            Assert.AreEqual("prompt-categories", config.FoundryPromptDeploymentName);
            Assert.AreEqual("gpt-4o-mini", config.FoundryPromptModelName);
            Assert.AreEqual(string.Empty, config.FoundryPromptModelVersion);
            Assert.AreEqual(1, config.FoundryPromptCapacity);
        }

        [TestMethod]
        public void FoundryValidation_RequiresValidResourceAndDeploymentInputsOnlyWhenEnabled()
        {
            var config = SolutionInstallConfig.NewConfig();
            config.FoundryPromptEnabled = true;
            config.FoundryPromptResourceName = "Invalid_Name";
            config.FoundryPromptDeploymentName = "";
            config.FoundryPromptModelName = "";
            config.FoundryPromptCapacity = 0;

            var foundryErrors = config.ValidatInputAndGetErrors()
                .Where(error => error.Contains("Azure AI Foundry") || error.Contains("Azure OpenAI"))
                .ToList();

            Assert.AreEqual(4, foundryErrors.Count);
        }

        [TestMethod]
        public void DeploymentData_UsesConfiguredOpenAIModelAndCapacity()
        {
            var data = AzureOpenAIInstallTask.BuildDeploymentData("gpt-4o-mini", "2024-07-18", 2);

            Assert.AreEqual("OpenAI", data.Properties.Model.Format);
            Assert.AreEqual("gpt-4o-mini", data.Properties.Model.Name);
            Assert.AreEqual("2024-07-18", data.Properties.Model.Version);
            Assert.AreEqual("Standard", data.Sku.Name);
            Assert.AreEqual(2, data.Sku.Capacity);
        }

        [TestMethod]
        public void AccountData_UsesOpenAIKindAndHonoursPublicNetworkChoice()
        {
            var data = AzureOpenAIInstallTask.BuildAccountData(AzureLocation.WestEurope, "contoso-prompt-ai", false);

            Assert.AreEqual("OpenAI", data.Kind);
            Assert.AreEqual("S0", data.Sku.Name);
            Assert.AreEqual("contoso-prompt-ai", data.Properties.CustomSubDomainName);
            Assert.AreEqual("Disabled", data.Properties.PublicNetworkAccess.ToString());
        }

        [TestMethod]
        public void FoundryRuntimeRole_IsInferenceOnlyBuiltInRole()
        {
            Assert.AreEqual("Cognitive Services OpenAI User", ResourceSecurityInstallJob.FOUNDRY_OPENAI_USER_ROLE_NAME);
        }

        [TestMethod]
        public void DisabledFoundrySettings_ClearManagedRuntimeValues()
        {
            var settings = FoundryPromptAppSettings.Build(false,
                new FoundryPromptInfo { Endpoint = "https://contoso.openai.azure.com/", Deployment = "old-model" });

            Assert.AreEqual(string.Empty, settings["FoundryPromptEndpoint"]);
            Assert.AreEqual(string.Empty, settings["FoundryPromptDeployment"]);
            Assert.AreEqual(string.Empty, settings["FoundryPromptKey"]);
            Assert.AreEqual(string.Empty, settings["FoundryPromptCategorisationEndpoint"]);
            Assert.AreEqual(string.Empty, settings["FoundryPromptCategorisationDeployment"]);
            Assert.AreEqual(string.Empty, settings["FoundryPromptCategorisationKey"]);
        }

        [TestMethod]
        public void DisabledFoundrySettings_WithNoProvisioningInfoRemainDisabled()
        {
            var settings = FoundryPromptAppSettings.Build(false, null);
            Assert.IsTrue(settings.Values.All(value => value == string.Empty));
        }

        [TestMethod]
        public void EnabledFoundrySettings_RequireValidProvisioningInfo()
        {
            Assert.ThrowsException<InstallException>(() => FoundryPromptAppSettings.Build(true, null));
            foreach (var service in new[]
            {
                new FoundryPromptInfo(),
                new FoundryPromptInfo { Endpoint = "http://contoso.openai.azure.com/", Deployment = "prompt-categories" },
                new FoundryPromptInfo { Endpoint = "https://contoso.example/", Deployment = "prompt-categories" },
                new FoundryPromptInfo { Endpoint = "https://" + new string('a', 64) + ".openai.azure.com/", Deployment = "prompt-categories" },
                new FoundryPromptInfo { Endpoint = "https://contoso.openai.azure.com/", Deployment = "" },
                new FoundryPromptInfo { Endpoint = "https://contoso.openai.azure.com/", Deployment = "prompt-categories\n" }
            })
                Assert.ThrowsException<InstallException>(() => FoundryPromptAppSettings.Build(true, service));
        }

        [TestMethod]
        public void FoundryValidation_RejectsWhitespaceAndTrailingNewlines()
        {
            var config = SolutionInstallConfig.NewConfig();
            config.FoundryPromptEnabled = true;
            config.FoundryPromptResourceName = " contoso-prompt-ai ";
            config.FoundryPromptDeploymentName = "prompt-categories\n";
            config.FoundryPromptModelName = "gpt-4o-mini\n";
            config.FoundryPromptModelVersion = "2024-07-18\n";
            Assert.AreEqual(4, config.ValidatInputAndGetErrors()
                .Count(error => error.Contains("Azure AI Foundry") || error.Contains("Azure OpenAI")));
        }

        [TestMethod]
        public void FoundryResourceName_IsLimitedToADnsLabel()
        {
            var config = SolutionInstallConfig.NewConfig();
            config.FoundryPromptEnabled = true;
            config.FoundryPromptResourceName = new string('a', 63);
            Assert.IsFalse(config.ValidatInputAndGetErrors().Any(error => error.Contains("resource name")));
            config.FoundryPromptResourceName += "a";
            Assert.IsTrue(config.ValidatInputAndGetErrors().Any(error => error.Contains("Azure AI Foundry resource name")));
        }

        [TestMethod]
        public void EnabledFoundrySettingsConfigureEndpointAndDeploymentWithoutAKey()
        {
            var settings = FoundryPromptAppSettings.Build(true,
                new FoundryPromptInfo { Endpoint = "https://contoso.openai.azure.com/", Deployment = "prompt-categories" });

            CollectionAssert.AreEquivalent(new[]
            {
                "FoundryPromptCategorisationEndpoint", "FoundryPromptCategorisationDeployment", "FoundryPromptCategorisationKey",
                "FoundryPromptEndpoint", "FoundryPromptDeployment", "FoundryPromptKey"
            }, settings.Keys.ToArray());
            Assert.AreEqual("https://contoso.openai.azure.com/", settings["FoundryPromptCategorisationEndpoint"]);
            Assert.AreEqual("prompt-categories", settings["FoundryPromptCategorisationDeployment"]);
            Assert.AreEqual(string.Empty, settings["FoundryPromptCategorisationKey"]);
            Assert.AreEqual(string.Empty, settings["FoundryPromptEndpoint"]);
            Assert.AreEqual(string.Empty, settings["FoundryPromptDeployment"]);
            Assert.AreEqual(string.Empty, settings["FoundryPromptKey"]);
        }
    }
}
