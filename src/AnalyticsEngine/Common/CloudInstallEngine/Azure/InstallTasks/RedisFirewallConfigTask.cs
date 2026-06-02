using Azure.Core;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Firewall configuration task for Azure Managed Redis.
    /// Azure Managed Redis (Redis Enterprise) does not expose IP-based firewall rules when using
    /// access key authentication. Access control is enforced via access keys and, optionally,
    /// private endpoints. This task is therefore a no-op passthrough that returns the Redis
    /// install result unchanged.
    /// When a pre-existing legacy classic Azure Cache for Redis is being reused, this task also
    /// skips — the previous install would have configured firewall rules on that resource already.
    /// </summary>
    public class RedisFirewallConfigTask : InstallTaskInAzResourceGroup<RedisInstallResult>
    {
        public const string CONFIG_KEY_APP_SERVICE_NAME = "appServiceName";

        public RedisFirewallConfigTask(TaskConfig config, ILogger logger, AzureLocation azureLocation) : base(config, logger, azureLocation, new Dictionary<string, string>())
        {
        }

        public override string TaskName => "configure Redis firewall for App Service IPs";

        public override Task<RedisInstallResult> ExecuteTaskReturnResult(object contextArg)
        {
            base.EnsureContextArgType<RedisInstallResult>(contextArg);
            var redis = (RedisInstallResult)contextArg;

            if (redis.IsLegacyClassicCache)
            {
                _logger.LogInformation($"Skipping Redis firewall configuration: reusing legacy classic Azure Cache for Redis '{redis.ResourceName}' which already has its own firewall rules from the previous install.");
                return Task.FromResult(redis);
            }

            _logger.LogInformation("Azure Managed Redis uses access key authentication — IP-based firewall configuration is not required.");

            return Task.FromResult(redis);
        }
    }
}

