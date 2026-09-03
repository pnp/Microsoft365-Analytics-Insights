using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Sql;
using CloudInstallEngine.Azure;
using DataUtils;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.User;
using static App.ControlPanel.Engine.Models.AutodetectedSqlDetails;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// All the tests we can run to make sure the installer will work
    /// </summary>
    public class SolutionInstallVerifier : BaseInstallProcess
    {
        protected TestConfiguration _testConfig;
        public SolutionInstallVerifier(SolutionInstallConfig config, ILogger logger, TestConfiguration testConfig)
            : base(config, logger)
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

            // Validate the configuration itself first (names, accounts, region, etc.) so fundamental
            // mistakes are reported before we make any Azure calls.
            ReportConfigValidationIssues();

            // When public access is disabled the data-plane checks below (DNS, Key Vault) only succeed
            // from a host with private-network line-of-sight to the resources - tell the operator up-front.
            if (PrivateNetworkGuidance.IsPrivateNetworkOnly(Config))
            {
                _logger.LogInformation(
                    $"Public network access is disabled for this deployment. Run 'Test Configuration' (and the install itself) " +
                    $"from a host with private-network line-of-sight to the resources - a VM on VNet '{Config.NetworkConfig?.VNetName}' " +
                    $"(or a peered VNet, VPN/ExpressRoute, or Azure Bastion-attached host) - otherwise public DNS may not resolve to " +
                    $"the private endpoint IPs and the DNS / Key Vault data-plane checks below will fail.");
            }

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

            // Verify the installer account can create RBAC role assignments
            // (Microsoft.Authorization/roleAssignments/write). Lacking this permission is the cause of
            // "...does not have authorization to perform action 'Microsoft.Authorization/roleAssignments/write'
            // over scope '/subscriptions/.../resourceGroups/...'" failures that abort an install part-way
            // through (e.g. when the installer account only has Contributor). Logs only; never throws.
            await VerifyInstallerCanAssignRoles(testRg, azCredsValid);

            // DNS resolution checks for the configured Azure resource hostnames. Catches the
            // "The remote name could not be resolved" class of failure (broken/limited DNS on the
            // installer host) up-front instead of letting it abort an install part-way through.
            await VerifyResourceDnsResolution();

            // Key Vault data-plane reachability with the installer account (the exact call that failed
            // mid-install). Only runs when the vault already exists and installer credentials are present.
            await VerifyKeyVaultDataPlaneAccess(testRg);

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
            // Misc checks
            WindowsVersionCheck();

            _logger.LogInformation("Tests completed.");
        }

        /// <summary>
        /// Return SQL details so connectivity tests can run against an existing server.
        /// We cannot read back the SQL password, so it must come from config.
        /// </summary>
        public async Task<AutodetectedSqlDetails> GetSqlDetails(string sqlPassword)
        {
            if (string.IsNullOrEmpty(sqlPassword))
            {
                throw new ArgumentException($"'{nameof(sqlPassword)}' cannot be null or empty.", nameof(sqlPassword));
            }

            var (testRg, _) = await GetResourceGroupIfValid();
            if (testRg != null)
            {
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

                return new AutodetectedSqlDetails { Sql = sqlInfo };
            }
            else
            {
                _logger.LogError($"Can't find resource-group '{Config.ResourceGroupName}'");
            }
            return null;
        }

        /// <summary>
        /// Do we have enough config to autodetect SQL connectivity details?
        /// </summary>
        public static bool ConfigIsReadyForSqlAutodetection(SolutionInstallConfig config)
        {
            if (config == null) return false;

            var installerAccErrors = config.InstallerAccount?.GetValidationErrors();
            return installerAccErrors != null && installerAccErrors.Count == 0
                && !string.IsNullOrEmpty(config.ResourceGroupName)
                && config.Subscription.IsValidSubscription
                && !string.IsNullOrEmpty(config.SQLServerAdminPassword) && !string.IsNullOrEmpty(config.SQLServerAdminUsername) && !string.IsNullOrEmpty(config.SQLServerName);
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

        #region Configuration & Key Vault checks

        /// <summary>
        /// Run the same configuration validation the installer uses and log any issues up-front, so
        /// fundamental mistakes (missing names, accounts, region, etc.) are reported before any Azure calls.
        /// Logs only; never throws.
        /// </summary>
        void ReportConfigValidationIssues()
        {
            try
            {
                var errors = Config?.ValidatInputAndGetErrors();
                if (errors != null && errors.Count > 0)
                {
                    _logger.LogError($"Configuration validation found {errors.Count} issue(s) that may block installation:");
                    foreach (var e in errors)
                    {
                        _logger.LogError($"  - {e}");
                    }
                }
                else
                {
                    _logger.LogInformation("Configuration validation passed - no issues found.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not run configuration validation: {ex.Message}");
            }
        }

        /// <summary>
        /// Probe the Key Vault data-plane endpoint with the installer account - the exact call
        /// (<c>KeyVaultSecretAddTask</c>) that aborts an install when DNS/network to
        /// <c>&lt;vault&gt;.vault.azure.net</c> is broken. Only runs when the vault already exists and the
        /// installer credentials are present, so it never false-alarms on a fresh install. Logs only; never throws.
        /// </summary>
        async Task VerifyKeyVaultDataPlaneAccess(ResourceGroupResource testRg)
        {
            if (Config == null || string.IsNullOrWhiteSpace(Config.KeyVaultName)) return;

            var installerErrs = Config.InstallerAccount?.GetValidationErrors();
            if (installerErrs == null || installerErrs.Count > 0)
            {
                _logger.LogInformation("Skipping Key Vault data-plane reachability check - installer account details are incomplete.");
                return;
            }

            if (testRg == null)
            {
                _logger.LogInformation("Skipping Key Vault data-plane reachability check - resource group is not available.");
                return;
            }

            KeyVaultResource vault;
            try
            {
                vault = testRg.GetKeyVaults().Where(v => v.Data.Name == Config.KeyVaultName).SingleOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not enumerate key vaults to check '{Config.KeyVaultName}': {ex.Message}");
                return;
            }

            if (vault == null)
            {
                _logger.LogInformation($"Key Vault '{Config.KeyVaultName}' not found yet; skipping data-plane reachability check (it will be created during install).");
                return;
            }

            _logger.LogInformation($"Checking Key Vault data-plane access to '{Config.KeyVaultName}' with the installer account...");

            var creds = new ClientSecretCredential(Config.InstallerAccount.DirectoryId, Config.InstallerAccount.ClientId, Config.InstallerAccount.Secret);
            var result = await KeyVaultDataPlaneProbe.TryReadAsync(Config.KeyVaultName, creds);

            switch (result.Status)
            {
                case KeyVaultProbeStatus.Reachable:
                    _logger.LogInformation($"Key Vault '{Config.KeyVaultName}' data-plane is reachable and the installer account can read secrets.");
                    break;

                case KeyVaultProbeStatus.TransportFailure:
                    var transportGuidance = PrivateNetworkGuidance.IsPrivateNetworkOnly(Config)
                        ? PrivateNetworkGuidance.BuildVmOnVNetGuidance("reaching the Key Vault", Config.NetworkConfig?.VNetName)
                        : $"Check DNS / network / firewall connectivity from this host to '{Config.KeyVaultName}.vault.azure.net'.";
                    _logger.LogError(
                        $"Could not reach Key Vault '{Config.KeyVaultName}' data-plane ({result.Message}). " +
                        $"This is the DNS/network failure that aborts secret upload during install. " + transportGuidance);
                    break;

                case KeyVaultProbeStatus.Unauthorized:
                    _logger.LogError(
                        $"Key Vault '{Config.KeyVaultName}' is reachable but the installer account was denied data-plane access (403): {result.Message} " +
                        $"Ensure the installer app registration has a Key Vault access policy granting secret Get/List/Set, " +
                        $"and that any vault firewall allows this host's IP.");
                    break;

                default:
                    _logger.LogError($"Unexpected error checking Key Vault '{Config.KeyVaultName}' data-plane access: {result.Message}");
                    break;
            }
        }

        #endregion

        #region Azure RBAC permission checks

        /// <summary>
        /// Check the installer service principal is actually allowed to create Azure RBAC role assignments
        /// (<c>Microsoft.Authorization/roleAssignments/write</c>) at the deployment scope - the permission whose
        /// absence aborts an install with "...does not have authorization to perform action
        /// 'Microsoft.Authorization/roleAssignments/write' over scope '/subscriptions/.../resourceGroups/...'".
        /// Checks at the resource-group scope when it exists (the exact scope the install writes to), otherwise at
        /// the subscription scope. Permissions are inherited, so a subscription-level grant is detected at the RG
        /// scope too. Logs only; never throws.
        /// </summary>
        async Task VerifyInstallerCanAssignRoles(ResourceGroupResource testRg, bool azCredsValid)
        {
            if (!azCredsValid)
            {
                // No usable subscription / credentials - already reported above; nothing to check against.
                return;
            }

            var installerErrs = Config.InstallerAccount?.GetValidationErrors();
            if (installerErrs == null || installerErrs.Count > 0)
            {
                _logger.LogInformation("Skipping installer role-assignment permission check - installer account details are incomplete.");
                return;
            }

            if (Config.Subscription == null || !Config.Subscription.IsValidSubscription)
            {
                _logger.LogInformation("Skipping installer role-assignment permission check - no valid subscription is configured.");
                return;
            }

            string scopeId;
            string scopeDescription;
            if (testRg != null)
            {
                scopeId = testRg.Id.ToString();
                scopeDescription = $"resource group '{Config.ResourceGroupName}'";
            }
            else
            {
                scopeId = $"/subscriptions/{Config.Subscription.SubId}";
                scopeDescription = $"subscription '{Config.Subscription.DisplayName}'";
            }

            _logger.LogInformation($"Checking the installer account can create Azure role assignments on {scopeDescription}...");

            var creds = new ClientSecretCredential(Config.InstallerAccount.DirectoryId, Config.InstallerAccount.ClientId, Config.InstallerAccount.Secret);
            var result = await RbacPermissionProbe.CanAssignRolesAsync(scopeId, creds);

            switch (result.Status)
            {
                case RbacAssignmentProbeStatus.CanAssignRoles:
                    _logger.LogInformation($"Installer account can create role assignments on {scopeDescription} - the RBAC assignment step should succeed.");
                    break;

                case RbacAssignmentProbeStatus.CannotAssignRoles:
                    _logger.LogError(
                        $"The installer account ('{Config.InstallerAccount.ClientId}') does NOT have permission to create Azure role assignments " +
                        $"(action '{RbacPermissionProbe.RoleAssignmentWriteAction}') on {scopeDescription}. " +
                        $"The install will fail part-way through with an error like \"...does not have authorization to perform action " +
                        $"'Microsoft.Authorization/roleAssignments/write' over scope '/subscriptions/.../resourceGroups/...'\". " +
                        $"Grant the installer app registration the 'Owner' or 'User Access Administrator' (or 'Role Based Access Control Administrator') role " +
                        $"on {scopeDescription} (or the subscription) and re-run. Note: 'Contributor' is NOT sufficient - it explicitly excludes role-assignment writes.");
                    break;

                case RbacAssignmentProbeStatus.TransportFailure:
                    var guidance = PrivateNetworkGuidance.IsPrivateNetworkOnly(Config)
                        ? PrivateNetworkGuidance.BuildVmOnVNetGuidance("checking installer role-assignment permissions", Config.NetworkConfig?.VNetName)
                        : "Check DNS / network / firewall connectivity from this host to 'management.azure.com'.";
                    _logger.LogError($"Could not check installer role-assignment permissions - Azure Resource Manager was unreachable ({result.Message}). " + guidance);
                    break;

                default:
                    _logger.LogWarning(
                        $"Could not determine whether the installer account can create role assignments on {scopeDescription}: {result.Message} " +
                        $"Ensure the installer account has 'Owner' or 'User Access Administrator' before installing.");
                    break;
            }
        }

        #endregion

        #region DNS resolution checks
        /// <summary>Public DNS suffix for each Azure PaaS resource the installer / runtime must resolve.</summary>
        private const string DNS_SUFFIX_KEY_VAULT = ".vault.azure.net";
        private const string DNS_SUFFIX_SQL = ".database.windows.net";
        private const string DNS_SUFFIX_STORAGE_BLOB = ".blob.core.windows.net";
        private const string DNS_SUFFIX_STORAGE_TABLE = ".table.core.windows.net";
        private const string DNS_SUFFIX_APP_SERVICE = ".azurewebsites.net";
        private const string DNS_SUFFIX_REDIS = ".redis.cache.windows.net";
        private const string DNS_SUFFIX_SERVICE_BUS = ".servicebus.windows.net";
        private const string DNS_SUFFIX_COGNITIVE = ".cognitiveservices.azure.com";

        /// <summary>
        /// Control host the installer always needs to resolve (ARM management plane). If even this fails,
        /// the installer host has no working DNS for Azure at all (broken DNS / no internet / proxy issue).
        /// </summary>
        private const string DNS_CONTROL_HOST = "management.azure.com";

        /// <summary>Max time to wait for a single DNS lookup before treating it as a failure.</summary>
        private static readonly TimeSpan _dnsLookupTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Resolve the public hostnames of the configured Azure resources so DNS / network problems on the
        /// installer host - the cause of "The remote name could not be resolved: '&lt;x&gt;.vault.azure.net'"
        /// install failures - are caught up-front rather than mid-install. Logs only; never throws.
        /// </summary>
        async Task VerifyResourceDnsResolution()
        {
            _logger.LogInformation("Checking DNS resolution for configured Azure resource hostnames...");

            // First confirm the host can resolve Azure DNS at all. management.azure.com always exists, so a
            // failure here means broken DNS / no connectivity / proxy issue - every data-plane call will fail.
            var (controlOk, controlError) = await TryResolveHost(DNS_CONTROL_HOST);
            if (!controlOk)
            {
                _logger.LogError(
                    $"The installer host cannot resolve Azure DNS names (failed to resolve '{DNS_CONTROL_HOST}': {controlError}). " +
                    $"Installation will fail until DNS / network / proxy connectivity is fixed on this machine.");
            }

            var targets = BuildResourceDnsTargets(Config);
            if (targets.Count == 0)
            {
                _logger.LogInformation("No named Azure resources configured to DNS-check.");
                return;
            }

            var privateOnly = PrivateNetworkGuidance.IsPrivateNetworkOnly(Config);

            foreach (var target in targets)
            {
                var (ok, error) = await TryResolveHost(target.Fqdn);
                if (ok)
                {
                    _logger.LogInformation($"DNS OK: {target.Label} '{target.Fqdn}' resolved.");
                    continue;
                }

                if (!controlOk)
                {
                    // Root cause already reported above; don't repeat the per-resource guidance.
                    _logger.LogError($"DNS check: could not resolve {target.Label} hostname '{target.Fqdn}' ({error}).");
                }
                else if (privateOnly)
                {
                    // Public access disabled: the host must be on the VNet to resolve the private endpoint IP.
                    _logger.LogError(
                        $"DNS check: could not resolve {target.Label} hostname '{target.Fqdn}' ({error}). " +
                        PrivateNetworkGuidance.BuildVmOnVNetGuidance($"reaching the {target.Label}", Config.NetworkConfig?.VNetName));
                }
                else
                {
                    // Public access path: if the resource already exists this is a real DNS / network problem
                    // that will break the install; if it has not been created yet, it is expected.
                    _logger.LogError(
                        $"DNS check: could not resolve {target.Label} hostname '{target.Fqdn}' ({error}). " +
                        $"If this resource has not been created yet this is expected and can be ignored; " +
                        $"if it already exists, the installer host cannot reach it and data-plane steps " +
                        $"(e.g. Key Vault secret upload) will fail.");
                }
            }

            _logger.LogInformation("DNS resolution checks complete.");
        }

        /// <summary>
        /// Build the list of (resource, public-FQDN) DNS targets for every resource that is both enabled
        /// and has a name configured. Pure string logic so it is unit-testable without any network access.
        /// </summary>
        public static List<ResourceDnsTarget> BuildResourceDnsTargets(SolutionInstallConfig config)
        {
            var targets = new List<ResourceDnsTarget>();
            if (config == null) return targets;

            void Add(string label, string name, string suffix)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    targets.Add(new ResourceDnsTarget(label, name.Trim() + suffix));
                }
            }

            Add("Key Vault", config.KeyVaultName, DNS_SUFFIX_KEY_VAULT);
            Add("SQL Server", config.SQLServerName, DNS_SUFFIX_SQL);
            Add("Storage account (blob)", config.StorageAccountName, DNS_SUFFIX_STORAGE_BLOB);
            // The audit-import blob checkpoint uses the storage account's Table endpoint, which has its own
            // private endpoint / DNS zone on private deployments - check it resolves too (see #207 / AzurePaaSInstallJob).
            Add("Storage account (table)", config.StorageAccountName, DNS_SUFFIX_STORAGE_TABLE);
            Add("App Service", config.AppServiceWebAppName, DNS_SUFFIX_APP_SERVICE);
            Add("Redis cache", config.RedisName, DNS_SUFFIX_REDIS);
            if (config.ServiceBusEnabled)
            {
                Add("Service Bus", config.ServiceBusName, DNS_SUFFIX_SERVICE_BUS);
            }
            if (config.CognitiveServicesEnabled)
            {
                Add("Cognitive Services", config.CognitiveServiceName, DNS_SUFFIX_COGNITIVE);
            }

            return targets;
        }

        /// <summary>
        /// Resolve a host name with a timeout. Returns (true, null) on success and (false, message) on
        /// failure or timeout. Never throws.
        /// </summary>
        static async Task<(bool ok, string error)> TryResolveHost(string host)
        {
            try
            {
                var lookup = Dns.GetHostAddressesAsync(host);
                var completed = await Task.WhenAny(lookup, Task.Delay(_dnsLookupTimeout));
                if (completed != lookup)
                {
                    return (false, $"DNS lookup timed out after {_dnsLookupTimeout.TotalSeconds:0}s");
                }

                var addresses = await lookup;   // also re-throws any lookup exception
                if (addresses != null && addresses.Length > 0)
                {
                    return (true, null);
                }
                return (false, "no IP addresses returned");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        #endregion

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
            if (Config.SolutionConfig.ImportTaskSettings.UsesActivityApi)
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
                var logger = AnalyticsLogger.ConsoleOnlyTracer();
                var auth = new ActivityAPIAppIndentityOAuthContext(logger, clientId, tenantId, clientSecret, null, false);
                var httpClient = new ConfidentialClientApplicationThrottledHttpClient(auth, false, logger);
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
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var auth = new GraphAppIndentityOAuthContext(logger, clientId, tenantId, clientSecret, null, false);
            await auth.InitClientCredential();

            var graphClient = new Microsoft.Graph.GraphServiceClient(auth.Creds);

            var teamsUserUsageLoader = new TeamsUserUsageLoader(new WebJob.Office365ActivityImporter.Engine.Graph.ManualGraphCallClient(auth, logger),
                new NoUsersHaveGroupsUserGroupsCache(_logger),
                new Common.Entities.Config.UserGroupsFilterModel(string.Empty),
                logger);

            // Usage reports. Both toggles read Microsoft 365 usage reports via Reports.Read.All, so verify the
            // permission when either is enabled - otherwise a tenant that only enabled the Copilot usage
            // reports would first discover a missing grant at runtime, hours after the install finished.
            if (Config.SolutionConfig.ImportTaskSettings.GraphUsageReports
                || Config.SolutionConfig.ImportTaskSettings.GraphCopilotUsageReports)
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
                var groups = await graphClient.Groups.GetAsync(rc =>
                {
                    rc.QueryParameters.Select = new[] { "displayName", "id", "resourceProvisioningOptions" };
                });
                bool channelsRead = false;
                foreach (var group in groups?.Value ?? new List<Microsoft.Graph.Models.Group>())
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
                                var channels = await graphClient.Teams[group.Id].Channels.GetAsync();
                                channelsRead = true;
                                break;
                            }
                        }
                    }
                    if (channelsRead) break;
                }
            }
            catch (Microsoft.Graph.Models.ODataErrors.ODataError ex)
            {
                _logger.LogError($"ERROR: Got error trying to read Graph API for Teams import: '{ex.Message}'");
                _logger.LogError("Important: ensure runtime account is correct and permissions are correctly configured to access Graph API.");
                return;
            }

            _logger.LogInformation("Successfully verified Graph API for Teams.");
        }

        // Graph usage-report aggregation period for the Teams user-activity verification call.
        // MUST be the bare OData value ("D7"): the typed Kiota builder
        // Reports.GetTeamsUserActivityUserDetailWithPeriod(period) wraps the value in single quotes
        // itself when composing getTeamsUserActivityUserDetail(period='...'). Passing a pre-quoted
        // "'D7'" produces period=''D7'', which Graph's OData parser rejects ("Syntax error at
        // position 13 in 'period=''D7'''"). See issue #133.
        internal const string TeamsUserActivityReportPeriod = "D7";
        internal const string GraphReportsUnknownTenantErrorCode = "UnknownTenantId";

        /// <summary>
        /// Reads the Teams user-activity usage report via the typed Graph SDK - the exact call the
        /// installer "Test Configuration" verification exercises. Extracted so the OData period
        /// quoting is covered by an integration test (issue #133).
        /// </summary>
        internal static async Task<string> ReadTeamsUserActivityReportAsync(Microsoft.Graph.GraphServiceClient graphClient)
        {
            using (var stream = await graphClient.Reports.GetTeamsUserActivityUserDetailWithPeriod(TeamsUserActivityReportPeriod).GetAsync())
            {
                if (stream == null)
                {
                    return null;
                }
                using (var reader = new StreamReader(stream))
                {
                    return await reader.ReadToEndAsync();
                }
            }
        }

        /// <summary>
        /// Graph Reports wraps service-specific errors inside the outer Graph error message. For
        /// example, the outer code can be "UnknownError" while its JSON message contains the real
        /// "UnknownTenantId" code. Recognize both shapes without treating unrelated text as a match.
        /// </summary>
        internal static bool IsGraphReportsUnknownTenant(Exception ex)
        {
            if (ex == null)
            {
                return false;
            }

            if (ex is Microsoft.Graph.Models.ODataErrors.ODataError oDataError)
            {
                if (string.Equals(oDataError.Error?.Code, GraphReportsUnknownTenantErrorCode, StringComparison.OrdinalIgnoreCase)
                    || JsonErrorPayloadContainsCode(oDataError.Error?.Message, GraphReportsUnknownTenantErrorCode))
                {
                    return true;
                }
            }

            return JsonErrorPayloadContainsCode(ex.Message, GraphReportsUnknownTenantErrorCode);
        }

        private static bool JsonErrorPayloadContainsCode(string payload, string expectedCode, int remainingNestedMessages = 2)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            try
            {
                var error = JObject.Parse(payload)["error"];
                if (error == null)
                {
                    return false;
                }

                if (string.Equals(error["code"]?.Value<string>(), expectedCode, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var nestedMessage = error["message"]?.Value<string>();
                return remainingNestedMessages > 0
                    && JsonErrorPayloadContainsCode(nestedMessage, expectedCode, remainingNestedMessages - 1);
            }
            catch (JsonReaderException)
            {
                return false;
            }
        }

        private void LogGraphReportsUnknownTenant()
        {
            _logger.LogWarning(
                $"Microsoft Graph Reports does not recognize this tenant ('{GraphReportsUnknownTenantErrorCode}'), so usage-report access could not be verified. " +
                "For a newly created tenant, the Microsoft 365 reporting service can take time to onboard it and start generating reports. " +
                "Retry after usage reports are available in the Microsoft 365 admin center. If the tenant is not new or this persists, contact Microsoft support. " +
                "This response is not a Reports.Read.All permission denial.");
        }

        async Task VerifyUserActivityImport(Microsoft.Graph.GraphServiceClient graphClient, TeamsUserUsageLoader teamsUserUsageLoader)
        {
            _logger.LogInformation("Verifying Graph API for user activity import...");

            // v5+: typed report endpoint returns the CSV report as a Stream directly, removing
            // the need for the v4 manual HttpRequestMessage / HttpProvider workaround.
            try
            {
                await ReadTeamsUserActivityReportAsync(graphClient);
            }
            catch (Exception ex) when (IsGraphReportsUnknownTenant(ex))
            {
                LogGraphReportsUnknownTenant();
                return;
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

            // The daily loaders use strict paging (see AbstractDailyActivityLoader), so a Graph failure
            // here throws rather than yielding an empty report. That is deliberate for the importer - a
            // failed download must never be recorded as a successful empty day - but this is the INSTALL
            // VERIFIER, whose whole job is to report problems rather than propagate them. Catch it and
            // report, matching the Teams-report check above.
            try
            {
                await teamsUserUsageLoader.PopulateLoadedReportPagesFromGraph(DAYS_BACK_CHECK);
            }
            catch (Exception ex) when (IsGraphReportsUnknownTenant(ex))
            {
                LogGraphReportsUnknownTenant();
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError($"ERROR: Got error trying to read the Teams user usage report from Graph: '{ex.Message}'");
                _logger.LogError("Important: ensure the runtime account has the Reports.Read.All application permission granted and admin-consented, and that this is a global-cloud tenant.");
                return;
            }

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

    /// <summary>
    /// A configured Azure resource and the public hostname that must resolve for the installer / runtime
    /// to reach it. Built by <see cref="SolutionInstallVerifier.BuildResourceDnsTargets"/>.
    /// </summary>
    public class ResourceDnsTarget
    {
        public ResourceDnsTarget(string label, string fqdn)
        {
            Label = label;
            Fqdn = fqdn;
        }

        /// <summary>Human-readable resource description, e.g. "Key Vault".</summary>
        public string Label { get; }

        /// <summary>Public fully-qualified hostname, e.g. "myvault.vault.azure.net".</summary>
        public string Fqdn { get; }
    }
}
