extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.Health;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory <see cref="IHealthDataSource"/> so the Health section-building logic can be tested with
    /// zero SQL Server dependency (issue #379). Every result is settable, including the partial-failure
    /// combinations that are the whole point of the section: a DMV permission failure, a timed-out
    /// volume scan, and a hard "database unreachable" failure.
    /// </summary>
    public class FakeHealthDataSource : IHealthDataSource
    {
        /// <summary>What the reachability probe returns. Reachable unless a test says otherwise.</summary>
        public DatabaseProbeResult ProbeResult { get; set; } = new DatabaseProbeResult();

        /// <summary>What the cheap counts block returns.</summary>
        public DatabaseCountsResult CountsResult { get; set; } = new DatabaseCountsResult();

        /// <summary>Per-table recent-volume results, keyed by table name.</summary>
        public Dictionary<string, RecentVolumeResult> RecentVolumeByTable { get; } = new Dictionary<string, RecentVolumeResult>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> PendingMigrations { get; set; } = new List<string>();

        public CallWebhookStatusResult CallWebhookStatus { get; set; } = new CallWebhookStatusResult();

        /// <summary>Set to throw from <see cref="GetPendingMigrationsAsync"/>, as a broken schema read does.</summary>
        public Exception PendingMigrationsException { get; set; }

        /// <summary>Set to throw from <see cref="GetCallWebhookStatusAsync"/>, as a broken config read does.</summary>
        public Exception CallWebhookStatusException { get; set; }

        public int ProbeCallCount { get; private set; }
        public int CountsCallCount { get; private set; }

        /// <summary>Makes <see cref="GetDatabaseCountsAsync"/> take this long, to stand in for a slow scan.</summary>
        public TimeSpan CountsDelay { get; set; } = TimeSpan.Zero;

        /// <summary>Every table a recent-volume scan was requested for, in order.</summary>
        public List<string> RecentVolumeRequests { get; } = new List<string>();

        public Task<DatabaseProbeResult> ProbeDatabaseAsync()
        {
            ProbeCallCount++;
            return Task.FromResult(ProbeResult);
        }

        public async Task<DatabaseCountsResult> GetDatabaseCountsAsync()
        {
            CountsCallCount++;
            if (CountsDelay > TimeSpan.Zero) await Task.Delay(CountsDelay);
            return CountsResult;
        }

        public Task<RecentVolumeResult> GetRecentVolumeAsync(string table, string timestampColumn)
        {
            RecentVolumeRequests.Add(table);
            return Task.FromResult(RecentVolumeByTable.TryGetValue(table ?? string.Empty, out var v) ? v : new RecentVolumeResult());
        }

        public Task<IReadOnlyList<string>> GetPendingMigrationsAsync()
        {
            if (PendingMigrationsException != null) throw PendingMigrationsException;
            return Task.FromResult(PendingMigrations);
        }

        public Task<CallWebhookStatusResult> GetCallWebhookStatusAsync()
        {
            if (CallWebhookStatusException != null) throw CallWebhookStatusException;
            return Task.FromResult(CallWebhookStatus);
        }
    }
}
