using Azure.Core;
using Azure.ResourceManager.RedisEnterprise;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Firewall configuration task for Azure Managed Redis.
    /// Azure Managed Redis (Redis Enterprise) does not expose IP-based firewall rules when using
    /// access key authentication. Access control is enforced via access keys and, optionally,
    /// private endpoints. This task is therefore a no-op passthrough that returns the database
    /// resource unchanged.
    /// </summary>
    public class RedisFirewallConfigTask : InstallTaskInAzResourceGroup<RedisEnterpriseDatabaseResource>
    {
        public const string CONFIG_KEY_APP_SERVICE_NAME = "appServiceName";

        public RedisFirewallConfigTask(TaskConfig config, ILogger logger, AzureLocation azureLocation) : base(config, logger, azureLocation, new Dictionary<string, string>())
        {
        }

        public override string TaskName => "configure Redis firewall for App Service IPs";

        public override Task<RedisEnterpriseDatabaseResource> ExecuteTaskReturnResult(object contextArg)
        {
            base.EnsureContextArgType<RedisEnterpriseDatabaseResource>(contextArg);
            var database = (RedisEnterpriseDatabaseResource)contextArg;

            _logger.LogInformation("Azure Managed Redis uses access key authentication — IP-based firewall configuration is not required.");

            return Task.FromResult(database);
        }
    }
}
