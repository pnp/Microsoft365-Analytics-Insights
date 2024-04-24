using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Sql;
using CloudInstallEngine.Azure;
using Common.DataUtils;
using Common.DataUtils.Http;
using FluentFTP.Exceptions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;
using static App.ControlPanel.Engine.Models.AutodetectedSqlAndFtpDetails;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// All the tests we can run to make sure the installer will work
    /// </summary>
    public class SolutionInstallVerifier : BaseInstallProcessWithFtp
    {
        protected TestConfiguration _testConfig;
        public SolutionInstallVerifier(SolutionInstallConfig config, ILogger logger, InstallerFtpConfig ftpConfig, TestConfiguration testConfig)
            : base(config, logger, ftpConfig)
        {
            this._testConfig = testConfig;
        }

        /// <summary>
        /// Test we might be able to install the solution.
        /// Run tests 1-by-1 and throw an exception if something serious is found.
        /// </summary>
        public async Task RunTests()
        {
            // Display warning if on old OS. 
            WindowsVersionCheck();
            _logger.LogInformation($"Starting installation/update tests...");

            // Check sub & az group if possible
            var (testRg, azCredsValid) = await GetResourceGroupIfValid();
            if (!azCredsValid)
                _logger.LogError("No valid Azure subscription information entered; can't test Azure configuration.");
            else
            {
                if (testRg == null)
                {
                    _logger.LogInformation($"No resource-group found with name {Config.ResourceGroupName}. Installer can try and create it but might not have permissions to do so - normally the RG is pre-created.");
                }
                else
                {
                    _logger.LogInformation($"Resource-group found with name {Config.ResourceGroupName}.");
                }
            }

            // Firewall tests
            if (_testConfig.IsValid)
            {
                await ExecuteAndReportFailure("SQL connectivity", () => base.VerifySQL(_testConfig.SQLConnectionString));
            }
            else
            {
                _logger.LogError($"Can't verify SQL Server access - configure a test target in solution tests configuration menu when SQL is created");
            }

            var activityAccountErrs = Config.RuntimeAccountOffice365.GetValidationErrors();
            if (activityAccountErrs.Count > 0)
            {
                _logger.LogError("Can't test runtime account details...");
                foreach (var err in activityAccountErrs)
                {
                    _logger.LogError(err);
                }
            }
            else
            {
                await ExecuteReportFailureAndThrowExceptionIfCritical("Runtime account permission checks", () => VerifyRuntimeAccountAllAPIs());
            }
            await ExecuteReportFailureAndThrowExceptionIfCritical("FTPS connectivity", () => VerifyFTPS(_ftpConfig, _testConfig));

            // Misc checks
            WindowsVersionCheck();

            _logger.LogInformation("Tests completed.");
        }

        /// <summary>
        /// Return FTP deployment details so network tests can be done against the real endpoint, if config is valid.
        /// We cannot read back the password for SQL, so it must come from config
        /// </summary>
        public async Task<AutodetectedSqlAndFtpDetails> GetFtpAndSQLDetails(string sqlPassword)
        {
            if (string.IsNullOrEmpty(sqlPassword))
            {
                throw new ArgumentException($"'{nameof(sqlPassword)}' cannot be null or empty.", nameof(sqlPassword));
            }

            FtpPublishInfo ftpDetails = null;
            var (testRg, azCredsValid) = await GetResourceGroupIfValid();
            if (testRg != null)
            {
                FtpDetails ftpInfo = null;
                var webApp = testRg.GetWebSites().Where(s => s.Data.Name == Config.AppServiceWebAppName).SingleOrDefault();
                if (webApp != null)
                {
                    var ftp = webApp.GetPublishingProfileXmlWithSecrets(new CsmPublishingProfile() { Format = PublishingProfileFormat.Ftp });
                    using (var ms = new StreamReader(ftp.Value))
                    {
                        var profileData = publishData.FromXml(ms);
                        ftpDetails = profileData.GetPublishFtpsUrl();

                        var ftpUrl = new Uri(ftpDetails.RootUrl);
                        ftpInfo = new FtpDetails() { Domain = ftpUrl.Host, Password = ftpDetails.Password, Username = ftpDetails.Username };
                    }
                }
                else
                {
                    _logger.LogError($"Can't find app-service with name '{Config.AppServiceWebAppName}' in resource-group '{testRg.Data.Name}'");
                }

                SqlDetails sqlInfo = null;
                var sqlServer = testRg.GetSqlServers().Where(s => s.Data.Name == Config.SQLServerName).SingleOrDefault();
                if (sqlServer == null)
                {
                    _logger.LogError($"Can't find SQL Server with name '{Config.SQLServerName}' in resource-group '{testRg.Data.Name}'");
                }
                else
                {
                    sqlInfo = new SqlDetails
                    {
                        SqlFqdn = sqlServer.Data.FullyQualifiedDomainName,
                        SqlPassword = sqlPassword,
                        SqlUsername = sqlServer.Data.AdministratorLogin
                    };
                }

                return new AutodetectedSqlAndFtpDetails() { Ftp = ftpInfo, Sql = sqlInfo };
            }
            else
            {
                _logger.LogError($"Can't find resource-group '{testRg.Data.Name}'");
            }
            return null;
        }

        public static bool ConfigReadyForFtpTest(SolutionInstallConfig config)
        {
            if (config == null) return false;

            var installerAccErrors = config.InstallerAccount?.GetValidationErrors();
            return installerAccErrors != null && installerAccErrors.Count == 0 && !string.IsNullOrEmpty(config.AppServiceWebAppName);
        }

        async Task<(ResourceGroupResource, bool)> GetResourceGroupIfValid()
        {
            if (Config.Subscription != null && Config.Subscription.IsValidSubscription)
            {
                var creds = new ClientSecretCredential(Config.InstallerAccount.DirectoryId, Config.InstallerAccount.ClientId, Config.InstallerAccount.Secret);

                var client = new ArmClient(creds);
                var allSubs = client.GetSubscriptions().ToList();
                var subscription = allSubs.Where(sub => sub.Data.SubscriptionId == Config.Subscription.SubId).SingleOrDefault();
                if (subscription == null)
                {
                    _logger.LogError($"Can't find subscription ID '{Config.Subscription.SubId}' (name '{Config.Subscription.DisplayName}') using installer Azure AD account '{Config.InstallerAccount.ClientId}'.");
                }
                else
                {
                    _logger.LogInformation($"Authenticating & selecting subscription '{Config.Subscription.DisplayName}'...");

                    var sub = AzureInstallJob.FromTokenCredential(creds, Config.Subscription.SubId);
                    var rgTestOnlyJob = new ResourceGroupTestOnlyInstallJob(_logger, Config.AzureLocation, sub, Config.ResourceGroupName);

                    // Try and get Az group
                    var success = await ExecuteAndReportFailure("Get/Create Azure Resource Group", async () => await rgTestOnlyJob.Install());
                    if (success)
                        return (rgTestOnlyJob.ResourceGroupFound, true);

                }
            }
            return (null, false);
        }

        /// <summary>
        /// Test FTP by uploading a small file to a site. It's the only way to make sure the full port range is working.
        /// This should probably be done to the customers' own FTP, not the test one we use just for this purpose...
        /// </summary>
        async Task VerifyFTPS(InstallerFtpConfig ftpConfig, TestConfiguration testInfo)
        {
            if (!testInfo.IsValid)
            {
                _logger.LogError($"Can't verify FTPS access - configure a test target in solution tests configuration menu when App Service is created");
                return;
            }

            _logger.LogInformation($"Verifying FTPS access to server {testInfo.FtpHostname} on port 990...");

            var success = false;
            try
            {
                // https://github.com/robinrodricks/FluentFTP/wiki/Quick-Start-Example
                using (var client = FtpClientFactory.GetFtpClient(testInfo.FtpHostname, testInfo.FtpUsername, testInfo.FtpPassword, ftpConfig))
                {
                    await client.Connect();
                    await client.GetListing();

                    var randomFileName = $"{DateTime.Now.Ticks}.txt";
                    var bytes = System.Text.Encoding.ASCII.GetBytes(randomFileName);

                    await client.UploadBytes(bytes, randomFileName);
                    await client.DeleteFile(randomFileName);

                    await client.Disconnect();
                    success = true;
                }
            }
            catch (FtpException ex)
            {
                // Nothing
                _logger.LogError("Got unexpected response from test FTPS server: " + ex.Message);
                Console.WriteLine(ex);
            }
            catch (SocketException ex)
            {
                // Fail
                _logger.LogError($"Got unexpected network response (proxy in use: {ftpConfig.UseFtpProxy}): {ex.Message}");
                Console.WriteLine(ex);
            }

            if (success)
            {
                _logger.LogInformation("Got expected response from test FTPS server");
            }
        }

        void WindowsVersionCheck()
        {
            var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;

            var supportedOses = new List<string>() { "Windows 10", "Windows Server 2016", "Windows Server 2019" };
            var osIsSupported = false;
            foreach (var supportedOS in supportedOses)
            {
                if (os.Contains(supportedOS)) osIsSupported = true;
            }

            if (!osIsSupported)
                _logger.LogError("Unsupported operating-system detected: this may cause unexpected installer errors. "
                    + "Please run this application on Windows 10/Windows Server 2016 or above.");
        }

        async Task VerifyRuntimeAccountAllAPIs()
        {
            // Activity API test 
            if (Config.SolutionConfig.ImportTaskSettings.ActivityLog)
            {
                await VerifyActivityAPIImport(Config.RuntimeAccountOffice365.ClientId, Config.RuntimeAccountOffice365.DirectoryId, Config.RuntimeAccountOffice365.Secret);
            }
            else
                _logger.LogInformation("Skipping Activity API checks as audit-data not being targeted");

            // Teams & Groups enumeration (All Graph tests). Individual tests skipped below
            await VerifyTeamsAndUserActivityImport(Config.RuntimeAccountOffice365.ClientId, Config.RuntimeAccountOffice365.DirectoryId, Config.RuntimeAccountOffice365.Secret);
        }

        async Task VerifyActivityAPIImport(string clientId, string tenantId, string clientSecret)
        {
            try
            {
                var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
                var auth = new ActivityAPIAppIndentityOAuthContext(telemetry, clientId, tenantId, clientSecret, null, false);
                var httpClient = new ConfidentialClientApplicationThrottledHttpClient(auth, false, telemetry);
                // This will start an auth & activity subscription read, which will fail if error with account and/or permissions
                var downloadSession = await ActivitySubscriptionManager.GetActiveSubscriptions(tenantId, _logger, httpClient);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError($"ERROR: Got error trying to read basic activity data from Office 365: '{ex.Message}'");
                _logger.LogError("Important: ensure runtime account is correct and permissions are correctly configured to access Office 365 Management APIs.");
                return;
            }

            _logger.LogInformation("Successfully verified runtime account permissions to Office 365 Management APIs for activity data.");
        }

        async Task VerifyTeamsAndUserActivityImport(string clientId, string tenantId, string clientSecret)
        {
            var telemetry = AnalyticsLogger.ConsoleOnlyTracer();
            var auth = new GraphAppIndentityOAuthContext(telemetry, clientId, tenantId, clientSecret, null, false);
            await auth.InitClientCredential();

            var graphClient = new Microsoft.Graph.GraphServiceClient(auth.Creds);

            var teamsUserUsageLoader = new TeamsUserUsageLoader(new WebJob.Office365ActivityImporter.Engine.Graph.ManualGraphCallClient(auth, telemetry), telemetry);

            // Usage reports
            if (Config.SolutionConfig.ImportTaskSettings.GraphUsageReports)
            {
                await VerifyUserActivityImport(graphClient, teamsUserUsageLoader);
            }
            else _logger.LogInformation("Skipping verifying Graph API for user activity import as not being targeted");

            // Groups
            if (Config.SolutionConfig.ImportTaskSettings.GraphTeams)
            {
                await VerifyTeamsImport(graphClient);
            }
            else _logger.LogInformation("Skipping verifying Graph API for Teams import as not being targeted");
        }

        async Task VerifyTeamsImport(Microsoft.Graph.GraphServiceClient graphClient)
        {
            _logger.LogInformation("Verifying Graph API for Teams...");
            try
            {
                var groups = await graphClient.Groups.Request().Select("displayName,id,resourceProvisioningOptions").GetAsync();
                bool channelsRead = false;
                foreach (var group in groups)
                {
                    if (group.AdditionalData.ContainsKey("resourceProvisioningOptions"))
                    {
                        var resourceProvisioningOptions = group.AdditionalData["resourceProvisioningOptions"].ToString();
                        var options = Newtonsoft.Json.Linq.JArray.Parse(resourceProvisioningOptions);
                        foreach (var option in options)
                        {
                            if (option.ToString().ToLower() == "team")
                            {
                                // Load team
                                var channels = await graphClient.Teams[group.Id].Channels.Request().GetAsync();
                                channelsRead = true;
                                break;
                            }
                        }
                    }
                    if (channelsRead) break;
                }
            }
            catch (Microsoft.Graph.ServiceException ex)
            {
                _logger.LogError($"ERROR: Got error trying to read Graph API for Teams import: '{ex.Message}'");
                _logger.LogError("Important: ensure runtime account is correct and permissions are correctly configured to access Graph API.");
                return;
            }

            _logger.LogInformation("Successfully verified Graph API for Teams.");
        }

        async Task VerifyUserActivityImport(Microsoft.Graph.GraphServiceClient graphClient, TeamsUserUsageLoader teamsUserUsageLoader)
        {
            _logger.LogInformation("Verifying Graph API for user activity import...");

            // WORKAROUND: for some reason, the default GraphClient call gives a Json exception. This workaround does the same call manually
            var userActivityRequest = graphClient.Reports.GetTeamsUserActivityUserDetail("D7").Request();

            var request = new HttpRequestMessage(HttpMethod.Get, userActivityRequest.RequestUrl);
            await graphClient.AuthenticationProvider.AuthenticateRequestAsync(request);

            try
            {
                var response = await graphClient.HttpProvider.SendAsync(request);
                response.EnsureSuccessStatusCode();

                // Get the csv report data
                var data = await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError($"ERROR: Got error trying to read Graph API for Teams user activity report: '{ex.Message}'");
                _logger.LogError("Important: ensure runtime account is correct and permissions are correctly configured to access Graph API.");
                return;
            }
            _logger.LogInformation("Successfully verified Graph API for Teams user activity report.");

            // Check anonymous settings for usage reports - https://learn.microsoft.com/en-us/microsoft-365/troubleshoot/miscellaneous/reports-show-anonymous-user-name
            _logger.LogInformation("Verifying usage report anonymization settings with test Teams user usage data report...");

            // 4 days should give us some data
            const int DAYS_BACK_CHECK = 3;
            await teamsUserUsageLoader.PopulateLoadedReportPagesFromGraph(DAYS_BACK_CHECK);

            bool? validEmailFound = null;
            foreach (var reportPage in teamsUserUsageLoader.LoadedReportPages.Values)
            {
                foreach (var reportPageItem in reportPage)
                {
                    validEmailFound = StringUtils.IsEmail(reportPageItem.UserEmailFieldVal);
                    break;
                }
                if (validEmailFound != null && validEmailFound.Value == true)
                {
                    // Found an email - don't bother looking in any more pages
                    break;
                }
            }
            if (validEmailFound == null)
            {
                _logger.LogInformation($"WARNING: Unable to verify usage report anonymization settings - no Teams user usage data found in {DAYS_BACK_CHECK} days.");
            }
            else
            {
                if (validEmailFound.Value == true)
                    _logger.LogInformation($"Verified usage report anonymization settings - found a user with a valid email address.");
                else
                {
                    _logger.LogError($"Usage report anonymization settings are invalid - users don't appear with real email to correlate to other activity.");
                    _logger.LogError($"Verify this is disabled: https://learn.microsoft.com/en-us/microsoft-365/troubleshoot/miscellaneous/reports-show-anonymous-user-name");
                }
            }

            _logger.LogInformation("Successfully verified user activity settings.");
        }
    }
}
