using Azure;
using Azure.Core;
using Azure.ResourceManager.Redis;
using Azure.ResourceManager.Redis.Models;
using Azure.ResourceManager.RedisEnterprise;
using Azure.ResourceManager.RedisEnterprise.Models;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public class RedisInstallTask : InstallTaskInAzResourceGroup<RedisInstallResult>
    {
        /// <summary>Azure Managed Redis always uses port 10000 for TLS connections.</summary>
        public const int DEFAULT_TLS_PORT = 10000;

        /// <summary>Classic Azure Cache for Redis uses port 6380 for TLS connections.</summary>
        public const int LEGACY_TLS_PORT = 6380;

        /// <summary>Private Link sub-resource ("group") ID used when targeting Azure Managed Redis.</summary>
        public const string MANAGED_REDIS_PE_GROUP_ID = "redisEnterprise";

        /// <summary>
        /// Private DNS zone that matches the CNAME chain produced by an Azure Managed Redis FQDN
        /// (<c>&lt;name&gt;.&lt;region&gt;.redis.azure.net</c> → <c>...privatelink.redis.azure.net</c>).
        /// Note: this is NOT <c>privatelink.redisenterprise.cache.azure.net</c> — that zone exists
        /// historically but does not match the actual hostnames Azure Managed Redis uses today, so
        /// a private endpoint pointed at it never auto-registers A records and VNet-integrated
        /// clients fall through to public DNS (which the PE-only firewall then blocks).
        /// </summary>
        public const string MANAGED_REDIS_PRIVATE_DNS_ZONE = "privatelink.redis.azure.net";

        /// <summary>Private Link sub-resource ("group") ID used when targeting classic Azure Cache for Redis.</summary>
        public const string LEGACY_CLASSIC_PE_GROUP_ID = "redisCache";

        /// <summary>Private DNS zone for classic Azure Cache for Redis (<c>&lt;name&gt;.redis.cache.windows.net</c>).</summary>
        public const string LEGACY_CLASSIC_PRIVATE_DNS_ZONE = "privatelink.redis.cache.windows.net";

        private readonly bool _requireStandardSku;
        private readonly bool _allowPublicAccess;

        public RedisInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, bool requireStandardSku = false, bool allowPublicAccess = true) : base(config, logger, azureLocation, tags)
        {
            _requireStandardSku = requireStandardSku;
            _allowPublicAccess = allowPublicAccess;
        }

        public override string TaskName => "get/create redis cache";

        /// <summary>
        /// Result of the most recent run of this task. Set by <see cref="ExecuteTaskReturnResult"/>
        /// and read by the Redis-aware private endpoint / DNS zone wrapper tasks so they can
        /// no-op when a legacy classic Azure Cache for Redis is being reused.
        /// </summary>
        public RedisInstallResult LastResult { get; private set; }

        public async override Task<RedisInstallResult> ExecuteTaskReturnResult(object contextArg)
        {
            var name = base._config.GetNameConfigValue();

            // Legacy detection: if a classic Azure Cache for Redis already exists with the
            // configured name, reuse it rather than provision Azure Managed Redis alongside it.
            // We don't store anything critical in Redis (just cached tokens), so an operator who
            // wants to upgrade can delete the legacy resource and re-run the installer.
            var legacy = await TryGetLegacyClassicCacheAsync(name);
            if (legacy != null)
            {
                LastResult = await ReuseLegacyClassicCacheAsync(legacy);
                return LastResult;
            }

            LastResult = await CreateOrGetManagedRedisAsync(name);
            return LastResult;
        }

        private Task<RedisResource> TryGetLegacyClassicCacheAsync(string name)
        {
            var allLegacy = base.Container.GetAllRedis();
            return Task.FromResult(allLegacy.Where(c => c.Data.Name == name).SingleOrDefault());
        }

        private async Task<RedisInstallResult> ReuseLegacyClassicCacheAsync(RedisResource legacy)
        {
            var name = legacy.Data.Name;

            _logger.LogWarning(
                $"Detected pre-existing classic Azure Cache for Redis '{name}' (resource type Microsoft.Cache/Redis). " +
                "This installer now provisions Azure Managed Redis (Microsoft.Cache/redisEnterprise) for new installs, " +
                "but the legacy cache will be reused as-is to avoid disruption. " +
                "To upgrade to Azure Managed Redis, delete the legacy cache in the Azure portal and re-run the installer — " +
                "a new Managed Redis cluster will be provisioned with the same name. " +
                "Nothing critical is stored in Redis (token cache only), so deletion is safe.");

            // Enforce TLS 1.2 and the configured public-network-access setting, but preserve
            // the existing SKU to avoid downgrade errors.
            var desiredAccess = _allowPublicAccess ? RedisPublicNetworkAccess.Enabled : RedisPublicNetworkAccess.Disabled;
            bool needsUpdate = false;
            var existingSku = legacy.Data.Sku;
            var updateData = new RedisCreateOrUpdateContent(AzureLocation, new RedisSku(existingSku.Name, existingSku.Family, existingSku.Capacity))
            {
                MinimumTlsVersion = RedisTlsVersion.Tls1_2
            };

            if (legacy.Data.MinimumTlsVersion == null || !legacy.Data.MinimumTlsVersion.Value.ToString().Equals(RedisTlsVersion.Tls1_2.ToString()))
            {
                _logger.LogInformation($"Updating legacy Redis cache '{name}' to enforce TLS 1.2...");
                needsUpdate = true;
            }

            if (legacy.Data.PublicNetworkAccess == null || legacy.Data.PublicNetworkAccess.Value != desiredAccess)
            {
                _logger.LogInformation($"Updating legacy Redis cache '{name}' public network access to '{desiredAccess}'...");
                updateData.PublicNetworkAccess = desiredAccess;
                needsUpdate = true;
            }

            if (needsUpdate)
            {
                await base.Container.GetAllRedis().CreateOrUpdateAsync(WaitUntil.Completed, name, updateData);
                // Re-fetch to pick up the updated keys/host info, just in case.
                legacy = (await base.Container.GetRedisAsync(name)).Value;
            }

            await base.EnsureTagsOnExisting(legacy.Data.Tags, legacy.GetTagResource());

            var keys = await legacy.GetKeysAsync();
            return new RedisInstallResult
            {
                IsLegacyClassicCache = true,
                HostName = legacy.Data.HostName,
                Port = legacy.Data.SslPort ?? LEGACY_TLS_PORT,
                PrimaryKey = keys.Value.PrimaryKey,
                ResourceId = legacy.Id.ToString(),
                ResourceName = legacy.Data.Name,
                PrivateLinkGroupId = LEGACY_CLASSIC_PE_GROUP_ID,
                PrivateDnsZoneName = LEGACY_CLASSIC_PRIVATE_DNS_ZONE,
            };
        }

        private async Task<RedisInstallResult> CreateOrGetManagedRedisAsync(string name)
        {
            // BalancedB0 is the smallest/cheapest SKU (256 MB, no VNet/PE support).
            // BalancedB1 is required when private endpoints are needed (VNet-enabled deployments).
            var skuName = _requireStandardSku ? RedisEnterpriseSkuName.BalancedB1 : RedisEnterpriseSkuName.BalancedB0;
            var skuLabel = _requireStandardSku ? "Balanced B1" : "Balanced B0";

            var allClusters = base.Container.GetRedisEnterpriseClusters();
            var cluster = allClusters.Where(c => c.Data.Name == name).SingleOrDefault();

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

            // Get or create the default database.
            //
            // For NEW databases we default to RBAC-only (AccessKeysAuthentication = Disabled)
            // — the runtime authenticates via Entra ID / managed identity / service principal
            // (see RedisAccessPolicyAssignmentTask and CacheConnectionManager). Key auth on a
            // fresh deployment is left off by design so that the cache can never be reached by
            // a leaked connection string alone.
            //
            // For EXISTING databases we preserve whatever access-key mode is already set:
            // disabling keys on a running cache mid-install would instantly break any in-flight
            // connection that still has the old keyed connection string, and existing
            // deployments may legitimately rely on key auth.
            var databases = cluster.GetRedisEnterpriseDatabases();
            RedisEnterpriseDatabaseResource database;
            bool isNewDatabase = false;
            try
            {
                database = await cluster.GetRedisEnterpriseDatabaseAsync("default");
                _logger.LogInformation($"Found existing Azure Managed Redis database on port {database.Data.Port}.");
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogInformation($"Creating default database for Azure Managed Redis cluster '{name}' with access keys disabled (RBAC/Entra ID auth)...");
                var dbData = new RedisEnterpriseDatabaseData
                {
                    ClusteringPolicy = RedisEnterpriseClusteringPolicy.OssCluster,
                    EvictionPolicy = RedisEnterpriseEvictionPolicy.AllKeysLru,
                    AccessKeysAuthentication = AccessKeysAuthentication.Disabled
                };
                var dbOp = await databases.CreateOrUpdateAsync(WaitUntil.Completed, "default", dbData);
                database = dbOp.Value;
                isNewDatabase = true;
                _logger.LogInformation($"Created Azure Managed Redis database on port {database.Data.Port} (RBAC-only — no access keys).");
            }

            // Decide auth mode for downstream tasks. Null = treat as Enabled (preserve current
            // behaviour and never silently break an install whose runtime is already authed
            // with keys).
            var keysAuth = database.Data.AccessKeysAuthentication;
            var rbacOnly = isNewDatabase
                || (keysAuth.HasValue && keysAuth.Value == AccessKeysAuthentication.Disabled);

            if (!isNewDatabase)
            {
                if (rbacOnly)
                {
                    _logger.LogInformation($"Azure Managed Redis database '{database.Data.Name}' has access keys disabled — using RBAC/Entra ID auth.");
                }
                else
                {
                    _logger.LogInformation($"Azure Managed Redis database '{database.Data.Name}' has access keys enabled — using key-based auth (existing configuration preserved).");
                }
            }

            string primaryKey = null;
            if (!rbacOnly)
            {
                var keys = await database.GetKeysAsync();
                primaryKey = keys.Value.PrimaryKey;
            }

            return new RedisInstallResult
            {
                IsLegacyClassicCache = false,
                UseRbacAuth = rbacOnly,
                HostName = cluster.Data.HostName,
                Port = database.Data.Port ?? DEFAULT_TLS_PORT,
                PrimaryKey = primaryKey,
                ResourceId = cluster.Id.ToString(),
                ResourceName = cluster.Data.Name,
                PrivateLinkGroupId = MANAGED_REDIS_PE_GROUP_ID,
                PrivateDnsZoneName = MANAGED_REDIS_PRIVATE_DNS_ZONE,
            };
        }
    }
}

