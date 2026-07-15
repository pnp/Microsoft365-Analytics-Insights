namespace DataUtils.Health
{
    /// <summary>
    /// A dependency or capability the solution health-checks at runtime (and at install time, via the
    /// installer's SolutionInstallVerifier). Emitted as the "Component" dimension of the structured
    /// <c>HealthCheck</c> App Insights event so alerting can collapse to a couple of generic rules.
    /// See the health-monitoring design record (HEALTH-MONITORING-DESIGN.md) and issue #144, Appendix E.
    /// </summary>
    public enum HealthComponent
    {
        /// <summary>SQL connectivity + schema/migration version matches expected.</summary>
        Sql,

        /// <summary>Office 365 Management (Activity) API token acquisition + reachability.</summary>
        ActivityApi,

        /// <summary>Microsoft Graph token + Teams/user report permission probe.</summary>
        Graph,

        /// <summary>Key Vault data-plane read.</summary>
        KeyVault,

        /// <summary>Azure Cache for Redis reachability.</summary>
        Redis,

        /// <summary>Service Bus (Teams call records) reachability / dead-letter depth.</summary>
        ServiceBus,

        /// <summary>Runtime credential validity and days-to-expiry (client secret / certificate).</summary>
        Credential,

        /// <summary>DNS / endpoint reachability for configured resources.</summary>
        Dns,

        /// <summary>Audit-importer processed-blob checkpoint durability. Healthy = durable Azure Table store;
        /// Degraded = fell back to the in-memory store (dedupes within this process but is lost on restart,
        /// so the overlapping API lookback window is re-downloaded after every restart/redeploy).</summary>
        BlobCheckpoint
    }

    /// <summary>
    /// Health of a single <see cref="HealthComponent"/> check. Emitted as the "Status" dimension of the
    /// structured <c>HealthCheck</c> App Insights event. See issue #144, Appendix E.
    /// </summary>
    public enum HealthStatus
    {
        /// <summary>Check passed.</summary>
        Healthy,

        /// <summary>Working but in a warning state (e.g. credential expiring soon, backlog building).</summary>
        Degraded,

        /// <summary>Check failed - the component is not usable.</summary>
        Unhealthy
    }
}
