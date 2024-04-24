using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using App.ControlPanel.Engine.InstallerTasks.Adoptify;
using App.ControlPanel.Engine.InstallerTasks.Adoptify.Models;
using App.ControlPanel.Engine.Models;
using App.ControlPanel.Engine.SharePointModelBuilder;
using App.ControlPanel.Engine.SharePointModelBuilder.ValueLookups;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager.Resources.Models;
using CloudInstallEngine;
using CloudInstallEngine.Models;
using Common.Entities.Config;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using Microsoft.SharePoint.Client;
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
                    Adoptify = new AdoptifySolutionInstallConfig
                    {
                        CreateDefaultData = true,
                        ProvisionSchema = true,
                        ExistingSiteUrl = "https://m365x72460609.sharepoint.com/sites/adoptifytests"
                    },
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
            var cObj2 = c.ToArmParamsObject(new { tagsArray = new { value = tagsDict }});

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

#if DEBUG
        //[TestMethod]
#endif
        public async Task InstallSPDefaultData()
        {
            var testConfig = GetSolutionInstallConfig(false);

            var authManager = new OfficeDevPnP.Core.AuthenticationManager();
            using (var ctx = authManager.GetWebLoginClientContext(testConfig.SolutionConfig.Adoptify.ExistingSiteUrl))
            {
                var cfg = TaskConfig.GetConfigForPropAndVal(ListItemsInstallTask.PROP_NAME_LANG, testConfig.SolutionConfig.SolutionLanguageCode);

                var siteInfo = await AdoptifySiteListInfo.GetFromSite(ctx, testConfig.SolutionConfig.Adoptify.ExistingSiteUrl);

                var assetsTask = new AssetsInstallTask(cfg, _logger, ctx);
                var adoptifyInstallJob = new ListItemsInstallTask(cfg, _logger, ctx);

                // Install new content
                await assetsTask.ExecuteTask(siteInfo);
                await adoptifyInstallJob.ExecuteTask(new AdoptifySiteListInfoWithAssetsUrl(siteInfo));
            }
        }

        [TestMethod]
        public void InstallModelsTests()
        {
            var fNoExtension = new SPFileInfo("file");
            Assert.IsTrue(fNoExtension.FileNameNoExtension == "file");
            Assert.IsTrue(fNoExtension.Extension == "");
            Assert.IsTrue(fNoExtension.ToString() == "file");


            var fExtension1 = new SPFileInfo("file.doc");
            Assert.IsTrue(fExtension1.FileNameNoExtension == "file");
            Assert.IsTrue(fExtension1.Extension == "doc");
            Assert.IsTrue(fExtension1.ToString() == "file.doc");

            var fExtension2 = new SPFileInfo("whatever/file.doc");
            Assert.IsTrue(fExtension2.FileNameNoExtension == "whatever/file");
            Assert.IsTrue(fExtension2.Extension == "doc");
            Assert.IsTrue(fExtension2.ToString() == "whatever/file.doc");


            var valid = new SoftwareReleaseConfig() { SoftwareDownloadURL = "https://spoinsights.blob.core.windows.net/v2downloads?sv=blah" };
            Assert.IsTrue(valid.ContainerName == "v2downloads");
            Assert.IsTrue(valid.AccountBaseUrl == "https://spoinsights.blob.core.windows.net");
            Assert.IsTrue(valid.SAS == "?sv=blah");


            var invalid1 = new SoftwareReleaseConfig() { SoftwareDownloadURL = "https://spoinsights.blob.core.windows.net/v2downloads" };
            Assert.IsTrue(invalid1.ContainerName == string.Empty);
            Assert.IsTrue(invalid1.SAS == string.Empty);
            Assert.IsFalse(invalid1.IsValid);
            var invalid2 = new SoftwareReleaseConfig() { SoftwareDownloadURL = "https://spoinsights" };
            Assert.IsTrue(invalid2.ContainerName == string.Empty);
            Assert.IsFalse(invalid2.IsValid);
        }

        /// <summary>
        /// Tests we can chain one lookup to result of another
        /// </summary>
#if DEBUG
        //[TestMethod]
#endif
        public async Task SharePointChainedLookupTests()
        {
            var testConfig = GetSolutionInstallConfig(false);

            var authManager = new OfficeDevPnP.Core.AuthenticationManager();
            using (var ctx = authManager.GetWebLoginClientContext(testConfig.SolutionConfig.Adoptify.ExistingSiteUrl))
            {
                var badges = ctx.Web.GetListByTitle("Badges");
                var allBadges = badges.GetItems(CamlQuery.CreateAllItemsQuery());
                ctx.Load(allBadges);
                await ctx.ExecuteQueryRetryAsync();

                // Content lookup classes.
                var starterBadgeLookupMetadataSubLookup = new
                {
                    lookupType = "IdLookup",
                    lookupParams = new
                    {
                        listTitle = "Badges",
                        fieldName = "BadgeName",
                        fieldValue = allBadges[0].FieldValues["BadgeName"]
                    }
                };

                var testFieldVal = "FirstLaunchBadgeID " + DateTime.Now.Ticks;
                var insertIfNotExistMetadata = new
                {
                    lookupType = InsertValueIfNotExists.PROP_LOOKUP_TYPE_ID_LOOKUP,
                    lookupParams = new
                    {
                        listTitle = "Settings",
                        fieldName = "Title",
                        fieldValue = testFieldVal,
                        insertValue = starterBadgeLookupMetadataSubLookup    // above lookup
                    }
                };

                // We should insert the result from the IdValueFromAnotherListValueLookup if the InsertValueIfNotExists lookup doesn't find something (which it wont)
                var lookup = AbstractSPListItemValueLookup.GetSPListLookup(ctx, JsonSerializer.Serialize(insertIfNotExistMetadata));
                var starterBadgeVal = await lookup.GetLookupValue();
                Assert.IsTrue(int.Parse(starterBadgeVal) > 0);
            }
        }

