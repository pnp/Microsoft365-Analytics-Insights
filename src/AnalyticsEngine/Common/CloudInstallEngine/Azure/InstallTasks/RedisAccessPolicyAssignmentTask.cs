using Azure.Core;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Access policy assignment task for Azure Managed Redis.
    /// Azure Managed Redis uses access key authentication by default, which does not require an
    /// explicit RBAC access policy assignment. This task is therefore a passthrough that returns
    /// the Redis install result unchanged.
    /// If Entra ID (AAD) authentication is required in the future, configure
    /// <c>AccessKeysAuthentication = Disabled</c> on the database and assign the appropriate
    /// built-in RBAC role (e.g. "Redis Cache Contributor") to the service principal instead.
    /// </summary>
    public class RedisAccessPolicyAssignmentTask : InstallTaskInAzResourceGroup<RedisInstallResult>
    {
        public const string CONFIG_KEY_CLIENT_ID = "clientId";
        public const string CONFIG_KEY_CLIENT_SECRET = "clientSecret";
        public const string CONFIG_KEY_TENANT_ID = "tenantId";
        public const string CONFIG_KEY_INSTALLER_CLIENT_ID = "installerClientId";
        public const string CONFIG_KEY_INSTALLER_CLIENT_SECRET = "installerClientSecret";
        public const string CONFIG_KEY_INSTALLER_TENANT_ID = "installerTenantId";
        public const string CONFIG_KEY_ACCESS_POLICY_NAME = "accessPolicyName";

        public RedisAccessPolicyAssignmentTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "assign Redis access policy";

        public override Task<RedisInstallResult> ExecuteTaskReturnResult(object contextArg)
        {
            var redis = contextArg as RedisInstallResult;
            if (redis == null)
            {
                throw new InstallException("RedisAccessPolicyAssignmentTask requires a RedisInstallResult as context");
            }

            if (redis.IsLegacyClassicCache)
            {
                _logger.LogInformation($"Skipping Redis access policy assignment: reusing legacy classic Azure Cache for Redis '{redis.ResourceName}' which already has its own access configuration from the previous install.");
                return Task.FromResult(redis);
            }

            _logger.LogInformation("Azure Managed Redis uses access key authentication — no additional RBAC access policy assignment is required.");

            return Task.FromResult(redis);
        }
    }
}

