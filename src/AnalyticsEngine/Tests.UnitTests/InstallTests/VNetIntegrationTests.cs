using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// Integration tests for VNet deployment. These tests deploy real Azure resources using the installer config file.
    /// Run manually - not included in CI/CD as they require Azure credentials and create billable resources.
    /// </summary>
    [TestClass]
    public class VNetIntegrationTests
    {
        private const string CONFIG_PASSWORD = "Corp123!";
        private readonly ILogger _logger;

        /// <summary>
        /// Path to the installer config file. Override via test runsettings if needed.
        /// </summary>
        private static string ConfigFilePath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "InstallTests", "TestConfigs", "InstallerTestConfig.json");

        public VNetIntegrationTests()
        {
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _logger = loggerFactory.CreateLogger("");
        }

        private SolutionInstallConfig LoadTestConfig()
        {
            Assert.IsTrue(File.Exists(ConfigFilePath), $"Test config file not found at '{ConfigFilePath}'. Copy your installer config JSON to this location.");

            var result = SolutionInstallConfig.LoadFromFile(ConfigFilePath, CONFIG_PASSWORD);
            Assert.IsTrue(result.DecryptedOk, "Failed to decrypt test config file. Check password.");
            return result.Config;
        }

        [TestMethod]
        [TestCategory("Integration")]
        public void VNetConfigValidation_EnabledWithoutName_ReturnsError()
        {
            var vnetConfig = new VNetConfig
            {
                Enabled = true,
                VNetName = "",
                SubnetName = "default",
                AddressPrefix = "10.0.0.0/16",
                SubnetAddressPrefix = "10.0.0.0/24"
            };

            var errors = vnetConfig.ValidatInputAndGetErrors();
            Assert.IsTrue(errors.Count > 0, "Should have validation errors when VNet name is empty");
        }

        [TestMethod]
        [TestCategory("Integration")]
        public void VNetConfigValidation_Disabled_NoErrors()
        {
            var vnetConfig = new VNetConfig { Enabled = false };
            var errors = vnetConfig.ValidatInputAndGetErrors();
            Assert.AreEqual(0, errors.Count, "Disabled VNet config should have no errors");
        }

        [TestMethod]
        [TestCategory("Integration")]
        public void VNetConfigValidation_ValidConfig_NoErrors()
        {
            var vnetConfig = new VNetConfig
            {
                Enabled = true,
                VNetName = "test-vnet",
                SubnetName = "default",
                AddressPrefix = "10.0.0.0/16",
                SubnetAddressPrefix = "10.0.0.0/24"
            };

            var errors = vnetConfig.ValidatInputAndGetErrors();
            Assert.AreEqual(0, errors.Count, $"Valid VNet config should have no errors but got: {string.Join(", ", errors)}");
        }

        [TestMethod]
        [TestCategory("Integration")]
        public void ConfigFileLoadsWithVNetDefaults()
        {
            var config = LoadTestConfig();
            Assert.IsNotNull(config);
            Assert.IsNotNull(config.NetworkConfig, "NetworkConfig should not be null - defaults should apply");
        }

        [TestMethod]
        [TestCategory("Integration")]
        public void SolutionInstallConfig_VNetEnabled_SkuUpgradeValidation()
        {
            var config = LoadTestConfig();
            config.NetworkConfig = new VNetConfig
            {
                Enabled = true,
                VNetName = "test-vnet",
                SubnetName = "default",
                AddressPrefix = "10.0.0.0/16",
                SubnetAddressPrefix = "10.0.0.0/24"
            };

            var errors = config.ValidatInputAndGetErrors();
            // Should not have VNet-related errors
            foreach (var err in errors)
            {
                Assert.IsFalse(err.Contains("VNet"), $"Unexpected VNet error: {err}");
                Assert.IsFalse(err.Contains("subnet"), $"Unexpected subnet error: {err}");
            }
        }

        [TestMethod]
        [TestCategory("Integration")]
        public void SolutionInstallConfig_SerializationRoundTrip_PreservesVNet()
        {
            var config = LoadTestConfig();
            config.NetworkConfig = new VNetConfig
            {
                Enabled = true,
                VNetName = "my-test-vnet",
                SubnetName = "my-subnet",
                AddressPrefix = "10.1.0.0/16",
                SubnetAddressPrefix = "10.1.0.0/24"
            };

            var json = config.ToJson(CONFIG_PASSWORD);
            var reloaded = SolutionInstallConfig.LoadFromJson(json, CONFIG_PASSWORD);

            Assert.IsNotNull(reloaded.Config.NetworkConfig);
            Assert.IsTrue(reloaded.Config.NetworkConfig.Enabled);
            Assert.AreEqual("my-test-vnet", reloaded.Config.NetworkConfig.VNetName);
            Assert.AreEqual("my-subnet", reloaded.Config.NetworkConfig.SubnetName);
            Assert.AreEqual("10.1.0.0/16", reloaded.Config.NetworkConfig.AddressPrefix);
            Assert.AreEqual("10.1.0.0/24", reloaded.Config.NetworkConfig.SubnetAddressPrefix);
        }

#if DEBUG
        /// <summary>
        /// Full integration test: deploys Azure PaaS resources with VNet integration.
        /// Only runs in DEBUG mode to avoid CI/CD costs. This test creates real Azure resources.
        /// </summary>
        [TestMethod]
        [TestCategory("Integration")]
        public async Task DeployWithVNet_CreatesVNetAndResources()
        {
            var config = LoadTestConfig();

            // Append unique suffix to avoid conflicts
            var suffix = DateTime.Now.Ticks.ToString().Substring(10);
            config.NetworkConfig = new VNetConfig
            {
                Enabled = true,
                VNetName = $"vnet-inttest-{suffix}",
                SubnetName = "default",
                AddressPrefix = "10.0.0.0/16",
                SubnetAddressPrefix = "10.0.0.0/24"
            };

            // Ensure we have valid subscription
            Assert.IsTrue(config.Subscription.IsValidSubscription, "Test config must have a valid Azure subscription");

            var azureSub = BaseAnalyticsSolutionInstallJob.FromConfig(config);
            var paasJob = new AzurePaaSInstallJob(_logger, config, azureSub);

            await paasJob.Install();

            // Verify resources were created
            Assert.IsNotNull(paasJob.CreatedSqlServer, "SQL Server should have been created");
            Assert.IsNotNull(paasJob.Redis, "Redis should have been created");
            Assert.IsNotNull(paasJob.Storage, "Storage should have been created");

            _logger.LogInformation("VNet integration deployment test completed successfully.");
        }
#endif
    }
}
