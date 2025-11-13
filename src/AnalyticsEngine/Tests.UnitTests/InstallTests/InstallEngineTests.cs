using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using App.ControlPanel.Engine.InstallerTasks.Tasks;
using App.ControlPanel.Engine.Models;
using Azure.Core;
using Azure.Identity;
using CloudInstallEngine;
using CloudInstallEngine.Models;
using Common.Entities.Config;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Text.Json;
using System.Threading.Tasks;
using Tests.UnitTests.InstallTests;

namespace Tests.UnitTests
{
    /// <summary>
    /// Most of these tests are disabled in release mode until someone can figure out a way to run without interactive login so DevOps releases don't fail
    /// </summary>
    [TestClass]
    public class InstallEngineTests
    {
        ILogger _logger;
        public InstallEngineTests()
        {
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

            _logger = loggerFactory.CreateLogger("");
        }

        SolutionInstallConfig GetSolutionInstallConfig(bool random)
        {
            string NAME = "o365advanalyticsunittest";
            if (random)
            {
                NAME += DateTime.Now.Ticks;
            }
            var azureAuthSettings = new AzureTestsConfigReader();
            var runtimeAuth = new AppConfig();
            var testConfig = new SolutionInstallConfig()
            {
                ResourceGroupName = "UnitTestsRG",
                AzureLocation = AzureLocation.WestEurope,
                AppInsightsName = NAME,
                AppServicePlanName = NAME,
                AppInsightsWorkspaceName = NAME,
                AppServiceWebAppName = NAME,
                CognitiveServicesEnabled = false,
                KeyVaultName = NAME,
                RedisName = NAME,
                StorageAccountName = NAME,
                SQLServerDatabaseName = NAME,
                SQLServerName = NAME,
                SQLServerAdminUsername = NAME,
                SQLServerAdminPassword = "Corp123!",
                ServiceBusName = NAME,
                Subscription = new App.ControlPanel.Engine.Entities.AzureSubscription(azureAuthSettings.SubId, "Test sub"),
                ActivityAccount = new App.ControlPanel.Engine.Entities.AppRegistrationCredentials
                {
                    ClientId = runtimeAuth.ClientID,
                    DirectoryId = runtimeAuth.TenantGUID.ToString(),
                    Secret = runtimeAuth.ClientSecret
                },
                InstallerAccount = new App.ControlPanel.Engine.Entities.AppRegistrationCredentials
                {
                    ClientId = azureAuthSettings.ClientID,
                    Secret = azureAuthSettings.ClientSecret,
                    DirectoryId = azureAuthSettings.TenantGUID
                },
                SolutionConfig = new TargetSolutionConfig
                {
                    SolutionLanguageCode = TargetSolutionConfig.LANG_ENGLISH,
                }

            };
            return testConfig;
        }

        [TestMethod]
        public void ExistingSolutionInstallConfigFileOpen()
        {
            // Try and load a pre-saved config file. 
            var configValidPassword = SolutionInstallConfig.LoadFromJson(Properties.Resources.TestInstallerConfig, "Corp123!");
            Assert.IsNotNull(configValidPassword);
            Assert.IsTrue(configValidPassword.DecryptedOk);

            var configInvalidPassword = SolutionInstallConfig.LoadFromJson(Properties.Resources.TestInstallerConfig, "weeeeee");
            Assert.IsNotNull(configInvalidPassword);
            Assert.IsFalse(configInvalidPassword.DecryptedOk);

        }

        [TestMethod]
        public void FtpConfigIsValid()
        {
            Assert.IsTrue(new InstallerFtpConfig { UseFtpProxy = false, IntegratedAuth = false, ProxyUsername = string.Empty, ProxyPassword = string.Empty }.IsValid);
            Assert.IsFalse(new InstallerFtpConfig { UseFtpProxy = true, IntegratedAuth = true, ProxyHost = "test", ProxyPort = -1 }.IsValid);
            Assert.IsTrue(new InstallerFtpConfig { UseFtpProxy = true, IntegratedAuth = true, ProxyHost = "test", ProxyPort = 1 }.IsValid);
            Assert.IsFalse(new InstallerFtpConfig { UseFtpProxy = true, IntegratedAuth = true, ProxyPort = 1 }.IsValid);
            Assert.IsFalse(new InstallerFtpConfig { UseFtpProxy = true, ProxyHost = "test", ProxyPort = 10, IntegratedAuth = false, ProxyUsername = string.Empty, ProxyPassword = string.Empty }.IsValid);
            Assert.IsTrue(new InstallerFtpConfig { UseFtpProxy = true, ProxyHost = "test", ProxyPort = 10, IntegratedAuth = true, ProxyUsername = string.Empty, ProxyPassword = string.Empty }.IsValid);
        }


        [TestMethod]
        public void PublishDataXmlTests()
        {
            var data = publishData.FromXml(Properties.Resources.PublishXml);
            Assert.IsNotNull(data);
        }

