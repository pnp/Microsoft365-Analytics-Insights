using Azure.Core;
using Azure.ResourceManager.PrivateDns;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    /// <summary>
    /// Redis-aware wrapper around <see cref="PrivateDnsZoneInstallTask"/>.
    /// Skips private DNS zone creation (with a warning) when the associated
    /// <see cref="RedisInstallTask"/> reused a pre-existing classic Azure Cache for
    /// Redis — that resource already has its own DNS configuration from the previous install,
    /// and creating a Managed-Redis DNS zone alongside it would not point to anything useful.
    /// Otherwise delegates to a standard <see cref="PrivateDnsZoneInstallTask"/>.
    /// </summary>
    public class RedisPrivateDnsZoneInstallTask : InstallTaskInAzResourceGroup<PrivateDnsZoneResource>
    {
        private readonly RedisInstallTask _redisTask;
        private readonly PrivateDnsZoneInstallTask _innerTask;

        public RedisPrivateDnsZoneInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, RedisInstallTask redisTask)
            : base(config, logger, azureLocation, tags)
        {
            _redisTask = redisTask;
            _innerTask = new PrivateDnsZoneInstallTask(config, logger, azureLocation, tags);
        }

        public override string TaskName => "get/create Redis private DNS zone";

        public override async Task<PrivateDnsZoneResource> ExecuteTaskReturnResult(object contextArg)
        {
            if (_redisTask?.LastResult == null)
            {
                throw new InstallException("RedisPrivateDnsZoneInstallTask requires the Redis install task to have run first");
            }

            if (_redisTask.LastResult.IsLegacyClassicCache)
            {
                _logger.LogInformation(
                    $"Skipping Redis private DNS zone creation: reusing legacy classic Azure Cache for Redis '{_redisTask.LastResult.ResourceName}'. " +
                    "The legacy resource keeps its existing DNS configuration.");
                return null;
            }

            _innerTask.Container = base.Container;
            return await _innerTask.ExecuteTaskReturnResult(contextArg);
        }
    }
}
