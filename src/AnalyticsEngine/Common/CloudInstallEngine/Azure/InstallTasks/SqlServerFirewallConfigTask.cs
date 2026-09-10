using Azure;
using Azure.Core;
using Azure.ResourceManager.Sql;
using Microsoft.Extensions.Logging;
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
        const string ALL_AZ_SERVICES_RULE_NAME = "AllowAllWindowsAzureIps";

        private readonly string _keyVaultName;

        /// <param name="keyVaultName">
        /// Optional. Enables the first-party Key Vault caller-IP source (see <see cref="PublicIpResolver"/>);
        /// harmlessly ignored when the vault does not exist yet.
        /// </param>
        public SqlServerFirewallConfigTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, string keyVaultName = null)
            : base(config, logger, azureLocation, new Dictionary<string, string>())
        {
            _keyVaultName = keyVaultName;
        }

        public override string TaskName => "configure SQL Server firewall for local IP";

        public override async Task<SqlServerResource> ExecuteTaskReturnResult(object contextArg)
        {
            base.EnsureContextArgType<SqlServerResource>(contextArg);

            var ruleName = _config.GetNameConfigValue();
            var server = (SqlServerResource)contextArg;
            var serverRules = server.GetSqlFirewallRules();

            await EnsureClientIpAllowed(server, serverRules, ruleName);

            var azServicesRule = GetRuleByName(server, ALL_AZ_SERVICES_RULE_NAME);
            if (azServicesRule == null)
            {
                // Add Azure Services to firewall - https://learn.microsoft.com/en-us/azure/azure-sql/database/firewall-configure?view=azuresql#connections-from-inside-azure
                await AddRule(serverRules, ALL_AZ_SERVICES_RULE_NAME, "0.0.0.0");
                _logger.LogInformation($"SQL Server firewall exception added for all Azure services.");
            }
            else
            {
                _logger.LogInformation($"SQL Server firewall exception for Azure services already present.");
            }

            return server;
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
            var clientIp = await PublicIpResolver.TryGetPublicIPv4Async(_keyVaultName, _logger);

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
        private static List<SqlFirewallRuleRange> ReadRules(SqlServerResource server)
        {
            return server.GetSqlFirewallRules()
                .Select(r => new SqlFirewallRuleRange(r.Data.Name, r.Data.StartIPAddress, r.Data.EndIPAddress))
                .ToList();
        }

        SqlFirewallRuleResource GetRuleByName(SqlServerResource server, string name)
        {
            return server.GetSqlFirewallRules().Where(r => r.Data.Name == name).SingleOrDefault();
        }

        private async Task AddRule(SqlFirewallRuleCollection serverRules, string ruleName, string ip)
        {
            await serverRules.CreateOrUpdateAsync(WaitUntil.Completed, ruleName, new SqlFirewallRuleData { StartIPAddress = ip, EndIPAddress = ip, Name = ruleName });
        }
    }
}