        [TestMethod]
        public void TaskConfigTests()
        {
            var c = TaskConfig.GetConfigForName("testName");

            // Test we get Json from a config
            var cObj = c.ToArmParamsObject();
            Assert.IsNotNull(cObj);

            var json1 = JsonSerializer.Serialize(cObj);
            Assert.IsNotNull(json1);

            // Test with anon object
            var c2 = TaskConfig.GetConfigForPropAndVal("testProp", "testVal");
            Assert.IsNotNull(c2);

            var tagsDict = new Dictionary<string, string>
            {
                { "testKey", "testVal" }
            };
            var cObj2 = c.ToArmParamsObject(new { tagsArray = new { value = tagsDict } });

            Assert.IsNotNull(cObj2);

            var json2 = JsonSerializer.Serialize(cObj2);
            Assert.IsNotNull(json2);
        }

        [TestMethod]
        public async Task InstallTestsFake()
        {
            var fakeJob = new TestInstallParentJob(_logger);

            // Not run yet
            Assert.ThrowsException<InstallException>(() => fakeJob.TaskResult);

            await fakeJob.Install();

            // Check result tree
            Assert.IsNotNull(fakeJob.TaskResult.FakeCloudResourceType1);
            Assert.IsNotNull(fakeJob.TaskResult.FakeCloudResourceType2);
            Assert.IsNotNull(fakeJob.ResultingContainer);
        }

        /// <summary>
        /// Needs an account with owner rights to the sub.
        /// The sub should have all the solution pre-reqs applied (resource providers, etc)
        /// </summary>
#if DEBUG
        //[TestMethod]
#endif
        public async Task InstallTestsAzureRealBackend()
        {
            var testConfig = GetSolutionInstallConfig(false);

            var azureSub = BaseAnalyticsSolutionInstallJob.FromConfig(testConfig);
            var azJob = new AzurePaaSInstallJob(_logger, testConfig, azureSub);

            // Install new
            await azJob.Install();
        }


        [TestMethod]
        public void Automation_Next_Tuesday()
        {
            var thisDayWasTuesday = new DateTime(2024, 7, 23, 1, 0, 0, DateTimeKind.Utc);
            var aSunday = AutomationAccountTask.Next(thisDayWasTuesday, DayOfWeek.Sunday);
            Assert.IsTrue(aSunday.DayOfWeek == DayOfWeek.Sunday);
            Assert.IsTrue(aSunday == new DateTime(2024, 7, 28, 1, 0, 0, DateTimeKind.Utc));
        }

        [TestMethod]
        public void Automation_Next_Sunday()
        {
            var thisDayWasSunday = new DateTime(2024, 7, 28, 1, 0, 0, DateTimeKind.Utc);
            var aSunday = AutomationAccountTask.Next(thisDayWasSunday, DayOfWeek.Sunday);
            Assert.IsTrue(aSunday.DayOfWeek == DayOfWeek.Sunday);
            Assert.IsTrue(aSunday == new DateTime(2024, 8, 4, 1, 0, 0, DateTimeKind.Utc));
        }

        [TestMethod]
        public void Automation_Next_Sunday_Midnight()
        {
            var thisDayWasSunday = new DateTime(2024, 7, 28, 0, 0, 0, DateTimeKind.Utc);
            var aSunday = AutomationAccountTask.Next(thisDayWasSunday, DayOfWeek.Sunday);
            Assert.IsTrue(aSunday.DayOfWeek == DayOfWeek.Sunday);
            Assert.IsTrue(aSunday == new DateTime(2024, 8, 4, 0, 0, 0, DateTimeKind.Utc));
        }

        [TestMethod]
        public void Automation_NextSundayAt_UTC()
        {
            var now = DateTime.UtcNow;
            var nextSunday = AutomationAccountTask.Next(now, DayOfWeek.Sunday);

            var nextSunday1pm = AutomationAccountTask.NextSundayAt(13);
            Assert.IsTrue(nextSunday1pm.DayOfWeek == DayOfWeek.Sunday);
            Assert.IsTrue(nextSunday1pm.Hour == 13);
            Assert.IsTrue(nextSunday1pm.Minute == 0);
            Assert.IsTrue(nextSunday1pm.Date == nextSunday.Date);
        }

        [TestMethod]
        public void Automation_NextSundayAt_Local()
        {
            var now = DateTime.Now;
            var nextSunday = AutomationAccountTask.Next(now, DayOfWeek.Sunday);

            var nextSunday4pm = AutomationAccountTask.NextSundayAt(16);
            Assert.IsTrue(nextSunday4pm.DayOfWeek == DayOfWeek.Sunday);
            Assert.IsTrue(nextSunday4pm.Hour == 16);
            Assert.IsTrue(nextSunday4pm.Minute == 0);
            Assert.IsTrue(nextSunday4pm.Date == nextSunday.Date);
        }
    }

    public class AzureTestsConfigReader
    {
        public AzureTestsConfigReader() : base()
        {
            this.ClientID = ConfigurationManager.AppSettings.Get("AzureSubClientID");
            this.ClientSecret = ConfigurationManager.AppSettings.Get("AzureSubClientSecret");
            this.TenantGUID = ConfigurationManager.AppSettings.Get("AzureSubTenantGUID");
            this.SubId = ConfigurationManager.AppSettings.Get("AzureSubId");
        }


        public string ClientID { get; set; }
        public string ClientSecret { get; set; }
        public string TenantGUID { get; set; }
        public string SubId { get; set; }
    }

}
