using Azure;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using App.ControlPanel.Engine.Models;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Automation;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Sql;
using Azure.ResourceManager.Storage;
using CloudInstallEngine.Azure;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Things to do once Azure components are created
    /// </summary>
    public class ConfigureAzureComponentsTasks : BaseInstallProcessWithProxy
    {
        private readonly string _installedByUsername;
        private readonly SoftwareReleaseConfig _softwareConfig;
        private readonly string _configPassword;

        public ConfigureAzureComponentsTasks(SolutionInstallConfig config, ILogger logger, InstallerProxyConfig proxyConfig, string installedByUsername,
            SoftwareReleaseConfig softwareConfig, string configPassword) : base(config, logger, proxyConfig)
        {
            _installedByUsername = installedByUsername;
            _softwareConfig = softwareConfig;
            _configPassword = configPassword;
        }

        /// <summary>
        /// Install configure & software on App Service, update target DB. 
        /// </summary>
        public async Task RunPostCreatePaaSTasks(WebSiteResource webApp, DatabasePaaSInfo dbInfo, StorageAccountResource storage, AutomationAccountResource automationAccount,
            AppInsightsInfo appInsights,
            RedisInstallResult redis, CognitiveServicesInfo cognitiveServicesInfo,
            KeyVaultResource keyVault, string serviceBusConnectionString, SubscriptionResource subscription,
            SqlServerResource sqlServer = null)
        {
            // Configure app-service connection-strings, etc
            await ConfigureWebApp(webApp, dbInfo, storage, redis, cognitiveServicesInfo, appInsights, serviceBusConnectionString, keyVault);

            // Download/extract the release while the App Service is still available. Kudu/SCM
            // rejects deployments while the site resource is stopped.
            var solutionSources = await GetSolutionFromSource(subscription, automationAccount, downloadReleaseOnly: true);

            // Prove SQL is reachable BEFORE taking the site offline, self-healing a stale firewall rule if
            // that is what is blocking us. Previously the site was stopped first and the connectivity test ran
            // inside the database step, so a firewall rejection left the customer's web app stopped with no
            // attempt to restart it (issue #326). VerifySQL caches success, so the test inside the database
            // step below becomes a no-op.
            var repairFirewall = BuildFirewallRepairCallback(sqlServer);
            var sqlReachable = await VerifySqlWithFirewallSelfHeal(dbInfo.ConnectionString, repairFirewall);

            // Terminate here rather than carrying a "SQL is broken" flag through the rest of the method. The
            // database step would fail anyway, but only after more work had been done, and the App Service
            // would have been stopped for it - which is the outage this whole change exists to prevent.
            if (!sqlReachable && (Config.TasksConfig.UpgradeSchema || Config.TasksConfig.RegisterConfig))
            {
                throw new UnexpectedInstallException(
                    "SQL Server is not reachable from this host, so the database upgrade cannot run. The App Service has " +
                    "deliberately NOT been stopped, so the existing deployment keeps running on its current schema. " +
                    "Fix the connectivity problem reported above and re-run the installer.");
            }

            // Find downloaded installer app
            var installerExeFile = GetInstallerExe(solutionSources.GetSolutionComponentLocation(SoftwareComponent.ControlPanel));

            // Stop the runtime while applying database changes so the existing website/WebJobs
            // cannot use a partially upgraded schema.
            var stopAttempted = false;
            Exception upgradeFailure = null;
            try
            {
                if (this.Config.TasksConfig.InstallLatestSolutionContent)
                {
                    _logger.LogInformation("Stopping app-service during database upgrade...");

                    // Set BEFORE awaiting: Azure can complete the stop server-side while the client sees a
                    // timeout or transport error, so "the call threw" does not mean "the site is still up".
                    // Assuming it stopped is the safe assumption - a redundant start is harmless.
                    stopAttempted = true;
                    await webApp.StopAsync();
                }

                var sqlInstallerTasks = new SqlInstallerTasks(Config, installerExeFile, dbInfo, _logger, _installedByUsername, _configPassword,
                    async (connectionString) => await VerifySqlWithFirewallSelfHeal(connectionString, repairFirewall));
                await sqlInstallerTasks.UpdateSqlDatabaseSchemaAndDataFromDownloadedInstaller(installerExeFile, _installLogEvents);
            }
            catch (Exception ex)
            {
                upgradeFailure = ex;
            }

            // Whatever happened above, bring the site back. A database step that turns out to be impossible
            // must never leave the customer's web app stopped.
            if (stopAttempted)
            {
                try
                {
                    await webApp.StartAsync();
                    _logger.LogInformation("App Service started for SCM HTTPS deployment");
                }
                catch (Exception startEx)
                {
                    if (upgradeFailure == null)
                    {
                        // Nothing to mask, and the site is down - this must be fatal, not a swallowed warning,
                        // or the install would carry on deploying content to a stopped site and report success.
                        throw new UnexpectedInstallException(
                            "The database step completed but the App Service could not be restarted afterwards: " +
                            $"{startEx.Message}. Start the App Service in the Azure portal, then re-run the installer.");
                    }

                    _logger.LogError($"IMPORTANT: could not restart the App Service after the database step: {startEx.Message}. " +
                        "Start it by hand in the Azure portal.");
                }
            }

            if (upgradeFailure != null)
            {
                // Rethrow preserving the original stack, now that the site is back up.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(upgradeFailure).Throw();
            }

            if (this.Config.TasksConfig.InstallLatestSolutionContent)
            {
                await InstallSolutionContent(solutionSources, subscription, automationAccount);
            }

            if (this.Config.SolutionConfig.ImportTaskSettings.WebTraffic)
            {
                if (this.Config.TasksConfig.InstallLatestSolutionContent)
                {
                    // Install AITracker from downloaded source
                    var aiTrackerDownload = solutionSources.GetSolutionComponentLocation(SoftwareComponent.AITracker);

                    var spTasks = new SharePointWebComponentsInstallJob(Config, _logger, webApp.Data.DefaultHostName);
                    await spTasks.InstallAITracker(this.Config.SharePointConfig, aiTrackerDownload, appInsights.ConnectionString);
                }
                else
                {
                    _logger.LogInformation("Skipping SharePoint web components (AITracker / SPFx) install because 'Update solution with latest release' is not selected.");
                }
            }
        }

        /// <summary>
        /// Builds the delegate that repairs the installer's own SQL firewall rule for a given client IP, or
        /// null when self-healing must not be attempted.
        /// </summary>
        /// <remarks>
        /// Returns null on a private-only deployment: Azure rejects firewall edits there with
        /// <c>DenyPublicEndpointEnabled</c>, and a public firewall rule is the wrong answer anyway - the
        /// existing VNet guidance is what the operator needs. Also null when no ARM server resource was
        /// supplied, so nothing changes for callers that have not been updated.
        /// </remarks>
        private Func<string, Task<bool>> BuildFirewallRepairCallback(SqlServerResource sqlServer)
        {
            if (sqlServer == null) return null;

            if (PrivateNetworkGuidance.IsPrivateNetworkOnly(Config))
            {
                _logger.LogInformation(
                    "Public network access is disabled for this deployment, so the SQL Server firewall rule will not be " +
                    "auto-repaired - Azure rejects firewall edits on a private-only server, and connectivity is expected " +
                    "to come via the private endpoint.");
                return null;
            }

            return async (clientIp) =>
            {
                try
                {
                    var rules = sqlServer.GetSqlFirewallRules();

                    var existing = rules
                        .Where(r => r.Data.Name == AzurePaaSInstallJob.INSTALLER_FIREWALL_RULE_NAME)
                        .Select(r => new SqlFirewallRuleRange(r.Data.Name, r.Data.StartIPAddress, r.Data.EndIPAddress))
                        .SingleOrDefault();

                    if (!SqlFirewallRules.CanSafelyReplaceWithSingleAddress(existing))
                    {
                        // An admin widened our rule into a range. Narrowing it to one address would revoke
                        // access for every other address it covers - worse than the problem being fixed.
                        _logger.LogError(
                            $"SQL Server firewall rule '{AzurePaaSInstallJob.INSTALLER_FIREWALL_RULE_NAME}' has been widened to the " +
                            $"range {existing.StartIp} - {existing.EndIp}. The installer will NOT narrow it to {clientIp}, because " +
                            "that would revoke access for every other address in that range. Extend the range to include " +
                            $"{clientIp} (or add a separate rule for it) and re-run the installer.");
                        return false;
                    }

                    await rules.CreateOrUpdateAsync(
                        WaitUntil.Completed,
                        AzurePaaSInstallJob.INSTALLER_FIREWALL_RULE_NAME,
                        new SqlFirewallRuleData
                        {
                            Name = AzurePaaSInstallJob.INSTALLER_FIREWALL_RULE_NAME,
                            StartIPAddress = clientIp,
                            EndIPAddress = clientIp,
                        });

                    _logger.LogInformation(
                        $"SQL Server firewall rule '{AzurePaaSInstallJob.INSTALLER_FIREWALL_RULE_NAME}' updated to allow {clientIp}.");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        $"Could not update the SQL Server firewall rule to allow {clientIp}: {ex.Message}. " +
                        $"Add a rule named '{AzurePaaSInstallJob.INSTALLER_FIREWALL_RULE_NAME}' for {clientIp} by hand and re-run the installer.");
                    return false;
                }
            };
        }

        FileInfo GetInstallerExe(LocalStorageBlobInfo localStorageBlobInfo)
        {
            // Get control-panel
            FileInfo installerExeFile = null;
            DirectoryInfo zipContentsDirControlPanel = null;
            try
            {
                zipContentsDirControlPanel = ZipFileTasks.Unzip(localStorageBlobInfo, _logger);
            }
            catch (Exception ex)
            {
                // Give context to the error
                throw new ApplicationException($"Could not extract control-panel app: '{ex.Message}'");
            }


            // Try and find new EXE name 1st
            foreach (var item in zipContentsDirControlPanel.GetFiles(InstallerConstants.FILENAME_EXE_INSTALLER))
            {
                if (item.Name.ToLower() == InstallerConstants.FILENAME_EXE_INSTALLER.ToLower()) installerExeFile = item;
            }

            if (installerExeFile == null)
            {
                throw new ApplicationException($"Could not find installer EXE in control-panel app");
            }
            return installerExeFile;
        }

        async Task<LocalStorageInstallSourceInfo> GetSolutionFromSource(
            SubscriptionResource subscription,
            AutomationAccountResource automationAccount,
            bool downloadReleaseOnly)
        {
            AppServiceContentInstallJob appServiceContentInstallJob = null;
            if (this.Config.DownloadLatestStable)
            {
                // Download webjobs from blob storage. Optionally install.
                appServiceContentInstallJob = new DownloadLatestAppServiceContentInstallJob(_logger, subscription, _softwareConfig, _proxyConfig, this.Config, downloadReleaseOnly, automationAccount);
            }
            else
            {
                // Use local sources. Optionally install.
                appServiceContentInstallJob = new UseLocalAppServiceContentInstallJob(_logger, subscription, this.Config.LocalSourceOverride, _proxyConfig, this.Config, downloadReleaseOnly, automationAccount);
            }

            // Install or just download, depending on config above
            await appServiceContentInstallJob.Install();

            return appServiceContentInstallJob.LocalStorageInstallSourceInfo;
        }

        async Task InstallSolutionContent(
            LocalStorageInstallSourceInfo solutionSources,
            SubscriptionResource subscription,
            AutomationAccountResource automationAccount)
        {
            var installJob = new UseLocalAppServiceContentInstallJob(
                _logger,
                subscription,
                solutionSources,
                _proxyConfig,
                this.Config,
                downloadReleaseOnly: false,
                automationAccount: automationAccount);
            await installJob.Install();
        }

        async Task ConfigureWebApp(WebSiteResource webApp, DatabasePaaSInfo backendInfo,
            StorageAccountResource storage,
            RedisInstallResult redis,
            CognitiveServicesInfo cognitiveServicesInfo,
            AppInsightsInfo appInsights, string serviceBusConnectionString, KeyVaultResource keyVault)
        {
            // App settings
            var url = $"https://{webApp.Data.HostNames.First()}/";

            var appSettings = new AppServiceConfigurationDictionary();
            appSettings.Properties.Add("WebAppURL", url);
            appSettings.Properties.Add("ClientID", this.Config.RuntimeAccountOffice365.ClientId);
            appSettings.Properties.Add("ClientSecret", this.Config.RuntimeAccountOffice365.Secret);
            appSettings.Properties.Add("TenantGUID", this.Config.RuntimeAccountOffice365.DirectoryId);
            appSettings.Properties.Add("KeyVaultURL", keyVault.Data.Properties.VaultUri.ToString());
            appSettings.Properties.Add("WEBSITE_LOAD_USER_PROFILE", "1");       // So certificate loading works - https://learn.microsoft.com/en-us/azure/app-service/reference-app-settings?tabs=kudu%2Cdotnet#build-automation

            // App Insights REST calls have sometimes failed. If they did & we have no config, just don't update this bit of the config & they'll have to do it manually
            if (!string.IsNullOrEmpty(appInsights?.ConnectionString))
            {
                appSettings.Properties.Add("AppInsightsConnectionString", appInsights.ConnectionString);
            }

            if (this.Config.CognitiveServicesEnabled)
            {
                appSettings.Properties.Add("CognitiveEndpoint", cognitiveServicesInfo.Endpoint);
                appSettings.Properties.Add("CognitiveKey", cognitiveServicesInfo.Key);
            }
            else
            {
                appSettings.Properties.Add("CognitiveEndpoint", string.Empty);
                appSettings.Properties.Add("CognitiveKey", string.Empty);
            }

            appSettings.Properties.Add("ImportJobSettings", this.Config.SolutionConfig.ImportTaskSettings.ToSettingsString());

            // Office 365 Management Activity API feeds to subscribe to, derived from the selected
            // audit-based imports (Copilot => Audit.General, SharePoint audit => Audit.SharePoint).
            appSettings.Properties.Add("ContentTypesListAsString", this.Config.SolutionConfig.ImportTaskSettings.ToActivityApiContentTypesString());

            // When private VNet is enabled, ensure the app service routes all outbound traffic through
            // the VNet so that private DNS zones resolve Azure PaaS hostnames to private endpoint IPs.
            //
            // Both keys are written in BOTH branches, so they are always installer-managed. That matters
            // because PreserveUnmanagedAppSettingsAsync only fills in keys the installer does not write:
            // if these were written solely in the enabled branch, disabling VNet on a later run would
            // leave the previous values in place instead of clearing them - the merge would preserve
            // exactly the setting the operator had just turned off. Writing empty values keeps
            // "disabled" an explicit, enforced state rather than an absence.
            if (this.Config.NetworkConfig?.Enabled == true)
            {
                appSettings.Properties.Add("WEBSITE_VNET_ROUTE_ALL", "1");
                appSettings.Properties.Add("WEBSITE_DNS_SERVER", "168.63.129.16"); // Azure DNS for private DNS zone resolution
                _logger.LogInformation("Private VNet enabled: app service will route all traffic through VNet for private endpoint DNS resolution.");
            }
            else
            {
                appSettings.Properties.Add("WEBSITE_VNET_ROUTE_ALL", string.Empty);
                appSettings.Properties.Add("WEBSITE_DNS_SERVER", string.Empty);
            }

            // Connection strings
            // Build the Redis connection string from the install-task result, which abstracts over
            // both Azure Managed Redis (port 10000) and pre-existing legacy classic Azure Cache for
            // Redis (port 6380) that the installer chose to reuse.
            //
            // When the cache is RBAC-only (no access keys), we deliberately omit the password
            // segment so that CacheConnectionManager skips its key-based attempt and authenticates
            // via Entra ID using the runtime service principal credentials.
            string redisConnectionString;
            if (redis.UseRbacAuth)
            {
                redisConnectionString = $"{redis.HostName}:{redis.Port},ssl=True,abortConnect=False";
                _logger.LogInformation("Redis connection string built for RBAC/Entra ID auth (no access key).");
            }
            else
            {
                redisConnectionString = $"{redis.HostName}:{redis.Port},password={redis.PrimaryKey},ssl=True,abortConnect=False";
                _logger.LogInformation("Redis connection string built for key-based auth.");
            }

            var storageInfo = new AzStorageConnectionInfo(storage);
            var connectionStrings = new ConnectionStringDictionary();
            connectionStrings.Properties.Add("SPOInsightsEntities", new ConnStringValueTypePair(backendInfo.ConnectionString, ConnectionStringType.SqlAzure));
            connectionStrings.Properties.Add("AzureWebJobsDashboard", new ConnStringValueTypePair(storageInfo.StorageConnectionString, ConnectionStringType.Custom));
            connectionStrings.Properties.Add("AzureWebJobsStorage", new ConnStringValueTypePair(storageInfo.StorageConnectionString, ConnectionStringType.Custom));
            connectionStrings.Properties.Add("Storage", new ConnStringValueTypePair(storageInfo.StorageConnectionString, ConnectionStringType.Custom));
            if (!string.IsNullOrWhiteSpace(serviceBusConnectionString))
            {
                connectionStrings.Properties.Add("ServiceBus", new ConnStringValueTypePair(serviceBusConnectionString, ConnectionStringType.Custom));
            }
            else
            {
                _logger.LogInformation("Service Bus is disabled; skipping 'ServiceBus' connection-string on the App Service.");
            }
            connectionStrings.Properties.Add("Redis", new ConnStringValueTypePair(redisConnectionString, ConnectionStringType.Custom));

            await webApp.UpdateAsync(new SitePatchInfo { SiteConfig = new SiteConfigProperties { Use32BitWorkerProcess = false, IsAlwaysOn = true } });
            await PreserveUnmanagedAppSettingsAsync(webApp, appSettings);
            await webApp.UpdateApplicationSettingsAsync(appSettings);
            await webApp.UpdateConnectionStringsAsync(connectionStrings);

            _logger.LogInformation("App Service connection-strings & app-settings configured");
        }

        /// <summary>
        /// Copies any app setting the installer does not manage from the live App Service into the
        /// settings about to be written.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>UpdateApplicationSettingsAsync</c> REPLACES the whole collection - anything absent from the
        /// dictionary is deleted. The dictionary above is built from scratch on every run, so without this
        /// merge an upgrade silently wipes every setting an operator added by hand.
        /// </para>
        /// <para>
        /// That is not a small set. <c>AppConfig</c> reads around 37 app settings and the installer writes
        /// roughly a dozen; the rest are operator-tunable and would be lost on each upgrade - including
        /// <c>TenantDomain</c>, <c>StatsApiSecret</c>, <c>UseClientCertificate</c>, every import-tuning knob
        /// (<c>ImportAggressiveness</c>, <c>ChunkSize</c>, <c>MaxSqlCommitConcurrency</c>, the
        /// <c>CopilotInteractionHistory*</c> values...), and <c>UserGroupsFilter</c>.
        /// </para>
        /// <para>
        /// <c>UserGroupsFilter</c> is the one with a privacy consequence rather than a performance one: it is
        /// the only way to narrow Copilot interaction-history import to a pilot group, and it is not exposed
        /// in the installer UI, so it can ONLY have been set by hand. Erasing it silently widens that import
        /// to every enabled user - and where Cognitive Services is configured, their prompt text is then sent
        /// to Azure AI Language. A scope that disappears on upgrade is worse than one that was never set.
        /// </para>
        /// <para>
        /// Installer-managed keys deliberately win: this only fills in keys the installer is not writing, so
        /// a value the wizard is responsible for still gets refreshed. Keys that are conditional on a feature
        /// being enabled are written in BOTH branches (empty when off) precisely so they stay installer-owned
        /// and cannot be preserved back after the operator turns the feature off.
        /// </para>
        /// <para>
        /// A read failure is FATAL rather than swallowed. Continuing would run the replacing update with a
        /// dictionary that is missing every unmanaged key, which is exactly the data-loss this method exists
        /// to prevent - and it would do it silently, after logging a warning nobody reads until the tuning
        /// values have already gone. A site that genuinely has no settings yet returns an empty collection,
        /// not an error, so a first-time deployment is unaffected.
        /// </para>
        /// </remarks>
        private async Task PreserveUnmanagedAppSettingsAsync(WebSiteResource webApp, AppServiceConfigurationDictionary appSettings)
        {
            Response<AppServiceConfigurationDictionary> existing;
            try
            {
                existing = await webApp.GetApplicationSettingsAsync();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not read the existing App Service application settings, so this deployment was "
                    + "stopped before it could overwrite them. Updating app settings replaces the whole "
                    + "collection, so continuing would have deleted every setting the installer does not "
                    + "manage - including any UserGroupsFilter scope and all import tuning values. Resolve "
                    + "the access problem and re-run the installer. Reason: " + ex.Message, ex);
            }

            if (existing?.Value?.Properties == null)
            {
                return;
            }

            var preserved = new List<string>();
            foreach (var setting in existing.Value.Properties)
            {
                if (appSettings.Properties.ContainsKey(setting.Key))
                {
                    continue;
                }

                appSettings.Properties.Add(setting.Key, setting.Value);
                preserved.Add(setting.Key);
            }

            if (preserved.Count > 0)
            {
                // Names only - values can be secrets.
                _logger.LogInformation(
                    $"Preserved {preserved.Count} existing app setting(s) the installer does not manage: "
                    + string.Join(", ", preserved) + ".");
            }
        }
    }
}
