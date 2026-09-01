namespace Common.Entities
{
    /// <summary>
    /// Port for creating an <see cref="AnalyticsEntitiesContext"/>, replacing the ad-hoc
    /// <c>Func&lt;AnalyticsEntitiesContext&gt;</c> parameters that three importers had each invented
    /// separately. See issue #368.
    ///
    /// Callers own the lifetime of what they create and must dispose it, exactly as they did with the
    /// factory delegate. This does not attempt to remove the <see cref="AnalyticsEntitiesContext"/>
    /// dependency itself - that is the job of the per-importer persistence-manager issues; the goal
    /// here is only to make context creation injectable and consistently named.
    /// </summary>
    public interface IAnalyticsDbContextFactory
    {
        /// <summary>Creates a new context. Each call returns a fresh instance that the caller disposes.</summary>
        AnalyticsEntitiesContext Create();
    }

    /// <summary>
    /// Production <see cref="IAnalyticsDbContextFactory"/>, using the parameterless context constructor
    /// (which resolves the connection string from configuration).
    /// </summary>
    public sealed class DefaultAnalyticsDbContextFactory : IAnalyticsDbContextFactory
    {
        public static readonly DefaultAnalyticsDbContextFactory Instance = new DefaultAnalyticsDbContextFactory();

        public AnalyticsEntitiesContext Create() => new AnalyticsEntitiesContext();
    }

    /// <summary>
    /// <see cref="IAnalyticsDbContextFactory"/> bound to an explicit connection string, for the
    /// installer, stress harnesses and load tests that must target a database other than the one in
    /// configuration.
    /// </summary>
    public sealed class ConnectionStringAnalyticsDbContextFactory : IAnalyticsDbContextFactory
    {
        private readonly string _connectionString;

        public ConnectionStringAnalyticsDbContextFactory(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new System.ArgumentException("A connection string is required.", nameof(connectionString));
            }
            _connectionString = connectionString;
        }

        public AnalyticsEntitiesContext Create() => new AnalyticsEntitiesContext(_connectionString, true, true);
    }
}
