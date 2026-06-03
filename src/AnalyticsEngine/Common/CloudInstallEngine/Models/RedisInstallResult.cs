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

        /// <summary>
        /// Primary access key. <c>null</c> when <see cref="UseRbacAuth"/> is true (the database
        /// was created with access keys disabled) — in that case clients must connect via
        /// Entra ID / RBAC instead. Always populated for legacy classic caches and for existing
        /// Managed Redis databases that still have key auth enabled.
        /// </summary>
        public string PrimaryKey { get; set; }

        /// <summary>
        /// True when this Managed Redis database has access keys disabled and clients must use
        /// Entra ID / RBAC to connect. Set for newly-created Managed Redis databases (which now
        /// default to RBAC-only) and for any existing database where the installer reads
        /// <c>AccessKeysAuthentication = Disabled</c>. Always <c>false</c> for legacy classic
        /// Azure Cache for Redis (those use key auth via the .NET SDK).
        /// </summary>
        public bool UseRbacAuth { get; set; }

        /// <summary>ARM resource ID of the underlying cache (the cluster for Managed Redis, the cache for classic).</summary>
        public string ResourceId { get; set; }

        /// <summary>Short resource name (as displayed in the portal).</summary>
        public string ResourceName { get; set; }
    }
}
