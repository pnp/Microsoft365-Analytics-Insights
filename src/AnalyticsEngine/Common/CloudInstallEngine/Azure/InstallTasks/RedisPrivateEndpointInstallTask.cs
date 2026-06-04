using Azure.Core;
using Azure.ResourceManager.Network;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Redis-aware wrapper around <see cref="PrivateEndpointInstallTask"/>.
    /// Skips private endpoint creation (with a warning) when the associated
    /// <see cref="RedisInstallTask"/> reused a pre-existing classic Azure Cache for
    /// Redis — that resource already has its own networking from the previous install.
    /// Otherwise derives the Private Link sub-resource ("group") ID and target ARM
    /// resource ID from <see cref="RedisInstallResult"/> at execution time (so the
    /// values always match the Redis kind we actually got) and delegates to a
    /// standard <see cref="PrivateEndpointInstallTask"/>.
    /// </summary>
    public class RedisPrivateEndpointInstallTask : InstallTaskInAzResourceGroup<PrivateEndpointResource>
    {
        private readonly RedisInstallTask _redisTask;

        public RedisPrivateEndpointInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, RedisInstallTask redisTask)
            : base(config, logger, azureLocation, tags)
        {
            _redisTask = redisTask;
        }

        public override string TaskName => "get/create Redis private endpoint";

        public override async Task<PrivateEndpointResource> ExecuteTaskReturnResult(object contextArg)
        {
            if (_redisTask?.LastResult == null)
            {
                throw new InstallException("RedisPrivateEndpointInstallTask requires the Redis install task to have run first");
            }

            if (_redisTask.LastResult.IsLegacyClassicCache)
            {
                _logger.LogWarning(
                    $"Skipping Redis private endpoint creation: reusing legacy classic Azure Cache for Redis '{_redisTask.LastResult.ResourceName}'. " +
                    "If you want a private endpoint for the legacy cache, configure one manually OR delete the legacy resource and re-run the installer " +
                    "to provision Azure Managed Redis with a managed private endpoint.");
                return null;
            }

            if (string.IsNullOrEmpty(_redisTask.LastResult.PrivateLinkGroupId)
                || string.IsNullOrEmpty(_redisTask.LastResult.ResourceId))
            {
                throw new InstallException(
                    "RedisPrivateEndpointInstallTask cannot build a private endpoint: the Redis install task did not populate " +
                    $"{nameof(RedisInstallResult.PrivateLinkGroupId)} / {nameof(RedisInstallResult.ResourceId)} on its result.");
            }

            var peName = _config.GetNameConfigValue();
            var subnetId = _config.GetConfigValue(PrivateEndpointInstallTask.CONFIG_KEY_SUBNET_ID);

            var innerConfig = TaskConfig.GetConfigForName(peName)
                .AddSetting(PrivateEndpointInstallTask.CONFIG_KEY_TARGET_RESOURCE_ID, _redisTask.LastResult.ResourceId)
                .AddSetting(PrivateEndpointInstallTask.CONFIG_KEY_GROUP_ID, _redisTask.LastResult.PrivateLinkGroupId)
                .AddSetting(PrivateEndpointInstallTask.CONFIG_KEY_SUBNET_ID, subnetId);

            var innerTask = new PrivateEndpointInstallTask(innerConfig, _logger, AzureLocation, Tags)
            {
                Container = base.Container,
            };
            return await innerTask.ExecuteTaskReturnResult(contextArg);
        }
    }
}