#if DEBUG
        //[TestMethod]
#endif
        public async Task SharePointThumbnailLookupTests()
        {
            var testConfig = GetSolutionInstallConfig(false);

            var authManager = new OfficeDevPnP.Core.AuthenticationManager();
            using (var ctx = authManager.GetWebLoginClientContext(testConfig.SolutionConfig.Adoptify.ExistingSiteUrl))
            {
                // Content lookup classes.
                var thumbnailLookupMetadata = new
                {
                    lookupType = "ThumbnailImageProvisionAndLookup",
                    lookupParams = new
                    {
                        thumbnailFieldHostListTitle = "Levels",
                        thumbnailFieldName = "LevelImage",
                        siteAssetRelativeFileName = "LevelImages/goldtrophy.png"
                    }
                };

                var lookup = AbstractSPListItemValueLookup.GetSPListLookup(ctx, JsonSerializer.Serialize(thumbnailLookupMetadata));
                var goldtrophyImageValJson = await lookup.GetLookupValue();
                var goldtrophyImageVal = JsonSerializer.Deserialize<ThumbnailFieldMetadata>(goldtrophyImageValJson);
                Assert.IsNotNull(goldtrophyImageVal);
            }
        }

        [TestMethod]
        public async Task SharePointJsonInsertLookupTests()
        {
            // Content lookup classes.

            var jsonPayLoad = new { whatever = "123" };
            var jsonObj = new
            {
                lookupType = "JsonObjectToStringLookup",
                lookupParams = new
                {
                    jsonPayLoad = jsonPayLoad,
                }
            };

            var lookup = AbstractValueLookup.GetListLookup(JsonSerializer.Serialize(jsonObj));
            var lookupJson = await lookup.GetLookupValue();
            Assert.IsNotNull(lookupJson);
            Assert.IsTrue(AbstractValueLookup.IsListLookupDefintion(JsonSerializer.Serialize(jsonObj)));
        }

#if DEBUG
        //[TestMethod]
#endif
        public async Task SharePointIdValueFromAnotherListValueLookupTests()
        {
            var testConfig = GetSolutionInstallConfig(false);

            var authManager = new OfficeDevPnP.Core.AuthenticationManager();
            using (var ctx = authManager.GetWebLoginClientContext(testConfig.SolutionConfig.Adoptify.ExistingSiteUrl))
            {
                // Content lookup classes.
                var validLookupMetadata = new
                {
                    lookupType = "IdLookup",
                    lookupParams = new
                    {
                        listTitle = "Badges",
                        fieldName = "BadgeName",
                        fieldValue = "Socializer"
                    }
                };

                // Test valid
                var lookup = AbstractSPListItemValueLookup.GetSPListLookup(ctx, JsonSerializer.Serialize(validLookupMetadata));
                Assert.IsNotNull(lookup);
                Assert.IsTrue(lookup.IsValid);
                Assert.IsInstanceOfType(lookup, typeof(IdValueFromAnotherListValueLookup));

                var id = await lookup.GetLookupValueInt();
                Assert.IsNotNull(id);
                Assert.IsInstanceOfType(id, typeof(int));

                // Non-existant value, but optional
                var invalidOptionalLookupMetadata = new
                {
                    lookupType = "IdLookup",
                    lookupParams = new
                    {
                        listTitle = "Badges",
                        fieldName = "BadgeName",
                        fieldValue = "Socializer2"
                    }
                };
                lookup = AbstractSPListItemValueLookup.GetSPListLookup(ctx, JsonSerializer.Serialize(invalidOptionalLookupMetadata));
                id = await lookup.GetLookupValueInt();
                Assert.IsTrue(id == 0);


                // Make sure it breaks if a lookup value is required & not found
                var invalidRequiredLookupMetadata = new
                {
                    lookupType = "IdLookup",
                    required = true,
                    lookupParams = new
                    {
                        listTitle = "Badges",
                        fieldName = "BadgeName",
                        fieldValue = "Socializer2"
                    }
                };
                lookup = AbstractSPListItemValueLookup.GetSPListLookup(ctx, JsonSerializer.Serialize(invalidRequiredLookupMetadata));
                await Assert.ThrowsExceptionAsync<LookupNoResultsException>(async () => await lookup.GetLookupValueInt());
            }
        }

        /// <summary>
        /// Check InsertValueIfNotExists works
        /// </summary>
