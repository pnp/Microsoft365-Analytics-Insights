using Azure;
using Azure.Core;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.Redis;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Configure Redis firewall to allow App Service outbound IP addresses.
    /// Expects a <see cref="RedisResource"/> as context argument.
    /// </summary>
    public class RedisFirewallConfigTask : InstallTaskInAzResourceGroup<RedisResource>
    {
        public const string CONFIG_KEY_APP_SERVICE_NAME = "appServiceName";
        private const string RULE_NAME_PREFIX = "AppService";

        public RedisFirewallConfigTask(TaskConfig config, ILogger logger, AzureLocation azureLocation) : base(config, logger, azureLocation, new Dictionary<string, string>())
        {
        }

        public override string TaskName => "configure Redis firewall for App Service IPs";

        public override async Task<RedisResource> ExecuteTaskReturnResult(object contextArg)
        {
            base.EnsureContextArgType<RedisResource>(contextArg);
            var redis = (RedisResource)contextArg;

            var appServiceName = _config.GetConfigValue(CONFIG_KEY_APP_SERVICE_NAME);
            if (string.IsNullOrWhiteSpace(appServiceName))
            {
                _logger.LogWarning("No App Service name configured. Skipping Redis firewall configuration.");
                return redis;
            }

            // Lookup the App Service to get its outbound IP addresses
            var webApp = Container.GetWebSites().Where(s => s.Data.Name == appServiceName).SingleOrDefault();
            if (webApp == null)
            {
                _logger.LogWarning($"App Service '{appServiceName}' not found in resource group. Skipping Redis firewall configuration.");
                return redis;
            }

            var outboundIps = webApp.Data.OutboundIPAddresses;
            if (string.IsNullOrWhiteSpace(outboundIps))
            {
                _logger.LogWarning($"App Service '{appServiceName}' has no outbound IP addresses. Skipping Redis firewall configuration.");
                return redis;
            }

            var ips = outboundIps.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(ip => ip.Trim())
                .Where(ip => !string.IsNullOrEmpty(ip))
                .Distinct()
                .ToList();

            if (ips.Count == 0)
            {
                _logger.LogWarning("No valid App Service outbound IP addresses found. Skipping Redis firewall configuration.");
                return redis;
            }

            var firewallRules = redis.GetRedisFirewallRules();
            var existingRules = firewallRules.GetAll().ToList();

            foreach (var ip in ips)
            {
                var ruleName = $"{RULE_NAME_PREFIX}_{ip.Replace(".", "_")}";

                var existingRule = existingRules.FirstOrDefault(r => r.Data.Name.Equals(ruleName, StringComparison.OrdinalIgnoreCase));
                if (existingRule != null)
                {
                    _logger.LogInformation($"Redis firewall rule '{ruleName}' already exists for IP '{ip}'. Skipping.");
                    continue;
                }

                _logger.LogInformation($"Adding Redis firewall rule '{ruleName}' for App Service IP '{ip}'...");
                var ruleData = new RedisFirewallRuleData(IPAddress.Parse(ip), IPAddress.Parse(ip));
                await firewallRules.CreateOrUpdateAsync(WaitUntil.Completed, ruleName, ruleData);
            }

            _logger.LogInformation($"Redis firewall configured with {ips.Count} App Service outbound IP address(es).");
            return redis;
        }
    }
}
