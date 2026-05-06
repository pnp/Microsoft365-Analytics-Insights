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
        private readonly bool _requireStandardSku;
        private readonly bool _allowPublicAccess;

        public RedisInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, bool requireStandardSku = false, bool allowPublicAccess = true) : base(config, logger, azureLocation, tags)
        {
            _requireStandardSku = requireStandardSku;
            _allowPublicAccess = allowPublicAccess;
        }

        public override string TaskName => "get/create redis cache";

        public async override Task<RedisResource> ExecuteTaskReturnResult(object contextArg)
        {
            var name = base._config.GetNameConfigValue();
            var skuName = _requireStandardSku ? RedisSkuName.Standard : RedisSkuName.Basic;
            var skuFamily = RedisSkuFamily.BasicOrStandard;
            var desiredAccess = _allowPublicAccess ? RedisPublicNetworkAccess.Enabled : RedisPublicNetworkAccess.Disabled;

            var allRedis = base.Container.GetAllRedis();
            RedisResource redisCache = allRedis.Where(c => c.Data.Name.ToLower() == name.ToLower()).SingleOrDefault();

            if (redisCache == null)
            {
                var skuLabel = _requireStandardSku ? "standard" : "basic";
                _logger.LogInformation($"Creating new redis cache '{name}' at {skuLabel} SKU (public access: {(_allowPublicAccess ? "enabled" : "disabled")}). This may take several minutes...");

                var newResourceData = new RedisCreateOrUpdateContent(AzureLocation, new RedisSku(skuName, skuFamily, 0))
                {
                    MinimumTlsVersion = RedisTlsVersion.Tls1_2,
                    PublicNetworkAccess = desiredAccess
                };
                base.EnsureTagsOnNew(newResourceData.Tags);
                var operation = await allRedis.CreateOrUpdateAsync(WaitUntil.Completed, name, newResourceData);
                _logger.LogInformation($"Created redis cache '{operation.Value.Data.Name}'.");

                return operation.Value;
            }
            else
            {
                bool needsUpdate = false;
                // Use the existing SKU for updates to avoid downgrade errors
                var existingSku = redisCache.Data.Sku;
                var updateData = new RedisCreateOrUpdateContent(AzureLocation, new RedisSku(existingSku.Name, existingSku.Family, existingSku.Capacity))
                {
                    MinimumTlsVersion = RedisTlsVersion.Tls1_2
                };

                // Ensure minimum TLS version is 1.2
                if (redisCache.Data.MinimumTlsVersion == null || !redisCache.Data.MinimumTlsVersion.Value.ToString().Equals(RedisTlsVersion.Tls1_2.ToString()))
                {
                    _logger.LogInformation($"Updating Redis cache '{name}' to enforce TLS 1.2...");
                    needsUpdate = true;
                }

                if (redisCache.Data.PublicNetworkAccess == null || redisCache.Data.PublicNetworkAccess.Value != desiredAccess)
                {
                    _logger.LogInformation($"Updating Redis cache '{name}' public network access to '{desiredAccess}'...");
                    updateData.PublicNetworkAccess = desiredAccess;
                    needsUpdate = true;
                }

                if (needsUpdate)
                {
                    await allRedis.CreateOrUpdateAsync(WaitUntil.Completed, name, updateData);
                }

                _logger.LogInformation($"Found existing Redis cache '{redisCache.Data.HostName}'.");
                await base.EnsureTagsOnExisting(redisCache.Data.Tags, redisCache.GetTagResource());
            }


            return redisCache;
        }
    }
}
