using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.Redis;
using Azure.ResourceManager.RedisEnterprise;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Sql;
using CloudInstallEngine.Azure;
using Common.Entities;
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
using WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory;
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
            await VerifyResourceDnsResolution(testRg);

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

        /// <summary>
        /// Public DNS suffix for Azure Managed Redis (Redis Enterprise), which the installer provisions
        /// by default. The full name is region-qualified: <c>&lt;name&gt;.&lt;region&gt;.redis.azure.net</c>.
        /// </summary>
        private const string DNS_SUFFIX_REDIS_MANAGED = ".redis.azure.net";

        /// <summary>
        /// Public DNS suffix for legacy classic Azure Cache for Redis. Only relevant when the installer
        /// detects and reuses a pre-existing classic cache (see <c>RedisInstallTask</c>).
        /// </summary>
        private const string DNS_SUFFIX_REDIS_CLASSIC = ".redis.cache.windows.net";
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
        async Task VerifyResourceDnsResolution(ResourceGroupResource testRg)
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

            var targets = BuildResourceDnsTargets(Config, TryGetRedisHostNameFromArm(testRg));
            if (targets.Count == 0)
            {
                _logger.LogInformation("No named Azure resources configured to DNS-check.");
                return;
            }

            var privateOnly = PrivateNetworkGuidance.IsPrivateNetworkOnly(Config);

            foreach (var target in targets)
            {
                // A target can carry more than one candidate hostname (Redis, where the same configured
                // name is either Azure Managed Redis or a reused legacy classic cache). Resolving any one
                // of them proves the resource is reachable, so only report a failure when all of them fail.
                var (ok, resolvedHost, failureDetail) = await TryResolveAnyHost(target.Fqdns);
                if (ok)
                {
                    _logger.LogInformation($"DNS OK: {target.Label} '{resolvedHost}' resolved.");
                    continue;
                }

                // "hostname 'x' (reason)" for a single candidate; "hostname - tried 'x' (reason), 'y' (reason)"
                // when there are several, so the admin can see every name that was attempted and why each failed.
                var failureDescription = target.Fqdns.Count == 1
                    ? $"hostname {failureDetail}"
                    : $"hostname - tried {failureDetail}";

                var hint = string.IsNullOrEmpty(target.FailureHint) ? string.Empty : " " + target.FailureHint;

                if (!controlOk)
                {
                    // Root cause already reported above; don't repeat the per-resource guidance.
                    _logger.LogError($"DNS check: could not resolve {target.Label} {failureDescription}.{hint}");
                }
                else if (privateOnly)
                {
                    // Public access disabled: the host must be on the VNet to resolve the private endpoint IP.
                    var message = $"DNS check: could not resolve {target.Label} {failureDescription}.{hint} " +
                        PrivateNetworkGuidance.BuildVmOnVNetGuidance($"reaching the {target.Label}", Config.NetworkConfig?.VNetName);

                    if (target.PrivateNetworkResolutionFailureIsWarning)
                    {
                        _logger.LogWarning(message);
                    }
                    else
                    {
                        _logger.LogError(message);
                    }
                }
                else
                {
                    // Public access path: if the resource already exists this is a real DNS / network problem
                    // that will break the install; if it has not been created yet, it is expected.
                    _logger.LogError(
                        $"DNS check: could not resolve {target.Label} {failureDescription}.{hint} " +
                        $"If this resource has not been created yet this is expected and can be ignored; " +
                        $"if it already exists, the installer host cannot reach it and data-plane steps " +
                        $"(e.g. Key Vault secret upload) will fail.");
                }
            }

            _logger.LogInformation("DNS resolution checks complete.");
        }

        /// <summary>
        /// Build the list of (resource, public-FQDN candidates) DNS targets for every resource that is both
        /// enabled and has a name configured. Pure string logic so it is unit-testable without any network
        /// access.
        /// </summary>
        public static List<ResourceDnsTarget> BuildResourceDnsTargets(SolutionInstallConfig config, string redisHostNameFromArm = null)
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

            var redisTarget = BuildRedisDnsTarget(config.RedisName, config.AzureLocationName, redisHostNameFromArm);
            if (redisTarget != null)
            {
                targets.Add(redisTarget);
            }

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
        /// Build the Redis DNS target. When ARM has reported the deployed cache hostname, use that exact
        /// hostname: it is the same value the installer writes into the runtime Redis connection string and
        /// avoids reconstructing a name from suffix guesses. Before the cache exists, fall back to the two
        /// possible hostnames for a current Managed Redis deployment and a reused legacy classic cache.
        /// Returns null when no cache name is configured.
        /// </summary>
        public static ResourceDnsTarget BuildRedisDnsTarget(string redisName, string azureLocationName, string hostNameFromArm = null)
        {
            if (string.IsNullOrWhiteSpace(redisName)) return null;

            var name = redisName.Trim();

            if (!string.IsNullOrWhiteSpace(hostNameFromArm))
            {
                return new ResourceDnsTarget(
                    "Redis cache",
                    hostNameFromArm.Trim(),
                    "The Redis hostname was read from the existing Azure resource.",
                    privateNetworkResolutionFailureIsWarning: true);
            }

            var candidates = new List<string>();

            // Managed Redis first: it is what a current install deploys, so it is the name that should be
            // reported when neither resolves. Needs the region, which older configs may not have.
            var region = NormaliseAzureRegionForDns(azureLocationName);
            if (region != null)
            {
                candidates.Add($"{name}.{region}{DNS_SUFFIX_REDIS_MANAGED}");
            }

            candidates.Add(name + DNS_SUFFIX_REDIS_CLASSIC);

            // The managed name is region-qualified and the region is taken from this config, not from ARM. An
            // existing cache that lives in a different region than the config now says would fail both
            // candidates, so name that possibility rather than leaving the admin to guess.
            var hint = region != null
                ? $"The Azure Managed Redis hostname is built from the configured region '{region}'; " +
                  $"if the cache exists in a different region, correct the region on the Azure tab."
                : "No Azure region is configured, so only the legacy classic cache hostname could be checked. " +
                  "Select the Azure region to also check the Azure Managed Redis hostname.";

            return new ResourceDnsTarget("Redis cache", candidates, hint, privateNetworkResolutionFailureIsWarning: true);
        }

        /// <summary>
        /// If the configured Redis resource already exists, read its actual public hostname from ARM. This
        /// mirrors <c>RedisInstallTask</c>: a pre-existing classic cache wins over Managed Redis because the
        /// installer reuses it instead of provisioning a replacement.
        /// </summary>
        string TryGetRedisHostNameFromArm(ResourceGroupResource testRg)
        {
            if (testRg == null || string.IsNullOrWhiteSpace(Config?.RedisName))
            {
                return null;
            }

            var redisName = Config.RedisName.Trim();

            try
            {
                var classic = testRg.GetAllRedis().Where(c => c.Data.Name == redisName).SingleOrDefault();
                if (!string.IsNullOrWhiteSpace(classic?.Data?.HostName))
                {
                    return classic.Data.HostName;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not read classic Redis cache hostname from ARM for '{redisName}': {ex.Message}");
            }

            try
            {
                var managed = testRg.GetRedisEnterpriseClusters().Where(c => c.Data.Name == redisName).SingleOrDefault();
                if (!string.IsNullOrWhiteSpace(managed?.Data?.HostName))
                {
                    return managed.Data.HostName;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not read Azure Managed Redis hostname from ARM for '{redisName}': {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Turn a configured Azure location into the region segment of an Azure Managed Redis FQDN, or null
        /// when it cannot be one. <c>AzureLocationName</c> normally holds the short name already
        /// (<c>westeurope</c>), but it is a free-text string on the config: it can hold the display name
        /// ("West Europe" - same value once spaces are stripped), the installer's "no region selected"
        /// placeholder, or nothing at all on an older config.
        /// </summary>
        static string NormaliseAzureRegionForDns(string azureLocationName)
        {
            if (string.IsNullOrWhiteSpace(azureLocationName)) return null;

            var normalised = azureLocationName.Trim().Replace(" ", string.Empty).ToLowerInvariant();

            // Every Azure region short name is lowercase alphanumeric (westeurope, uksouth, eastus2). Anything
            // else - notably the "---" placeholder - is not a region and must not be baked into a hostname.
            return System.Text.RegularExpressions.Regex.IsMatch(normalised, "^[a-z0-9]+$") ? normalised : null;
        }

        /// <summary>
        /// Resolve the first host name that resolves out of <paramref name="hosts"/>. Returns
        /// (true, the host that resolved, null) on success, and (false, null, detail) when none resolve -
        /// where detail lists every candidate tried and why each failed, e.g.
        /// <c>'a.redis.azure.net' (No such host is known), 'a.redis.cache.windows.net' (No such host is known)</c>.
        /// Reporting only the last candidate's error would hide, say, a timeout on the name that actually
        /// matters behind an NXDOMAIN on the legacy fallback. Never throws.
        /// </summary>
        static async Task<(bool ok, string resolvedHost, string failureDetail)> TryResolveAnyHost(IReadOnlyList<string> hosts)
        {
            var failures = new List<string>();
            foreach (var host in hosts)
            {
                var (ok, error) = await TryResolveHost(host);
                if (ok) return (true, host, null);
                failures.Add($"'{host}' ({error})");
            }
            return (false, null, string.Join(", ", failures));
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
            // Activity API test. Gated by ImportTaskSettings.UsesActivityApi (ActivityLog || Copilot ||
            // ImportPowerPlatform) - the same condition the web-job runs its activity import on - so a
            // Copilot-only or Power-Platform-only tenant is checked here instead of being told "audit-data
            // not being targeted" (issue #329).
            if (Config.SolutionConfig.ImportTaskSettings.UsesActivityApi)
            {
                await VerifyActivityAPIImport(Config.RuntimeAccountOffice365.ClientId, Config.RuntimeAccountOffice365.DirectoryId, Config.RuntimeAccountOffice365.Secret);
            }
            else
                _logger.LogInformation("Skipping Activity API checks as audit-data not being targeted");

            // Teams & Groups enumeration (All Graph tests). Individual tests skipped below
            await VerifyTeamsAndUserActivityImport(Config.RuntimeAccountOffice365.ClientId, Config.RuntimeAccountOffice365.DirectoryId, Config.RuntimeAccountOffice365.Secret);

            // Finally, say out loud which enabled imports Test Configuration could NOT check, so an admin is
            // never left assuming a green run proves every toggle is ready.
            ReportUnverifiedEnabledImports();
        }

        #region Import-toggle verification coverage

        /// <summary>
        /// Declares, for every <c>ImportTaskSettings</c> <c>[ImportProp]</c> toggle, whether Test Configuration
        /// can verify it and - when it cannot - why not.
        /// </summary>
        /// <remarks>
        /// This exists because toggles have three times now shipped with no Test Configuration check at all,
        /// silently: the omission is invisible in the test output, so a green run reads as "everything is
        /// ready" when it is not. A unit test asserts this table covers exactly the toggles that exist, so
        /// adding a toggle without deciding how it is verified fails the build rather than shipping unnoticed.
        /// It is not just documentation - <see cref="ReportUnverifiedEnabledImports"/> logs the unverifiable
        /// ones so the admin sees the gap too.
        /// </remarks>
        public static readonly IReadOnlyList<ImportToggleCoverage> ImportToggleCoverages = new List<ImportToggleCoverage>
        {
            new ImportToggleCoverage(nameof(ImportTaskSettings.ActivityLog),
                "Office 365 Management Activity API subscription read"),

            new ImportToggleCoverage(nameof(ImportTaskSettings.Copilot),
                "Office 365 Management Activity API subscription read (Copilot arrives on the Audit.General feed)"),

            new ImportToggleCoverage(nameof(ImportTaskSettings.ImportPowerPlatform),
                "Office 365 Management Activity API subscription read (Power Platform arrives on the Audit.General feed)"),

            new ImportToggleCoverage(nameof(ImportTaskSettings.GraphUsageReports),
                "Microsoft 365 usage reports via Reports.Read.All"),

            new ImportToggleCoverage(nameof(ImportTaskSettings.GraphCopilotUsageReports),
                "Microsoft 365 usage reports via Reports.Read.All"),

            new ImportToggleCoverage(nameof(ImportTaskSettings.GraphTeams),
                "Graph group enumeration and Teams channel read"),

            new ImportToggleCoverage(nameof(ImportTaskSettings.CopilotInteractionHistory),
                "AiEnterpriseInteraction.Read.All on the runtime app token"),

            new ImportToggleCoverage(nameof(ImportTaskSettings.WebTraffic), null,
                "there is no runtime API permission to check - page traffic is pushed to the web app by the "
                + "in-page AI Tracker script, so correctness depends on the SharePoint deployment rather than "
                + "on a Graph or Management API grant."),

            new ImportToggleCoverage(nameof(ImportTaskSettings.GraphUsersMetadata), null,
                "no check exercises the Graph user-metadata read (User.Read.All); a missing grant would first "
                + "appear at runtime."),

            new ImportToggleCoverage(nameof(ImportTaskSettings.Calls), null,
                "the Teams call-records import needs a Graph change-notification subscription pointed at the "
                + "web app plus a Service Bus queue, neither of which exists until the install has finished."),

            new ImportToggleCoverage(nameof(ImportTaskSettings.SentEmails), null,
                "no check exercises the Graph mailbox read (Mail.Read); a missing grant would first appear at "
                + "runtime."),
        };

        /// <summary>
        /// The ENABLED import toggles that Test Configuration cannot verify. Pure, so it is unit-testable.
        /// </summary>
        internal static IReadOnlyList<ImportToggleCoverage> GetUnverifiedEnabledImports(ImportTaskSettings settings)
        {
            if (settings == null) return new List<ImportToggleCoverage>();

            return ImportToggleCoverages
                .Where(c => !c.IsVerified && settings.IsImportEnabled(c.PropertyName))
                .ToList();
        }

        /// <summary>
        /// Logs every ENABLED import toggle that Test Configuration cannot verify, so a clean run is never
        /// mistaken for "every workload is proven ready". Disabled toggles are not mentioned - they are already
        /// covered by the per-check "not being targeted" lines.
        /// </summary>
        void ReportUnverifiedEnabledImports()
        {
            foreach (var toggle in GetUnverifiedEnabledImports(Config.SolutionConfig.ImportTaskSettings))
            {
                _logger.LogWarning(
                    $"No Test Configuration check exists for the '{toggle.PropertyName}' import: {toggle.NotVerifiedReason}");
            }
        }

        #endregion

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
            var manualGraphClient = new WebJob.Office365ActivityImporter.Engine.Graph.ManualGraphCallClient(auth, logger);

            var teamsUserUsageLoader = new TeamsUserUsageLoader(manualGraphClient,
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

            // Copilot AI interaction history
            if (Config.SolutionConfig.ImportTaskSettings.CopilotInteractionHistory)
            {
                await VerifyCopilotInteractionHistoryImport(auth, manualGraphClient);
            }
            else _logger.LogInformation("Skipping verifying Copilot AI interaction history import as not being targeted");
        }

        /// <summary>
        /// Verifies the runtime app registration actually holds <c>AiEnterpriseInteraction.Read.All</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the only import whose permission the installer does not grant - it needs separate, explicit
        /// admin consent on the app registration - which makes it simultaneously the most likely toggle to be
        /// misconfigured and, until now, the only one Test Configuration said nothing about.
        /// </para>
        /// <para>
        /// The failure is silent by design at runtime: <c>CopilotInteractionHistoryImporter.ImportAsync</c>
        /// logs a warning and returns, so a missing grant surfaces only as an empty table and a line buried in
        /// web-job logs, hours after the install finished.
        /// </para>
        /// <para>
        /// Deliberately reuses <c>GraphAiInteractionSourceLoader</c>'s permission check - the exact logic the
        /// importer itself gates on - so a passing test guarantees the importer will actually run, which is a
        /// stronger guarantee than most of the other checks give. It reads the <c>roles</c> claim off the app
        /// token: one token acquisition, no Graph call, no tenant data read. The tri-state
        /// <c>GetInteractionReadAccessAsync</c> is used rather than the importer's boolean form, so "could not
        /// tell" is never reported to the admin as "definitely not granted".
        /// </para>
        /// </remarks>
        async Task VerifyCopilotInteractionHistoryImport(
            GraphAppIndentityOAuthContext auth,
            WebJob.Office365ActivityImporter.Engine.Graph.ManualGraphCallClient graphClient)
        {
            _logger.LogInformation("Verifying Copilot AI interaction history import permissions...");

            if (auth == null)
            {
                // HasInteractionReadAccessAsync fails OPEN on a null identity, which is right for the importer
                // (let the per-user calls report the truth) but wrong for a verifier, whose whole job is to
                // prove the permission is there. Never report "couldn't determine" as a pass.
                _logger.LogWarning(
                    "Could not verify the Copilot AI interaction history permission: no runtime app identity was available.");
                return;
            }

            InteractionReadAccess access;
            try
            {
                var loader = new GraphAiInteractionSourceLoader(graphClient, auth, _logger);
                access = await loader.GetInteractionReadAccessAsync();
            }
            catch (Exception ex)
            {
                // GetInteractionReadAccessAsync catches its own failures, so this is belt-and-braces against a
                // future change; a verifier must never let an unexpected throw read as a pass.
                _logger.LogWarning(
                    $"Could not verify whether the runtime account holds 'AiEnterpriseInteraction.Read.All': {ex.Message}. " +
                    "Confirm the grant by hand on the app registration before relying on the Copilot AI interaction history import.");
                return;
            }

            var (level, message) = DescribeInteractionReadAccess(access);
            _logger.Log(level, message);

            if (access != InteractionReadAccess.Granted) return;

            // Scope warning, only worth making once the permission is actually in place. UserGroupsFilter is
            // an App Service application setting, not part of the installer config, so it cannot be checked
            // here - but an unscoped run is a per-user Graph call for every enabled user in the tenant, and
            // where Cognitive Services is configured it also sends their prompt text to Azure AI Language.
            // Worth saying plainly at install time rather than after the first cycle.
            _logger.LogWarning(
                "Copilot AI interaction history is tenant-wide unless scoped. This import makes one Graph call PER USER per cycle, " +
                "so on a large tenant it should be narrowed to a pilot group with the 'UserGroupsFilter' App Service application " +
                "setting (it is not exposed in this wizard and must be set by hand on the web app).");
        }

        /// <summary>
        /// Maps a permission-check outcome to the log level and message Test Configuration reports.
        /// </summary>
        /// <remarks>
        /// Pure and separate from the check itself so the mapping is unit-testable. The levels are the point
        /// of the whole exercise: only a token that was read successfully and lacks the role is an ERROR.
        /// "Could not tell" must be a warning, or an admin is sent off to re-consent a permission they may
        /// already hold; and it must not be silence either, because the check did not actually prove anything.
        /// </remarks>
        internal static (LogLevel level, string message) DescribeInteractionReadAccess(InteractionReadAccess access)
        {
            switch (access)
            {
                case InteractionReadAccess.Granted:
                    return (LogLevel.Information,
                        "Successfully verified 'AiEnterpriseInteraction.Read.All' for the Copilot AI interaction history import.");

                case InteractionReadAccess.NotGranted:
                    return (LogLevel.Error,
                        "ERROR: the runtime account does not hold the 'AiEnterpriseInteraction.Read.All' application permission, " +
                        "so the Copilot AI interaction history import will do nothing (it logs a warning and skips, rather than failing). " +
                        "IMPORTANT: unlike every other import, the installer does NOT grant this permission - add it to the runtime " +
                        "app registration as an APPLICATION permission and grant admin consent, then re-run these tests. " +
                        "Note it also requires the M365_COPILOT_BUSINESS_CHAT service plan on each user you expect data for.");

                default:
                    // Unknown / NoIdentityToInspect. Not a failure of the grant itself, and not a pass either.
                    return (LogLevel.Warning,
                        "Could not confirm whether the runtime account holds 'AiEnterpriseInteraction.Read.All' - the check could " +
                        "not read the app token's permissions. This is NOT a failure of the grant itself; verify it by hand on the " +
                        "app registration. Note the installer does not grant this permission, so it does need explicit admin consent.");
            }
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
    /// Whether the installer's Test Configuration can verify one <c>ImportTaskSettings</c> import toggle, and
    /// - when it cannot - the reason, so the gap is stated rather than silently absent from the test output.
    /// </summary>
    public class ImportToggleCoverage
    {
        public ImportToggleCoverage(string propertyName, string verifiedBy, string notVerifiedReason = null)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
                throw new ArgumentException("An import toggle needs a property name.", nameof(propertyName));

            var isVerified = !string.IsNullOrWhiteSpace(verifiedBy);
            if (isVerified == !string.IsNullOrWhiteSpace(notVerifiedReason))
            {
                // Exactly one must be supplied. Both would be contradictory; neither would let a toggle be
                // registered as "covered" while saying nothing about how - the very gap this table exists for.
                throw new ArgumentException(
                    $"Import toggle '{propertyName}' must declare either what verifies it or why nothing can.",
                    nameof(verifiedBy));
            }

            PropertyName = propertyName;
            VerifiedBy = verifiedBy;
            NotVerifiedReason = notVerifiedReason;
        }

        /// <summary>The <c>ImportTaskSettings</c> property name this covers, e.g. "CopilotInteractionHistory".</summary>
        public string PropertyName { get; }

        /// <summary>What Test Configuration checks for this toggle, or null when nothing can be checked.</summary>
        public string VerifiedBy { get; }

        /// <summary>Why nothing is checked. Set only when <see cref="VerifiedBy"/> is null.</summary>
        public string NotVerifiedReason { get; }

        public bool IsVerified => !string.IsNullOrWhiteSpace(VerifiedBy);
    }

    /// <summary>
    /// A configured Azure resource and the public hostname(s) that must resolve for the installer / runtime
    /// to reach it. Built by <see cref="SolutionInstallVerifier.BuildResourceDnsTargets"/>.
    /// Most resources have exactly one candidate; Redis has two because the same configured name is either
    /// Azure Managed Redis or a reused legacy classic cache, and the config does not record which.
    /// </summary>
    public class ResourceDnsTarget
    {
        public ResourceDnsTarget(string label, string fqdn) : this(label, new List<string> { fqdn }, null)
        {
        }

        public ResourceDnsTarget(
            string label,
            string fqdn,
            string failureHint,
            bool privateNetworkResolutionFailureIsWarning)
            : this(label, new List<string> { fqdn }, failureHint, privateNetworkResolutionFailureIsWarning)
        {
        }

        public ResourceDnsTarget(
            string label,
            IReadOnlyList<string> fqdns,
            string failureHint = null,
            bool privateNetworkResolutionFailureIsWarning = false)
        {
            if (fqdns == null || fqdns.Count == 0)
            {
                throw new ArgumentException("A DNS target needs at least one candidate hostname.", nameof(fqdns));
            }

            Label = label;
            Fqdns = fqdns;
            FailureHint = failureHint;
            PrivateNetworkResolutionFailureIsWarning = privateNetworkResolutionFailureIsWarning;
        }

        /// <summary>Human-readable resource description, e.g. "Key Vault".</summary>
        public string Label { get; }

        /// <summary>
        /// Candidate fully-qualified hostnames, in preference order. The resource is considered reachable
        /// when any one of them resolves.
        /// </summary>
        public IReadOnlyList<string> Fqdns { get; }

        /// <summary>
        /// Optional resource-specific guidance appended to the failure message, for anything the generic
        /// "the resource may not exist yet" wording cannot explain. Null when there is nothing extra to say.
        /// </summary>
        public string FailureHint { get; }

        /// <summary>
        /// True when failing to resolve this target from outside a private-endpoint deployment is advisory
        /// rather than proof that installation will fail from that host.
        /// </summary>
        public bool PrivateNetworkResolutionFailureIsWarning { get; }

        /// <summary>Primary (most likely) public fully-qualified hostname, e.g. "myvault.vault.azure.net".</summary>
        public string Fqdn => Fqdns[0];
    }
}
