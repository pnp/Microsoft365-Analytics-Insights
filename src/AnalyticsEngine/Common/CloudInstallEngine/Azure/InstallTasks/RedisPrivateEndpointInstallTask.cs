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
    /// Otherwise delegates to a standard <see cref="PrivateEndpointInstallTask"/>.
    /// </summary>
    public class RedisPrivateEndpointInstallTask : InstallTaskInAzResourceGroup<PrivateEndpointResource>
    {
        private readonly RedisInstallTask _redisTask;
        private readonly PrivateEndpointInstallTask _innerTask;

        public RedisPrivateEndpointInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, RedisInstallTask redisTask)
            : base(config, logger, azureLocation, tags)
        {
            _redisTask = redisTask;
            _innerTask = new PrivateEndpointInstallTask(config, logger, azureLocation, tags);
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

            _innerTask.Container = base.Container;
            return await _innerTask.ExecuteTaskReturnResult(contextArg);
        }
    }
}
