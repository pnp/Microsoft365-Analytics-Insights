namespace CloudInstallEngine.Models
{
    /// <summary>
    /// Result returned by <see cref="Azure.InstallTasks.RedisInstallTask"/>.
    /// Wraps either a newly-created Azure Managed Redis (Redis Enterprise) database
    /// or a pre-existing legacy classic Azure Cache for Redis that the installer
    /// detected and chose to reuse.
    /// </summary>
    public class RedisInstallResult
    {
        /// <summary>
        /// True when this result represents a pre-existing classic
        /// <c>Microsoft.Cache/Redis</c> resource that the installer detected and
        /// reused instead of provisioning a new Azure Managed Redis cluster.
        /// Downstream tasks (firewall, access policy, private endpoint, DNS zone)
        /// should skip themselves in this case because the previous install already
        /// configured them on the legacy resource.
        /// </summary>
        public bool IsLegacyClassicCache { get; set; }

        /// <summary>Public hostname used by clients to connect.</summary>
        public string HostName { get; set; }

        /// <summary>TLS port (6380 for classic Azure Cache for Redis, 10000 for Azure Managed Redis).</summary>
        public int Port { get; set; }

        /// <summary>Primary access key (used in the App Service connection string).</summary>
        public string PrimaryKey { get; set; }

        /// <summary>ARM resource ID of the underlying cache (the cluster for Managed Redis, the cache for classic).</summary>
        public string ResourceId { get; set; }

        /// <summary>Short resource name (as displayed in the portal).</summary>
        public string ResourceName { get; set; }
    }
}
