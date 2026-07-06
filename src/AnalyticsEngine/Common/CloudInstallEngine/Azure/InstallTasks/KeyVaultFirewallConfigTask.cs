using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.KeyVault.Models;
using Azure.ResourceManager.AppService;
using Azure.Core;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Enables the Key Vault firewall (network ACLs) and allow-lists the addresses the solution
    /// actually uses, so the install both complies with Azure policies that require a firewall
    /// (e.g. "Azure Key Vault should have firewall enabled or public network access disabled") and
    /// keeps the vault reachable.
    /// <para>
    /// Runs only for public-access deployments (private deployments keep <c>publicNetworkAccess =
    /// Disabled</c> and reach the vault through a private endpoint, so no IP rules are needed). It
    /// allow-lists, depending on config:
    /// <list type="bullet">
    /// <item>the installer machine's public IP (so the runtime secret upload succeeds),</item>
    /// <item>the App Service outbound IPs (so the web app / WebJobs can read secrets),</item>
    /// <item>for VNet-integrated deployments, a virtual-network rule for the App Service integration subnet.</item>
    /// </list>
    /// The actual <c>defaultAction = Deny</c> / <c>bypass = AzureServices</c> is already set by
    /// <see cref="KeyVaultTask"/> at create/update time; this task adds the allow rules via a PATCH so
    /// existing access policies are preserved. Best-effort (non-critical): a networking hiccup logs
    /// actionable guidance and does not abort an otherwise-successful install. See issue #136.
    /// </summary>
    public class KeyVaultFirewallConfigTask : InstallTaskInAzResourceGroup<KeyVaultResource>
    {
        public const string CONFIG_KEY_APP_SERVICE_NAME = "appServiceName";

        /// <summary>Optional: App Service VNet-integration subnet resource ID (added as a virtual-network rule).</summary>
        public const string CONFIG_KEY_VNET_SUBNET_ID = "vnetSubnetId";

        const string IP_CHECK_URL = "http://icanhazip.com";
        static readonly Regex IPv4Regex = new Regex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$", RegexOptions.Compiled);

        public KeyVaultFirewallConfigTask(TaskConfig config, ILogger logger, AzureLocation azureLocation)
            : base(config, logger, azureLocation, new Dictionary<string, string>())
        {
        }

        public override string TaskName => "configure Key Vault firewall (allow installer + App Service IPs)";

        // Best-effort networking config: a transient failure here must not abort an otherwise-successful install.
        public override bool IsCritical => false;

        public override async Task<KeyVaultResource> ExecuteTaskReturnResult(object contextArg)
        {
            var vault = EnsureContextArgType<KeyVaultResource>(contextArg);
            var appServiceName = _config.GetConfigValue(CONFIG_KEY_APP_SERVICE_NAME);
            var vnetSubnetId = _config.ContainsKey(CONFIG_KEY_VNET_SUBNET_ID) ? _config[CONFIG_KEY_VNET_SUBNET_ID] : null;

            var allowIps = new List<string>();

            // 1. Installer machine public IP - needed for the runtime secret upload (data-plane).
            var installerIp = TryGetInstallerPublicIp();
            if (!string.IsNullOrEmpty(installerIp))
            {
                allowIps.Add(installerIp);
                _logger.LogInformation($"Allowing installer public IP '{installerIp}' through the Key Vault firewall.");
            }
            else
            {
                _logger.LogWarning("Could not determine the installer's public IP; the runtime secret upload may fail until the IP is added to the Key Vault firewall manually (vault Networking blade).");
            }

            // 2. App Service outbound IPs - needed so the web app / WebJobs can read secrets.
            var appIps = GetAppServiceOutboundIps(appServiceName);
            if (appIps.Count > 0)
            {
                allowIps.AddRange(appIps);
                _logger.LogInformation($"Allowing {appIps.Count} App Service outbound IP(s) for '{appServiceName}' through the Key Vault firewall.");
            }
            else
            {
                _logger.LogWarning($"No outbound IPs resolved for App Service '{appServiceName}'; the web app may be unable to read secrets until its IPs are added to the Key Vault firewall manually.");
            }

            // 3. VNet-integrated deployments: allow the App Service integration subnet.
            if (!string.IsNullOrWhiteSpace(vnetSubnetId))
            {
                _logger.LogInformation("Allowing the App Service VNet-integration subnet through the Key Vault firewall (virtual-network rule).");
            }

            var ruleSet = BuildFirewallRuleSet(vault.Data.Properties?.NetworkRuleSet, allowIps, vnetSubnetId);

            _logger.LogInformation($"Enabling Key Vault '{vault.Data.Name}' firewall: default action 'Deny', bypass 'AzureServices', {ruleSet.IPRules.Count} IP rule(s), {ruleSet.VirtualNetworkRules.Count} virtual-network rule(s).");

            var patch = new KeyVaultPatch { Properties = new KeyVaultPatchProperties { NetworkRuleSet = ruleSet } };
            try
            {
                var updated = await vault.UpdateAsync(patch);
                return updated.Value;
            }
            catch (global::Azure.RequestFailedException ex) when (KeyVaultTask.IsDisallowedByPolicy(ex))
            {
                // Tenant Azure Policy denies writes to Microsoft.KeyVault/vaults. The vault already
                // exists and is usable; firewall allow-listing is best-effort, so reuse it as-is.
                _logger.LogWarning($"Could not allow-list IPs on key vault '{vault.Data.Name}': the write was disallowed by Azure Policy. Reusing the existing firewall configuration; ensure the installer and App Service IPs already have access, or grant a policy exemption and re-run.");
                return vault;
            }
        }

        List<string> GetAppServiceOutboundIps(string appServiceName)
        {
            var site = Container.GetWebSites().Where(s => s.Data.Name == appServiceName).SingleOrDefault();
            if (site == null)
            {
                _logger.LogWarning($"App Service '{appServiceName}' not found in the resource group; cannot allow its outbound IPs on the Key Vault firewall.");
                return new List<string>();
            }
            return ParseOutboundIPv4Addresses(site.Data.PossibleOutboundIPAddresses);
        }

        string TryGetInstallerPublicIp()
        {
            try
            {
                var ip = new WebClient().DownloadString(IP_CHECK_URL)?.Trim();
                if (!string.IsNullOrEmpty(ip) && IPv4Regex.IsMatch(ip))
                {
                    return ip;
                }
                _logger.LogWarning($"Public IP lookup from '{IP_CHECK_URL}' returned an unexpected value ('{ip}').");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not determine the installer's public IP from '{IP_CHECK_URL}': {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Builds a firewall-enabled <see cref="KeyVaultNetworkRuleSet"/> (default action <c>Deny</c>,
        /// bypass <c>AzureServices</c>) that preserves any existing IP / virtual-network rules and adds
        /// the supplied allow IPs and (optional) VNet integration subnet. De-duplicates rules. Pure and
        /// side-effect free so it can be unit tested without Azure.
        /// </summary>
        public static KeyVaultNetworkRuleSet BuildFirewallRuleSet(KeyVaultNetworkRuleSet existing, IEnumerable<string> allowIpAddresses, string vnetSubnetId)
        {
            var ruleSet = new KeyVaultNetworkRuleSet
            {
                DefaultAction = KeyVaultNetworkRuleAction.Deny,
                Bypass = KeyVaultNetworkRuleBypassOption.AzureServices,
            };

            var seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenSubnets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (existing != null)
            {
                foreach (var rule in existing.IPRules)
                {
                    if (!string.IsNullOrWhiteSpace(rule?.AddressRange) && seenIps.Add(rule.AddressRange))
                    {
                        ruleSet.IPRules.Add(new KeyVaultIPRule(rule.AddressRange));
                    }
                }
                foreach (var rule in existing.VirtualNetworkRules)
                {
                    var existingSubnetId = rule?.Id?.ToString();
                    if (!string.IsNullOrWhiteSpace(existingSubnetId) && seenSubnets.Add(existingSubnetId))
                    {
                        ruleSet.VirtualNetworkRules.Add(new KeyVaultVirtualNetworkRule(existingSubnetId)
                        {
                            IgnoreMissingVnetServiceEndpoint = rule.IgnoreMissingVnetServiceEndpoint
                        });
                    }
                }
            }

            if (allowIpAddresses != null)
            {
                foreach (var ip in allowIpAddresses)
                {
                    var trimmed = ip?.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && seenIps.Add(trimmed))
                    {
                        ruleSet.IPRules.Add(new KeyVaultIPRule(trimmed));
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(vnetSubnetId) && seenSubnets.Add(vnetSubnetId))
            {
                // IgnoreMissingVnetServiceEndpoint = true so the rule is accepted even if the subnet's
                // Microsoft.KeyVault service endpoint isn't (yet) enabled - the rule simply won't enforce
                // until it is, which is harmless when a private endpoint already covers VNet access.
                ruleSet.VirtualNetworkRules.Add(new KeyVaultVirtualNetworkRule(vnetSubnetId)
                {
                    IgnoreMissingVnetServiceEndpoint = true
                });
            }

            return ruleSet;
        }

        /// <summary>
        /// Parses a comma-separated list of IPv4 addresses (e.g. App Service
        /// <c>PossibleOutboundIPAddresses</c>), trimming, validating and de-duplicating. Non-IPv4
        /// entries (e.g. IPv6, which Key Vault IP rules don't support) and blanks are ignored.
        /// </summary>
        public static List<string> ParseOutboundIPv4Addresses(string commaSeparated)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(commaSeparated))
            {
                return result;
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in commaSeparated.Split(','))
            {
                var ip = part.Trim();
                if (ip.Length > 0 && IPv4Regex.IsMatch(ip) && seen.Add(ip))
                {
                    result.Add(ip);
                }
            }
            return result;
        }
    }
}
