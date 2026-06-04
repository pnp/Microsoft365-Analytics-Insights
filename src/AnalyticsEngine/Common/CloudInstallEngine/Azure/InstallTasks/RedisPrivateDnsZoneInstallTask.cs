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
    /// Otherwise derives the private DNS zone name from <see cref="RedisInstallResult"/> at
    /// execution time (so the zone always matches the Redis kind we actually got — e.g.
    /// <c>privatelink.redis.azure.net</c> for Azure Managed Redis) and delegates to a
    /// standard <see cref="PrivateDnsZoneInstallTask"/>.
    /// </summary>
    public class RedisPrivateDnsZoneInstallTask : InstallTaskInAzResourceGroup<PrivateDnsZoneResource>
    {
        private readonly RedisInstallTask _redisTask;

        public RedisPrivateDnsZoneInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, RedisInstallTask redisTask)
            : base(config, logger, azureLocation, tags)
        {
            _redisTask = redisTask;
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

            if (string.IsNullOrEmpty(_redisTask.LastResult.PrivateDnsZoneName))
            {
                throw new InstallException(
                    "RedisPrivateDnsZoneInstallTask cannot deploy a DNS zone: the Redis install task did not populate " +
                    $"{nameof(RedisInstallResult.PrivateDnsZoneName)} on its result.");
            }

            var vnetId = _config.GetConfigValue(PrivateDnsZoneInstallTask.CONFIG_KEY_VNET_ID);
            var peName = _config.GetConfigValue(PrivateDnsZoneInstallTask.CONFIG_KEY_PE_NAME);

            var innerConfig = TaskConfig.GetConfigForName(_redisTask.LastResult.PrivateDnsZoneName)
                .AddSetting(PrivateDnsZoneInstallTask.CONFIG_KEY_VNET_ID, vnetId)
                .AddSetting(PrivateDnsZoneInstallTask.CONFIG_KEY_PE_NAME, peName);

            var innerTask = new PrivateDnsZoneInstallTask(innerConfig, _logger, AzureLocation, Tags)
            {
                Container = base.Container,
            };
            return await innerTask.ExecuteTaskReturnResult(contextArg);
        }
    }
}
