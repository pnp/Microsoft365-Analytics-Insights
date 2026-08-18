using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using App.ControlPanel.Engine.InstallerTasks.Tasks;
using App.ControlPanel.Engine.Models;
using App.ControlPanel.Engine.SharePointModelBuilder;
using App.ControlPanel.Engine.SharePointModelBuilder.ValueLookups;
using Azure;
using Azure.Core;
using Azure.Identity;
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
using System.IO;
using System.IO.Compression;
using System.Linq;
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
                SolutionConfig = new TargetSolutionConfig()

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
        public void DeploymentProxyConfigIsValid()
        {
            Assert.IsTrue(new InstallerProxyConfig().IsValid);
            Assert.IsFalse(new InstallerProxyConfig { UseProxy = true, IntegratedAuth = true, Host = "proxy.contoso.test", Port = -1 }.IsValid);
            Assert.IsTrue(new InstallerProxyConfig { UseProxy = true, IntegratedAuth = true, Host = "proxy.contoso.test", Port = 8080 }.IsValid);
            Assert.IsFalse(new InstallerProxyConfig { UseProxy = true, IntegratedAuth = true, Port = 8080 }.IsValid);
            Assert.IsFalse(new InstallerProxyConfig { UseProxy = true, Host = "proxy.contoso.test", Port = 8080, Username = "installer" }.IsValid);
            Assert.IsTrue(new InstallerProxyConfig { UseProxy = true, Host = "proxy.contoso.test", Port = 8080, Username = "installer", Password = "synthetic-password" }.IsValid);
        }

        [TestMethod]
        public void DeploymentProxyConfigLoadsLegacyPreferenceNames()
        {
            const string legacyJson = @"{
                ""UseFtpProxy"": true,
                ""ProxyHost"": ""proxy.contoso.test"",
                ""ProxyPort"": 8080,
                ""IntegratedAuth"": false,
                ""ProxyUsername"": ""installer"",
                ""ProxyPassword"": ""synthetic-password""
            }";

            var config = Newtonsoft.Json.JsonConvert.DeserializeObject<InstallerProxyConfig>(legacyJson);

            Assert.IsTrue(config.UseProxy);
            Assert.AreEqual("proxy.contoso.test", config.Host);
            Assert.AreEqual(8080, config.Port);
            Assert.AreEqual("installer", config.Username);
            Assert.IsTrue(config.IsValid);
        }


        [TestMethod]
        public void PublishDataXmlTests()
        {
            var data = publishData.FromXml(Properties.Resources.PublishXml);
            Assert.IsNotNull(data);
        }

        [TestMethod]
        public void KuduPublishingProfileBuildsHttpsPublishUri()
        {
            const string publishXml = @"<publishData>
                <publishProfile publishMethod=""MSDeploy""
                                publishUrl=""contoso.scm.azurewebsites.net:443""
                                userName=""$contoso""
                                userPWD=""not-a-real-password"" />
            </publishData>";

            var publishInfo = publishData.FromXml(publishXml).GetKuduPublishInfo();
            var publishUri = InstallAppServiceContentsTask.BuildKuduPublishUri(publishInfo.RootUrl);

            // async=true is not cosmetic: Azure's front end aborts any single request at ~230 seconds, so a
            // synchronous publish of a large package fails with "500 - The request timed out" even when the
            // deployment is fine. Losing this parameter silently reintroduces that failure.
            Assert.AreEqual("https://contoso.scm.azurewebsites.net/api/publish?type=zip&async=true", publishUri.ToString());
            Assert.AreEqual("$contoso", publishInfo.Username);
        }

        [TestMethod]
        public void DeploymentStatusUriPrefersKuduLocationHeader()
        {
            var fromHeader = InstallAppServiceContentsTask.ResolveDeploymentStatusUri(
                new Uri("https://contoso.scm.azurewebsites.net/api/deployments/abc123"),
                "contoso.scm.azurewebsites.net:443");

            Assert.AreEqual("https://contoso.scm.azurewebsites.net/api/deployments/abc123", fromHeader.ToString());
        }

        [TestMethod]
        public void DeploymentStatusUriFallsBackToLatestWhenNoLocationHeader()
        {
            var noHeader = InstallAppServiceContentsTask.ResolveDeploymentStatusUri(
                null, "contoso.scm.azurewebsites.net:443");
            Assert.AreEqual("https://contoso.scm.azurewebsites.net/api/deployments/latest", noHeader.ToString());

            // A relative Location can't be polled directly, so it must fall back too.
            var relativeHeader = InstallAppServiceContentsTask.ResolveDeploymentStatusUri(
                new Uri("/api/deployments/abc123", UriKind.Relative), "contoso.scm.azurewebsites.net:443");
            Assert.AreEqual("https://contoso.scm.azurewebsites.net/api/deployments/latest", relativeHeader.ToString());
        }

        [TestMethod]
        public void KuduDeploymentStatusDistinguishesSuccessFailureAndInProgress()
        {
            // Kudu: 3 = Failed, 4 = Success. "complete" gates both.
            var success = InstallAppServiceContentsTask.ParseDeploymentStatus(
                @"{""id"":""abc"",""status"":4,""status_text"":"""",""complete"":true,""progress"":""""}");
            Assert.IsTrue(success.IsSuccess, "status 4 + complete must be treated as a successful deployment.");
            Assert.IsFalse(success.IsFailed);

            var failed = InstallAppServiceContentsTask.ParseDeploymentStatus(
                @"{""id"":""abc"",""status"":3,""status_text"":""Deployment failed"",""complete"":true,""log_url"":""https://contoso.scm.azurewebsites.net/api/deployments/abc/log""}");
            Assert.IsTrue(failed.IsFailed, "status 3 + complete must be surfaced as a failure, not a success.");
            Assert.IsFalse(failed.IsSuccess);

            // Still running: must NOT be reported as either outcome, or we'd stop polling early.
            var running = InstallAppServiceContentsTask.ParseDeploymentStatus(
                @"{""id"":""abc"",""status"":2,""status_text"":""Deploying"",""complete"":false,""progress"":""Copying files""}");
            Assert.IsFalse(running.IsSuccess);
            Assert.IsFalse(running.IsFailed);
            Assert.AreEqual("Copying files", running.DescribeProgress());
        }

        [TestMethod]
        public void KuduContinuousWebJobsUriUsesHttpsApiEndpoint()
        {
            var uri = AppServiceWebJobHealthVerifier.BuildKuduContinuousWebJobsUri(
                "contoso.scm.azurewebsites.net:443");

            Assert.AreEqual(
                "https://contoso.scm.azurewebsites.net/api/continuouswebjobs",
                uri.ToString());
        }

        [TestMethod]
        public void WebJobHealthCheckRequiresBothJobsRunning()
        {
            const string statusesJson = @"[
                { ""name"": ""Office365ActivityImporter"", ""status"": ""Running"" },
                { ""name"": ""AppInsightsImporter"", ""status"": ""Stopped"" }
            ]";

            var failures = AppServiceWebJobHealthVerifier.FindFailures(
                AppServiceWebJobHealthVerifier.ParseStatuses(statusesJson));

            CollectionAssert.AreEqual(
                new[] { "AppInsightsImporter=Stopped" },
                failures);
        }

        [TestMethod]
        public void WebJobHealthCheckReportsMissingJob()
        {
            const string statusesJson = @"[
                { ""name"": ""Office365ActivityImporter"", ""status"": ""Running"" }
            ]";

            var failures = AppServiceWebJobHealthVerifier.FindFailures(
                AppServiceWebJobHealthVerifier.ParseStatuses(statusesJson));

            CollectionAssert.AreEqual(
                new[] { "AppInsightsImporter=missing" },
                failures);
        }

        [TestMethod]
        public void WebJobHealthCheckPassesWhenBothJobsAreRunning()
        {
            const string statusesJson = @"[
                { ""name"": ""AppInsightsImporter"", ""status"": ""Running"" },
                { ""name"": ""Office365ActivityImporter"", ""status"": ""Running"" }
            ]";

            var failures = AppServiceWebJobHealthVerifier.FindFailures(
                AppServiceWebJobHealthVerifier.ParseStatuses(statusesJson));

            Assert.AreEqual(0, failures.Count);
        }

        [TestMethod]
        public void AppServiceDeploymentPackageContainsWebsiteAndWebJobs()
        {
            var testRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);

            try
            {
                var websiteZip = CreateTestZip(testRoot, "Website", "default.htm");
                var activityZip = CreateTestZip(testRoot, "Office365ActivityImporter", "job.exe");
                var appInsightsZip = CreateTestZip(testRoot, "AppInsightsImporter", "job.exe");
                var sources = new LocalStorageInstallSourceInfo();
                sources.GetSolutionComponentLocation(SoftwareComponent.WebSite).FileLocation = websiteZip;
                sources.GetSolutionComponentLocation(SoftwareComponent.WebJobActivity).FileLocation = activityZip;
                sources.GetSolutionComponentLocation(SoftwareComponent.WebJobAppInsights).FileLocation = appInsightsZip;

                var package = InstallAppServiceContentsTask.BuildDeploymentPackage(sources, _logger);
                using (var archive = ZipFile.OpenRead(package.FullName))
                {
                    var entries = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToList();
                    CollectionAssert.Contains(entries, "default.htm");
                    CollectionAssert.Contains(entries, "app_data/jobs/continuous/Office365ActivityImporter/job.exe");
                    CollectionAssert.Contains(entries, "app_data/jobs/continuous/AppInsightsImporter/job.exe");
                }
            }
            finally
            {
                Directory.Delete(testRoot, true);
            }
        }

        private static string CreateTestZip(string testRoot, string rootDirectoryName, string fileName)
        {
            var sourceDirectory = Path.Combine(testRoot, rootDirectoryName);
            Directory.CreateDirectory(sourceDirectory);
            System.IO.File.WriteAllText(Path.Combine(sourceDirectory, fileName), "synthetic test content");

            var zipPath = Path.Combine(testRoot, rootDirectoryName + ".zip");
            ZipFile.CreateFromDirectory(sourceDirectory, zipPath, CompressionLevel.Optimal, true);
            return zipPath;
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
        /// A non-critical task that fails must be logged but NOT abort the install - the next task still runs.
        /// This is the KeyVaultSecretAddTask DNS-failure scenario from the field.
        /// </summary>
        [TestMethod]
        public async Task NonCriticalTaskFailureDoesNotAbortInstall()
        {
            var job = new FakeSequentialJob(_logger);
            var failing = new FakeThrowingTask(TaskConfig.NoConfig, _logger, isCritical: false);
            var after = new FakeMarkerTask(TaskConfig.NoConfig, _logger);
            job.AddTask(failing);
            job.AddTask(after);

            // Should complete without throwing despite the failing task.
            await job.Install();

            Assert.IsTrue(failing.WasRun, "The non-critical failing task should have run.");
            Assert.IsTrue(after.WasRun, "A task after a non-critical failure should still run.");
        }

        /// <summary>A critical task that fails must abort the install - the next task does not run.</summary>
        [TestMethod]
        public async Task CriticalTaskFailureAbortsInstall()
        {
            var job = new FakeSequentialJob(_logger);
            var failing = new FakeThrowingTask(TaskConfig.NoConfig, _logger, isCritical: true);
            var after = new FakeMarkerTask(TaskConfig.NoConfig, _logger);
            job.AddTask(failing);
            job.AddTask(after);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => job.Install());

            Assert.IsTrue(failing.WasRun, "The critical failing task should have run.");
            Assert.IsFalse(after.WasRun, "A task after a critical failure should NOT run.");
        }

        [TestMethod]
        public void BuildResourceDnsTargetsIncludesEnabledResources()
        {
            var config = new SolutionInstallConfig
            {
                KeyVaultName = "myvault",
                SQLServerName = "mysqlsvr",
                StorageAccountName = "mystorage",
                AppServiceWebAppName = "myapp",
                RedisName = "mycache",
                ServiceBusName = "mysb",
                ServiceBusEnabled = true,
                CognitiveServiceName = "mycog",
                CognitiveServicesEnabled = true,
            };

            var fqdns = SolutionInstallVerifier.BuildResourceDnsTargets(config).Select(t => t.Fqdn).ToList();

            CollectionAssert.Contains(fqdns, "myvault.vault.azure.net");
            CollectionAssert.Contains(fqdns, "mysqlsvr.database.windows.net");
            CollectionAssert.Contains(fqdns, "mystorage.blob.core.windows.net");
            CollectionAssert.Contains(fqdns, "mystorage.table.core.windows.net");
            CollectionAssert.Contains(fqdns, "myapp.azurewebsites.net");
            CollectionAssert.Contains(fqdns, "mycache.redis.cache.windows.net");
            CollectionAssert.Contains(fqdns, "mysb.servicebus.windows.net");
            CollectionAssert.Contains(fqdns, "mycog.cognitiveservices.azure.com");
        }

        [TestMethod]
        public void BuildResourceDnsTargetsExcludesDisabledAndEmptyResources()
        {
            var config = new SolutionInstallConfig
            {
                KeyVaultName = "myvault",
                SQLServerName = "",                 // empty -> excluded
                ServiceBusName = "mysb",
                ServiceBusEnabled = false,          // disabled -> excluded even though named
                CognitiveServiceName = "mycog",
                CognitiveServicesEnabled = false,   // disabled -> excluded even though named
            };

            var labels = SolutionInstallVerifier.BuildResourceDnsTargets(config).Select(t => t.Label).ToList();

            CollectionAssert.Contains(labels, "Key Vault");
            CollectionAssert.DoesNotContain(labels, "SQL Server");
            CollectionAssert.DoesNotContain(labels, "Service Bus");
            CollectionAssert.DoesNotContain(labels, "Cognitive Services");
        }

        [TestMethod]
        public void BuildResourceDnsTargetsNullConfigReturnsEmpty()
        {
            Assert.AreEqual(0, SolutionInstallVerifier.BuildResourceDnsTargets(null).Count);
        }

        [TestMethod]
        public void BuildResourceDnsTargetsDefaultConfigReturnsEmpty()
        {
            // A brand-new config has no resource names set yet.
            Assert.AreEqual(0, SolutionInstallVerifier.BuildResourceDnsTargets(new SolutionInstallConfig()).Count);
        }

        [TestMethod]
        public void BuildResourceDnsTargetsTrimsAndIgnoresWhitespaceNames()
        {
            var config = new SolutionInstallConfig
            {
                KeyVaultName = "  myvault  ",   // padded -> trimmed
                SQLServerName = "   ",           // whitespace only -> excluded
            };

            var targets = SolutionInstallVerifier.BuildResourceDnsTargets(config);

            Assert.AreEqual(1, targets.Count);
            Assert.AreEqual("myvault.vault.azure.net", targets.Single().Fqdn);
        }

        [TestMethod]
        public void BuildResourceDnsTargetsServiceBusEnabledButEmptyIsExcluded()
        {
            var config = new SolutionInstallConfig
            {
                ServiceBusEnabled = true,
                ServiceBusName = "",   // enabled but no name -> nothing to resolve
            };

            CollectionAssert.DoesNotContain(
                SolutionInstallVerifier.BuildResourceDnsTargets(config).Select(t => t.Label).ToList(),
                "Service Bus");
        }

        [TestMethod]
        public void BuildResourceDnsTargetsSingleResourceHasCorrectLabelAndFqdn()
        {
            var config = new SolutionInstallConfig { RedisName = "mycache" };

            var targets = SolutionInstallVerifier.BuildResourceDnsTargets(config);

            Assert.AreEqual(1, targets.Count);
            Assert.AreEqual("Redis cache", targets.Single().Label);
            Assert.AreEqual("mycache.redis.cache.windows.net", targets.Single().Fqdn);
        }

        [TestMethod]
        public void TransportFailureDetectorDetectsDnsAggregateException()
        {
            // Mirrors the field failure: AggregateException -> RequestFailedException(Status 0) -> WebException.
            var dns = new System.Net.WebException("The remote name could not be resolved: 'x.vault.azure.net'");
            var ex = new AggregateException(new RequestFailedException(0, "Retry failed after 4 tries", dns));

            Assert.IsTrue(TransportFailureDetector.IsTransportOrDnsFailure(ex, out var leaf));
            StringAssert.Contains(leaf, "could not be resolved");
        }

        [TestMethod]
        public void TransportFailureDetectorIgnoresHttpErrorResponses()
        {
            Assert.IsFalse(TransportFailureDetector.IsTransportOrDnsFailure(new RequestFailedException(403, "Forbidden"), out _));
            Assert.IsFalse(TransportFailureDetector.IsTransportOrDnsFailure(new RequestFailedException(404, "Not Found"), out _));
        }

        [TestMethod]
        public void TransportFailureDetectorDetectsSocketAndHttpExceptions()
        {
            Assert.IsTrue(TransportFailureDetector.IsTransportOrDnsFailure(new System.Net.Sockets.SocketException(11001), out _));
            Assert.IsTrue(TransportFailureDetector.IsTransportOrDnsFailure(
                new InvalidOperationException("wrap", new System.Net.Http.HttpRequestException("transport down")), out _));
        }

        [TestMethod]
        public void TransportFailureDetectorNullAndGenericAreNotTransport()
        {
            Assert.IsFalse(TransportFailureDetector.IsTransportOrDnsFailure(null, out _));
            Assert.IsFalse(TransportFailureDetector.IsTransportOrDnsFailure(new InvalidOperationException("boom"), out _));
        }

        [TestMethod]
        public void TransportFailureDetectorInnermostMessageWins()
        {
            var sock = new System.Net.Sockets.SocketException(11001);
            var ex = new AggregateException(new RequestFailedException(0, "outer status-0 wrapper", sock));

            Assert.IsTrue(TransportFailureDetector.IsTransportOrDnsFailure(ex, out var leaf));
            Assert.AreEqual(sock.Message, leaf, "The innermost (most specific) transport message should win.");
        }

        [TestMethod]
        public async Task NonCriticalTaskFailureCarriesPreviousResultForward()
        {
            var job = new FakeSequentialJob(_logger);
            var sentinel = new object();
            var produce = new FakeResultTask(TaskConfig.NoConfig, _logger, sentinel);
            var failing = new FakeThrowingTask(TaskConfig.NoConfig, _logger, isCritical: false);
            var capture = new FakeContextCapturingTask(TaskConfig.NoConfig, _logger);
            job.AddTask(produce);
            job.AddTask(failing);
            job.AddTask(capture);

            await job.Install();

            Assert.IsTrue(capture.WasRun, "Task after a non-critical failure should still run.");
            Assert.AreSame(sentinel, capture.ReceivedContext,
                "After a non-critical failure, the previous task's result must carry forward to the next task.");
        }

        [TestMethod]
        public async Task NonCriticalThenCriticalFailureStillAborts()
        {
            var job = new FakeSequentialJob(_logger);
            var nonCrit = new FakeThrowingTask(TaskConfig.NoConfig, _logger, isCritical: false);
            var crit = new FakeThrowingTask(TaskConfig.NoConfig, _logger, isCritical: true);
            var after = new FakeMarkerTask(TaskConfig.NoConfig, _logger);
            job.AddTask(nonCrit);
            job.AddTask(crit);
            job.AddTask(after);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => job.Install());

            Assert.IsTrue(nonCrit.WasRun);
            Assert.IsTrue(crit.WasRun);
            Assert.IsFalse(after.WasRun, "A task after a critical failure must not run.");
        }

        [TestMethod]
        public void TaskIsCriticalDefaultsToTrue()
        {
            Assert.IsTrue(new FakeMarkerTask(TaskConfig.NoConfig, _logger).IsCritical,
                "Install tasks should be critical by default.");
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


            var config = new SoftwareReleaseConfig();
            Assert.IsTrue(config.RepoOwner == SoftwareReleaseConfig.GITHUB_REPO_OWNER);
            Assert.IsTrue(config.RepoName == SoftwareReleaseConfig.GITHUB_REPO_NAME);
            Assert.IsTrue(config.RepoOwner == "pnp");
            Assert.IsTrue(config.RepoName == "Microsoft365-Analytics-Insights");
        }


        [TestMethod]
        public async Task DownloadLatestReleaseZipsHaveContent()
        {
            var cfg = TaskConfig.GetConfigForPropAndVal(
                    LatestStableSoftwarePackageDownloadTask.CFG_KEY_RepoOwner, SoftwareReleaseConfig.GITHUB_REPO_OWNER)
                .AddSetting(LatestStableSoftwarePackageDownloadTask.CFG_KEY_RepoName, SoftwareReleaseConfig.GITHUB_REPO_NAME);

            var task = new LatestStableSoftwarePackageDownloadTask(cfg, _logger);
            var result = await task.ExecuteTaskReturnResult(null);

            Assert.IsNotNull(result);
            Assert.IsTrue(result.IsValid, "Downloaded release should be valid (5 non-empty .zip files)");

            // Verify each component zip exists and has content
            var components = new[]
            {
                SoftwareComponent.WebJobActivity,
                SoftwareComponent.WebJobAppInsights,
                SoftwareComponent.AITracker,
                SoftwareComponent.ControlPanel,
                SoftwareComponent.WebSite
            };
            foreach (var component in components)
            {
                var fileLocation = result.GetSolutionComponentLocation(component).FileLocation;
                Assert.IsTrue(System.IO.File.Exists(fileLocation), $"{component} zip should exist at {fileLocation}");

                var fileInfo = new System.IO.FileInfo(fileLocation);
                Assert.IsTrue(fileInfo.Length > 0, $"{component} zip should not be empty");
                Assert.IsTrue(fileLocation.EndsWith(".zip"), $"{component} should be a .zip file");
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
