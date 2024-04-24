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
    /// Configure SQL Server firewall to allow local IP
    /// </summary>
    public class SqlServerFirewallConfigTask : InstallTaskInAzResourceGroup<SqlServerResource>
    {
        const string ALL_AZ_SERVICES_RULE_NAME = "AllowAllWindowsAzureIps";
        public SqlServerFirewallConfigTask(TaskConfig config, ILogger logger, AzureLocation azureLocation) : base(config, logger, azureLocation, new Dictionary<string, string>())
        {
        }

        public override string TaskName => "configure SQL Server firewall for local IP";

        public override async Task<SqlServerResource> ExecuteTaskReturnResult(object contextArg)
        {
            base.EnsureContextArgType<SqlServerResource>(contextArg);

            var ruleName = _config.GetNameConfigValue();
            var server = (SqlServerResource)contextArg;

            var clientIpRule = GetRuleByName(server, ruleName);

            var serverRules = server.GetSqlFirewallRules();
            if (clientIpRule == null)
            {
                const string URL = "http://icanhazip.com";
                _logger.LogInformation($"No SQL Server firewall exception detected for installer (no exception with name '{ruleName}'). Checking public IP address from '{URL}'...");
                var externalip = new System.Net.WebClient().DownloadString(URL);
                if (externalip.EndsWith("\n"))
                {
                    externalip = externalip.TrimEnd("\n".ToCharArray());
                }

                // Validate IP address string
                var ipRegEx = new System.Text.RegularExpressions.Regex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b");
                if (ipRegEx.IsMatch(externalip))
                {
                    _logger.LogInformation($"Detected public IP address as '{externalip}'...opening firewall of SQL Server to allow edits from local machine...");

                    // Add users IP
                    await AddRule(serverRules, ruleName, externalip);

                }
                else
                {
                    _logger.LogInformation($"Invalid external IP address detected? '{externalip}' doesn't seem to be a valid IPv4 address.", true);
                    _logger.LogInformation($"Add manually a rule to Azure SQL Server with name '{ruleName}' or DB tests will fail.", true);
                }

            }
            else
            {
                _logger.LogInformation($"SQL Server firewall exception for installer already present ('{ruleName}'). Skipping SQL Server firewall configuration for client IP.");
            }

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
