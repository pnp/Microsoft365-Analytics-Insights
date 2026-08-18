namespace DataUtils.Sql.Inserts
{
    /// <summary>
    /// Process-wide cap on the number of parallel SQL insert threads <see cref="InsertBatch{T}"/>
    /// uses when staging rows. Every InsertBatch-based importer (audit-event persistence, Copilot,
    /// Power Platform, App Insights hits) funnels its SQL commit through one
    /// <see cref="ParallelListProcessor{T}"/>, so this is the single lever for the SQL Server
    /// CPU/DTU burst the importers cause on commit.
    ///
    /// Defaults to the legacy 20 so behaviour is unchanged unless a host lowers it (issue #161 /
    /// PR #162 - easing importer CPU AND SQL Server CPU/DTU spikes). Intended to be set once at
    /// WebJob startup from the aggressiveness preset (and is what the load-test harness sweeps).
    /// </summary>
    public static class InsertBatchConcurrency
    {
        private static int _maxConcurrentThreads = 20;

        /// <summary>Max simultaneous SQL insert threads. Values &lt; 1 are clamped to 1.</summary>
        public static int MaxConcurrentThreads
        {
            get { return _maxConcurrentThreads; }
            set { _maxConcurrentThreads = value < 1 ? 1 : value; }
        }
    }
}
