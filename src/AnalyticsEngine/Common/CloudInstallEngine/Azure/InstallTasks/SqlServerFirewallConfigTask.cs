using Azure;
using Azure.Core;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.Sql;
using Azure.ResourceManager.Sql.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Configure the Azure SQL Server firewall so the installer host can reach the database.
    /// </summary>
    /// <remarks>
    /// This used to decide by RULE NAME alone: if a rule called "O365 Adv Analytics Setup Rule" existed it
    /// logged "already present ... Skipping" without ever reading the IP range it held. So once the rule went
    /// stale - a DHCP lease renewal, a different office, VPN on/off, a mobile hotspot - every later install
    /// reported success here and then died two minutes later at the database step with
    /// "Client with IP address '...' is not allowed to access the server", by which point the App Service had
    /// already been stopped. See issue #326.
    /// </remarks>
    public class SqlServerFirewallConfigTask : InstallTaskInAzResourceGroup<SqlServerResource>
    {
        public const string ALL_AZ_SERVICES_RULE_NAME = "AllowAllWindowsAzureIps";

        private readonly string _keyVaultName;
        private readonly string _appServiceName;
        private readonly Func<bool> _serverCreatedThisRun;

        /// <param name="keyVaultName">
        /// Optional. Enables the first-party Key Vault caller-IP source (see <see cref="PublicIpResolver"/>);
        /// harmlessly ignored when the vault does not exist yet.
        /// </param>
        /// <param name="appServiceName">
        /// Optional. The solution's App Service, used to tell whether an operator who removed the Azure-services rule
        /// left the App Service another way to reach the server.
        /// </param>
        /// <param name="serverCreatedThisRun">
        /// Optional. True when this run created the server. Without it every server is treated as pre-existing.
        /// </param>
        public SqlServerFirewallConfigTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, string keyVaultName = null,
            string appServiceName = null, Func<bool> serverCreatedThisRun = null)
            : base(config, logger, azureLocation, new Dictionary<string, string>())
        {
            _keyVaultName = keyVaultName;
            _appServiceName = appServiceName;
            _serverCreatedThisRun = serverCreatedThisRun ?? (() => false);
        }

        public override string TaskName => "configure SQL Server firewall for local IP";

        public override async Task<SqlServerResource> ExecuteTaskReturnResult(object contextArg)
        {
            base.EnsureContextArgType<SqlServerResource>(contextArg);

            var ruleName = _config.GetNameConfigValue();
            var server = (SqlServerResource)contextArg;
            var serverRules = server.GetSqlFirewallRules();

            await EnsureClientIpAllowed(server, serverRules, ruleName);
            await EnsureAzureServicesRuleAsync(server);
            return server;
        }

        /// <summary>
        /// Creates the "allow Azure services" exception on a server this run created. On an existing server that lacks it,
        /// puts it back only when the App Service would otherwise have no way to reach the server.
        /// </summary>
        internal async Task EnsureAzureServicesRuleAsync(SqlServerResource server)
        {
            var serverRules = server.GetSqlFirewallRules();
            var azServicesRule = GetRuleByName(server, ALL_AZ_SERVICES_RULE_NAME);
            if (azServicesRule != null)
            {
                _logger.LogInformation($"SQL Server firewall exception for Azure services already present.");
                return;
            }

            if (_serverCreatedThisRun())
            {
                // Add Azure Services to firewall - https://learn.microsoft.com/en-us/azure/azure-sql/database/firewall-configure?view=azuresql#connections-from-inside-azure
                await AddRule(serverRules, ALL_AZ_SERVICES_RULE_NAME, SqlFirewallRules.AzureServicesSentinelIp);
                _logger.LogInformation($"SQL Server firewall exception added for all Azure services.");
                return;
            }

            // An existing server without the rule. Every earlier install created it, so an operator has most likely
            // removed it on purpose (Defender for Cloud recommends doing so). Leave it removed when the App Service can
            // still reach the server some other way; put it back only when the App Service would otherwise be cut off.
            var otherPath = await FindAppServicePathToServerAsync(server);
            if (otherPath != null)
            {
                _logger.LogInformation(
                    $"SQL Server firewall exception for Azure services ('{ALL_AZ_SERVICES_RULE_NAME}') is not present on '{server.Data.Name}'. " +
                    $"Leaving it removed: the App Service reaches the server through {otherPath}.");
                return;
            }

            await AddRule(serverRules, ALL_AZ_SERVICES_RULE_NAME, SqlFirewallRules.AzureServicesSentinelIp);
            _logger.LogWarning(
                $"SQL Server firewall exception for Azure services ('{ALL_AZ_SERVICES_RULE_NAME}') was missing on '{server.Data.Name}' and has been added again, " +
                "because the App Service has no other way to reach the server: none of its outbound IP addresses is covered by a firewall rule, " +
                "and it has no VNet integration with a virtual network rule or private endpoint on the server. To keep the exception removed, " +
                "give the App Service one of those paths and re-run the installer; it then leaves the exception removed.");
        }

        /// <summary>
        /// Describes how the App Service can reach the server without the Azure-services exception, or null when it can't
        /// (or that can't be worked out).
        /// </summary>
        private async Task<string> FindAppServicePathToServerAsync(SqlServerResource server)
        {
            if (string.IsNullOrWhiteSpace(_appServiceName)) return null;
            try
            {
                var site = (await Container.GetWebSiteAsync(_appServiceName, CancellationToken)).Value;
                var vnetRuleSubnets = string.IsNullOrWhiteSpace(site.Data.VirtualNetworkSubnetId?.ToString())
                    ? new List<string>()
                    : server.GetSqlServerVirtualNetworkRules().GetAll(cancellationToken: CancellationToken)
                        .Select(r => r.Data.VirtualNetworkSubnetId?.ToString()).Where(id => id != null).ToList();
                var approvedEndpoints = server.Data.PrivateEndpointConnections
                    .Count(c => c?.Properties?.ConnectionState?.Status == SqlPrivateLinkServiceConnectionStatus.Approved);

                return DescribeAppServicePath(
                    KeyVaultFirewallConfigTask.ParseOutboundIPv4Addresses(site.Data.PossibleOutboundIPAddresses),
                    ReadRules(server), site.Data.VirtualNetworkSubnetId?.ToString(), vnetRuleSubnets, approvedEndpoints);
            }
            catch (RequestFailedException ex) when (!CancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning($"Could not check how App Service '{_appServiceName}' reaches SQL Server '{server.Data.Name}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Pure decision behind <see cref="FindAppServicePathToServerAsync"/>: a firewall rule set that covers every
        /// outbound IP address the App Service can use, or VNet integration into a subnet the server has a virtual network
        /// rule for, or VNet integration plus an approved private endpoint on the server. Null when none applies.
        /// </summary>
        public static string DescribeAppServicePath(IReadOnlyCollection<string> appOutboundIps, IReadOnlyCollection<SqlFirewallRuleRange> rules,
            string appIntegrationSubnetId, IEnumerable<string> serverVnetRuleSubnetIds, int approvedPrivateEndpoints)
        {
            if (!string.IsNullOrWhiteSpace(appIntegrationSubnetId))
            {
                if (serverVnetRuleSubnetIds != null && serverVnetRuleSubnetIds.Any(id => string.Equals(id, appIntegrationSubnetId, StringComparison.OrdinalIgnoreCase)))
                    return "its VNet integration subnet, which has a virtual network rule on the server";
                if (approvedPrivateEndpoints > 0)
                    return "its VNet integration and the server's private endpoint";
            }
            if (appOutboundIps != null && appOutboundIps.Count > 0 && rules != null &&
                appOutboundIps.All(ip => SqlFirewallRules.RulesCovering(ip, rules).Count > 0))
                return $"firewall rules that cover all {appOutboundIps.Count} of its outbound IP addresses";
            return null;
        }

        /// <summary>
        /// Make sure this host's public IP is covered by SOME rule on the server, creating or updating the
        /// installer's own rule when it is not.
        /// </summary>
        private async Task EnsureClientIpAllowed(SqlServerResource server, SqlFirewallRuleCollection serverRules, string ruleName)
        {
            // Always resolve the public IP, whether or not our named rule exists. The old code only looked it
            // up inside the "rule missing" branch, so when a stale rule existed the installer never even asked
            // what its own address was - and so could not report the mismatch it was about to die from.
            var clientIp = await PublicIpResolver.TryGetPublicIPv4Async(_keyVaultName, _logger, cancellationToken: CancellationToken);

            var existingRules = ReadRules(server);
            var ourRule = existingRules.FirstOrDefault(r => r.Name == ruleName);

            if (clientIp == null)
            {
                // Already reported by the resolver. Don't touch the rule: overwriting a working rule with a
                // guess would be worse than leaving it, and the database step can still repair it from the
                // address Azure itself reports.
                _logger.LogWarning(
                    $"Could not determine this host's public IP address, so the SQL Server firewall rule '{ruleName}' " +
                    (ourRule == null
                        ? "could not be created. Add it by hand, or let the database step repair it automatically."
                        : $"was left as-is ({ourRule.StartIp} - {ourRule.EndIp}). If that range no longer covers this host, " +
                          "the database step will detect it and repair the rule automatically."));
                return;
            }

            // "Is my IP allowed?", not "does my named rule exist?". An admin-added corporate range is a
            // perfectly good reason to leave things alone.
            var covering = SqlFirewallRules.RulesCovering(clientIp, existingRules);
            if (covering.Count > 0)
            {
                _logger.LogInformation(
                    $"SQL Server firewall already allows this host's IP {clientIp} via {string.Join(", ", covering.Select(r => r.ToString()))}. " +
                    "Skipping SQL Server firewall configuration for client IP.");
                return;
            }

            if (ourRule == null)
            {
                _logger.LogInformation(
                    $"No SQL Server firewall rule allows this host's IP {clientIp}. Creating '{ruleName}' for it...");
            }
            else if (!SqlFirewallRules.CanSafelyReplaceWithSingleAddress(ourRule))
            {
                // An admin has widened the installer's rule into a range. Narrowing it back to one address
                // would silently revoke access for every other address it covers.
                _logger.LogError(
                    $"SQL Server firewall rule '{ruleName}' has been widened to the range {ourRule.StartIp} - {ourRule.EndIp}, " +
                    $"which does not include this host's IP {clientIp}. The installer will NOT narrow it to a single address, " +
                    "because that would revoke access for every other address in that range. Extend the range to include " +
                    $"{clientIp} (or add a separate rule for it) and re-run the installer.");
                return;
            }
            else
            {
                // The message the old code could never produce, and the one an admin actually needs.
                _logger.LogWarning(
                    $"SQL Server firewall rule '{ruleName}' allows {ourRule.StartIp} - {ourRule.EndIp} but this host's public IP " +
                    $"is {clientIp}, which no rule on this server covers. Updating the rule so the database step can connect.");
            }

            await AddRule(serverRules, ruleName, clientIp);
            _logger.LogInformation($"SQL Server firewall rule '{ruleName}' now allows {clientIp}.");
        }

        /// <summary>Snapshot of the server's firewall rules in the shape the pure coverage logic works on.</summary>
        private List<SqlFirewallRuleRange> ReadRules(SqlServerResource server)
        {
            return server.GetSqlFirewallRules().GetAll(cancellationToken: CancellationToken)
                .Select(r => new SqlFirewallRuleRange(r.Data.Name, r.Data.StartIPAddress, r.Data.EndIPAddress))
                .ToList();
        }

        SqlFirewallRuleResource GetRuleByName(SqlServerResource server, string name)
        {
            return server.GetSqlFirewallRules().GetAll(cancellationToken: CancellationToken).Where(r => r.Data.Name == name).SingleOrDefault();
        }

        private async Task AddRule(SqlFirewallRuleCollection serverRules, string ruleName, string ip)
        {
            await serverRules.CreateOrUpdateAsync(WaitUntil.Completed, ruleName, new SqlFirewallRuleData { StartIPAddress = ip, EndIPAddress = ip, Name = ruleName }, cancellationToken: CancellationToken);
        }
    }
}
