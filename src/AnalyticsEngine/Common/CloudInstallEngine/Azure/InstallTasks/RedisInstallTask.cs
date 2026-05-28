using Azure;
using Azure.Core;
using Azure.ResourceManager.RedisEnterprise;
using Azure.ResourceManager.RedisEnterprise.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public class RedisInstallTask : InstallTaskInAzResourceGroup<RedisEnterpriseDatabaseResource>
    {
        private readonly bool _requireStandardSku;
        private readonly bool _allowPublicAccess;

        public RedisInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, bool requireStandardSku = false, bool allowPublicAccess = true) : base(config, logger, azureLocation, tags)
        {
            _requireStandardSku = requireStandardSku;
            _allowPublicAccess = allowPublicAccess;
        }

        public override string TaskName => "get/create redis cache";

        public async override Task<RedisEnterpriseDatabaseResource> ExecuteTaskReturnResult(object contextArg)
        {
            var name = base._config.GetNameConfigValue();
            // Balanced_B0 is the smallest/cheapest SKU (256 MB, no VNet/PE support).
            // Balanced_B1 is required when private endpoints are needed (VNet-enabled deployments).
            var skuName = _requireStandardSku ? RedisEnterpriseSkuName.BalancedB1 : RedisEnterpriseSkuName.BalancedB0;
            var skuLabel = _requireStandardSku ? "Balanced B1" : "Balanced B0";

            var allClusters = base.Container.GetRedisEnterpriseClusters();
            var cluster = allClusters.Where(c => c.Data.Name.ToLower() == name.ToLower()).SingleOrDefault();

            if (cluster == null)
            {
                _logger.LogInformation($"Creating new Azure Managed Redis cluster '{name}' with {skuLabel} SKU (public access: {(_allowPublicAccess ? "enabled" : "disabled")}). This may take several minutes...");

                var clusterData = new RedisEnterpriseClusterData(AzureLocation, new RedisEnterpriseSku(skuName));
                base.EnsureTagsOnNew(clusterData.Tags);
                var clusterOp = await allClusters.CreateOrUpdateAsync(WaitUntil.Completed, name, clusterData);
                cluster = clusterOp.Value;
                _logger.LogInformation($"Created Azure Managed Redis cluster '{cluster.Data.Name}'.");
            }
            else
            {
                _logger.LogInformation($"Found existing Azure Managed Redis cluster '{cluster.Data.HostName}'.");
                await base.EnsureTagsOnExisting(cluster.Data.Tags, cluster.GetTagResource());
            }

            // Get or create the default database
            var databases = cluster.GetRedisEnterpriseDatabases();
            RedisEnterpriseDatabaseResource database;
            try
            {
                database = await cluster.GetRedisEnterpriseDatabaseAsync("default");
                _logger.LogInformation($"Found existing Azure Managed Redis database on port {database.Data.Port}.");
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogInformation($"Creating default database for Azure Managed Redis cluster '{name}'...");
                var dbData = new RedisEnterpriseDatabaseData
                {
                    ClusteringPolicy = RedisEnterpriseClusteringPolicy.OssCluster,
                    EvictionPolicy = RedisEnterpriseEvictionPolicy.AllKeysLru,
                    AccessKeysAuthentication = AccessKeysAuthentication.Enabled
                };
                var dbOp = await databases.CreateOrUpdateAsync(WaitUntil.Completed, "default", dbData);
                database = dbOp.Value;
                _logger.LogInformation($"Created Azure Managed Redis database on port {database.Data.Port}.");
            }

            return database;
        }
    }
}
