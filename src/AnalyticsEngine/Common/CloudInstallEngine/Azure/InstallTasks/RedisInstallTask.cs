using Azure;
using Azure.Core;
using Azure.ResourceManager.Redis;
using Azure.ResourceManager.Redis.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public class RedisInstallTask : InstallTaskInAzResourceGroup<RedisResource>
    {
        public RedisInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "get/create redis cache";

        public async override Task<RedisResource> ExecuteTaskReturnResult(object contextArg)
        {
            var name = base._config.GetNameConfigValue();

            var allRedis = base.Container.GetAllRedis();
            RedisResource redisCache = allRedis.Where(c => c.Data.Name.ToLower() == name.ToLower()).SingleOrDefault();

            if (redisCache == null)
            {
                _logger.LogInformation($"Creating new redis cache '{name}' at basic SKU. This may take several minutes...");

                var newResourceData = new RedisCreateOrUpdateContent(AzureLocation, new RedisSku(RedisSkuName.Basic, RedisSkuFamily.BasicOrStandard, 0));
                base.EnsureTagsOnNew(newResourceData.Tags);
                var operation = await allRedis.CreateOrUpdateAsync(WaitUntil.Completed, name, newResourceData);
                _logger.LogInformation($"Created redis cache '{operation.Value.Data.Name}'.");

                return operation.Value;
            }
            else
            {
                _logger.LogInformation($"Found existing Redis cache '{redisCache.Data.HostName}'.");
                await base.EnsureTagsOnExisting(redisCache.Data.Tags, redisCache.GetTagResource());
            }


            return redisCache;
        }
    }
}