#if DEBUG
        //[TestMethod]
#endif
        public async Task SharePointInsertValueIfNotExistsTests()
        {
            var testConfig = GetSolutionInstallConfig(false);

            var authManager = new OfficeDevPnP.Core.AuthenticationManager();
            using (var ctx = authManager.GetWebLoginClientContext(testConfig.SolutionConfig.Adoptify.ExistingSiteUrl))
            {
                // Generate lookup that won't exist yet
                var testDefaulVal = "TestVal" + DateTime.Now.Ticks;
                var testFieldVal = "AppIdTestProp" + DateTime.Now.Ticks;
                var insertIfNotExistMetadata = new
                {
                    lookupType = InsertValueIfNotExists.PROP_LOOKUP_TYPE_ID_LOOKUP,
                    lookupParams = new
                    {
                        listTitle = "Settings",
                        fieldName = "Title",
                        fieldValue = testFieldVal,
                        insertValue = testDefaulVal       // Value we expect back assuming Title != testFieldVal
                    }
                };

                // Test valid
                var lookup = AbstractSPListItemValueLookup.GetSPListLookup(ctx, JsonSerializer.Serialize(insertIfNotExistMetadata));
                Assert.IsNotNull(lookup);
                Assert.IsTrue(lookup.IsValid);
                Assert.IsInstanceOfType(lookup, typeof(InsertValueIfNotExists));

                var lookupResultVal = await lookup.GetLookupValue();

                // Value wouldn't exist before; check we get our insertValue value
                Assert.AreEqual(lookupResultVal, testDefaulVal);


                var list = ctx.Web.GetListByTitle(((InsertValueIfNotExists)lookup).Params.ListTitle);
                ctx.Load(list.Fields);
                await ctx.ExecuteQueryAsync();

                var item = list.AddItem(new ListItemCreationInformation());
                item["Title"] = testFieldVal;
                item.Update();
                await ctx.ExecuteQueryAsync();

                // Check again. Should now match testFieldVal as we inserted it
                lookupResultVal = await lookup.GetLookupValue();
                Assert.AreEqual(lookupResultVal, testFieldVal);
            }
        }

#if DEBUG
        //[TestMethod]
#endif
        public async Task AdoptifyInstallJob()
        {
            var testConfig = GetSolutionInstallConfig(false);

            var authManager = new OfficeDevPnP.Core.AuthenticationManager();
            using (var ctx = authManager.GetWebLoginClientContext(testConfig.SolutionConfig.Adoptify.ExistingSiteUrl))
            {
                var azureSub = BaseAnalyticsSolutionInstallJob.FromConfig(testConfig);
                var adoptifyInstallJob = new AdoptifyInstallJob(_logger, testConfig, azureSub, ctx);

                // Install new
                await adoptifyInstallJob.Install();
            }

        }

#if DEBUG
        //[TestMethod]
#endif
        public async Task AdoptifySiteProvisionTask()
        {
            var testConfig = GetSolutionInstallConfig(false);

            var authManager = new OfficeDevPnP.Core.AuthenticationManager();
            using (var ctx = authManager.GetWebLoginClientContext(testConfig.SolutionConfig.Adoptify.ExistingSiteUrl))
            {
                var job = new AdoptifySiteProvisionTask(TaskConfig.NoConfig, _logger, ctx);

                await job.ExecuteTask();
            }
        }


#if DEBUG
        //[TestMethod]
#endif
        public async Task AdoptifySiteFieldUpdatesTask()
        {
            var testConfig = GetSolutionInstallConfig(false);

            var authManager = new OfficeDevPnP.Core.AuthenticationManager();
            using (var ctx = authManager.GetWebLoginClientContext(testConfig.SolutionConfig.Adoptify.ExistingSiteUrl))
            {
                var t = new AdoptifySiteFieldUpdatesTask(TaskConfig.NoConfig, _logger, ctx);

                var siteInfo = AdoptifySiteListInfo.GetFromSite(ctx, testConfig.SolutionConfig.Adoptify.ExistingSiteUrl);

                // Install new
                await t.ExecuteTask(siteInfo);
            }

        }

#if DEBUG
        //[TestMethod]
#endif
        public async Task AdoptifyArmResourcesInstallJob()
        {
            var testConfig = GetSolutionInstallConfig(false);

            var authManager = new OfficeDevPnP.Core.AuthenticationManager();
            using (var ctx = authManager.GetWebLoginClientContext(testConfig.SolutionConfig.Adoptify.ExistingSiteUrl))
            {
                var siteInfo = await AdoptifySiteListInfo.GetFromSite(ctx, testConfig.SolutionConfig.Adoptify.ExistingSiteUrl);

                var authSettings = new AzureTestsConfigReader();
                var auth = new ClientSecretCredential(authSettings.TenantGUID, authSettings.ClientID, authSettings.ClientSecret);

                var azureSub = BaseAnalyticsSolutionInstallJob.FromConfig(testConfig);
                var job = new AdoptifyArmResourcesInstallJob(_logger, testConfig, siteInfo.ToConfig(), azureSub);

                await job.Install();
            }
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
